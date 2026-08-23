using FireAlt.Core.Editor;
using FireAlt.Core.Editor.Inspectors;
using FireAlt.Mosaic.Authoring;
using UnityEditor;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    [CustomEditor(typeof(IntGridDefinition))]
    public class IntGridDefinitionEditor : ElementEditor
    {
        private IntGridDefinition _target;
        
        protected override void PostElementCreation(VisualElement root, bool createdElements)
        {
            _target = (IntGridDefinition)target;

            var btn = new Button(CreateRuleGroup)
            {
                text = "Create Rule Group"
            };
            root.Add(btn);
        }
        
        private void CreateRuleGroup()
        {
            var instance = AssetDatabaseUtils.CreateNewScriptableObjectAsset<RuleGroup>(name + "Group", _target);
            instance.intGrid = _target;
            EditorUtility.SetDirty(instance);

            var ruleGroups = serializedObject.FindProperty(nameof(IntGridDefinition.ruleGroups));
            var index = ruleGroups.arraySize;
            ruleGroups.arraySize++;
            ruleGroups.GetArrayElementAtIndex(index).objectReferenceValue = instance;
            serializedObject.ApplyModifiedProperties();
        }
    }
}
