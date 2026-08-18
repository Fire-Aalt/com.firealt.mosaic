using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(TilemapInitializationSystemGroup))]
    public partial struct EntityInitializationSystem : ISystem
    {
        private NativeList<EntityCommand> _commandsList;
        private NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> _intGridLayers;
        
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.CompleteDependencyBeforeRO<TilemapIntGridSingleton>();
            var dataSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW;
            _commandsList = dataSingleton.EntityCommands.List;
            _intGridLayers = dataSingleton.IntGridLayers;
            
            if (_commandsList.Length == 0) return;
            _commandsList.Sort(new DeferredCommandComparer());
            
            var beginBatchIndex = 0;
            for (int i = 0; i < _commandsList.Length - 1; i++)
            {
                var currentCommand = _commandsList[i];
                var nextCommand = _commandsList[i + 1];
                if (currentCommand.SrcEntity == nextCommand.SrcEntity) continue;
                
                UploadBatch(ref state, beginBatchIndex, i, currentCommand.SrcEntity);
                beginBatchIndex = i + 1;
            }
            UploadBatch(ref state, beginBatchIndex, _commandsList.Length - 1, _commandsList[^1].SrcEntity);
            
            dataSingleton.EntityCommands.Clear();
        }
        
        private void UploadBatch(ref SystemState state, int beginIndex, int endIndex, in Entity srcEntity)
        {
            var length = endIndex - beginIndex + 1;
            if (length <= 0 || !state.EntityManager.Exists(srcEntity)) return;
            
            var srcTransform = state.EntityManager.GetComponentData<LocalTransform>(srcEntity);
            var hasTilemapCellComponent = state.EntityManager.HasComponent<TilemapCell>(srcEntity);
            
            var instances = new NativeArray<Entity>(length, Allocator.Temp);
            state.EntityManager.Instantiate(srcEntity, instances);

            for (var i = 0; i < instances.Length; i++)
            {
                var currentCommand = _commandsList[beginIndex + i];
                var instance = instances[i];
                    
                var cell = currentCommand.Position;

                ref var dataLayer = ref _intGridLayers.GetValueAsRef(currentCommand.IntGridHash);
                var rendererData = state.EntityManager.GetComponentData<TilemapTransform>(dataLayer.IntGridEntity);
                
                state.EntityManager.SetComponentData(instance, new LocalTransform
                {
                    Position = MosaicUtils.ToWorldSpace(cell, rendererData) + srcTransform.Position, 
                    Scale = srcTransform.Scale,
                    Rotation = srcTransform.Rotation
                });
                if (hasTilemapCellComponent)
                {
                    state.EntityManager.SetComponentData(instance, new TilemapCell 
                    { 
                        IntGridLayerHash = currentCommand.IntGridHash,
                        Cell = cell 
                    });
                }
                
                dataLayer.SpawnedEntities[cell] = instance;
            }
        }
    }
}
