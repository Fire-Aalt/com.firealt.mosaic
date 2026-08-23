using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace FireAlt.Mosaic.Authoring
{
    [BurstCompile]
    internal static class IntGridCellPacking
    {
        private const byte FORMAT_VERSION = 1;
        private const int MAX_VARINT_BYTES = 5;

        public static void Pack(IReadOnlyList<SerializedIntGridCell> cells,
            List<SerializedIntGridRectangle> rectangles)
        {
            PackAndEncode(cells, rectangles);
        }

        public static byte[] PackAndEncode(IReadOnlyList<SerializedIntGridCell> cells,
            List<SerializedIntGridRectangle> rectangles)
        {
            rectangles.Clear();
            if (cells.Count == 0) return Array.Empty<byte>();

            var nativeCells = new NativeArray<NativeCell>(cells.Count, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var nativeRectangles = new NativeList<NativeRectangle>(Allocator.TempJob);
            var bytes = new NativeList<byte>(Allocator.TempJob);
            var values = new NativeParallelHashMap<int2, short>(cells.Count, Allocator.TempJob);
            var result = new NativeReference<int>(Allocator.TempJob);
            try
            {
                result.Value = 0;
                for (var i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];
                    nativeCells[i] = new NativeCell(cell.Position, cell.Value);
                }

                Pack(ref nativeCells, ref nativeRectangles, ref bytes, ref values, ref result);

                if (result.Value != 0) throw InvalidData(GetPackError(result.Value));

                rectangles.Capacity = Math.Max(rectangles.Capacity, nativeRectangles.Length);
                for (var i = 0; i < nativeRectangles.Length; i++)
                {
                    var rectangle = nativeRectangles[i];
                    rectangles.Add(new SerializedIntGridRectangle(new Vector2Int(rectangle.Position.x,
                        rectangle.Position.y), new Vector2Int(rectangle.Size.x, rectangle.Size.y), rectangle.Value));
                }

                var packedBytes = new byte[bytes.Length];
                for (var i = 0; i < bytes.Length; i++) packedBytes[i] = bytes[i];
                return packedBytes;
            }
            finally
            {
                result.Dispose();
                values.Dispose();
                bytes.Dispose();
                nativeRectangles.Dispose();
                nativeCells.Dispose();
            }
        }

        public static byte[] Encode(IReadOnlyList<SerializedIntGridRectangle> rectangles, int cellCount)
        {
            if (rectangles.Count == 0)
            {
                if (cellCount != 0) throw InvalidData("A non-empty grid has no rectangles.");
                return Array.Empty<byte>();
            }

            long rectangleCellCount = 0;
            var length = 1 + GetUnsignedLength((uint)rectangles.Count) + GetUnsignedLength((uint)cellCount);
            foreach (var rectangle in rectangles)
            {
                rectangleCellCount += GetCellCount(rectangle);
                if (rectangleCellCount > int.MaxValue) throw InvalidData("The encoded cell count is too large.");
                length = checked(length + GetSignedLength(rectangle.Position.x)
                                        + GetSignedLength(rectangle.Position.y)
                                        + GetUnsignedLength((uint)rectangle.Size.x)
                                        + GetUnsignedLength((uint)rectangle.Size.y)
                                        + GetSignedLength(rectangle.Value));
            }

            if (rectangleCellCount != cellCount) throw InvalidData("The cell count does not match its rectangles.");

            var bytes = new byte[length];
            var index = 0;
            bytes[index++] = FORMAT_VERSION;
            WriteUnsigned(bytes, ref index, (uint)rectangles.Count);
            WriteUnsigned(bytes, ref index, (uint)cellCount);
            foreach (var rectangle in rectangles)
            {
                WriteSigned(bytes, ref index, rectangle.Position.x);
                WriteSigned(bytes, ref index, rectangle.Position.y);
                WriteUnsigned(bytes, ref index, (uint)rectangle.Size.x);
                WriteUnsigned(bytes, ref index, (uint)rectangle.Size.y);
                WriteSigned(bytes, ref index, rectangle.Value);
            }

            return bytes;
        }

        public static int Decode(byte[] bytes, List<SerializedIntGridRectangle> rectangles)
        {
            rectangles.Clear();
            if (bytes == null || bytes.Length == 0) return 0;

            var index = 0;
            if (bytes[index++] != FORMAT_VERSION) throw InvalidData("The format version is unsupported.");

            var rectangleCount = ReadCount(bytes, ref index, "rectangle");
            var expectedCellCount = ReadCount(bytes, ref index, "cell");
            long cellCount = 0;
            rectangles.Capacity = Math.Max(rectangles.Capacity, rectangleCount);
            for (var i = 0; i < rectangleCount; i++)
            {
                var position = new Vector2Int(ReadSigned(bytes, ref index), ReadSigned(bytes, ref index));
                var size = new Vector2Int(ReadPositive(bytes, ref index, "width"),
                    ReadPositive(bytes, ref index, "height"));
                var value = ReadSigned(bytes, ref index);
                if (value is < short.MinValue or > short.MaxValue || value == 0)
                {
                    throw InvalidData("A rectangle value is invalid.");
                }

                var rectangle = new SerializedIntGridRectangle(position, size, (short)value);
                cellCount += GetCellCount(rectangle);
                if (cellCount > int.MaxValue) throw InvalidData("The decoded cell count is too large.");
                rectangles.Add(rectangle);
            }

            if (index != bytes.Length) throw InvalidData("The payload has trailing bytes.");
            if (cellCount != expectedCellCount) throw InvalidData("The encoded cell count does not match its rectangles.");
            return expectedCellCount;
        }

        public static void Expand(IReadOnlyList<SerializedIntGridRectangle> rectangles,
            List<SerializedIntGridCell> cells)
        {
            cells.Clear();
            foreach (var rectangle in rectangles)
            {
                for (var y = 0; y < rectangle.Size.y; y++)
                {
                    for (var x = 0; x < rectangle.Size.x; x++)
                    {
                        cells.Add(new SerializedIntGridCell(rectangle.Position + new Vector2Int(x, y),
                            rectangle.Value));
                    }
                }
            }
        }

        private static long GetCellCount(SerializedIntGridRectangle rectangle)
        {
            if (rectangle.Size.x <= 0 || rectangle.Size.y <= 0)
            {
                throw InvalidData("A rectangle size is invalid.");
            }

            if (rectangle.Value == 0) throw InvalidData("A rectangle value is invalid.");
            if ((long)rectangle.Position.x + rectangle.Size.x - 1 > int.MaxValue
                || (long)rectangle.Position.y + rectangle.Size.y - 1 > int.MaxValue)
            {
                throw InvalidData("A rectangle exceeds the coordinate range.");
            }

            return (long)rectangle.Size.x * rectangle.Size.y;
        }

        private static int ReadCount(byte[] bytes, ref int index, string name)
        {
            var value = ReadUnsigned(bytes, ref index);
            if (value > int.MaxValue) throw InvalidData($"The {name} count is too large.");
            return (int)value;
        }

        private static int ReadPositive(byte[] bytes, ref int index, string name)
        {
            var value = ReadUnsigned(bytes, ref index);
            if (value == 0 || value > int.MaxValue) throw InvalidData($"The rectangle {name} is invalid.");
            return (int)value;
        }

        private static int ReadSigned(byte[] bytes, ref int index)
        {
            var value = ReadUnsigned(bytes, ref index);
            return (int)(value >> 1) ^ -((int)value & 1);
        }

        private static uint ReadUnsigned(byte[] bytes, ref int index)
        {
            uint value = 0;
            for (var byteIndex = 0; byteIndex < MAX_VARINT_BYTES; byteIndex++)
            {
                if (index >= bytes.Length) throw InvalidData("The payload is truncated.");

                var current = bytes[index++];
                if (byteIndex == MAX_VARINT_BYTES - 1 && (current & 0xf0) != 0)
                {
                    throw InvalidData("A variable-length integer is too large.");
                }

                value |= (uint)(current & 0x7f) << (byteIndex * 7);
                if ((current & 0x80) == 0) return value;
            }

            throw InvalidData("A variable-length integer is invalid.");
        }

        private static int GetSignedLength(int value)
        {
            return GetUnsignedLength(ZigZag(value));
        }

        private static int GetUnsignedLength(uint value)
        {
            var length = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                length++;
            }

            return length;
        }

        private static void WriteSigned(byte[] bytes, ref int index, int value)
        {
            WriteUnsigned(bytes, ref index, ZigZag(value));
        }

        private static void WriteUnsigned(byte[] bytes, ref int index, uint value)
        {
            while (value >= 0x80)
            {
                bytes[index++] = (byte)((value & 0x7f) | 0x80);
                value >>= 7;
            }

            bytes[index++] = (byte)value;
        }

        private static uint ZigZag(int value)
        {
            return unchecked((uint)((value << 1) ^ (value >> 31)));
        }

        private static string GetPackError(int error)
        {
            return error switch
            {
                1 => "Zero-valued cells must not be serialized.",
                2 => "Duplicate cell positions must not be serialized.",
                3 => "A packed rectangle contains a missing cell.",
                _ => "The Burst packer failed.",
            };
        }

        private static FormatException InvalidData(string message)
        {
            return new FormatException($"Invalid Mosaic painted cell data. {message}");
        }

        private readonly struct NativeCell : IComparable<NativeCell>
        {
            public NativeCell(Vector2Int position, short value)
            {
                Position = new int2(position.x, position.y);
                Value = value;
            }

            public int2 Position { get; }

            public short Value { get; }

            public int CompareTo(NativeCell other)
            {
                var y = Position.y.CompareTo(other.Position.y);
                return y != 0 ? y : Position.x.CompareTo(other.Position.x);
            }
        }

        private readonly struct NativeRectangle
        {
            public NativeRectangle(int2 position, int2 size, short value)
            {
                Position = position;
                Size = size;
                Value = value;
            }

            public int2 Position { get; }

            public int2 Size { get; }

            public short Value { get; }
        }

        [BurstCompile]
        private static void Pack(ref NativeArray<NativeCell> cells,
            ref NativeList<NativeRectangle> rectangles, ref NativeList<byte> bytes,
            ref NativeParallelHashMap<int2, short> values, ref NativeReference<int> result)
        {
            cells.Sort();
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                if (cell.Value == 0)
                {
                    result.Value = 1;
                    return;
                }

                if (!values.TryAdd(cell.Position, cell.Value))
                {
                    result.Value = 2;
                    return;
                }
            }

            for (var i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                if (!values.TryGetValue(cell.Position, out var value)) continue;

                var width = GetWidth(ref values, cell.Position, value);
                var height = GetHeight(ref values, cell.Position, width, value);
                if (!RemoveRectangle(ref values, cell.Position, width, height))
                {
                    result.Value = 3;
                    return;
                }

                rectangles.Add(new NativeRectangle(cell.Position, new int2(width, height), value));
            }

            bytes.Add(FORMAT_VERSION);
            WriteUnsigned(ref bytes, (uint)rectangles.Length);
            WriteUnsigned(ref bytes, (uint)cells.Length);
            for (var i = 0; i < rectangles.Length; i++)
            {
                var rectangle = rectangles[i];
                WriteSigned(ref bytes, rectangle.Position.x);
                WriteSigned(ref bytes, rectangle.Position.y);
                WriteUnsigned(ref bytes, (uint)rectangle.Size.x);
                WriteUnsigned(ref bytes, (uint)rectangle.Size.y);
                WriteSigned(ref bytes, rectangle.Value);
            }
        }

        private static int GetWidth(ref NativeParallelHashMap<int2, short> values, int2 position, short value)
        {
            var width = 1;
            while (position.x <= int.MaxValue - width
                   && values.TryGetValue(position + new int2(width, 0), out var next) && next == value)
            {
                width++;
            }

            return width;
        }

        private static int GetHeight(ref NativeParallelHashMap<int2, short> values, int2 position,
            int width, short value)
        {
            var height = 1;
            while (position.y <= int.MaxValue - height
                   && IsCompleteRow(ref values, position, width, height, value))
            {
                height++;
            }

            return height;
        }

        private static bool IsCompleteRow(ref NativeParallelHashMap<int2, short> values, int2 position,
            int width, int row, short value)
        {
            for (var x = 0; x < width; x++)
            {
                if (!values.TryGetValue(position + new int2(x, row), out var next) || next != value)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RemoveRectangle(ref NativeParallelHashMap<int2, short> values, int2 position,
            int width, int height)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!values.Remove(position + new int2(x, y))) return false;
                }
            }

            return true;
        }

        private static void WriteSigned(ref NativeList<byte> bytes, int value)
        {
            WriteUnsigned(ref bytes, unchecked((uint)((value << 1) ^ (value >> 31))));
        }

        private static void WriteUnsigned(ref NativeList<byte> bytes, uint value)
        {
            while (value >= 0x80)
            {
                bytes.Add((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
        }
    }
}
