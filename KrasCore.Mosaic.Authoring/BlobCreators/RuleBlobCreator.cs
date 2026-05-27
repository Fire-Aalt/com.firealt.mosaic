using System;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using FireAlt.Core;

namespace FireAlt.Mosaic.Authoring
{
    public struct RuleBlobCreator
    {
        public static BlobAssetReference<RuleBlob> Create(RuleGroup.Rule rule, int entityCount, NativeHashSet<int2> refreshPositions)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<RuleBlob>();

            root.Chance = rule.ruleChance;
            root.RuleTransform = rule.ruleTransformation;
            root.ResultTransform = rule.resultTransformation;
            
            AddPatterns(ref builder, ref root, rule, refreshPositions);
            AddResults(ref builder, ref root, rule, entityCount);

            return builder.CreateBlobAssetReference<RuleBlob>(Allocator.Persistent);
        }

        private static void AddPatterns(ref BlobBuilder builder, ref RuleBlob root, RuleGroup.Rule rule, NativeHashSet<int2> refreshPositions)
        {
            var cells = new NativeList<RuleCell>(Allocator.Temp);
            
            AddMirrorPattern(true, rule, cells, refreshPositions, default);
            root.CellsToCheckCount = cells.Length;
            
            AddMirrorPattern(rule.ruleTransformation.IsMirroredX(), rule, cells, refreshPositions, new bool2(true, false));
            AddMirrorPattern(rule.ruleTransformation.IsMirroredY(), rule, cells, refreshPositions, new bool2(false, true));
            AddMirrorPattern(rule.ruleTransformation.IsMirroredX() && rule.ruleTransformation.IsMirroredY(), rule, cells, refreshPositions, new bool2(true, true));
            if (rule.ruleTransformation.HasFlagBurst(Transformation.Rotated)) AddRotatedPattern(rule, cells, refreshPositions);

            builder.Construct(ref root.Cells, cells);
        }

        private static void AddResults(ref BlobBuilder builder, ref RuleBlob root, RuleGroup.Rule rule, int entityCount)
        {
            if (rule.TileSprites != null)
            {
                var spritesWeights = builder.Allocate(ref root.SpritesWeights, rule.TileSprites.Count);
                var spriteMeshes = builder.Allocate(ref root.SpriteMeshes, rule.TileSprites.Count);
                
                var sum = 0;
                for (int i = 0; i < spriteMeshes.Length; i++)
                {
                    spritesWeights[i] = rule.TileSprites[i].weight;
                    spriteMeshes[i] = new SpriteMesh(rule.TileSprites[i].result);
                    sum += spritesWeights[i];
                }
                root.SpritesWeightSum = sum;
            }
            
            if (rule.TileEntities != null)
            {
                var entitiesWeights = builder.Allocate(ref root.EntitiesWeights, rule.TileEntities.Count);
                var entitiesPointers = builder.Allocate(ref root.EntitiesPointers, rule.TileEntities.Count);

                var sum = 0;
                for (int i = 0; i < entitiesPointers.Length; i++)
                {
                    entitiesWeights[i] = rule.TileEntities[i].weight;
                    entitiesPointers[i] = entityCount + i;
                    sum += entitiesWeights[i];
                }
                root.EntitiesWeightSum = sum;
            }
        }
        
        private static void AddMirrorPattern(bool addToRuleCells, RuleGroup.Rule rule, NativeList<RuleCell> cells,
            NativeHashSet<int2> refreshPositions, bool2 mirror)
        {
            var matrix = rule.ruleMatrix.GetCurrentMatrix(rule.BoundIntGridDefinition);
            for (var index = 0; index < matrix.Length; index++)
            {
                ApplyTransformation(matrix, addToRuleCells, cells, refreshPositions, index, mirror, rule.GetOffsetFromCenterMirrored);
            }
        }
        
        private static void AddRotatedPattern(RuleGroup.Rule rule, NativeList<RuleCell> cells,
            NativeHashSet<int2> refreshPositions)
        {
            var matrix = rule.ruleMatrix.GetCurrentMatrix(rule.BoundIntGridDefinition);
            for (int rotation = 1; rotation < 4; rotation++)
            {
                for (var index = 0; index < matrix.Length; index++)
                {
                    ApplyTransformation(matrix, true, cells, refreshPositions, index, rotation, rule.GetOffsetFromCenterRotated);
                }
            }
        }
        
        private static void ApplyTransformation<TParam>(IntGridValue[] matrix, bool addToRuleCells,
            NativeList<RuleCell> cells, NativeHashSet<int2> refreshPositions,
            int index, TParam param, Func<int, TParam, int2> transformationMethod)
        {
            var intGridValue = matrix[index];
            if (intGridValue == 0) return;

            var pos = transformationMethod.Invoke(index, param);
            
            refreshPositions.Add(pos);
            if (addToRuleCells)
            {
                cells.Add(new RuleCell
                {
                    IntGridValue = intGridValue,
                    Offset = pos
                });
            }
        }
    }
}