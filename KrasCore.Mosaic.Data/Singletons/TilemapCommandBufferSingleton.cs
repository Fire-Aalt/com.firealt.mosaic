using System;
using FireAlt.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct TilemapCommandBufferSingleton : IComponentData, IDisposable
    {
        public struct IntGridLayer : IDisposable
        {
            public UnsafeThreadList<SetCommand> SetCommands;
            public bool ClearCommand;

            public IntGridLayer(int capacity, Allocator allocator)
            {
                SetCommands = new UnsafeThreadList<SetCommand>(capacity, allocator);
                ClearCommand = false;
            }

            public void Dispose()
            {
                SetCommands.Dispose();
            }
        }

        public struct ParallelWriter
        {
            [ReadOnly]
            private readonly NativeHashMap<Hash128, IntGridLayer> _layers;
            
            [NativeSetThreadIndex] 
            private int _threadIndex;
            
            internal ParallelWriter(NativeHashMap<Hash128, IntGridLayer> layers)
            {
                _layers = layers;
                _threadIndex = 0;
            }
            
            public void SetIntGridValue(in Hash128 intGridHash, int2 position, IntGridValue intGridValue)
            {
                ref var layer = ref _layers.GetValueAsRef(intGridHash).SetCommands;
                ref var threadList = ref layer.GetUnsafeList(_threadIndex);
                threadList.Add(new SetCommand { Position = position, IntGridValue = intGridValue });
            }
        }
        
        internal NativeHashMap<Hash128, IntGridLayer> IntGridLayers;
        
        internal NativeReference<uint> GlobalSeed;
        internal NativeReference<AABB2D> CullingBounds;
        internal NativeReference<AABB2D> PrevCullingBounds;

        private readonly Allocator _allocator;
        
        public TilemapCommandBufferSingleton(int layersCapacity, Allocator allocator)
        {
            _allocator = allocator;

            IntGridLayers = new NativeHashMap<Hash128, IntGridLayer>(layersCapacity, allocator);
            GlobalSeed = new NativeReference<uint>(allocator);
            CullingBounds = new NativeReference<AABB2D>(allocator);
            PrevCullingBounds = new NativeReference<AABB2D>(allocator);
            
            CullingBounds.Value = new AABB2D { Extents = new float2(float.MaxValue, float.MaxValue) / 2f };
            PrevCullingBounds.Value = CullingBounds.Value;
        }

        public ParallelWriter AsParallelWriter() => new(IntGridLayers);
        
        public void SetIntGridValue(in Hash128 intGridHash, in int2 position, short intGridValue)
        {
            ref var layer = ref IntGridLayers.GetValueAsRef(intGridHash).SetCommands;
            ref var threadList = ref layer.GetUnsafeList(0);
            
            threadList.Add(new SetCommand { Position = position, IntGridValue = intGridValue });
        }

        public void ClearAll()
        {
            foreach (var kvp in IntGridLayers)
            {
                Clear(kvp.Key);
            }
        }
        
        public void Clear(Hash128 intGridHash)
        {
            ref var layer = ref IntGridLayers.GetValueAsRef(intGridHash);
            layer.ClearCommand = true;
        }
        
        public void SetCullingBounds(AABB2D bounds)
        {
            CullingBounds.Value = bounds;
        }

        public void SetGlobalSeed(uint seed)
        {
            GlobalSeed.Value = seed;
        }
        
        internal bool TryRegisterIntGridLayer(in Hash128 intGridHash)
        {
            if (IntGridLayers.ContainsKey(intGridHash)) return false;
            
            var layer = new IntGridLayer(256, _allocator);
            IntGridLayers.Add(intGridHash, layer);
            return true;
        }

        public void Dispose()
        {
            foreach (var layer in IntGridLayers)
            {
                layer.Value.Dispose();
            }
            IntGridLayers.Dispose();
            GlobalSeed.Dispose();
            CullingBounds.Dispose();
            PrevCullingBounds.Dispose();
        }
    }
}