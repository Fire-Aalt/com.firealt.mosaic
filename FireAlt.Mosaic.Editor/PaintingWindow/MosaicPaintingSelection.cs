using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal readonly struct MosaicPaintingOperation
    {
        public MosaicPaintingOperation(MosaicPaintingTarget target, short value)
        {
            Target = target;
            Value = value;
        }

        public MosaicPaintingTarget Target { get; }

        public short Value { get; }
    }

    internal sealed class MosaicPaintingSelection
    {
        private readonly List<MosaicPaintingOperation> _operations;
        private readonly LinkedTilemapLayers _linkedOwner;
        private readonly int _linkedLayerIndex;
        private readonly StageHandle _stage;

        private MosaicPaintingSelection(string id, string name, Color color, Texture icon,
            List<MosaicPaintingOperation> operations, StageHandle stage, string validationMessage,
            LinkedTilemapLayers linkedOwner = null, int linkedLayerIndex = -1)
        {
            Id = id;
            Name = name;
            Color = color;
            Icon = icon;
            _operations = operations;
            _stage = stage;
            ValidationMessage = validationMessage;
            _linkedOwner = linkedOwner;
            _linkedLayerIndex = linkedLayerIndex;
        }

        public string Id { get; }

        public string Name { get; }

        public Color Color { get; }

        public Texture Icon { get; }

        public string ValidationMessage { get; }

        public MosaicPaintingTarget Anchor => _operations.Count == 0 ? null : _operations[0].Target;

        public short PrimaryValue => _operations.Count == 0 ? (short)0 : _operations[0].Value;

        public bool IsValid => string.IsNullOrEmpty(ValidationMessage) && ValidateCurrentConfiguration() == null;

        internal IReadOnlyList<MosaicPaintingOperation> Operations => _operations;

        internal static MosaicPaintingSelection Create(MosaicPaintingTarget target, IntGridValueDefinition value,
            StageHandle stage)
        {
            var name = string.IsNullOrWhiteSpace(value.name) ? value.value.ToString() : value.name;
            var operations = new List<MosaicPaintingOperation> { new(target, value.value) };
            var validation = ValidateTarget(target, value.value, stage, 0);
            return new MosaicPaintingSelection($"{target.Id}:{value.value}", name, value.color, value.texture,
                operations, stage, validation);
        }

        internal static MosaicPaintingSelection Create(LinkedTilemapLayers owner, int layerIndex,
            IReadOnlyDictionary<TilemapAuthoring, MosaicPaintingTarget> targets, StageHandle stage)
        {
            var id = $"{GlobalObjectId.GetGlobalObjectIdSlow(owner)}:{layerIndex}";
            var layer = owner?.layers != null && layerIndex >= 0 && layerIndex < owner.layers.Count
                ? owner.layers[layerIndex]
                : null;
            var name = layer == null || string.IsNullOrWhiteSpace(layer.name) ? $"Layer {layerIndex + 1}" : layer.name;
            var color = layer?.color ?? Color.white;
            var icon = layer?.icon;
            var operations = new List<MosaicPaintingOperation>();
            var validation = BuildLinkedOperations(owner, layer, targets, stage, operations);
            return new MosaicPaintingSelection(id, name, color, icon, operations, stage, validation, owner, layerIndex);
        }

        internal bool TryBeginStroke(bool erase, out MosaicPaintingSelectionStroke stroke)
        {
            stroke = null;
            if (!IsValid) return false;

            stroke = new MosaicPaintingSelectionStroke(_operations, erase);
            return true;
        }

        private string ValidateCurrentConfiguration()
        {
            if (!_stage.Equals(StageUtility.GetCurrentStageHandle())) return "The current stage changed.";

            if (_linkedOwner == null)
            {
                return _operations.Count == 1
                    ? ValidateTarget(_operations[0].Target, _operations[0].Value, _stage, 0)
                    : "The painting selection has no operation.";
            }

            if (!MosaicPaintingPreviewService.BelongsToStage(_linkedOwner, _stage))
            {
                return "The linked-layer component is not loaded in the current stage.";
            }

            if (_linkedOwner.layers == null || _linkedLayerIndex < 0 || _linkedLayerIndex >= _linkedOwner.layers.Count)
            {
                return "The linked layer no longer exists.";
            }

            var layer = _linkedOwner.layers[_linkedLayerIndex];
            if (layer?.Operations == null || layer.Operations.Count != _operations.Count)
            {
                return "The linked-layer configuration changed.";
            }

            var uniqueTargets = new HashSet<TilemapAuthoring>();
            for (var i = 0; i < _operations.Count; i++)
            {
                var operation = layer.Operations[i];
                if (operation == null || operation.target != _operations[i].Target.Owner
                                      || operation.valueToSet != _operations[i].Value)
                {
                    return "The linked-layer configuration changed.";
                }

                if (!uniqueTargets.Add(operation.target)) return $"Operation {i + 1} repeats a target.";
                var validation = ValidateTarget(_operations[i].Target, _operations[i].Value, _stage, i);
                if (validation != null) return validation;
            }

            return null;
        }

        private static string BuildLinkedOperations(LinkedTilemapLayers owner, LinkedLayer layer,
            IReadOnlyDictionary<TilemapAuthoring, MosaicPaintingTarget> targets, StageHandle stage,
            List<MosaicPaintingOperation> operations)
        {
            if (owner == null || !MosaicPaintingPreviewService.BelongsToStage(owner, stage))
            {
                return "The linked-layer component is not loaded in the current stage.";
            }

            if (layer?.Operations == null || layer.Operations.Count == 0)
            {
                return "At least one operation is required.";
            }

            var uniqueTargets = new HashSet<TilemapAuthoring>();
            for (var i = 0; i < layer.Operations.Count; i++)
            {
                var operation = layer.Operations[i];
                if (operation?.target == null) return $"Operation {i + 1} has no target.";
                if (!uniqueTargets.Add(operation.target)) return $"Operation {i + 1} repeats a target.";
                if (!targets.TryGetValue(operation.target, out var target))
                {
                    return $"Operation {i + 1} target must be loaded and paintable in the current stage.";
                }

                var value = operation.valueToSet;
                var validation = ValidateTarget(target, value, stage, i);
                if (validation != null) return validation;
                operations.Add(new MosaicPaintingOperation(target, value));
            }

            return null;
        }

        private static string ValidateTarget(MosaicPaintingTarget target, short value, StageHandle stage,
            int operationIndex)
        {
            if (target?.Owner is not TilemapAuthoring tilemap || !target.IsPaintable
                || !MosaicPaintingPreviewService.BelongsToStage(tilemap, stage))
            {
                return $"Operation {operationIndex + 1} target must be loaded and paintable in the current stage.";
            }

            if (value < 0) return $"Operation {operationIndex + 1} value cannot be negative.";
            if (value == 0) return null;

            foreach (var definition in tilemap.intGrid.intGridValues)
            {
                if (definition.value == value) return null;
            }

            return $"Operation {operationIndex + 1} value {value} is not declared by the target IntGridDefinition.";
        }
    }

    internal sealed class MosaicPaintingSelectionStroke : IDisposable
    {
        private readonly IReadOnlyList<MosaicPaintingOperation> _operations;
        private readonly MosaicPaintingTarget.PaintStroke[] _strokes;
        private readonly short[] _values;

        public MosaicPaintingSelectionStroke(IReadOnlyList<MosaicPaintingOperation> operations, bool erase)
        {
            _operations = operations;
            _strokes = new MosaicPaintingTarget.PaintStroke[operations.Count];
            _values = new short[operations.Count];
            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var value = erase ? (short)0 : operation.Value;
                _values[i] = value;
                _strokes[i] = operation.Target.BeginStroke(value);
            }
        }

        public bool SetCells(IReadOnlyCollection<Vector2Int> positions)
        {
            var changed = false;
            for (var i = 0; i < _strokes.Length; i++)
            {
                if (!_strokes[i].SetCells(positions)) continue;
                changed = true;
                MosaicPaintingSession.NotifyCellsChanged(_operations[i].Target, positions, _values[i]);
            }

            return changed;
        }

        public void Dispose()
        {
            foreach (var stroke in _strokes) stroke.Dispose();
        }
    }
}
