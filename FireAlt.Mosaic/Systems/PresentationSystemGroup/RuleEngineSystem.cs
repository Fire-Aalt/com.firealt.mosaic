using System.Runtime.CompilerServices;
using FireAlt.Core.Collections;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(TilemapUpdateSystemGroup))]
    public partial struct RuleEngineSystem : ISystem
    {
        private EntityQuery _query;
        private uint _previousSeed;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.EntityManager.CreateSingleton(new TilemapCommandBufferSingleton(8, Allocator.Persistent));
            state.EntityManager.CreateSingleton(new TilemapIntGridSingleton
            {
                IntGridLayers = new NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer>(256, Allocator.Persistent),
                EntityCommands = new NativeThreadToListMapper<EntityCommand>(256, Allocator.Persistent)
            });
            
            _query = SystemAPI.QueryBuilder()
                .WithAll<IntGridData, RuleBlobReferenceElement, RefreshPositionElement, WeightedEntityElement>()
                .Build();
            
            state.RequireForUpdate(_query);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            SystemAPI.GetSingleton<TilemapCommandBufferSingleton>().Dispose();
            SystemAPI.GetSingleton<TilemapIntGridSingleton>().Dispose();
        }
        
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        { 
            var tcb = SystemAPI.GetSingletonRW<TilemapCommandBufferSingleton>().ValueRW; 
            var dataSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW;
            var seed = tcb.GlobalSeed.Value;
            var randomize = seed != _previousSeed;
            _previousSeed = seed;
            
            var intGridEntities = _query.ToEntityListAsync(state.WorldUpdateAllocator,
                state.Dependency, out var dependency);
            
            var tilemapDataLookup = SystemAPI.GetComponentLookup<IntGridData>(true);
            var rulesBufferLookup = SystemAPI.GetBufferLookup<RuleBlobReferenceElement>(true);
            var refreshOffsetsBufferLookup = SystemAPI.GetBufferLookup<RefreshPositionElement>(true);
            var entitiesBufferLookup = SystemAPI.GetBufferLookup<WeightedEntityElement>(true);
            var tilemapTransformLookup = SystemAPI.GetComponentLookup<TilemapTransform>(true);
            
            state.Dependency = new ClearAndFindRefreshPositionsJob
            {
                IntGridEntities = intGridEntities.AsDeferredJobArray(),
                IntGridLayerDataLookup = tilemapDataLookup,
                RulesBufferLookup = rulesBufferLookup,
                RefreshOffsetsBufferLookup = refreshOffsetsBufferLookup,
                EntitiesBufferLookup = entitiesBufferLookup,
                IntGridLayers = dataSingleton.IntGridLayers,
                TcbLayers = tcb.IntGridLayers,
                RefreshAll = randomize,
                LastSystemVersion = state.LastSystemVersion,
            }.Schedule(intGridEntities, 1, dependency);
            
            state.Dependency = new ExecuteRulesJob
            {
                IntGridEntities = intGridEntities.AsDeferredJobArray(),
                TilemapData = tilemapDataLookup,
                RulesBufferLookup = rulesBufferLookup,
                EntitiesBufferLookup = entitiesBufferLookup,
                TilemapTransformLookup = tilemapTransformLookup,
                IntGridLayers = dataSingleton.IntGridLayers,
                EntityCommands = dataSingleton.EntityCommands.AsThreadWriter(),
                Seed = seed,
            }.Schedule(intGridEntities, 1, state.Dependency);
            
            state.Dependency = dataSingleton.EntityCommands.CopyParallelToListSingle(state.Dependency);
        }
        
        [BurstCompile]
        private struct ClearAndFindRefreshPositionsJob : IJobParallelForDefer // This job has to be a IJobParallelForDefer because it is heavy and should be redistributed in parallel
        {
            public NativeArray<Entity> IntGridEntities;
            [ReadOnly]
            public ComponentLookup<IntGridData> IntGridLayerDataLookup;
            [ReadOnly]
            public BufferLookup<RuleBlobReferenceElement> RulesBufferLookup;
            [ReadOnly]
            public BufferLookup<RefreshPositionElement> RefreshOffsetsBufferLookup;
            [ReadOnly]
            public BufferLookup<WeightedEntityElement> EntitiesBufferLookup;
            
            [NativeDisableContainerSafetyRestriction]
            public NativeHashMap<Hash128, TilemapCommandBufferSingleton.IntGridLayer> TcbLayers;
            [NativeDisableParallelForRestriction]
            public NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> IntGridLayers;
            public bool RefreshAll;
            public uint LastSystemVersion;

            public void Execute(int index)
            {
                var intGridEntity = IntGridEntities[index];
                var intGridData = IntGridLayerDataLookup[intGridEntity];
                var intGridHash = intGridData.Hash;
                
                ref var commandsLayer = ref TcbLayers.GetValueAsRef(intGridHash);
                ref var dataLayer = ref IntGridLayers.GetValueAsRef(intGridHash);

                dataLayer.Cleared = false;
                dataLayer.PositionsToRefresh.Clear();
                dataLayer.RefreshedPositions.Clear();

                var definitionChanged = dataLayer.DualGrid != intGridData.DualGrid
                    || RulesBufferLookup.DidChange(intGridEntity, LastSystemVersion)
                    || RefreshOffsetsBufferLookup.DidChange(intGridEntity, LastSystemVersion)
                    || EntitiesBufferLookup.DidChange(intGridEntity, LastSystemVersion);
                dataLayer.DualGrid = intGridData.DualGrid;
                dataLayer.ForceRuleRefresh = RefreshAll || definitionChanged;
                if (dataLayer.ForceRuleRefresh) PrepareFullRefresh(ref dataLayer);
                
                if (TryClearAll(ref commandsLayer, ref dataLayer))
                    return;
                
                ExecuteSetCommands(ref commandsLayer, ref dataLayer);
                
                if (dataLayer.ChangedPositions.Count == 0)
                    return;
                
                RefreshOffsetsBufferLookup.TryGetBuffer(intGridEntity, out var refreshPositionsBuffer);
                FindPositionsToRefresh(ref dataLayer, refreshPositionsBuffer);
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool TryClearAll(ref TilemapCommandBufferSingleton.IntGridLayer commandsLayer, ref TilemapIntGridSingleton.IntGridLayer dataLayer)
            {
                if (!commandsLayer.ClearCommand) return false;

                commandsLayer.SetCommands.Clear();
                commandsLayer.ClearCommand = false;
                
                dataLayer.IntGrid.Clear();
                dataLayer.RuleGrid.Clear();
                dataLayer.RenderedSprites.Clear();
                dataLayer.Cleared = true;
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ExecuteSetCommands(ref TilemapCommandBufferSingleton.IntGridLayer commandsLayer, ref TilemapIntGridSingleton.IntGridLayer dataLayer)
            {
                foreach (var command in commandsLayer.SetCommands)
                {
                    dataLayer.SetValue(command.Position, command.IntGridValue);
                }
                commandsLayer.SetCommands.Clear();
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void FindPositionsToRefresh(ref TilemapIntGridSingleton.IntGridLayer dataLayer, in DynamicBuffer<RefreshPositionElement> refreshPositionsBuffer)
            {
                foreach (var changedPosition in dataLayer.ChangedPositions)
                {
                    foreach (var refreshOffset in refreshPositionsBuffer)
                    {
                        var pos = changedPosition + refreshOffset.Value;
                        dataLayer.PositionsToRefresh.Add(pos);
                    }
                }
                dataLayer.ChangedPositions.Clear();
            }
        }

        [BurstCompile]
        private struct ExecuteRulesJob : IJobParallelForDefer
        {
            private const int STANDING_TRANSFORM_COUNT = 7;

            public NativeArray<Entity> IntGridEntities;
            [ReadOnly]
            public ComponentLookup<IntGridData> TilemapData;
            [ReadOnly]
            public BufferLookup<RuleBlobReferenceElement> RulesBufferLookup;
            [ReadOnly]
            public BufferLookup<WeightedEntityElement> EntitiesBufferLookup;
            [ReadOnly]
            public ComponentLookup<TilemapTransform> TilemapTransformLookup;
            
            [NativeDisableParallelForRestriction]
            public NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> IntGridLayers;
            public NativeThreadList<EntityCommand>.ThreadWriter EntityCommands;
            
            public uint Seed;

            private Hash128 _intGridHash;
            private Entity _intGridEntity;
            private bool _standingTile;
            private uint _layerSeed;

            [ReadOnly]
            private DynamicBuffer<RuleBlobReferenceElement> _rulesBuffer;
            [ReadOnly]
            private DynamicBuffer<WeightedEntityElement> _entityBuffer;

            private UnsafeHashMap<int2, IntGridValue>.ReadOnly _intGridMap;
            
            public void Execute(int index)
            {
                var intGridEntity = IntGridEntities[index];
                
                _intGridHash = TilemapData[intGridEntity].Hash;
                _intGridEntity = intGridEntity;
                _layerSeed = Seed ^ math.hash(_intGridHash.Value);
                ref var dataLayer = ref IntGridLayers.GetValueAsRef(_intGridHash);
                _standingTile = MosaicUtils.IsStandingTile(TilemapTransformLookup[intGridEntity]);
                var forceRuleRefresh = dataLayer.ForceRuleRefresh;
                dataLayer.ForceRuleRefresh = false;
                
                if (dataLayer.PositionsToRefresh.Count == 0) 
                    return;

                _intGridMap = dataLayer.IntGrid.AsReadOnly();
                
                RulesBufferLookup.TryGetBuffer(intGridEntity, out _rulesBuffer);
                EntitiesBufferLookup.TryGetBuffer(intGridEntity, out _entityBuffer);

                foreach (var posToRefresh in dataLayer.PositionsToRefresh)
                {
                    var ruleHashExists = dataLayer.RuleGrid.TryGetValue(posToRefresh, out var ruleState);
                    
                    var positionStillValid = RefreshPosition(
                        ref dataLayer, posToRefresh, ruleHashExists, ruleState, forceRuleRefresh);

                    if (ruleHashExists && !positionStillValid)
                    {
                        dataLayer.RenderedSprites.Remove(posToRefresh);
                        dataLayer.RuleGrid.Remove(posToRefresh);
                        dataLayer.RefreshedPositions.Add(posToRefresh);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool RefreshPosition(ref TilemapIntGridSingleton.IntGridLayer dataLayer, int2 posToRefresh,
                bool ruleHashExists, RuleResultState ruleState, bool forceRuleRefresh)
            {
                for (var ruleIndex = 0; ruleIndex < _rulesBuffer.Length; ruleIndex++)
                {
                    var ruleElement = _rulesBuffer[ruleIndex];
                    if (!ruleElement.Enabled)
                        continue;
                    
                    ref var rule = ref ruleElement.Value.Value;
                        
                    var random = new Random(MosaicUtils.Hash(_layerSeed, posToRefresh));
                    if (random.NextFloat() * 100f > rule.Chance)
                        continue;

                    byte selectedSlots = 0;
                    var appliedRotation = 0;
                    var appliedMirror = new bool2();
                    if (_standingTile)
                    {
                        selectedSlots = ExecuteStandingRules(ref rule, posToRefresh);
                        if (selectedSlots == 0) continue;
                    }
                    else if (!ExecuteRules(ref rule, posToRefresh, out appliedRotation, out appliedMirror))
                    {
                        continue;
                    }

                    var currentRuleHash = ruleHashExists ? ruleState.Hash : 0;
                    var newRuleHash = _standingTile ? MosaicUtils.Hash(ruleIndex, default, selectedSlots)
                        : MosaicUtils.Hash(ruleIndex, appliedMirror, appliedRotation);
                        
                    if (!RuleResultChanged(currentRuleHash, newRuleHash, forceRuleRefresh))
                        return true;
                    
                    dataLayer.RefreshedPositions.Add(posToRefresh);
                    var version = ++dataLayer.NextRuleVersion;
                    dataLayer.RuleGrid[posToRefresh] = new RuleResultState { Hash = newRuleHash, Version = version };
                    dataLayer.RenderedSprites.Remove(posToRefresh);

                    if (_standingTile)
                    {
                        var emittedPrefab = false;
                        for (var slot = 0; slot < STANDING_TRANSFORM_COUNT; slot++)
                        {
                            if ((selectedSlots & (1 << slot)) == 0) continue;
                            GetStandingTransform(rule.RuleTransform, slot, out _, out appliedMirror, out appliedRotation);
                            var face = MosaicUtils.StandingTileFace(appliedMirror, appliedRotation);
                            var faceSeed = _layerSeed ^ (uint)(ruleIndex * 431 + slot * 701);
                            var entityRandom = new Random(MosaicUtils.Hash(faceSeed, posToRefresh));
                            var spriteRandom = new Random(MosaicUtils.Hash(faceSeed ^ 0x9E3779B9u, posToRefresh));
                            if (!rule.UniquePrefabPerCell || !emittedPrefab)
                            {
                                TryAddEntity(ref rule, ref entityRandom, posToRefresh, version, face, appliedMirror, appliedRotation);
                                emittedPrefab = true;
                            }
                            TryAddSpriteMesh(ref dataLayer, ref rule, ref spriteRandom, posToRefresh, appliedMirror, appliedRotation);
                        }
                    }
                    else
                    {
                        TryAddEntity(ref rule, ref random, posToRefresh, version, 0, appliedMirror, appliedRotation);
                        TryAddSpriteMesh(ref dataLayer, ref rule, ref random, posToRefresh, appliedMirror, appliedRotation);
                    }
                    
                    return true;
                }
                return false;
            }

            private byte ExecuteStandingRules(ref RuleBlob rule, int2 posToRefresh)
            {
                byte selectedSlots = 0;
                byte faces = 0;
                for (var slot = 0; slot < STANDING_TRANSFORM_COUNT; slot++)
                {
                    if (!GetStandingTransform(rule.RuleTransform, slot, out var patternOffset, out var mirror, out var rotation)) continue;
                    var face = MosaicUtils.StandingTileFace(mirror, rotation);
                    var faceBit = (byte)(1 << face);
                    if ((faces & faceBit) != 0) continue;
                    if (!ExecuteRule(ref rule, posToRefresh, patternOffset)) continue;
                    faces |= faceBit;
                    selectedSlots |= (byte)(1 << slot);
                }
                return selectedSlots;
            }

            private static bool GetStandingTransform(Transformation transforms, int slot, out int patternOffset,
                out bool2 mirror, out int rotation)
            {
                patternOffset = 0;
                mirror = default;
                rotation = 0;
                if (slot == 0) return true;
                var mirrorX = transforms.IsMirroredX();
                var mirrorY = transforms.IsMirroredY();
                if (slot == 1 && mirrorX) { patternOffset = 1; mirror.x = true; return true; }
                if (mirrorX) patternOffset++;
                if (slot == 2 && mirrorY) { patternOffset++; mirror.y = true; return true; }
                if (mirrorY) patternOffset++;
                if (slot == 3 && mirrorX && mirrorY) { patternOffset++; mirror = new bool2(true, true); return true; }
                if (mirrorX && mirrorY) patternOffset++;
                if (slot >= 4 && transforms.HasFlagBurst(Transformation.Rotated))
                {
                    rotation = slot - 3;
                    patternOffset += rotation;
                    return true;
                }
                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool ExecuteRules(ref RuleBlob rule, int2 posToRefresh, out int appliedRotation, out bool2 appliedMirror)
            {
                appliedRotation = 0;
                appliedMirror = new bool2(false, false);
                if (ExecuteRule(ref rule, posToRefresh, 0))
                    return true;
                
                if (rule.RuleTransform == 0)
                    return false;

                var patternOffset = 1;
                if (rule.RuleTransform.IsMirroredX())
                {
                    appliedMirror = new bool2(true, false);
                    if (ExecuteRule(ref rule, posToRefresh, patternOffset)) 
                        return true;

                    patternOffset++;
                }

                if (rule.RuleTransform.IsMirroredY())
                {
                    appliedMirror = new bool2(false, true);
                    if (ExecuteRule(ref rule, posToRefresh, patternOffset)) 
                        return true;

                    patternOffset++;
                }

                if (rule.RuleTransform.IsMirroredX() && rule.RuleTransform.IsMirroredY())
                {
                    appliedMirror = new bool2(true, true);
                    if (ExecuteRule(ref rule, posToRefresh, patternOffset)) 
                        return true;

                    patternOffset++;
                }

                if (rule.RuleTransform.HasFlagBurst(Transformation.Rotated))
                {
                    appliedMirror = new bool2(false, false);
                    for (appliedRotation = 1; appliedRotation < 4; appliedRotation++)
                    {
                        if (ExecuteRule(ref rule, posToRefresh, patternOffset)) 
                            return true;

                        patternOffset++;
                    }
                }
                return false;
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void TryAddEntity(ref RuleBlob rule, ref Random random, int2 posToRefresh, uint version,
                byte face, bool2 appliedMirror, int appliedRotation)
            {
                if (rule.TryGetEntity(ref random, _entityBuffer, out var newEntity))
                {
                    EntityCommands.Add(new EntityCommand
                    {
                        SrcEntity = newEntity,
                        Position = posToRefresh,
                        IntGridHash = _intGridHash,
                        IntGridEntity = _intGridEntity,
                        RuleVersion = version,
                        Face = face,
                        MatchedMirror = appliedMirror,
                        MatchedRotation = appliedRotation
                    });
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void TryAddSpriteMesh(ref TilemapIntGridSingleton.IntGridLayer dataLayer, ref RuleBlob rule, ref Random random, int2 posToRefresh,
                bool2 appliedMirror, int appliedRotation)
            {
                if (rule.TryGetSpriteMesh(ref random, out var newSprite))
                {
                    var resultFlip = new bool2();
                    var resultRotation = 0;
                    if (rule.ResultTransform.HasFlagBurst(Transformation.MirrorX))
                    {
                        resultFlip.x = random.NextBool();
                    }
                    if (rule.ResultTransform.HasFlagBurst(Transformation.MirrorY))
                    {
                        resultFlip.y = random.NextBool();
                    }
                    if (rule.ResultTransform.HasFlagBurst(Transformation.Rotated))
                    {
                        resultRotation = random.NextInt(0, 4);
                    }
                            
                    newSprite.Flip = appliedMirror ^ resultFlip;
                    newSprite.MatchedMirror = appliedMirror;
                    newSprite.MatchedRotation = appliedRotation;
                    newSprite.Rotation = appliedRotation + resultRotation;
                    if (newSprite.Rotation > 3)
                        newSprite.Rotation -= 4;
                            
                    dataLayer.RenderedSprites.Add(posToRefresh, newSprite);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool ExecuteRule(ref RuleBlob rule, in int2 posToRefresh, int patternOffset)
            {
                var offset = patternOffset * rule.CellsToCheckCount;

                for (int i = 0; i < rule.CellsToCheckCount; i++)
                {
                    var cell = rule.Cells[offset + i];

                    var posToCheck = posToRefresh + cell.Offset;
                    _intGridMap.TryGetValue(posToCheck, out var value);

                    if (!MosaicUtils.CanPlace(cell.IntGridValue, value))
                        return false;
                }
                return true;
            }
        }

        internal static void PrepareFullRefresh(ref TilemapIntGridSingleton.IntGridLayer dataLayer)
        {
            foreach (var cell in dataLayer.IntGrid) dataLayer.MarkChanged(cell.Key);
            foreach (var rule in dataLayer.RuleGrid) dataLayer.PositionsToRefresh.Add(rule.Key);
        }

        internal static bool RuleResultChanged(int currentRuleHash, int newRuleHash, bool forceRuleRefresh)
        {
            return forceRuleRefresh || currentRuleHash != newRuleHash;
        }
    }
}
