using Unity.Entities;
using UnityEngine.Rendering;

namespace FireAlt.Mosaic.Data
{
    public struct MosaicRendererInitialized : IComponentData, IEnableableComponent
    {
    }

    public struct MosaicRendererCleanup : ICleanupComponentData
    {
        public Hash128 MeshHash;
        public BatchMeshID MeshID;
        public BatchMaterialID MaterialID;
        public bool IsTerrain;
    }
}
