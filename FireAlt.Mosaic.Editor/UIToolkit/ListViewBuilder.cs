using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

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
            
            AddSerializedItem(item);
            ListView.Rebuild();
            
            HighlightLastElement();
        }

        private void OnRemoveClicked()
        {
            var indices = ListView.selectedIndices
                .OrderByDescending(x => x)
                .ToList();

            if (indices.Count == 0) return;

            var serializedObject = SerializedListProperty.serializedObject;
            serializedObject.Update();
            RecordUndo(serializedObject);

            foreach (var i in indices)
            {
                if (i >= 0 && i < SerializedListProperty.arraySize)
                {
                    SerializedListProperty.DeleteArrayElementAtIndex(i);
                }
            }

            ListView.ClearSelection();
            ApplySerializedObject(serializedObject);
            ListView.Rebuild();
        }

        protected void AddSerializedItem(T item)
        {
            AddSerializedItems(new[] { item });
        }

        protected void AddSerializedItems(IReadOnlyList<T> items)
        {
            if (items.Count == 0) return;

            var serializedObject = SerializedListProperty.serializedObject;
            serializedObject.Update();
            RecordUndo(serializedObject);

            for (int i = 0; i < items.Count; i++)
            {
                var index = SerializedListProperty.arraySize;
                SerializedListProperty.arraySize++;
                SerializedListProperty.GetArrayElementAtIndex(index).boxedValue = items[i];
            }

            ApplySerializedObject(serializedObject);
        }

        protected void ApplySerializedObject(SerializedObject serializedObject)
        {
            serializedObject.ApplyModifiedProperties();
            if (serializedObject.targetObject is Object target)
            {
                EditorUtility.SetDirty(target);
            }

            serializedObject.Update();
        }

        private void RecordUndo(SerializedObject serializedObject)
        {
            if (serializedObject.targetObject is Object target)
            {
                Undo.RecordObject(target, $"Edit {ListLabel}");
            }
        }
        
        protected void HighlightLastElement()
        {
            var last = SerializedListProperty.arraySize - 1;

            if (last >= 0)
            {
                ListView.SetSelection(new[] { last });
                ListView.ScrollToItem(last);
            }
        }
    }
}
