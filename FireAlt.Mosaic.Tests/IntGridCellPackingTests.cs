using System;
using System.Collections.Generic;
using System.Linq;
using FireAlt.Mosaic.Authoring;
using NUnit.Framework;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace FireAlt.Mosaic.Tests
{
    public class IntGridCellPackingTests
    {
        private const int PERFORMANCE_CELL_COUNT = 25000;
        private const double MAX_COLLAPSE_MILLISECONDS = 50;

        [Test]
        public void Pack_DenseMixedValuesAndHoleCreatesLosslessRectangles()
        {
            var cells = new List<SerializedIntGridCell>();
            for (var y = -2; y <= 1; y++)
            {
                for (var x = -3; x <= 2; x++)
                {
                    if (x == 0 && y == 0) continue;
                    cells.Add(new SerializedIntGridCell(new Vector2Int(x, y), y == 1 ? (short)2 : (short)1));
                }
            }

            var rectangles = new List<SerializedIntGridRectangle>();
            IntGridCellPacking.Pack(cells, rectangles);

            var expanded = new List<SerializedIntGridCell>();
            IntGridCellPacking.Expand(rectangles, expanded);
            AssertCellsEqual(cells, expanded);
            Assert.IsFalse(rectangles.Any(rectangle => rectangle.Value == 1
                                                       && Contains(rectangle, new Vector2Int(0, 0))));
            Assert.IsFalse(rectangles.Any(rectangle => rectangle.Position.y < 1
                                                       && rectangle.Position.y + rectangle.Size.y > 1));
        }

        [Test]
        public void Pack_ShuffledInputProducesDeterministicBytes()
        {
            var cells = CreateIrregularCells();
            var shuffled = cells.OrderByDescending(cell => cell.Position.x)
                .ThenByDescending(cell => cell.Position.y).ToList();

            var first = PackAndEncode(cells);
            var second = PackAndEncode(shuffled);

            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void Codec_RoundTripsSignedExtremes()
        {
            var rectangles = new List<SerializedIntGridRectangle>
            {
                new(new Vector2Int(int.MinValue, int.MinValue), Vector2Int.one, short.MinValue),
                new(new Vector2Int(int.MaxValue - 1, int.MaxValue), new Vector2Int(2, 1), short.MaxValue),
            };

            var bytes = IntGridCellPacking.Encode(rectangles, 3);
            var decoded = new List<SerializedIntGridRectangle>();

            Assert.AreEqual(3, IntGridCellPacking.Decode(bytes, decoded));
            AssertRectanglesEqual(rectangles, decoded);
        }

        [Test]
        public void Codec_EmptyDataUsesNoBytes()
        {
            var bytes = IntGridCellPacking.Encode(Array.Empty<SerializedIntGridRectangle>(), 0);
            var decoded = new List<SerializedIntGridRectangle>();

            Assert.IsEmpty(bytes);
            Assert.AreEqual(0, IntGridCellPacking.Decode(bytes, decoded));
            Assert.IsEmpty(decoded);
        }

        [Test]
        public void Codec_RejectsMismatchedAndOverflowingRectangleCounts()
        {
            var mismatched = new[]
            {
                new SerializedIntGridRectangle(Vector2Int.zero, new Vector2Int(2, 2), 1),
            };
            var overflowing = new[]
            {
                new SerializedIntGridRectangle(Vector2Int.zero,
                    new Vector2Int(int.MaxValue, int.MaxValue), 1),
            };

            Assert.Throws<FormatException>(() => IntGridCellPacking.Encode(mismatched, 3));
            Assert.Throws<FormatException>(() => IntGridCellPacking.Encode(overflowing, int.MaxValue));
        }

        [TestCase(new byte[] { 2, 0, 0 })]
        [TestCase(new byte[] { 1 })]
        [TestCase(new byte[] { 1, 1, 1, 0, 0, 0, 1, 2 })]
        [TestCase(new byte[] { 1, 0, 0, 0 })]
        [TestCase(new byte[] { 1, 0x80, 0x80, 0x80, 0x80, 0x10, 0 })]
        public void Codec_InvalidPayloadThrows(byte[] bytes)
        {
            Assert.Throws<FormatException>(() =>
                IntGridCellPacking.Decode(bytes, new List<SerializedIntGridRectangle>()));
        }

        [Test]
        public void PackedData_CollapseKeepsCellsAndInvalidationRebuildsThem()
        {
            var data = new SerializedIntGridData();
            var cells = data.MutableCells;
            cells.AddRange(CreateIrregularCells());
            data.Collapse();
            var bytes = data.Bytes.ToArray();
            AssertCellsEqual(cells, data.Cells);

            data.OnAfterDeserialize();
            AssertCellsEqual(cells, data.Cells);
            CollectionAssert.AreEqual(bytes, data.Bytes);
        }

        [Test]
        public void Pack_Representative984CellsProduces92RectanglesAndSmallResponse()
        {
            var cells = new List<SerializedIntGridCell>(984);
            for (var rectangleIndex = 0; rectangleIndex < 92; rectangleIndex++)
            {
                var width = rectangleIndex < 64 ? 11 : 10;
                for (var x = 0; x < width; x++)
                {
                    cells.Add(new SerializedIntGridCell(new Vector2Int(x - 20, rectangleIndex * 2 - 100),
                        (short)(rectangleIndex % 3 + 1)));
                }
            }

            var rectangles = new List<SerializedIntGridRectangle>();
            IntGridCellPacking.Pack(cells, rectangles);
            var cellResponseSize = cells.Sum(cell =>
                $"{{\"x\":{cell.Position.x},\"y\":{cell.Position.y},\"value\":{cell.Value}}},".Length);
            var rectangleResponseSize = rectangles.Sum(rectangle =>
                ($"{{\"x\":{rectangle.Position.x},\"y\":{rectangle.Position.y},\"width\":{rectangle.Size.x},"
                 + $"\"height\":{rectangle.Size.y},\"value\":{rectangle.Value}}},").Length);

            Assert.AreEqual(984, cells.Count);
            Assert.AreEqual(92, rectangles.Count);
            Assert.Less(rectangleResponseSize, cellResponseSize * 0.2);
        }

        [Test]
        public void PackAndEncode_25000CellsCompletesUnderBudget()
        {
            var cells = new List<SerializedIntGridCell>(PERFORMANCE_CELL_COUNT);
            for (var i = 0; i < 20000; i++)
            {
                cells.Add(new SerializedIntGridCell(new Vector2Int(i, 0), 1));
            }

            for (var i = 0; i < 5000; i++)
            {
                cells.Add(new SerializedIntGridCell(new Vector2Int(-i - 1, 1), 1));
            }

            var rectangles = new List<SerializedIntGridRectangle>();
            for (var i = 0; i < 2; i++) PackAndEncode(cells, rectangles);

            var samples = new double[5];
            for (var i = 0; i < samples.Length; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                PackAndEncode(cells, rectangles);
                stopwatch.Stop();
                samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            Assert.Less(samples[samples.Length / 2], MAX_COLLAPSE_MILLISECONDS,
                $"Median collapse took {samples[samples.Length / 2]:0.00} ms.");
        }

        private static List<SerializedIntGridCell> CreateIrregularCells()
        {
            return new List<SerializedIntGridCell>
            {
                new(new Vector2Int(-3, -2), 1),
                new(new Vector2Int(-2, -2), 1),
                new(new Vector2Int(-3, -1), 1),
                new(new Vector2Int(-2, -1), 1),
                new(new Vector2Int(4, 7), 2),
                new(new Vector2Int(5, 7), 2),
                new(new Vector2Int(5, 8), 2),
            };
        }

        private static byte[] PackAndEncode(IReadOnlyList<SerializedIntGridCell> cells)
        {
            return PackAndEncode(cells, new List<SerializedIntGridRectangle>());
        }

        private static byte[] PackAndEncode(IReadOnlyList<SerializedIntGridCell> cells,
            List<SerializedIntGridRectangle> rectangles)
        {
            return IntGridCellPacking.PackAndEncode(cells, rectangles);
        }

        private static bool Contains(SerializedIntGridRectangle rectangle, Vector2Int position)
        {
            return position.x >= rectangle.Position.x && position.y >= rectangle.Position.y
                   && position.x < rectangle.Position.x + rectangle.Size.x
                   && position.y < rectangle.Position.y + rectangle.Size.y;
        }

        private static void AssertCellsEqual(IReadOnlyList<SerializedIntGridCell> expected,
            IReadOnlyList<SerializedIntGridCell> actual)
        {
            var expectedValues = expected.ToDictionary(cell => cell.Position, cell => cell.Value);
            var actualValues = actual.ToDictionary(cell => cell.Position, cell => cell.Value);
            CollectionAssert.AreEquivalent(expectedValues, actualValues);
        }

        private static void AssertRectanglesEqual(IReadOnlyList<SerializedIntGridRectangle> expected,
            IReadOnlyList<SerializedIntGridRectangle> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual(expected[i].Position, actual[i].Position);
                Assert.AreEqual(expected[i].Size, actual[i].Size);
                Assert.AreEqual(expected[i].Value, actual[i].Value);
            }
        }
    }
}
