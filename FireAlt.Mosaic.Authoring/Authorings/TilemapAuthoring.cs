using System;
using System.Collections.Generic;
using FireAlt.Core.EntityCommands;
using FireAlt.Mosaic.Data;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Authoring
{
    public class TilemapAuthoring : MonoBehaviour
    {
        public IntGridDefinition intGrid;
        [Tooltip("True: to access the runtime IntGrid of this tilemap, the IntGridDefinition can be used directly.\n\n" +
                 "False: the hash of the runtime IntGrid is unique per each instance and the entity needs to be queried to get the runtime hash.")]
        public bool isGlobal = true;
        public RenderingData renderingData = new();

        [SerializeField, HideInInspector] private List<SerializedIntGridCell> _paintedCells = new();

        public IReadOnlyList<SerializedIntGridCell> PaintedCells => _paintedCells;

        internal List<SerializedIntGridCell> MutablePaintedCells => _paintedCells;

        private void Bake<TCommands>(ref TCommands commands, Entity gridEntity,
            Func<GameObject, Entity> entityResolver)
            where TCommands : IEntityCommands
        {
            if (intGrid == null)
            {
                throw new Exception("IntGridDefinition is null");
            }

            var tilePivot = float2.zero;
            var tileSize = float2.zero;
            var refSprite = new RefSprite();
            var runtimeHash = BakerUtils.GetHash(this, intGrid, isGlobal, 0);

            BakerUtils.AddTilemapTransform(ref commands, gridEntity, renderingData);
            BakerUtils.AddIntGridLayerData(ref commands, intGrid, runtimeHash, refSprite, false,
                ref tilePivot, ref tileSize, _paintedCells, entityResolver);
            BakerUtils.AddRenderingData(ref commands, gameObject, runtimeHash, renderingData, refSprite);
        }

        public class Baker : Baker<TilemapAuthoring>
        {
            public override void Bake(TilemapAuthoring authoring)
            {
                var gridAuthoring = GetComponentInParent<GridAuthoring>();
                if (gridAuthoring == null)
                {
                    throw new Exception("GridAuthoring not found");
                }
                
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                RegisterDependencies(authoring);

                var commands = new BakerCommands(this, entity);
                authoring.Bake(ref commands, GetEntity(gridAuthoring, TransformUsageFlags.None),
                    go => GetEntity(go, TransformUsageFlags.None));
            }

            private void RegisterDependencies(TilemapAuthoring authoring)
            {
                DependsOn(authoring.intGrid);
                if (authoring.intGrid == null) return;

                foreach (var group in authoring.intGrid.ruleGroups)
                {
                    DependsOn(group);
                }
            }
        }
    }
}
