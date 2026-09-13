using FireAlt.Mosaic.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public class TransformationButton : Image
    {
        private const string Disabled = "icon-button-disabled";
        private const string Enabled = "icon-button-enabled";

        private readonly Transformation _transformation;
        private SerializedProperty _property;
        
        public TransformationButton(Transformation transformation, string tooltip)
        {
            _transformation = transformation;
            this.tooltip = tooltip;
            AddToClassList("icon-button");
            RegisterCallback<ClickEvent>(OnClicked);
        }
        
        public void Bind(SerializedProperty property)
        {
            _property = property;

            if (((Transformation)_property.boxedValue).HasFlag(_transformation))
            {
                EnableIconButton();
            }
            else
            {
                DisableIconButton();
            }
        }
        
        private void EnableIconButton()
        {
            RemoveFromClassList(Disabled);
            AddToClassList(Enabled);
        }
        
        private void DisableIconButton()
        {
            RemoveFromClassList(Enabled);
            AddToClassList(Disabled);
        }
        
        private void OnClicked(ClickEvent _)
        {
            _property.boxedValue = (Transformation)_property.boxedValue ^ _transformation;
            
            if (((Transformation)_property.boxedValue).HasFlag(_transformation))
            {
                EnableIconButton();
            }
            else
            {
                DisableIconButton();
            }
            _property.serializedObject.ApplyModifiedProperties();
        }
    }

    public class UniquePrefabButton : Image
    {
        private const string DISABLED = "icon-button-disabled";
        private const string ENABLED = "icon-button-enabled";

        private SerializedProperty _property;

        public UniquePrefabButton(Texture image)
        {
            this.image = image;
            tooltip = "Only 1 unique prefab per cell for this rule. Matching faces still render, but only the first face spawns a prefab.";
            AddToClassList("icon-button");
            RegisterCallback<ClickEvent>(OnClicked);
        }

        public void Bind(SerializedProperty property)
        {
            _property = property;
            Refresh();
        }

        private void OnClicked(ClickEvent _)
        {
            _property.boolValue = !_property.boolValue;
            _property.serializedObject.ApplyModifiedProperties();
            Refresh();
        }

        private void Refresh()
        {
            EnableInClassList(ENABLED, _property.boolValue);
            EnableInClassList(DISABLED, !_property.boolValue);
        }
    }
}
