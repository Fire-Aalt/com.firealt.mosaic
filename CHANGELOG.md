## [4.0.0] - 2026-08-23

### Added
* Added Mosaic Tool (Tool selection in Scene View) which opens a Mosaic Painting window for painting serialized `IntGrid` values directly in the Scene View.
* Added a circular Brush Size, a depth-aware brush preview, continuous drag painting and one Undo operation per stroke.
* Added rectangle fill and clear by holding `Alt` while dragging with LMB or RMB.
* Added `Show IntGrid` (`Shift+R`) to replace the regular Mosaic output with raw IntGrid colors, `Bounds` to display tilemap render bounds and `Randomize` to preview another RuleEngine seed.
* Added `LinkedTilemapLayers`, an editor-only authoring component for painting several `TilemapAuthoring` layers with one palette value. Each linked layer has its own name, color, optional icon and ordered list of target/value operations.
* Added packed rectangle storage for painted IntGrid data on `TilemapAuthoring` and `TilemapTerrainAuthoring`, greatly reducing the serialized size of large filled areas.

## [3.2.0] - 2026-08-15

### Changed
* Add support for `IsGlobal` property in `Tilemap` and `TilemapTerrain`. 
It is by default `true` and preserves the old behavior of `IntGridDefinition` having the same hash as the runtime `IntGrid`.
When the value is false, the runtime `IntGrid` hash is unique per each instance of the `Tilemap` and `TilemapTerrain`, allowing for multiple Tilemaps to use the same `IntGrid` without conflicts.

## [3.1.0] - 2026-07-01

### Changed
* Add support for `UNITY_DISABLE_MANAGED_COMPONENTS` and Entities 6.6.1b/6.7.1a.

## [3.0.0] - 2026-05-30

### Breaking Changes
* Renamed the package.
* Renamed namespaces.
* Users must update using directives and package references.

### Changed
* Removed hard dependency on BovineLabs.Core and BovineLabs as a whole.
* Tightened up all depedencies.

## [2.1.0] - 2026-03-13

### Changed
* Added `IntGridLayerHash` to `TilemapCell`.

### Fixed
* Cell entity spawning for Terrain Tilemaps by separating TilemapTransform.

## [2.0.2] - 2026-03-08

### Changed
* Added support for `RenderingLayerMask`.

## [2.0.1] - 2026-02-25

### Changed
* Added `TilemapCellAuthoring` and `TilemapCell` runtime cell assignment.

# Changelog
## [2.0.0] - 2025-09-12

### Removed
* Dependency on OdinInspector

### Added
* Custom Editor Inspectors for `IntGridDefinition`, `RuleGroup` and `IntGridMatrixWindow` using UI Toolkit
* Automatic data migration from `RuleTransform` into `Transformation`

### Changed
* Greatly improved performance of editor inspectors for `IntGridDefinition`, `RuleGroup` and `IntGridMatrixWindow`
* Visually improved aforementioned editor inspectors

### Fixed
* Several bugs related to incorrect saving of serialized data in `IntGridMatrixWindow`

## [1.4.0] - 2025-08-21

### Breaking Changes
* `TilemapAuthoring` serialization layout has been changed. Rendering data has been reset to default

### Added
* Tilemap Terrain. A special terrain that merges multiple `IntGrid` layers into a single mesh using a custom TerrainShader and blending logic.
There is no limit to the number of layers used in the Terrain, the only limit is the amount of blended `IntGrid` layers,
which can be dynamically adjusted in the authoring script. This Terrain can greatly improve GPU performance in cases where several `IntGrid` layers overlap and share the same rendering data

### Changed
* Internal renaming
* Further optimize `IntGridMeshDataSystem` by removing global parallel execution. Separate layers are processed in parallel, but each layer essentially does only single threaded work
* Moved internal singletons into their own systems

## [1.3.0] - 2025-07-25

### Breaking Changes
* `IntGridMatrix` serialization layout has been changed to support Dual-Grid configuration. All `IntGridMatrix` in `RuleGroup`s have to be reconfigured

### Added
* Dual-Grid system support! For more info check jess::codes video: https://www.youtube.com/watch?v=jEWFSv3ivTg
* Optional Debug assembly with BovineLabs.Core, BobineLabs.Anchor and BovineLabs.Quill integration to visually inspect IntGrid values
* Custom editor drawers for `IntGridMatrix` and `IntGridValueSelector`

### Changed
* Overall code cleanup and simplification with better separation
* 2-3x rule matching performance improvement. Instead of 7 jobs per `IntGrid` + 1 const, now `TilemapRuleEngineSystem` sends only 3 jobs const
* Reduced number of jobs scheduled by `TilemapMeshDataSystem`
* Moved `TilemapRuleEngineSystem` and `TilemapMeshDataSystem` to the end of `PresentationSystemGroup` to not waste jobs space when gameplay logic is performed
* Removed singleton sync points in both `TilemapRuleEngineSystem` and `TilemapMeshDataSystem`
* Mosaic meshes are now updated on the next frame, just like entities instantiated/destroyed by `TilemapRuleEngineSystem`
* IntGrid value is now a struct `IntGridValue`, which is a wrapper for `short` instead of an `int`. This change would lead to 50% less memory required to store serialized IntGrid values
* Improve `TilemapEntityCleanupSystem` performance by using `EntityManager` batch API

### Fixed
* Setting `IntGrid` value to 0 will now remove the entry from the hash map, instead of just setting to 0

## [1.2.0] - 2025-06-22

### Added
* Separate assemblies for Main, Data, Authoring and Editor
* Bounds culling to tilemap layers

### Changed
* Tilemap layers are now rendered using Unity.Entities.Graphics
* ScriptableObjects are now editor only (used for baking)

## [1.1.0] - 2025-06-05

### Added
* `AsParallelWriter()` to TilemapCommandBufferSingleton
* TilemapInitializationSystemGroup and TilemapInitializationSystem

### Changed
* Marked singletons as internal
* TilemapData is now an IEnableableComponent

### Removed
* TilemapCommandBuffer, now merged into TilemapCommandBufferSingleton
* GridBakingSystem, now merged into TilemapInitializationSystem

### Fixed
* GridData changes at runtime now correctly affect tilemap layers
