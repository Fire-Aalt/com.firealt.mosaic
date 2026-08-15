using System;
using FireAlt.Core.Rendering;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;
using Random = Unity.Mathematics.Random;

namespace FireAlt.Mosaic.Authoring
{
    public static class BakerUtils
    {
        public static void AddTilemapTransform(IBaker baker, Entity entity, RenderingData renderingData, GridAuthoring gridAuthoring)
        {
            baker.AddComponent(entity, new TilemapTransform
            {
                GridEntity = baker.GetEntity(gridAuthoring, TransformUsageFlags.None),
                Orientation = renderingData.orientation,
            });
        }
        
        public static void AddRenderingData(IBaker baker, GameObject gameObject, Entity entity, Hash128 hash, RenderingData renderingData, RefSprite refSprite)
        {
            if (renderingData.material == null)
            {
                throw new Exception("Material is null");
            }
            
            baker.AddComponent(entity, new TilemapRendererData
            {
                MeshHash = hash,
                LayerMask = gameObject.layer,
                RenderingLayerMask = renderingData.renderingLayerMask,
                ShadowCastingMode = renderingData.shadowCastingMode,
                ReceiveShadows = renderingData.receiveShadows,
            });
            
            baker.AddComponent(entity, new RuntimeMaterialLookup(renderingData.material, refSprite.Sprite));
            baker.AddComponent<RuntimeMaterial>(entity);
        }
        
        public static void AddIntGridLayerData(IBaker baker, Entity entity, IntGridDefinition intGrid, bool isGlobal,
            RefSprite refSprite, bool constPivotAndSize, ref float2 tilePivot, ref float2 tileSize, out Hash128 runtimeHash)
        {
            var ruleBlobBuffer = baker.AddBuffer<RuleBlobReferenceElement>(entity);
            var weightedEntityBuffer = baker.AddBuffer<WeightedEntityElement>(entity);

            var refreshPositions = new NativeHashSet<int2>(64, Allocator.Temp);

            var entityCount = 0;
            baker.DependsOn(intGrid);
            foreach (var group in intGrid.ruleGroups)
            {
                baker.DependsOn(group);
                
                foreach (var rule in group.rules)
                {
                    var blob = RuleBlobCreator.Create(rule, entityCount, refreshPositions);
                    baker.AddBlobAsset(ref blob, out _);

                    ruleBlobBuffer.Add(new RuleBlobReferenceElement
                    {
                        Enabled = rule.enabled.HasFlag(RuleGroup.Enabled.Enabled),
                        Value = blob
                    });
                    
                    AddResults(baker, rule, weightedEntityBuffer, refSprite, constPivotAndSize, ref tilePivot, ref tileSize);
                    entityCount += rule.TileEntities.Count;
                }
            }

            runtimeHash = GetHash(intGrid, isGlobal);
            baker.AddComponent(entity, new IntGridData
            {
                Hash = runtimeHash,
                DebugName = intGrid.name,
                DualGrid = intGrid.useDualGrid
            });
            baker.SetComponentEnabled<IntGridData>(entity, false);
            
            var refreshPositionsBuffer = baker.AddBuffer<RefreshPositionElement>(entity);
            refreshPositionsBuffer.AddRange(refreshPositions.ToNativeArray(Allocator.Temp).Reinterpret<RefreshPositionElement>());

            var intGridValues = baker.AddBuffer<IntGridValueElement>(entity);
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
        }
        
        private static void AddResults(IBaker baker, RuleGroup.Rule rule,
            DynamicBuffer<WeightedEntityElement> weightedEntityBuffer, RefSprite refSprite,
            bool constPivotAndSize, ref float2 tilePivot, ref float2 tileSize)
        {
            for (var i = 0; i < rule.TileEntities.Count; i++)
            {
                var go = rule.TileEntities[i].result;

                weightedEntityBuffer.Add(new WeightedEntityElement
                {
                    Value = baker.GetEntity(go, TransformUsageFlags.None)
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
            if (isGlobal)
            {
                return intGrid.Hash;
            }
            var seed = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            return new Hash128(Random.CreateFromIndex(seed).NextUInt4());
        } 
    }
}