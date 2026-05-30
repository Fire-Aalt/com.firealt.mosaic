using FireAlt.Core.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public class WeightedListEntryController
    {
        private ObjectField _objectField;
        private IntegerField _weightField;
        private Image _image;
    
        public void SetVisualElement<T>(VisualElement visualElement) where T : Object
        {
            _objectField = visualElement.Q<ObjectField>("ObjectField");
            _weightField = visualElement.Q<IntegerField>("WeightField");
            
            var imageHolder = visualElement.Q<VisualElement>("ImageHolder");

            if (typeof(T) == typeof(Sprite))
            {
                _image = new Image();
                _image.AddToClassList("list-view-element-image");
                imageHolder.Add(_image);
            }
            else
            {
                imageHolder.RemoveFromHierarchy();
            }
        }
    
        public void BindData<T>(int index, SerializedProperty list) where T : Object
        {
            var serializedTileSprites = list.GetArrayElementAtIndex(index);

            var resultProperty = serializedTileSprites.FindPropertyRelative("result");
            var weightProperty = serializedTileSprites.FindPropertyRelative("weight");

            _objectField.objectType = typeof(T);
            
            _objectField.BindProperty(resultProperty);
            _weightField.BindProperty(weightProperty);

            if (typeof(T) == typeof(Sprite))
            {
                _image.SetBinding("sprite", new DataBinding
                {
                    dataSourcePath = SerializationUtils.ToPropertyPath(resultProperty),
                    bindingMode = BindingMode.ToTarget
                });
            }
        }
    }
}