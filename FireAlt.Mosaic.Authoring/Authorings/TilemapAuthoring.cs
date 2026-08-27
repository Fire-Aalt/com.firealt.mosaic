using System;
using System.Collections.Generic;
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

        [SerializeField, HideInInspector] private SerializedIntGridData _paintedData = new();

        public IReadOnlyList<SerializedIntGridCell> PaintedCells => _paintedData.Cells;

        internal SerializedIntGridData PaintedData => _paintedData;

        public class Baker : Baker<TilemapAuthoring>
        {
            public override void Bake(TilemapAuthoring authoring)
            {
                BakerUtils.RegisterDependencies(this, authoring.intGrid);
                if (authoring.intGrid == null) return;
                var includeRules = BakerUtils.TryValidateRuleResults(authoring.intGrid, out var validationError);
                if (!includeRules)
                {
                    BakerUtils.LogBakingError(authoring, validationError);
                }

                var gridAuthoring = GetComponentInParent<GridAuthoring>();
                if (gridAuthoring == null)
                {
                    throw new Exception("GridAuthoring not found");
                }
                
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                var gridEntity = GetEntity(gridAuthoring, TransformUsageFlags.None);
                var tilePivot = float2.zero;
                var tileSize = float2.zero;
                var refSprite = new RefSprite();
                var runtimeHash = BakerUtils.GetHash(authoring.intGrid, authoring.isGlobal);
                
                BakerUtils.AddTilemapTransform(this, entity, gridEntity, authoring.renderingData);
                if (includeRules)
                {
                    BakerUtils.AddIntGridLayerData(this, entity, authoring.intGrid, runtimeHash, refSprite, false,
                        ref tilePivot, ref tileSize, authoring._paintedData.Rectangles, go => GetEntity(go, TransformUsageFlags.None));
                }
                else
                {
                    BakerUtils.AddEmptyIntGridLayerData(this, entity, authoring.intGrid, runtimeHash, authoring._paintedData.Rectangles);
                }

                BakerUtils.AddRenderingData(this, entity, authoring.gameObject, runtimeHash, authoring.renderingData, refSprite);
            }
        }
    }
}
