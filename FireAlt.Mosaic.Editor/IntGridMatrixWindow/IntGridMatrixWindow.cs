using System.Reflection;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public class IntGridMatrixWindow : EditorWindow
    {
        [SerializeField] private IntGridValueSelector _selectedIntGridValue;
        
        private SerializedObject _window;
        private SerializedObject _serializedObject;
        private SerializedProperty _matrixProperty;
        
        private RuleGroup _ruleGroup;
        private int _ruleIndex;
        private DragMode _rightClickMode = DragMode.None;
        
        private RuleGroup.Rule TargetRule => _ruleGroup.rules[_ruleIndex];

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Close;
        }
        
        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Close;
        }
        
        public static void OpenWindow(RuleGroup ruleGroup, int ruleIndex)
        {
            var wnd = GetWindow<IntGridMatrixWindow>(
                true,
                "Rule Matrix Window",
                true
            );
            wnd.Init(ruleGroup, ruleIndex);
            wnd.Show();
        }
        
        private void Init(RuleGroup ruleGroup, int ruleIndex)
        {
            _selectedIntGridValue = new IntGridValueSelector
            {
                intGrid = ruleGroup.intGrid
            };

            _ruleGroup = ruleGroup;
            _ruleIndex = ruleIndex;
            
            _window = new SerializedObject(this);
            _serializedObject = new SerializedObject(_ruleGroup);
            
            CreateUI();
        }
        
        private void CreateUI()
        {
            var root = rootVisualElement;
            root.Clear();
            
            root.styleSheets.Add(EditorResources.StyleSheet);

            // 4 columns: 0.2 | 0.4 | 0.2 | 0.2
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexGrow = 1
                }
            };
            root.Add(row);

            VisualElement MakeCol(float grow) =>
                new()
                {
                    style =
                    {
                        flexDirection = FlexDirection.Column,
                        width = Length.Percent(grow * 100f),
                        marginLeft = 4,
                        marginRight = 4
                    }
                };

            var colSelect = MakeCol(0.2f); // 20%
            var colMatrix = MakeCol(0.4f); // 40%
            var colSprites = MakeCol(0.2f); // 20%
            var colEntities = MakeCol(0.2f); // 20%

            row.Add(colSelect);
            row.Add(colMatrix);
            row.Add(colSprites);
            row.Add(colEntities);

            // Column 1: IntGridValue selector
            {
                var box = new GroupBox { name = "IntGridSelectorBox" };
                
                var property = _window.FindProperty(nameof(_selectedIntGridValue));
                var fieldInfo = GetType().GetField(nameof(_selectedIntGridValue),BindingFlags.NonPublic | BindingFlags.Instance);
                
                var intGridSelector = IntGridValueSelectorDrawer.Create(fieldInfo, property);
                box.Add(intGridSelector);
                colSelect.Add(box);
            }
            
            var targetRuleProperty = _serializedObject.FindProperty(nameof(RuleGroup.rules)).GetArrayElementAtIndex(_ruleIndex);
            
            // Column 2: Matrix
            {
                _matrixProperty = targetRuleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.ruleMatrix));
                var matrixView = new IntGridMatrixView(false, _ruleGroup.intGrid);
                matrixView.Bind(_matrixProperty);
                
                colMatrix.Add(matrixView);
                
                var dragger = new IntGridMatrixManipulator
                {
                    DragEnter = OnDragEnter,
                    HoverEnter = (cell) => { cell.AddToClassList("int-grid-matrix-cell-hover"); },
                    HoverLeave = (cell) => { cell.RemoveFromClassList("int-grid-matrix-cell-hover"); },
                    DragStop = () => _rightClickMode = DragMode.None
                };
                matrixView.AddManipulator(dragger);
            }
            
            // Column 3: Sprites
            {
                var tileSpritesSerializedList = targetRuleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.TileSprites));
                
                var spritesListView = new WeightedListViewBuilder<Sprite, SpriteResult>("SpritesListView", "Tile Sprites",
                    _ruleGroup, tileSpritesSerializedList, TargetRule.TileSprites, sprite => new SpriteResult(sprite));
                
                colSprites.Add(spritesListView.Build());
            }
            
            // Column 4: Prefabs
            {
                var tileEntitiesSerializedList = targetRuleProperty.FindPropertyRelative(nameof(RuleGroup.Rule.TileEntities));
                
                var prefabsListView = new WeightedListViewBuilder<GameObject, PrefabResult>("PrefabsListView", "Tile Entities",
                    _ruleGroup, tileEntitiesSerializedList, TargetRule.TileEntities, prefab => new PrefabResult(prefab));
                
                colEntities.Add(prefabsListView.Build());
            }
        }
        
        private void Update()
        {
            if (_ruleGroup == null || _ruleIndex < 0 || _ruleIndex >= _ruleGroup.rules.Count)
            {
                Close();
                return;
            }

            foreach (var spriteResult in TargetRule.TileSprites)
            {
                spriteResult.Validate();
            }
            foreach (var entityResult in TargetRule.TileEntities)
            {
                entityResult.Validate();
            }
        }

        private void OnDragEnter(VisualElement cell, IntGridMatrixManipulator.Pressed pressed)
        {
            var hc = cell.parent.childCount;
            for (int i = 0; i < hc; i++)
            {
                if (cell.parent[i] == cell)
                {
                    if (pressed == IntGridMatrixManipulator.Pressed.RightMouseButton)
                        RightClick(i);
                    else
                        LeftClick(i);
                    return;
                }
            }
        }
        
        private void LeftClick(int cellIndex)
        {
            _serializedObject.Update();
            var slotProperty = GetCurrentMatrixSlotProperty(cellIndex);
            var slot = slotProperty.intValue;
            var selectedValue = _selectedIntGridValue.value;

            if (slot != selectedValue)
            {
                Undo.RecordObject(_ruleGroup, "Edit Rule Matrix");
                slotProperty.intValue = slot < 0 ? 0 : selectedValue;
                ApplyMatrixChange();
            }
        }

        private void RightClick(int cellIndex)
        {
            _serializedObject.Update();
            var slotProperty = GetCurrentMatrixSlotProperty(cellIndex);
            var slot = slotProperty.intValue;

            if (_rightClickMode == DragMode.None)
            {
                if (slot == 0) _rightClickMode = DragMode.Set;
                else if (slot != 0) _rightClickMode = DragMode.Clear;
            }

            var value = _rightClickMode switch
            {
                DragMode.Set => -_selectedIntGridValue.value,
                DragMode.Clear => 0,
                _ => slot
            };

            if (slot != value)
            {
                Undo.RecordObject(_ruleGroup, "Edit Rule Matrix");
                slotProperty.intValue = value;
                ApplyMatrixChange();
            }
        }

        private SerializedProperty GetCurrentMatrixSlotProperty(int cellIndex)
        {
            var matrixArrayProperty = _matrixProperty.FindPropertyRelative(
                _ruleGroup.intGrid.useDualGrid
                    ? nameof(IntGridMatrix.dualGridMatrix)
                    : nameof(IntGridMatrix.singleGridMatrix));

            return matrixArrayProperty
                .GetArrayElementAtIndex(cellIndex)
                .FindPropertyRelative(nameof(IntGridValue.value));
        }

        private void ApplyMatrixChange()
        {
            _serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(_ruleGroup);
            _serializedObject.Update();
        }
        
        private enum DragMode
        {
            None,
            Clear,
            Set
        }
    }
}
