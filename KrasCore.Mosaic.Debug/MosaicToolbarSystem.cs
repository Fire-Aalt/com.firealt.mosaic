using BovineLabs.Anchor.Debug.Toolbar;
using FireAlt.Mosaic.Data;
using FireAlt.Core.Quill;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;
using Random = Unity.Mathematics.Random;
using TerrainLayer = FireAlt.Mosaic.Data.TerrainLayer;

#if BL_QUILL
using BovineLabs.Quill;
#endif

namespace FireAlt.Mosaic.Debug
{
    [UpdateInGroup(typeof(ToolbarSystemGroup))]
    public partial struct MosaicToolbarSystem : ISystem, ISystemStartStop
    {
        private ToolbarHelper<MosaicToolbarView, MosaicToolbarViewModel, MosaicToolbarViewModel.Data> _toolbar;
        
        private NativeList<MosaicToolbarViewModel.Data.IntGridName> _intGridsBuffer;
        
        public void OnCreate(ref SystemState state)
        {
            _toolbar = new ToolbarHelper<MosaicToolbarView, MosaicToolbarViewModel, MosaicToolbarViewModel.Data>(ref state, "Mosaic");
            
            _intGridsBuffer = new NativeList<MosaicToolbarViewModel.Data.IntGridName>(Allocator.Persistent);
            
            state.RequireForUpdate<TilemapIntGridSingleton>();
        }
        
        public void OnDestroy(ref SystemState state)
        {
            _intGridsBuffer.Dispose();
        }
        
        public void OnStartRunning(ref SystemState state)
        {
            _toolbar.Load();
        }

        public void OnStopRunning(ref SystemState state)
        {
            _toolbar.Unload();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            ref var data = ref _toolbar.Binding;
            if (_toolbar.IsVisible())
            {
                _intGridsBuffer.Clear();
                foreach (var tilemapDataRO in SystemAPI.Query<RefRO<IntGridData>>())
                {
                    _intGridsBuffer.Add(new MosaicToolbarViewModel.Data.IntGridName
                    {
                        IntGridHash = tilemapDataRO.ValueRO.Hash,
                        Name = tilemapDataRO.ValueRO.DebugName,
                    });
                }
                _intGridsBuffer.Sort();

                data.IntGrids = _intGridsBuffer;
            }
            
            var singleton = SystemAPI.GetSingleton<TilemapIntGridSingleton>();
            
#if BL_QUILL
            var drawer = SystemAPI.GetSingleton<DrawSystem.Singleton>().CreateDrawer();
            if (!drawer.IsEnabled || data.IntGridValues.Value.Length == 0)
            {
                return;
            }
            
            state.Dependency = new DrawIntGridJob
            {
                Drawer = drawer,
                TilemapTransformLookup = SystemAPI.GetComponentLookup<TilemapTransform>(true),
                TilemapTerrainLayerLookup = SystemAPI.GetComponentLookup<TerrainLayer>(true),
                SelectedIndices = data.IntGridValues.Value.ToArray(state.WorldUpdateAllocator),
                IntGridsBuffer = _intGridsBuffer.AsArray(),
                IntGridLayers = singleton.IntGridLayers,
            }.ScheduleParallel(data.IntGridValues.Value.Length, 1, state.Dependency);
#endif
        }

#if BL_QUILL
        [BurstCompile]
        private struct DrawIntGridJob : IJobFor
        {
            public Drawer Drawer;

            [ReadOnly]
            public ComponentLookup<TilemapTransform> TilemapTransformLookup;
            [ReadOnly]
            public ComponentLookup<TerrainLayer> TilemapTerrainLayerLookup;
            
            [ReadOnly]
            public NativeArray<int> SelectedIndices;
            [ReadOnly]
            public NativeArray<MosaicToolbarViewModel.Data.IntGridName> IntGridsBuffer;
            
            [ReadOnly]
            public NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> IntGridLayers;
            
            public void Execute(int index)
            {
                var key = IntGridsBuffer[SelectedIndices[index]].IntGridHash;
                var layer = IntGridLayers[key];

                TilemapTransform rendererData;
                if (layer.IsTerrainLayer)
                {
                    var terrainEntity = TilemapTerrainLayerLookup[layer.IntGridEntity].TerrainEntity;
                    rendererData = TilemapTransformLookup[terrainEntity];
                }
                else
                {
                    rendererData = TilemapTransformLookup[layer.IntGridEntity];
                }

                var rnd = new Random((uint)key.GetHashCode());
                var rgb = rnd.NextFloat3();
                var color = new Color(rgb.x, rgb.y, rgb.z, 1f);
                    
                foreach (var kvPair in layer.IntGrid)
                {
                    var pos = kvPair.Key;
                    var value = kvPair.Value.value;

                    var str = new FixedString32Bytes();
                    str.Append(value);
                    
                    var cellCenter = MosaicUtils.ToWorldSpace((float2)pos + 0.5f, rendererData);
                    Drawer.SolidSquareXZ(cellCenter, MosaicUtils.ApplySwizzle(rendererData.CellSize, rendererData.Swizzle).xy, color);
                    Drawer.Text32(cellCenter, str, Color.black, size: 32f);
                }
            }
        }
    }
#endif
}