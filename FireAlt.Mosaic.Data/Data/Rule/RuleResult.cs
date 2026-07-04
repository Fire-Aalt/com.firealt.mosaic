using System;
using UnityEngine;

namespace FireAlt.Mosaic
{
    [Serializable]
    public class PrefabResult
    {
        public int weight;
        public GameObject result;
        
        public PrefabResult()
        {
            weight = 1;
        }
        
        public PrefabResult(GameObject result)
        {
            weight = 1;
            this.result = result;
        }

        public void Validate()
        {
            weight = Mathf.Max(1, weight);
        }
    }
    
    [Serializable]
    public class SpriteResult
    {
        public int weight;
        public Sprite result;

        public SpriteResult()
        {
            weight = 1;
        }
        
        public SpriteResult(Sprite result)
        {
            weight = 1;
            this.result = result;
        }

        public void Validate()
        {
            weight = Mathf.Max(1, weight);
        }
    }
}
