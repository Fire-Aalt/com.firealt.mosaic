using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public class ListViewBuilder<T> where T : new()
    {
        public string Name;
        
        public List<T> List;
        public string ListLabel;
        public object DataSource;
        public SerializedProperty SerializedListProperty;
        public Func<VisualElement> MakeItem;
        public Action<VisualElement, int> BindItem;
        public Func<T> CreateDataItem;
        
        protected ListView ListView;
        
        public virtual ListView Build()
        {
            ListView = new ListView
            {
                reorderable = true,
                makeHeader = () =>
                {
                    var toolbar = new Toolbar();
                    toolbar.AddToClassList("list-view-header");
                    
                    var label = new Label { text = ListLabel };
                    var spacer = new ToolbarSpacer();
                    var addBtn = new ToolbarButton(OnAddClicked) { text = "Add" };
                    var removeBtn = new ToolbarButton(OnRemoveClicked) { text = "Remove" };
                    
                    label.AddToClassList("list-view-header-label");
                    spacer.AddToClassList("list-view-header-spacer");
                    
                    toolbar.Add(label);
                    toolbar.Add(spacer);
                    toolbar.Add(addBtn);
                    toolbar.Add(removeBtn);
                    
                    return toolbar;
                },
                selectionType = SelectionType.Multiple,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                dataSource = DataSource,
                makeItem = MakeItem
            };
            ListView.AddToClassList("list-view");
            
            if (!string.IsNullOrEmpty(Name))
            {
                ListView.name = Name;
            }

            SetBindings();
            return ListView;
        }
        
        private void SetBindings()
        {
            ListView.bindItem = BindItem;
            ListView.BindProperty(SerializedListProperty);
        }
        
        private void OnAddClicked()
        {
            var item = CreateDataItem != null 
                ? CreateDataItem.Invoke() 
                : new T();
            
            List.Add(item);
            ListView.Rebuild();
            
            HighlightLastElement();
        }

        private void OnRemoveClicked()
        {
            var indices = ListView.selectedIndices
                .OrderByDescending(x => x)
                .ToList();

            if (indices.Count == 0) return;

            foreach (var i in indices)
            {
                if (i >= 0 && i < List.Count)
                {
                    List.RemoveAt(i);
                }
            }

            ListView.Rebuild();
        }
        
        protected void HighlightLastElement()
        {
            var last = List.Count - 1;

            if (last >= 0)
            {
                ListView.SetSelection(new[] { last });
                ListView.ScrollToItem(last);
            }
        }
    }
}