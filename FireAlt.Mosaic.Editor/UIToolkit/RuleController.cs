using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.AppUI.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Toggle = Unity.AppUI.UI.Toggle;

namespace FireAlt.Mosaic.Editor
{
    public class RuleController
    {
        private Toggle _enabledToggle;
        
        private IntGridMatrixView _intGridMatrixView;

        private TouchSliderFloat _chanceSlider;
        private TransformationButton _horizontalRuleTransformation;
        private TransformationButton _verticalRuleTransformation;
        private TransformationButton _rotationRuleTransformation;
        
        private TransformationButton _horizontalResultTransformation;
        private TransformationButton _verticalResultTransformation;
        private TransformationButton _rotationResultTransformation;
        
        private RuleGroup _ruleGroup;
        private int _ruleIndex;
        private SerializedProperty _ruleProperty;
        
        public void SetVisualElement(RuleGroup target, VisualElement visualElement)
        {
            _ruleGroup = target;

            {
                _enabledToggle = visualElement.Q<Toggle>("EnabledToggle");
                _enabledToggle.RegisterValueChangedCallback(OnEnableFieldChange);
            }

            {
                var matrixCol = visualElement.Q<VisualElement>("MatrixCol");
                _intGridMatrixView = new IntGridMatrixView(true, target.intGrid)
                {
                    tooltip = "IntGrid Rule Matrix. Click to edit"
                };
                matrixCol.Add(_intGridMatrixView);
                _intGridMatrixView.RegisterCallback<ClickEvent>(OnMatrixClicked);
            }

            {
                _chanceSlider = visualElement.Q<TouchSliderFloat>("ChanceSlider");
                _chanceSlider.RegisterValueChangedCallback(OnChanceFieldChange);
                var root = visualElement.Q<VisualElement>("RuleTransformations");

                const string horTooltip = "X mirror. Enable this to also check for match when mirrored horizontally";
                const string verTooltip = "Y mirror. Enable this to also check for match when mirrored vertically";
                const string rotTooltip = "Rotate the pattern by 90 degrees 4 times to check for matches";
                
                _horizontalRuleTransformation = CreateIconButton(Transformation.MirrorX, horTooltip, root, EditorResources.HorizontalSprite);
                _verticalRuleTransformation = CreateIconButton(Transformation.MirrorY, verTooltip, root, EditorResources.VerticalSprite);
                _rotationRuleTransformation = CreateIconButton(Transformation.Rotated, rotTooltip, root, EditorResources.RotatedSprite);
            }

            {
                var root = visualElement.Q<VisualElement>("ResultTransformations");
                
                const string horTooltip = "X mirror. Enable this to randomize a horizontal flip of the resulting sprite";
                const string verTooltip = "Y mirror. Enable this to randomize a vertical flip of the resulting sprite";
                const string rotTooltip = "Rotates the resulting sprite by 90 degrees random number of times";
                
                _horizontalResultTransformation = CreateIconButton(Transformation.MirrorX, horTooltip, root, EditorResources.HorizontalSprite);
                _verticalResultTransformation = CreateIconButton(Transformation.MirrorY, verTooltip, root, EditorResources.VerticalSprite);
                _rotationResultTransformation = CreateIconButton(Transformation.Rotated, rotTooltip, root, EditorResources.RotatedSprite);
            }
        }

        private static TransformationButton CreateIconButton(Transformation transformation, string tooltip, VisualElement root, Texture image)
        {
            var iconButton = new TransformationButton(transformation, tooltip)
            {
                image = image
            };
            root.Add(iconButton);
            return iconButton;
        }
    
        public void BindData(int index, SerializedProperty list)
        {
            _ruleIndex = index;
            _ruleProperty = list.GetArrayElementAtIndex(index);

            var enabledProperty = _ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.enabled));
            _enabledToggle.SetValueWithoutNotify(
                ((RuleGroup.Enabled)enabledProperty.intValue).HasFlag(RuleGroup.Enabled.Enabled));

            var chanceProperty = _ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.ruleChance));
            _chanceSlider.SetValueWithoutNotify(chanceProperty.floatValue);
            
            var matrixProperty = _ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.ruleMatrix));
                    
            _intGridMatrixView.Bind(matrixProperty);
            
            var ruleTransformationProperty = _ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.ruleTransformation));
            
            _horizontalRuleTransformation.Bind(ruleTransformationProperty);
            _verticalRuleTransformation.Bind(ruleTransformationProperty);
            _rotationRuleTransformation.Bind(ruleTransformationProperty);
            
            var resultTransformationProperty = _ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.resultTransformation));
            
            _horizontalResultTransformation.Bind(resultTransformationProperty);
            _verticalResultTransformation.Bind(resultTransformationProperty);
            _rotationResultTransformation.Bind(resultTransformationProperty);
        }
        
        private void OnEnableFieldChange(ChangeEvent<bool> evt)
        {
            ApplyEnabled(_ruleProperty, evt.newValue);
        }
        
        private void OnMatrixClicked(ClickEvent clickEvent)
        {
            if (clickEvent.button != 0) return;
            IntGridMatrixWindow.OpenWindow(_ruleGroup, _ruleIndex);
        }
        
        private void OnChanceFieldChange(ChangeEvent<float> evt)
        {
            ApplyChance(_ruleProperty, evt.newValue);
        }

        internal static void ApplyEnabled(SerializedProperty ruleProperty, bool enabled)
        {
            var property = ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.enabled));
            property.intValue = enabled ? (int)RuleGroup.Enabled.Enabled : 0;
            property.serializedObject.ApplyModifiedProperties();
        }

        internal static void ApplyChance(SerializedProperty ruleProperty, float chance)
        {
            var property = ruleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.ruleChance));
            property.floatValue = chance;
            property.serializedObject.ApplyModifiedProperties();
        }
    }
}
