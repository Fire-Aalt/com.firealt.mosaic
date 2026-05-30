using FireAlt.Mosaic.Data;
using UnityEditor;
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
            RegisterCallback<ClickEvent>(OnClicked);
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
            _property.serializedObject.Update();
        }
    }
}