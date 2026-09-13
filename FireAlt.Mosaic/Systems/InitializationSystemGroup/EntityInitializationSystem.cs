using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(TilemapInitializationSystemGroup))]
    public partial struct EntityInitializationSystem : ISystem
    {
        private NativeList<EntityCommand> _commandsList;
        private NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> _intGridLayers;
        
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.CompleteDependencyBeforeRO<TilemapIntGridSingleton>();
            var dataSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW;
            _commandsList = dataSingleton.EntityCommands.List;
            _intGridLayers = dataSingleton.IntGridLayers;
            
            var queuedLength = _commandsList.Length;
            if (queuedLength == 0) return;
            var validCount = 0;
            for (var i = 0; i < queuedLength; i++)
            {
                var command = _commandsList[i];
                if (!_intGridLayers.TryGetValue(command.IntGridHash, out var layer)
                    || layer.IntGridEntity != command.IntGridEntity
                    || !layer.RuleGrid.TryGetValue(command.Position, out var rule)
                    || rule.Version != command.RuleVersion) continue;

                var alreadySpawned = false;
                if (layer.SpawnedEntities.TryGetFirstValue(command.Position, out var spawned, out var iterator))
                {
                    do
                    {
                        if (spawned.RuleVersion == command.RuleVersion && spawned.Face == command.Face)
                        {
                            alreadySpawned = true;
                            break;
                        }
                    }
                    while (layer.SpawnedEntities.TryGetNextValue(out spawned, ref iterator));
                }
                if (!alreadySpawned) _commandsList[validCount++] = command;
            }
            _commandsList.ResizeUninitialized(validCount);
            if (_commandsList.Length == 0)
            {
                dataSingleton.EntityCommands.Clear();
                return;
            }
            _commandsList.Sort(new DeferredCommandComparer());
            
            var beginBatchIndex = 0;
            for (int i = 0; i < _commandsList.Length - 1; i++)
            {
                var currentCommand = _commandsList[i];
                var nextCommand = _commandsList[i + 1];
                if (currentCommand.SrcEntity == nextCommand.SrcEntity) continue;
                
                UploadBatch(ref state, beginBatchIndex, i, currentCommand.SrcEntity);
                beginBatchIndex = i + 1;
            }
            UploadBatch(ref state, beginBatchIndex, _commandsList.Length - 1, _commandsList[^1].SrcEntity);
            
            dataSingleton.EntityCommands.Clear();
        }
        
        private void UploadBatch(ref SystemState state, int beginIndex, int endIndex, in Entity srcEntity)
        {
            var length = endIndex - beginIndex + 1;
            if (length <= 0 || !state.EntityManager.Exists(srcEntity)) return;
            
            var srcTransform = state.EntityManager.GetComponentData<LocalTransform>(srcEntity);
            var hasTilemapCellComponent = state.EntityManager.HasComponent<TilemapCell>(srcEntity);
            
            var instances = new NativeArray<Entity>(length, Allocator.Temp);
            state.EntityManager.Instantiate(srcEntity, instances);

            for (var i = 0; i < instances.Length; i++)
            {
                var currentCommand = _commandsList[beginIndex + i];
                var instance = instances[i];
                    
                var cell = currentCommand.Position;

                ref var dataLayer = ref _intGridLayers.GetValueAsRef(currentCommand.IntGridHash);
                var rendererData = state.EntityManager.GetComponentData<TilemapTransform>(dataLayer.IntGridEntity);
                var tilemapTransform = state.EntityManager.GetComponentData<LocalToWorld>(dataLayer.IntGridEntity);
                
                var position = MosaicUtils.ToWorldSpace(cell, rendererData) + srcTransform.Position;
                var rotation = srcTransform.Rotation;
                if (MosaicUtils.IsStandingTile(rendererData))
                {
                    var center = MosaicUtils.ToWorldSpace(cell + new float2(0.5f), rendererData);
                    position = center + MosaicUtils.TransformStandingTile(position - center, currentCommand.MatchedMirror, currentCommand.MatchedRotation);
                    rotation = math.mul(quaternion.RotateY(currentCommand.Face * (math.PI / 2f)), rotation);
                }

                state.EntityManager.SetComponentData(instance, new LocalTransform
                {
                    Position = position + tilemapTransform.Position,
                    Scale = srcTransform.Scale,
                    Rotation = rotation
                });
                if (hasTilemapCellComponent)
                {
                    state.EntityManager.SetComponentData(instance, new TilemapCell 
                    { 
                        IntGridLayerHash = currentCommand.IntGridHash,
                        Cell = cell 
                    });
                }
                
                dataLayer.SpawnedEntities.Add(cell, new TilemapIntGridSingleton.SpawnedEntity
                {
                    Entity = instance,
                    RuleVersion = currentCommand.RuleVersion,
                    Face = currentCommand.Face
                });
            }
        }
    }
}
