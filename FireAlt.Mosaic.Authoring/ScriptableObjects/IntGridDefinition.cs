using System;
using System.Collections.Generic;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;
using FireAlt.Core.Editor;

namespace FireAlt.Mosaic.Authoring
{
    [CreateAssetMenu(menuName = "Mosaic/IntGrid")]
    public class IntGridDefinition : ScriptableObject
    {
        [field: SerializeField, HideInInspector] public Hash128 Hash { get; private set; }

        public bool useDualGrid;
        public List<IntGridValueDefinition> intGridValues = new();
        public List<RuleGroup> ruleGroups = new();

        public readonly Dictionary<int, IntGridValueDefinition> IntGridValuesDict = new();

        private void OnEnable()
        {
            ValidateDict();
        }
        
        private void OnValidate()
        {
            Hash = AssetDatabaseUtils.ToGuidHash(this);
            foreach (var intGridValue in intGridValues)
            {
                ValidateIntGridValue(intGridValue);
            }

            foreach (var ruleGroup in ruleGroups)
            {
                ruleGroup.intGrid = this;
            }
            
            ValidateDict();
        }

        private void ValidateIntGridValue(IntGridValueDefinition intGridValue)
        {
            while (intGridValue.value <= 0)
            {
                short expected = 1;

                var sorted = new List<IntGridValueDefinition>(intGridValues);
                sorted.Sort();
                    
                foreach (var val in sorted)
                {
                    if (val.value <= 0) continue;
                    
                    if (val.value == expected)
                    {
                        expected++;
                    }
                    else
                    {
                        intGridValue.value = expected;
                        return;
                    }
                }
            }
        }

        private void ValidateDict()
        {
            foreach (var intGridValue in intGridValues)
            {
                IntGridValuesDict[intGridValue.value] = intGridValue;
            }
        }
    }
    
    [Serializable]
    public class IntGridValueDefinition : IComparable<IntGridValueDefinition>
    {
        public short value = -1;
        public string name;
        [ColorUsage(false)]
        public Color color;
        public Texture texture;

        public int CompareTo(IntGridValueDefinition other)
        {
            return value.CompareTo(other.value);
        }
    }
}
