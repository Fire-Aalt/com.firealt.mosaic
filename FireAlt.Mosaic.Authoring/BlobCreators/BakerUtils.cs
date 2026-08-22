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
        public static void AddTilemapTransform<TCommands>(ref TCommands commands, Entity gridEntity,
            RenderingData renderingData)
            where TCommands : IEntityCommands
        {
            commands.AddComponent(new TilemapTransform
            {
                GridEntity = gridEntity,
                Orientation = renderingData.orientation,
            });
        }
        
        public static void AddRenderingData<TCommands>(ref TCommands commands, GameObject gameObject, Hash128 hash,
            RenderingData renderingData, RefSprite refSprite)
            where TCommands : IEntityCommands
        {
            if (renderingData.material == null)
            {
                throw new Exception("Material is null");
            }
            
            commands.AddComponent(new TilemapRendererData
            {
                MeshHash = hash,
                LayerMask = gameObject.layer,
                RenderingLayerMask = renderingData.renderingLayerMask,
                ShadowCastingMode = renderingData.shadowCastingMode,
                ReceiveShadows = renderingData.receiveShadows,
            });
            
            commands.AddComponent(refSprite.Sprite == null
                ? new RuntimeMaterialLookup(renderingData.material, renderingData.material.mainTexture)
                : new RuntimeMaterialLookup(renderingData.material, refSprite.Sprite));
            commands.AddComponent<RuntimeMaterial>();
            commands.AddComponent<MosaicRendererInitialized>();
            commands.SetComponentEnabled<MosaicRendererInitialized>(false);
        }
        
        public static void AddIntGridLayerData<TCommands>(ref TCommands commands, IntGridDefinition intGrid,
            Hash128 runtimeHash, RefSprite refSprite, bool constPivotAndSize, ref float2 tilePivot, ref float2 tileSize,
            IReadOnlyList<SerializedIntGridCell> initialValues, Func<GameObject, Entity> entityResolver)
            where TCommands : IEntityCommands
        {
            commands.AddBuffer<RuleBlobReferenceElement>();
            commands.AddBuffer<WeightedEntityElement>();
            commands.AddComponent(new IntGridData
            {
                Hash = runtimeHash,
                DebugName = intGrid.name,
                DualGrid = intGrid.useDualGrid
            });
            commands.SetComponentEnabled<IntGridData>(false);
            commands.AddBuffer<RefreshPositionElement>();
            commands.AddBuffer<IntGridValueElement>();
            commands.AddBuffer<IntGridInitialValueElement>();

            // Acquire buffers only after all structural commands, which can invalidate DynamicBuffer handles.
            var ruleBlobBuffer = commands.SetBuffer<RuleBlobReferenceElement>();
            var weightedEntityBuffer = commands.SetBuffer<WeightedEntityElement>();
            var refreshPositionsBuffer = commands.SetBuffer<RefreshPositionElement>();
            var intGridValues = commands.SetBuffer<IntGridValueElement>();
            var initialValuesBuffer = commands.SetBuffer<IntGridInitialValueElement>();

            var refreshPositions = new NativeHashSet<int2>(64, Allocator.Temp);

            var entityCount = 0;
            foreach (var group in intGrid.ruleGroups)
            {
                foreach (var rule in group.rules)
                {
                    var blob = RuleBlobCreator.Create(rule, entityCount, refreshPositions);
                    commands.AddBlobAsset(ref blob, out _);

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

            foreach (var initialValue in initialValues)
            {
                if (initialValue.Value == 0) continue;

                initialValuesBuffer.Add(new IntGridInitialValueElement
                {
                    Position = new int2(initialValue.Position.x, initialValue.Position.y),
                    Value = initialValue.Value,
                });
            }
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
