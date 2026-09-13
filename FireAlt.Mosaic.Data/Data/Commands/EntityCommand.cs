using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct EntityCommand
    {
        public Entity SrcEntity;
        public int2 Position;
        public Hash128 IntGridHash;
        public Entity IntGridEntity;
        public uint RuleVersion;
        public byte Face;
        public bool2 MatchedMirror;
        public int MatchedRotation;
    }
    
    public struct DeferredCommandComparer : IComparer<EntityCommand>
    {
        public int Compare(EntityCommand x, EntityCommand y)
        {
            return x.SrcEntity.Index.CompareTo(y.SrcEntity.Index);
        }
    }
}
