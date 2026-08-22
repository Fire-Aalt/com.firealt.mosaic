using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Data;
using FireAlt.Mosaic.Editor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Authoring;
using Unity.Rendering;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FireAlt.Mosaic.Pipeline.Editor
{
    internal static class MosaicPipelineCommands
    {
        private const int MAX_CELL_RESULTS = 4096;
        private const int MAX_PAINT_CELLS = 4096;

        [CliCommand("mosaic_targets",
            "Discover ready Mosaic tilemap and terrain IntGrids in the current stage, including "
            + "closed-SubScene read-only targets.",
            Tags = new[] { "authoring/mosaic" })]
        private static object GetTargets(
            [CliArg("include_values", "Include each target's configured IntGrid values.")] bool includeValues = false)
        {
            var targets = DiscoverTargets(out var authoringCandidates, out var pendingAuthoring);
            var result = new List<object>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                result.Add(CreateTargetSummary(targets[i], i, includeValues));
            }

            var writable = 0;
            foreach (var target in targets)
            {
                if (target.IsPaintable) writable++;
            }

            return new
            {
                stage = StageUtility.GetCurrentStageHandle().ToString(),
                authoringCandidates,
                pendingAuthoring,
                targetCount = targets.Count,
                writableCount = writable,
                readOnlyCount = targets.Count - writable,
                targets = result,
            };
        }

        [CliCommand("mosaic_get_target",
            "Get values, cells, entity bindings, and render bounds for one ready Mosaic target. "
            + "Use mosaic_targets to find selectors.",
            Tags = new[] { "authoring/mosaic" })]
        private static object GetTarget(
            [CliArg("target", "Target index, exact display name, unique name fragment, or current IntGrid hash.",
                Required = true)] string selector,
            [CliArg("include_cells", "Include serialized or runtime cells up to cell_limit.")]
            bool includeCells = false,
            [CliArg("cell_limit", "Maximum returned cells when include_cells=true.")] int cellLimit = 256)
        {
            var targets = DiscoverTargets(out _, out _);
            var target = ResolveTarget(targets, selector, out var index);
            cellLimit = math.clamp(cellLimit, 0, MAX_CELL_RESULTS);

            var values = CreateValues(target);
            var cells = includeCells ? CreateCells(target, cellLimit) : null;
            return new
            {
                target = CreateTargetSummary(target, index, false),
                values,
                cellCount = target.Cells.Count,
                cellsTruncated = includeCells && target.Cells.Count > cellLimit,
                cells,
                renderBounds = CreateRenderBounds(target),
            };
        }

        [CliCommand("mosaic_paint",
            "Paint or erase authoring cells on one writable Mosaic target. Requires confirm=true; "
            + "use dry_run=true to validate. Closed-SubScene targets are read-only.",
            Tags = new[] { "authoring/mosaic" })]
        private static object Paint(
            [CliArg("target", "Target index, exact display name, unique name fragment, or current IntGrid hash.",
                Required = true)] string selector,
            [CliArg("cells", "Semicolon-separated cell coordinates, for example '0,0;1,0;1,1'.",
                Required = true)] string cells,
            [CliArg("value", "IntGrid value to paint. Use 0 to erase.")] int value = 0,
            [CliArg("confirm", "Apply the edit. Without it the command is refused.")] bool confirm = false,
            [CliArg("dry_run", "Validate and preview the edit without changing authoring.")] bool dryRun = false)
        {
            var targets = DiscoverTargets(out _, out _);
            var target = ResolveTarget(targets, selector, out var index);
            if (!target.IsPaintable)
            {
                throw new ArgumentException($"Target '{target.DisplayName}' is read-only or invalid.");
            }

            if (value < short.MinValue || value > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Value must fit in an Int16.");
            }

            var intGridValue = (short)value;
            if (intGridValue != 0 && !target.TryGetValueDefinition(intGridValue, out _))
            {
                throw new ArgumentException($"Value {value} is not defined by '{target.DisplayName}'.");
            }

            var positions = ParseCells(cells);
            if (dryRun)
            {
                return new
                {
                    status = "dry_run",
                    target = CreateTargetSummary(target, index, false),
                    value = intGridValue,
                    cellCount = positions.Count,
                    cells = CreateCellCoordinates(positions),
                };
            }

            if (!confirm)
            {
                throw new ArgumentException(
                    "Refusing to paint Mosaic cells. Pass confirm=true to apply, or dry_run=true to preview.");
            }

            bool changed;
            var undoName = intGridValue == 0 ? "Erase Mosaic IntGrid" : "Paint Mosaic IntGrid";
            using (new AuthoringUndoScope(undoName))
            {
                using (var stroke = target.BeginStroke(intGridValue))
                {
                    changed = stroke.SetCells(positions);
                }
            }

            if (changed)
            {
                MosaicPaintingController.NotifyCellsChanged(target, positions, intGridValue);
                MosaicPaintingController.NotifyChanged();
            }

            return new
            {
                status = changed ? "changed" : "unchanged",
                target = CreateTargetSummary(target, index, false),
                value = intGridValue,
                cellCount = positions.Count,
            };
        }

        private static List<MosaicPaintingTarget> DiscoverTargets(out int authoringCandidates,
            out bool pendingAuthoring)
        {
            var stage = StageUtility.GetCurrentStageHandle();
            var candidates = MosaicPaintingCatalog.DiscoverAuthoringCandidates(stage);
            var targets = new List<MosaicPaintingTarget>();
            pendingAuthoring = MosaicPaintingCatalog.DiscoverTargets(targets, stage, candidates);
            authoringCandidates = candidates.Count;
            return targets;
        }

        private static MosaicPaintingTarget ResolveTarget(IReadOnlyList<MosaicPaintingTarget> targets,
            string selector, out int index)
        {
            if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("Target is required.");
            if (int.TryParse(selector, out index))
            {
                if (index >= 0 && index < targets.Count) return targets[index];
                throw new ArgumentOutOfRangeException(nameof(selector),
                    $"Target index must be between 0 and {targets.Count - 1}.");
            }

            var matches = new List<int>();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (string.Equals(target.DisplayName, selector, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target.IntGridHash.ToString(), selector, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return target;
                }

                if (target.DisplayName.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(i);
            }

            if (matches.Count == 1)
            {
                index = matches[0];
                return targets[index];
            }

            index = -1;
            if (matches.Count == 0) throw new ArgumentException($"No Mosaic target matches '{selector}'.");
            throw new ArgumentException(
                $"Target '{selector}' is ambiguous. Use an index, exact name, or IntGrid hash.");
        }

        private static List<Vector2Int> ParseCells(string cells)
        {
            if (string.IsNullOrWhiteSpace(cells)) throw new ArgumentException("At least one cell is required.");

            var positions = new List<Vector2Int>();
            var unique = new HashSet<Vector2Int>();
            foreach (var cell in cells.Split(';'))
            {
                var coordinates = cell.Trim().Split(',');
                if (coordinates.Length != 2 || !int.TryParse(coordinates[0].Trim(), out var x)
                                            || !int.TryParse(coordinates[1].Trim(), out var y))
                {
                    throw new ArgumentException($"Invalid cell '{cell}'. Expected x,y; for example '0,0;1,0'.");
                }

                var position = new Vector2Int(x, y);
                if (unique.Add(position)) positions.Add(position);
                if (positions.Count > MAX_PAINT_CELLS)
                {
                    throw new ArgumentException($"A single command may paint at most {MAX_PAINT_CELLS} cells.");
                }
            }

            return positions;
        }

        private static object CreateTargetSummary(MosaicPaintingTarget target, int index, bool includeValues)
        {
            var binding = target.RuntimeBinding;
            var entityManager = binding.World.EntityManager;
            var sceneGuid = entityManager.HasComponent<SceneSection>(binding.RendererEntity)
                ? entityManager.GetSharedComponent<SceneSection>(binding.RendererEntity).SceneGUID.ToString()
                : null;
            return new
            {
                index,
                name = target.DisplayName,
                kind = target.IsTerrain ? "terrain" : "tilemap",
                layerIndex = target.LayerIndex,
                access = target.IsPaintable ? "writable" : "read_only",
                valid = target.IsValid,
                validation = target.AdditionalValidationMessage,
                authoringScene = target.Owner == null ? null : target.Owner.gameObject.scene.path,
                sceneGuid,
                intGridHash = target.IntGridHash.ToString(),
                rendererHash = target.RendererHash.ToString(),
                intGridEntity = binding.IntGridEntity.ToString(),
                rendererEntity = binding.RendererEntity.ToString(),
                cellCount = target.Cells.Count,
                values = includeValues ? CreateValues(target) : null,
            };
        }

        private static List<object> CreateValues(MosaicPaintingTarget target)
        {
            var values = new List<object>(target.Values.Count);
            foreach (var value in target.Values)
            {
                values.Add(new
                {
                    value = value.value,
                    name = value.name,
                    color = $"#{ColorUtility.ToHtmlStringRGBA(value.color)}",
                    texture = value.texture == null ? null : AssetDatabase.GetAssetPath(value.texture),
                });
            }

            return values;
        }

        private static List<object> CreateCells(MosaicPaintingTarget target, int limit)
        {
            var count = math.min(target.Cells.Count, limit);
            var cells = new List<object>(count);
            for (var i = 0; i < count; i++)
            {
                var cell = target.Cells[i];
                cells.Add(new { x = cell.Position.x, y = cell.Position.y, value = cell.Value });
            }

            return cells;
        }

        private static List<object> CreateCellCoordinates(IReadOnlyList<Vector2Int> positions)
        {
            var cells = new List<object>(positions.Count);
            foreach (var position in positions) cells.Add(new { x = position.x, y = position.y });
            return cells;
        }

        private static List<object> CreateRenderBounds(MosaicPaintingTarget target)
        {
            var result = new List<object>();
            var binding = target.RuntimeBinding;
            var entityManager = binding.World.EntityManager;
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData, RenderBounds, LocalToWorld>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in query.ToEntityArray(Allocator.Temp))
            {
                if (entityManager.GetComponentData<TilemapRendererData>(entity).MeshHash != target.RendererHash)
                {
                    continue;
                }

                var local = entityManager.GetComponentData<RenderBounds>(entity).Value;
                var localToWorld = entityManager.GetComponentData<LocalToWorld>(entity).Value;
                var matrix = new float3x3(localToWorld.c0.xyz, localToWorld.c1.xyz, localToWorld.c2.xyz);
                var center = math.transform(localToWorld, local.Center);
                var absoluteMatrix = new float3x3(
                    math.abs(matrix.c0), math.abs(matrix.c1), math.abs(matrix.c2));
                var extents = math.mul(absoluteMatrix, local.Extents);
                result.Add(new
                {
                    entity = entity.ToString(),
                    center = new[] { center.x, center.y, center.z },
                    extents = new[] { extents.x, extents.y, extents.z },
                });
            }

            query.Dispose();
            return result;
        }
    }
}
