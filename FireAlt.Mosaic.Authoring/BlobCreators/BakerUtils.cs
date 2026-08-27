using System;
using System.Collections.Generic;
using FireAlt.Core.EntityCommands;
using FireAlt.Core.Rendering;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Authoring
{
    public static class BakerUtils
    {
        internal static void RegisterDependencies(IBaker baker, IntGridDefinition intGrid)
        {
            if (intGrid == null) return;

            baker.DependsOn(intGrid);
            foreach (var group in intGrid.ruleGroups)
            {
                if (group != null) baker.DependsOn(group);
            }
        }

        public static void AddTilemapTransform(IBaker baker, Entity entity, Entity gridEntity,
            RenderingData renderingData)
        {
            baker.AddComponent(entity, new TilemapTransform
            {
                GridEntity = gridEntity,
                Orientation = renderingData.orientation,
            });
        }
        
        public static void AddRenderingData(IBaker baker, Entity entity, GameObject gameObject, Hash128 hash,
            RenderingData renderingData, RefSprite refSprite)
        {
            if (renderingData.material == null)
            {
                throw new Exception("Material is null");
            }
            baker.DependsOn(renderingData.material);
            
            baker.AddComponent(entity, new TilemapRendererData
            {
                MeshHash = hash,
                LayerMask = gameObject.layer,
                RenderingLayerMask = renderingData.renderingLayerMask,
                ShadowCastingMode = renderingData.shadowCastingMode,
                ReceiveShadows = renderingData.receiveShadows,
            });
            
            baker.AddComponent(entity, refSprite.Sprite == null
                ? new RuntimeMaterialLookup(renderingData.material, renderingData.material.mainTexture)
                : new RuntimeMaterialLookup(renderingData.material, refSprite.Sprite));
            baker.AddComponent<RuntimeMaterial>(entity);
            baker.AddComponent<MosaicRendererInitialized>(entity);
            baker.SetComponentEnabled<MosaicRendererInitialized>(entity, false);
        }
        
        public static void AddIntGridLayerData(IBaker baker, Entity entity, IntGridDefinition intGrid,
            Hash128 runtimeHash, RefSprite refSprite, bool constPivotAndSize, ref float2 tilePivot, ref float2 tileSize,
            IReadOnlyList<SerializedIntGridRectangle> initialValues, Func<GameObject, Entity> entityResolver)
        {
            if (!TryValidateRuleResults(intGrid, out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            AddIntGridLayerData(baker, entity, intGrid, runtimeHash, refSprite, constPivotAndSize, ref tilePivot,
                ref tileSize, initialValues, entityResolver, true);
        }

        internal static void AddEmptyIntGridLayerData(IBaker baker, Entity entity, IntGridDefinition intGrid,
            Hash128 runtimeHash, IReadOnlyList<SerializedIntGridRectangle> initialValues)
        {
            var refSprite = new RefSprite();
            var tilePivot = float2.zero;
            var tileSize = float2.zero;
            AddIntGridLayerData(baker, entity, intGrid, runtimeHash, refSprite, false, ref tilePivot, ref tileSize,
                initialValues, null, false);
        }

        private static void AddIntGridLayerData(IBaker baker, Entity entity, IntGridDefinition intGrid,
            Hash128 runtimeHash, RefSprite refSprite, bool constPivotAndSize, ref float2 tilePivot, ref float2 tileSize,
            IReadOnlyList<SerializedIntGridRectangle> initialValues, Func<GameObject, Entity> entityResolver,
            bool includeRules)
        {
            baker.AddComponent(entity, new IntGridData
            {
                Hash = runtimeHash,
                DebugName = intGrid.name,
                DualGrid = intGrid.useDualGrid
            });
            baker.SetComponentEnabled<IntGridData>(entity, false);
            
            var ruleBlobBuffer = baker.AddBuffer<RuleBlobReferenceElement>(entity);
            var weightedEntityBuffer = baker.AddBuffer<WeightedEntityElement>(entity);
            var refreshPositionsBuffer = baker.AddBuffer<RefreshPositionElement>(entity);
            var intGridValues = baker.AddBuffer<IntGridValueElement>(entity);
            var initialValuesBuffer = baker.AddBuffer<IntGridInitialValueElement>(entity);

            var refreshPositions = new NativeHashSet<int2>(64, Allocator.Temp);

            var entityCount = 0;
            if (includeRules)
            {
                foreach (var group in intGrid.ruleGroups)
                {
                    foreach (var rule in group.rules)
                    {
                        var blob = RuleBlobCreator.Create(rule, entityCount, refreshPositions);
                        baker.AddBlobAsset(ref blob, out _);

                        ruleBlobBuffer.Add(new RuleBlobReferenceElement
                        {
                            Enabled = rule.enabled.HasFlag(RuleGroup.Enabled.Enabled),
                            Value = blob
                        });
                        
                        AddResults(rule, weightedEntityBuffer, refSprite, constPivotAndSize, ref tilePivot, ref tileSize,
                            entityResolver);
                        entityCount += rule.TileEntities.Count;
                    }
                }
            }

            refreshPositionsBuffer.AddRange(refreshPositions.ToNativeArray(Allocator.Temp).Reinterpret<RefreshPositionElement>());

            foreach (var definition in intGrid.intGridValues)
            {
                intGridValues.Add(new IntGridValueElement
                {
                    Value = definition.value,
                    Name = definition.name,
                    Color = definition.color,
                    Texture = definition.texture,
                });
            }

            foreach (var rectangle in initialValues)
            {
                for (var y = 0; y < rectangle.Size.y; y++)
                {
                    for (var x = 0; x < rectangle.Size.x; x++)
                    {
                        initialValuesBuffer.Add(new IntGridInitialValueElement
                        {
                            Position = new int2(rectangle.Position.x + x, rectangle.Position.y + y),
                            Value = rectangle.Value,
                        });
                    }
                }
            }
        }

        internal static bool TryValidateRuleResults(IntGridDefinition intGrid, out string error)
        {
            for (var groupIndex = 0; groupIndex < intGrid.ruleGroups.Count; groupIndex++)
            {
                var group = intGrid.ruleGroups[groupIndex];
                if (group == null)
                {
                    error = $"IntGrid '{intGrid.name}' has no RuleGroup assigned at index {groupIndex}.";
                    return false;
                }

                for (var ruleIndex = 0; ruleIndex < group.rules.Count; ruleIndex++)
                {
                    var rule = group.rules[ruleIndex];
                    if (rule == null)
                    {
                        error = $"RuleGroup '{group.name}' has no rule assigned at index {ruleIndex}.";
                        return false;
                    }

                    for (var resultIndex = 0; resultIndex < rule.TileSprites.Count; resultIndex++)
                    {
                        if (rule.TileSprites[resultIndex]?.result != null) continue;
                        error = $"RuleGroup '{group.name}' rule {ruleIndex} tile sprite {resultIndex} has no Sprite " +
                                "assigned. Assign a Sprite or remove the entry.";
                        return false;
                    }

                    for (var resultIndex = 0; resultIndex < rule.TileEntities.Count; resultIndex++)
                    {
                        if (rule.TileEntities[resultIndex]?.result != null) continue;
                        error = $"RuleGroup '{group.name}' rule {ruleIndex} tile entity {resultIndex} has no prefab " +
                                "assigned. Assign a prefab or remove the entry.";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        internal static void LogBakingError(UnityEngine.Object context, string error)
        {
            Debug.LogError($"Mosaic did not bake '{context.name}': {error}", context);
        }
        
        private static void AddResults(RuleGroup.Rule rule, DynamicBuffer<WeightedEntityElement> weightedEntityBuffer,
            RefSprite refSprite, bool constPivotAndSize, ref float2 tilePivot, ref float2 tileSize,
            Func<GameObject, Entity> entityResolver)
        {
            for (var i = 0; i < rule.TileEntities.Count; i++)
            {
                weightedEntityBuffer.Add(new WeightedEntityElement
                {
                    Value = entityResolver(rule.TileEntities[i].result)
                });
            }

            for (int i = 0; i < rule.TileSprites.Count; i++)
            {
                var sprite = rule.TileSprites[i].result;

                if (constPivotAndSize)
                {
                    var spriteMesh = new SpriteMesh(sprite);
                    var uvPivot = spriteMesh.NormalizedPivot;
                    var uvTileSize = spriteMesh.MaxUv - spriteMesh.MinUv;
                    
                    //Debug.Log($"Found {uvTileSize.ToString()} in sprite {sprite}, atlas: {sprite.texture}, expected: {tileSize.ToString()}");
                    
                    if (math.all(tilePivot == float2.zero))
                    {
                        tilePivot = uvPivot;
                    }
                    if (math.all(tileSize == float2.zero))
                    {
                        tileSize = uvTileSize;
                    }
                    
                    if (math.any(math.abs(tilePivot - uvPivot) > new float2(0.0001f)))
                    {
                        throw new Exception("Different pivots in one tilemap terrain. This is not supported");
                    }
                    if (math.any(math.abs(tileSize - uvTileSize) > new float2(0.0001f)))
                    {
                        throw new Exception($"Different tile sizes in one tilemap terrain. Found {uvTileSize.ToString()} in sprite {sprite}, expected: {tileSize.ToString()}. This is not supported");
                    }
                }

                if (refSprite.Sprite == null)
                {
                    refSprite.Sprite = sprite;
                }
                else if (refSprite.Sprite.texture != sprite.texture)
                {
                    throw new Exception("Different textures in one tilemap. This is not supported");
                }
            }
        }
        
        public static Hash128 GetHash(IntGridDefinition intGrid, bool isGlobal)
        {
            return intGrid != null && isGlobal ? intGrid.Hash : default;
        } 
    }
}
