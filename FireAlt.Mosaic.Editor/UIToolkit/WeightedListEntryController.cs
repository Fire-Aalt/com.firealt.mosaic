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
        private EventCallback<ChangeEvent<Object>> _objectChangedCallback;
        private EventCallback<ChangeEvent<int>> _weightChangedCallback;
    
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

            if (_objectChangedCallback != null)
            {
                _objectField.UnregisterValueChangedCallback(_objectChangedCallback);
            }

            if (_weightChangedCallback != null)
            {
                _weightField.UnregisterValueChangedCallback(_weightChangedCallback);
            }
            
            _objectField.BindProperty(resultProperty);
            _weightField.BindProperty(weightProperty);

            var resultPropertyPath = resultProperty.propertyPath;
            var weightPropertyPath = weightProperty.propertyPath;
            var serializedObject = list.serializedObject;

            _objectChangedCallback = evt =>
            {
                serializedObject.Update();
                Undo.RecordObject(serializedObject.targetObject, "Edit Weighted Result");
                serializedObject.FindProperty(resultPropertyPath).objectReferenceValue = evt.newValue;
                ApplySerializedChange(serializedObject);
            };
            _objectField.RegisterValueChangedCallback(_objectChangedCallback);

            _weightChangedCallback = evt =>
            {
                serializedObject.Update();
                Undo.RecordObject(serializedObject.targetObject, "Edit Weighted Result");
                serializedObject.FindProperty(weightPropertyPath).intValue = Mathf.Max(1, evt.newValue);
                ApplySerializedChange(serializedObject);
            };
            _weightField.RegisterValueChangedCallback(_weightChangedCallback);

            if (typeof(T) == typeof(Sprite))
            {
                _image.SetBinding("sprite", new DataBinding
                {
                    dataSourcePath = SerializationUtils.ToPropertyPath(resultProperty),
                    bindingMode = BindingMode.ToTarget
                });
            }
        }

        private static void ApplySerializedChange(SerializedObject serializedObject)
        {
            serializedObject.ApplyModifiedProperties();
            if (serializedObject.targetObject != null)
            {
                EditorUtility.SetDirty(serializedObject.targetObject);
            }

            serializedObject.Update();
        }
    }
}
