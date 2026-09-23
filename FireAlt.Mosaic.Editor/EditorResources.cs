using Unity.Scripting.LifecycleManagement;
using FireAlt.Core.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    
    [NoAutoStaticsCleanup]
    public static partial class EditorResources
    {
        public static Texture MosaicPaintingToolIcon;
        
        public static Texture NotTexture;
        public static Texture AnyTexture;
        public static Texture MatrixCenterTexture;
        
        public static Texture HorizontalSprite;
        public static Texture VerticalSprite;
        public static Texture RotatedSprite;
        public static Texture UniquePrefabSprite;
        
        public static StyleSheet StyleSheet;
        public static StyleSheet PaintingStyleSheet;
        public static VisualTreeAsset WeightedListElementAsset;
        public static VisualTreeAsset RuleGroupElementAsset;
        
        [OnCodeInitializing]
        private static void Initialize()
        {
            MosaicPaintingToolIcon = Load<Texture>("MosaicPaintingIcon.png");
            
            NotTexture = Load<Texture>("not.png");
            AnyTexture = Load<Texture>("any.png");
            MatrixCenterTexture = Load<Texture>("matrixCenter.png");

            HorizontalSprite = Load<Texture>("Horizontal.png");
            VerticalSprite = Load<Texture>("Vertical.png");
            RotatedSprite = Load<Texture>("Rotated.png");
            UniquePrefabSprite = Load<Texture>("UniquePrefab.png");
            
            StyleSheet = Load<StyleSheet>("IntGridMatrix.uss");
            PaintingStyleSheet = Load<StyleSheet>("MosaicPainting.uss");
            WeightedListElementAsset = Load<VisualTreeAsset>("WeightedListViewItem.uxml");
            RuleGroupElementAsset = Load<VisualTreeAsset>("RuleGroupElement.uxml");
        }

        private static T Load<T>(string path) where T : Object
        {
            return AssetDatabaseUtils.LoadEditorResource<T>(path, "com.firealt.mosaic");
        }
    }
}
