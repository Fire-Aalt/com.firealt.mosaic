using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using UnityEditor;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingPreviewInvalidation
    {
        private readonly HashSet<EntityId> _trackedHierarchy = new();
        private readonly List<GameObject> _roots = new();

        internal void Reset(IReadOnlyList<MosaicPaintingTarget> targets)
        {
            _trackedHierarchy.Clear();
            _roots.Clear();

            foreach (var target in targets)
            {
                if (target.IsEntityTarget) continue;

                var root = target.Grid != null ? target.Grid.gameObject : target.Owner?.gameObject;
                if (root == null || _roots.Contains(root)) continue;
                _roots.Add(root);

                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    _trackedHierarchy.Add(transform.gameObject.GetEntityId());
                }

                for (var parent = root.transform.parent; parent != null; parent = parent.parent)
                {
                    _trackedHierarchy.Add(parent.gameObject.GetEntityId());
                }
            }
        }

        internal bool IsRelevant(EntityId entityId)
        {
            return _trackedHierarchy.Contains(entityId) || IsRelevant(EditorUtility.EntityIdToObject(entityId));
        }

        internal bool IsRelevant(Object value)
        {
            var gameObject = value switch
            {
                GameObject go => go,
                Component component => component.gameObject,
                _ => null,
            };
            if (gameObject == null) return false;

            foreach (var root in _roots)
            {
                if (gameObject == root || gameObject.transform.IsChildOf(root.transform)
                                       || root.transform.IsChildOf(gameObject.transform))
                {
                    return true;
                }
            }

            return gameObject.GetComponentInChildren<GridAuthoring>(true) != null
                   || gameObject.GetComponentInChildren<TilemapAuthoring>(true) != null
                   || gameObject.GetComponentInChildren<TilemapTerrainAuthoring>(true) != null;
        }
    }
}