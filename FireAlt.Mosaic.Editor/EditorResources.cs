using Unity.Scripting.LifecycleManagement;
using FireAlt.Core.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    [NoAutoStaticsCleanup]
    public static class EditorResources
    {
        private static readonly PackageEditorResources PackageResources = new("com.firealt.mosaic");

        public static Texture MosaicPaintingToolIcon => PackageResources.Load<Texture>("MosaicPaintingIcon.png");

        public static Texture NotTexture => PackageResources.Load<Texture>("not.png");
        public static Texture AnyTexture => PackageResources.Load<Texture>("any.png");
        public static Texture MatrixCenterTexture => PackageResources.Load<Texture>("matrixCenter.png");

        public static Texture HorizontalSprite => PackageResources.Load<Texture>("Horizontal.png");
        public static Texture VerticalSprite => PackageResources.Load<Texture>("Vertical.png");
        public static Texture RotatedSprite => PackageResources.Load<Texture>("Rotated.png");
        public static Texture UniquePrefabSprite => PackageResources.Load<Texture>("UniquePrefab.png");

        public static StyleSheet StyleSheet => PackageResources.Load<StyleSheet>("IntGridMatrix.uss");
        public static StyleSheet PaintingStyleSheet => PackageResources.Load<StyleSheet>("MosaicPainting.uss");
        public static VisualTreeAsset WeightedListElementAsset => PackageResources.Load<VisualTreeAsset>("WeightedListViewItem.uxml");
        public static VisualTreeAsset RuleGroupElementAsset => PackageResources.Load<VisualTreeAsset>("RuleGroupElement.uxml");
    }
}
