using FireAlt.Core.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    [InitializeOnLoad]
    public static class EditorResources
    {
        public static readonly Texture NotTexture;
        public static readonly Texture AnyTexture;
        public static readonly Texture MatrixCenterTexture;
        
        public static readonly Texture HorizontalSprite;
        public static readonly Texture VerticalSprite;
        public static readonly Texture RotatedSprite;
        
        public static readonly StyleSheet StyleSheet;
        public static readonly VisualTreeAsset WeightedListElementAsset;
        public static readonly VisualTreeAsset RuleGroupElementAsset;
        
        static EditorResources()
        {
            NotTexture = Load<Texture>("not.png");
            AnyTexture = Load<Texture>("any.png");
            MatrixCenterTexture = Load<Texture>("matrixCenter.png");

            HorizontalSprite = Load<Texture>("Horizontal.png");
            VerticalSprite = Load<Texture>("Vertical.png");
            RotatedSprite = Load<Texture>("Rotated.png");
            
            StyleSheet = Load<StyleSheet>("IntGridMatrix.uss");
            WeightedListElementAsset = Load<VisualTreeAsset>("WeightedListViewItem.uxml");
            RuleGroupElementAsset = Load<VisualTreeAsset>("RuleGroupElement.uxml");
        }

        private static T Load<T>(string path) where T : Object
        {
            return AssetDatabaseUtils.LoadEditorResource<T>(path, "com.firealt.mosaic");
        }
    }
}