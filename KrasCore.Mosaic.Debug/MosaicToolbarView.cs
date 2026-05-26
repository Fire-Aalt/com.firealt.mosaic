using System;
using System.ComponentModel;
using BovineLabs.Anchor;
using Unity.AppUI.UI;

namespace FireAlt.Mosaic.Debug
{
    [Transient]
    public class MosaicToolbarView : View<MosaicToolbarViewModel>, IDisposable
    {
        public const string UssClassName = "bl-quality-tab";

        private readonly Dropdown _dropdown;
        
        public MosaicToolbarView()
            : base(new MosaicToolbarViewModel())
        {
            AddToClassList(UssClassName);
            
            _dropdown = new Dropdown
            {
                dataSource = ViewModel,
                selectionType = PickerSelectionType.Multiple,
                closeOnSelection = false,
                defaultMessage = "Draw IntGrids",
                bindTitle = (item, _) => item.labelElement.text = "Draw IntGrids",
                bindItem = ViewModel.BindItem,
            };

            _dropdown.SetBindingTwoWay(nameof(Dropdown.value), nameof(MosaicToolbarViewModel.IntGridValues));

            Add(_dropdown);

            ViewModel.PropertyChanged += OnPropertyChanged;
        }

        public void Dispose()
        {
            ViewModel.PropertyChanged -= OnPropertyChanged;
        }
        
        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MosaicToolbarViewModel.IntGrids))
            {
                _dropdown.sourceItems = ViewModel.IntGrids;
                _dropdown.value = ViewModel.IntGridValues; // Can't rely on binding to have updated in time
                _dropdown.Refresh();
            }
        }
    }
}