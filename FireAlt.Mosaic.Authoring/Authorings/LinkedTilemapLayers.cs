using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace FireAlt.Mosaic.Authoring
{
    public class LinkedTilemapLayers : MonoBehaviour
    {
        [Tooltip("Hide the individual IntGrid value entries for Tilemaps referenced by this component " +
                 "from the Mosaic Painting palette. Linked layer entries remain available.")]
        public bool hideRawTargetValues;

        public List<LinkedLayer> layers = new();

        private void OnValidate()
        {
            foreach (var layer in layers)
            {
                foreach (var operation in layer.Operations)
                {
                    operation.valueToSet = (short)math.max(0, operation.valueToSet);
                }
            }
        }
    }

    [Serializable]
    public class LinkedLayer
    {
        public string name;
        [ColorUsage(false)]
        public Color color = Color.white;
        public Texture icon;

        [Tooltip("The first operation's Tilemap is used as the Scene View painting anchor.")]
        public List<LayerOperation> Operations = new();
    }

    [Serializable]
    public class LayerOperation
    {
        public TilemapAuthoring target;
        public short valueToSet;
    }
}
