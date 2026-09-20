using FireAlt.Mosaic.Debug;
using NUnit.Framework;
using Unity.AppUI.UI;
using Unity.Collections;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Tests
{
    public class MosaicToolbarMigrationTests
    {
        [Test]
        public void RecreatedViewsRetainRegisteredModelAndSelection()
        {
            var model = new MosaicToolbarViewModel();
            model.Load();
            try
            {
                var intGrids = new NativeList<MosaicToolbarViewModel.Data.IntGridName>(Allocator.Temp);
                intGrids.Add(new MosaicToolbarViewModel.Data.IntGridName
                {
                    Name = new FixedString128Bytes("Terrain"),
                });
                model.Value.IntGrids = intGrids;
                model.IntGridValues = new[] { 0 };

                using var first = (MosaicToolbarView)model.CreateElement();
                using var second = (MosaicToolbarView)model.CreateElement();
                Assert.That(first, Is.Not.SameAs(second));
                Assert.That(first.dataSource, Is.SameAs(model));
                Assert.That(second.dataSource, Is.SameAs(model));
                Assert.That(second.Q<Dropdown>().sourceItems.Count, Is.EqualTo(1));
                CollectionAssert.AreEqual(new[] { 0 }, second.Q<Dropdown>().value);
            }
            finally
            {
                model.Unload();
            }
        }

        [Test]
        public void DisposedViewStopsReceivingModelChanges()
        {
            var model = new MosaicToolbarViewModel();
            model.Load();
            try
            {
                var view = (MosaicToolbarView)model.CreateElement();
                var dropdown = view.Q<Dropdown>();
                view.Dispose();
                dropdown.sourceItems = null;
                model.OnPropertyChanged(new FixedString64Bytes(nameof(MosaicToolbarViewModel.IntGrids)));
                Assert.That(dropdown.sourceItems, Is.Null);
            }
            finally
            {
                model.Unload();
            }
        }
    }
}
