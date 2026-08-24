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
        private SerializedObject _serializedObject;
        private string _resultPropertyPath;
        private string _weightPropertyPath;
    
        public void SetVisualElement<T>(VisualElement visualElement) where T : Object
        {
            _objectField = visualElement.Q<ObjectField>("ObjectField");
            _weightField = visualElement.Q<IntegerField>("WeightField");
            _objectField.objectType = typeof(T);
            _objectField.RegisterValueChangedCallback(OnObjectChanged);
            _weightField.RegisterValueChangedCallback(OnWeightChanged);
            
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
            _serializedObject = null;
            _resultPropertyPath = null;
            _weightPropertyPath = null;
            if (index < 0 || index >= list.arraySize)
            {
                _objectField.SetValueWithoutNotify(null);
                _weightField.SetValueWithoutNotify(1);
                if (_image != null) _image.sprite = null;
                return;
            }

            var entry = list.GetArrayElementAtIndex(index);
            var resultProperty = entry.FindPropertyRelative("result");
            var weightProperty = entry.FindPropertyRelative("weight");

            _serializedObject = list.serializedObject;
            _resultPropertyPath = resultProperty.propertyPath;
            _weightPropertyPath = weightProperty.propertyPath;
            _objectField.SetValueWithoutNotify(resultProperty.objectReferenceValue);
            _weightField.SetValueWithoutNotify(Mathf.Max(1, weightProperty.intValue));
            if (_image != null) _image.sprite = resultProperty.objectReferenceValue as Sprite;
        }

        private void OnObjectChanged(ChangeEvent<Object> evt)
        {
            if (!TryGetProperty(_resultPropertyPath, out var property)) return;

            Undo.RecordObject(_serializedObject.targetObject, "Edit Weighted Result");
            property.objectReferenceValue = evt.newValue;
            ApplySerializedChange(_serializedObject);
            if (_image != null) _image.sprite = evt.newValue as Sprite;
        }

        private void OnWeightChanged(ChangeEvent<int> evt)
        {
            if (!TryGetProperty(_weightPropertyPath, out var property)) return;

            var weight = Mathf.Max(1, evt.newValue);
            Undo.RecordObject(_serializedObject.targetObject, "Edit Weighted Result");
            property.intValue = weight;
            ApplySerializedChange(_serializedObject);
            _weightField.SetValueWithoutNotify(weight);
        }

        private bool TryGetProperty(string propertyPath, out SerializedProperty property)
        {
            property = null;
            if (_serializedObject == null || _serializedObject.targetObject == null
                || string.IsNullOrEmpty(propertyPath))
            {
                return false;
            }

            _serializedObject.Update();
            property = _serializedObject.FindProperty(propertyPath);
            return property != null;
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
