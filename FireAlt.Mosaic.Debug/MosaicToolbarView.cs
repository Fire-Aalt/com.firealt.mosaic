using System;
using System.ComponentModel;
using BovineLabs.Anchor;
using Unity.AppUI.UI;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Debug
{
    public class MosaicToolbarView : VisualElement, IDisposable
    {
        public const string USS_CLASS_NAME = "bl-quality-tab";

        private readonly Dropdown _dropdown;
        
        private MosaicToolbarViewModel ViewModel => (MosaicToolbarViewModel)dataSource;

        public MosaicToolbarView(MosaicToolbarViewModel viewModel)
        {
            dataSource = viewModel;
            AddToClassList(USS_CLASS_NAME);
            
            _dropdown = new Dropdown
            {
                dataSource = ViewModel,
                selectionType = PickerSelectionType.Multiple,
                closeOnSelection = false,
                defaultMessage = "Draw IntGrids",
                bindTitle = (item, _) => item.labelElement.text = "Draw IntGrids",
                bindItem = ViewModel.BindItem,
                sourceItems = ViewModel.IntGrids,
                value = ViewModel.IntGridValues,
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
