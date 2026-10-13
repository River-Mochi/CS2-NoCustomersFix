// <copyright file="CommercialProductionReliefSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialProductionReliefSystem.cs
// Purpose: Reduce service production for commercial companies already above
// vanilla's No Customers threshold.

using Colossal.Serialization.Entities;

using CS2Shared.RiverMochi;

using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;

using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Temporarily adjusts ServiceAvailable around vanilla ServiceCompanySystem
    /// so vanilla itself calculates a lower production amount.
    /// </summary>
    public partial class CommercialProductionReliefPrepareSystem : GameSystemBase
    {
        private const float kReliefStartRatio = 0.90f;
        private const float kZeroProductionRatio = 0.95f;

        private EntityQuery m_CompanyQuery;

        private SimulationSystem m_SimulationSystem = null!;

        // Stores the temporary ServiceAvailable amount that must be removed
        // immediately after ServiceCompanySystem finishes.
        private NativeParallelHashMap<Entity, int> m_Boosts;

        private JobHandle m_BoostJob;
        private JobHandle m_RestoreJob;

#if DEBUG
        private int m_UpdateCount;
        private int m_WindowRelievedCompanies;
#endif

        internal NativeParallelHashMap<Entity, int> Boosts => m_Boosts;

        internal JobHandle BoostJob => m_BoostJob;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            // Match vanilla ServiceCompanySystem exactly.
            return 262144 / (EconomyUtils.kCompanyUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_CompanyQuery =
                GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Companies.CommercialCompany>(),
                                ComponentType.ReadWrite<Game.Companies.ServiceAvailable>(),
                                ComponentType.ReadOnly<PrefabRef>(),
                                ComponentType.ReadOnly<Game.Economy.Resources>(),
                                ComponentType.ReadOnly<UpdateFrame>(),
                            },

                        None =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Common.Deleted>(),
                                ComponentType.ReadOnly<Game.Tools.Temp>(),
                            },
                    });

            m_Boosts = new NativeParallelHashMap<Entity, int>(64, Allocator.Persistent);

            RequireForUpdate(m_CompanyQuery);
        }

        protected override void OnDestroy()
        {
            m_RestoreJob.Complete();
            m_BoostJob.Complete();

            if (m_Boosts.IsCreated)
            {
                m_Boosts.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_RestoreJob.Complete();
            m_BoostJob.Complete();

            m_Boosts.Clear();

#if DEBUG
            m_UpdateCount = 0;
            m_WindowRelievedCompanies = 0;
#endif
        }

        protected override void OnUpdate()
        {
            // The previous restore normally finished many frames ago. Complete
            // it before reusing the shared native map.
            m_RestoreJob.Complete();

#if DEBUG
            m_WindowRelievedCompanies += m_Boosts.Count();
            m_UpdateCount++;

            if (m_UpdateCount % 256 == 0)
            {
                LogUtils.Info(
                    "[NCF-PRODUCTION] " +
                    $"updates=256 relieved={m_WindowRelievedCompanies} " +
                    "curve=>90-<95:25%, 95+=0%");

                m_WindowRelievedCompanies = 0;
            }
#endif

            m_Boosts.Clear();

            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    m_SimulationSystem.frameIndex,
                    (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation),
                    16);

            m_CompanyQuery.SetSharedComponentFilter(new UpdateFrame(updateFrame));

            int companyCount = m_CompanyQuery.CalculateEntityCount();

            if (m_Boosts.Capacity < companyCount)
            {
                m_Boosts.Capacity = companyCount;
            }

            ApplyProductionReliefJob job =
                new()
                {
                    m_EntityType = SystemAPI.GetEntityTypeHandle(),

                    m_ServiceAvailableType =
                        SystemAPI.GetComponentTypeHandle<Game.Companies.ServiceAvailable>(),

                    m_PrefabRefType =
                        SystemAPI.GetComponentTypeHandle<PrefabRef>(true),

                    m_ResourcesType =
                        SystemAPI.GetBufferTypeHandle<Game.Economy.Resources>(true),

                    m_ServiceCompanyDatas =
                        SystemAPI.GetComponentLookup<Game.Companies.ServiceCompanyData>(true),

                    m_ProcessDatas =
                        SystemAPI.GetComponentLookup<IndustrialProcessData>(true),

                    m_Boosts = m_Boosts,
                };

            // The company bucket can still be sizable, so keep this scan in Burst.
            // It is deliberately single-threaded because it writes one shared map.
            m_BoostJob = job.ScheduleByRef(m_CompanyQuery, Dependency);
            Dependency = m_BoostJob;
        }

        internal void SetRestoreJob(JobHandle handle)
        {
            m_RestoreJob = handle;
        }

        [BurstCompile]
        private struct ApplyProductionReliefJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            public ComponentTypeHandle<Game.Companies.ServiceAvailable>
                m_ServiceAvailableType;

            [ReadOnly]
            public ComponentTypeHandle<PrefabRef> m_PrefabRefType;

            [ReadOnly]
            public BufferTypeHandle<Game.Economy.Resources> m_ResourcesType;

            [ReadOnly]
            public ComponentLookup<Game.Companies.ServiceCompanyData>
                m_ServiceCompanyDatas;

            [ReadOnly]
            public ComponentLookup<IndustrialProcessData> m_ProcessDatas;

            public NativeParallelHashMap<Entity, int> m_Boosts;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);

                NativeArray<Game.Companies.ServiceAvailable> services =
                    chunk.GetNativeArray(ref m_ServiceAvailableType);

                NativeArray<PrefabRef> prefabs =
                    chunk.GetNativeArray(ref m_PrefabRefType);

                BufferAccessor<Game.Economy.Resources> resources =
                    chunk.GetBufferAccessor(ref m_ResourcesType);

                ChunkEntityEnumerator enumerator =
                    new(
                        useEnabledMask,
                        chunkEnabledMask,
                        chunk.Count);

                while (enumerator.NextEntityIndex(out int i))
                {
                    Entity prefab = prefabs[i].m_Prefab;

                    if (!m_ServiceCompanyDatas.HasComponent(prefab) ||
                        !m_ProcessDatas.HasComponent(prefab))
                    {
                        continue;
                    }

                    Resource resource = m_ProcessDatas[prefab].m_Output.m_Resource;

                    // Hotels keep their separate NCF/vanilla occupancy behavior.
                    if (resource == Resource.NoResource ||
                        resource == Resource.Lodging ||
                        EconomyUtils.IsOfficeResource(resource))
                    {
                        continue;
                    }

                    // Match vanilla NEC's physical-stock requirement.
                    if (EconomyUtils.GetResources(resource, resources[i]) <= 200)
                    {
                        continue;
                    }

                    Game.Companies.ServiceCompanyData serviceData =
                        m_ServiceCompanyDatas[prefab];

                    if (serviceData.m_MaxService <= 0)
                    {
                        continue;
                    }

                    Game.Companies.ServiceAvailable service = services[i];

                    int available =
                        math.clamp(
                            service.m_ServiceAvailable,
                            0,
                            serviceData.m_MaxService);

                    float unusedRatio =
                        (float)available /
                        serviceData.m_MaxService;

                    // Do not touch businesses below vanilla's 90% NEC threshold.
                    if (unusedRatio <= kReliefStartRatio)
                    {
                        continue;
                    }

                    float targetProduction =
                        GetTargetProductionRatio(unusedRatio);

                    // Vanilla's own commercial production falls linearly from
                    // 100% at 80% unused to 0% at 100% unused.
                    //
                    // Feed vanilla the virtual fullness that corresponds to our
                    // desired production percentage. We remove this temporary
                    // offset immediately after ServiceCompanySystem.
                    float virtualUnusedRatio =
                        1f - (0.2f * targetProduction);

                    int virtualAvailable =
                        math.clamp(
                            (int)math.ceil(
                                virtualUnusedRatio *
                                serviceData.m_MaxService),
                            available,
                            serviceData.m_MaxService);

                    int boost = virtualAvailable - available;

                    if (boost <= 0 ||
                        !m_Boosts.TryAdd(entities[i], boost))
                    {
                        continue;
                    }

                    service.m_ServiceAvailable = virtualAvailable;
                    services[i] = service;
                }
            }

            private static float GetTargetProductionRatio(float unusedRatio)
            {
                // Once a business is already in vanilla NEC territory, strongly reduce
                // replenishment so customer demand has time to catch up.
                if (unusedRatio >= kZeroProductionRatio)
                {
                    return 0f;
                }

                return 0.25f;
            }

        }
    }

    public partial class CommercialProductionReliefRestoreSystem : GameSystemBase
    {
        private EntityQuery m_CompanyQuery;

        private SimulationSystem m_SimulationSystem = null!;
        private CommercialProductionReliefPrepareSystem m_PrepareSystem = null!;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 262144 / (EconomyUtils.kCompanyUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_PrepareSystem =
                World.GetOrCreateSystemManaged<CommercialProductionReliefPrepareSystem>();

            m_CompanyQuery =
                GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Companies.CommercialCompany>(),
                                ComponentType.ReadWrite<Game.Companies.ServiceAvailable>(),
                                ComponentType.ReadOnly<UpdateFrame>(),
                            },

                        None =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Common.Deleted>(),
                                ComponentType.ReadOnly<Game.Tools.Temp>(),
                            },
                    });

            RequireForUpdate(m_CompanyQuery);
        }

        protected override void OnUpdate()
        {
            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    m_SimulationSystem.frameIndex,
                    (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation),
                    16);

            m_CompanyQuery.SetSharedComponentFilter(new UpdateFrame(updateFrame));

            RestoreProductionReliefJob job =
                new()
                {
                    m_EntityType = SystemAPI.GetEntityTypeHandle(),

                    m_ServiceAvailableType =
                        SystemAPI.GetComponentTypeHandle<Game.Companies.ServiceAvailable>(),

                    m_Boosts = m_PrepareSystem.Boosts,
                };

            // ServiceCompanySystem must first see the temporary fullness.
            // This job then removes only NCF's temporary offset.
            JobHandle dependencies =
                JobHandle.CombineDependencies(
                    Dependency,
                    m_PrepareSystem.BoostJob);

            JobHandle restoreJob =
                job.ScheduleByRef(
                    m_CompanyQuery,
                    dependencies);

            Dependency = restoreJob;

            m_PrepareSystem.SetRestoreJob(restoreJob);
        }

        [BurstCompile]
        private struct RestoreProductionReliefJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            public ComponentTypeHandle<Game.Companies.ServiceAvailable>
                m_ServiceAvailableType;

            [ReadOnly]
            public NativeParallelHashMap<Entity, int> m_Boosts;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);

                NativeArray<Game.Companies.ServiceAvailable> services =
                    chunk.GetNativeArray(ref m_ServiceAvailableType);

                ChunkEntityEnumerator enumerator =
                    new(
                        useEnabledMask,
                        chunkEnabledMask,
                        chunk.Count);

                while (enumerator.NextEntityIndex(out int i))
                {
                    if (!m_Boosts.TryGetValue(
                            entities[i],
                            out int boost))
                    {
                        continue;
                    }

                    Game.Companies.ServiceAvailable service = services[i];

                    service.m_ServiceAvailable =
                        math.max(
                            0,
                            service.m_ServiceAvailable - boost);

                    services[i] = service;
                }
            }
        }
    }
}
