using System;

namespace FireAlt.Mosaic.Data
{
    [Serializable]
    public struct IntGridValue : IEquatable<IntGridValue>
    {
        public short value;
     
        public IntGridValue(int value)
        {
            this.value = (short)value;
        }
        
        public static implicit operator IntGridValue(int value) => new(value);
        public static implicit operator short(IntGridValue value) => value.value;

        public bool Equals(IntGridValue other)
        {
            return value == other.value;
        }

        public override bool Equals(object obj)
        {
            return obj is IntGridValue other && Equals(other);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }
    }
}