using System.Collections.Generic;
using UnityEngine;

namespace FireAlt.Mosaic.Data
{
    public struct MeshDataArrayWrapper
    {
        public Mesh.MeshDataArray Array;
        
        public bool IsCreated { private set; get; }
        
        public void AllocateWritableMeshData(int count)
        {
            if (IsCreated) Array.Dispose();
            Array = Mesh.AllocateWritableMeshData(count);
            IsCreated = true;
        }

        public void ApplyAndDisposeWritableMeshData(List<Mesh> meshesToUpdate)
        {
            Mesh.ApplyAndDisposeWritableMeshData(Array, meshesToUpdate);
            IsCreated = false;
        }
    }
}