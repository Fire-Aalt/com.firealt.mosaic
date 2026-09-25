using System;
using FireAlt.Mosaic.Data;
using Unity.Entities;
using UnityEngine;

namespace FireAlt.Mosaic.Authoring
{
    [Serializable]
    public class IntGridMatrix
    {
        public IntGridValue[] singleGridMatrix;
        public IntGridValue[] dualGridMatrix;
        
        public IntGridValue[] GetCurrentMatrix(IntGridDefinition intGrid) => intGrid.useDualGrid ? dualGridMatrix : singleGridMatrix;
        public IntGridValue[] GetCurrentMatrix(IBaker baker, IntGridDefinition intGrid)
        {
            baker.DependsOn(intGrid);
            return GetCurrentMatrix(intGrid);
        }

        public int GetCurrentSize(IntGridDefinition intGrid) => intGrid.useDualGrid ? (int)Mathf.Sqrt(dualGridMatrix.Length) : (int)Mathf.Sqrt(singleGridMatrix.Length);
        
        public IntGridMatrix(int singleGridSize)
        {
            var dualGridSize = singleGridSize + 1;
            
            singleGridMatrix = new IntGridValue[singleGridSize * singleGridSize];
            dualGridMatrix = new IntGridValue[dualGridSize * dualGridSize];
        }
    }
}
