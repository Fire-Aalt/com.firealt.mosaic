using System;
using System.Collections.Generic;
using System.Linq;
using FireAlt.Mosaic.Authoring;
using UnityEditor;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FireAlt.Mosaic.Editor
{
    public class WeightedListViewBuilder<TObject, TEntry> : ListViewBuilder<TEntry>
        where TObject : Object
        where TEntry : new()
    {
        private readonly Func<TObject, TEntry> _convertFunc;
        
        public WeightedListViewBuilder(string name, string listLabel, RuleGroup dataSource,
            SerializedProperty serializedListProperty, List<TEntry> boundList, Func<TObject, TEntry> convertFunc)
        {
            _convertFunc = convertFunc;
            
            ListLabel = listLabel;
            Name = name;
            DataSource = dataSource;
            SerializedListProperty = serializedListProperty;
            List = boundList;
            MakeItem = () =>
            {
                var newListEntry = EditorResources.WeightedListElementAsset.Instantiate();

                var newListEntryLogic = new WeightedListEntryController();
                newListEntryLogic.SetVisualElement<TObject>(newListEntry);
                newListEntry.userData = newListEntryLogic;

                return newListEntry;
            };
            BindItem = (item, index) =>
            {
                (item.userData as WeightedListEntryController)?.BindData<TObject>(index, serializedListProperty);
            };
        }

        public override ListView Build()
        {
            base.Build();
            RegisterDragAndDrop();
            
            return ListView;
        }

        private void RegisterDragAndDrop()
        {
            ListView.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (TryGetDraggedAssets(out _))
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.StopPropagation();
                }
            });

            ListView.RegisterCallback<DragPerformEvent>(evt =>
            {
                if (!TryGetDraggedAssets(out var assets)) return;

                DragAndDrop.AcceptDrag();
                
                var entries = assets.Where(o => IsAsset(o) && o is TObject)
                    .Select(o => _convertFunc.Invoke(o as TObject))
                    .ToList();
                
                AddEntries(entries);
                evt.StopPropagation();
            });
        }
        
        private void AddEntries(List<TEntry> entries)
        {
            if (entries.Count == 0) return;

            List.AddRange(entries);
            ListView.Rebuild();

            HighlightLastElement();
        }

        private static bool IsAsset(Object o)
        {
            return o != null && EditorUtility.IsPersistent(o);
        }

        private static bool TryGetDraggedAssets(out List<Object> assets)
        {
            assets = DragAndDrop.objectReferences
                .Where(IsAsset)
                .Distinct()
                .ToList();

            return assets.Count > 0;
        }
    }
}