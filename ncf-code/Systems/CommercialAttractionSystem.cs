// <copyright file="CommercialAttractionSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialAttractionSystem.cs
// Purpose: Temporarily favor pressured shops during vanilla seller pathfinding.

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
    internal struct CommercialAttractionRestore
    {
        public Resource m_Resource;
        public float m_BuyCost;
    }

    /// <summary>
    /// Temporarily biases vanilla seller selection toward pressured shops.
    /// </summary>
    public partial class CommercialAttractionPrepareSystem : GameSystemBase
    {
        private const float kPreventiveRatio = 0.82f;
        private const float kStrongRatio = 0.90f;

        private const float kPreventiveDiscount = 0.10f;
        private const float kStrongDiscount = 0.25f;

        private EntityQuery m_CompanyQuery;
        private ResourceSystem m_ResourceSystem = null!;

        private NativeParallelHashMap<Entity, CommercialAttractionRestore> m_RestoreData;
        private NativeArray<int> m_Counts;

        private JobHandle m_AdjustJob;
        private JobHandle m_RestoreJob;

#if DEBUG
        private int m_LogFrameCount;
#endif

        internal NativeParallelHashMap<Entity, CommercialAttractionRestore> RestoreData => m_RestoreData;

        internal JobHandle AdjustJob => m_AdjustJob;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();

            m_CompanyQuery =
                GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Companies.CommercialCompany>(),
                                ComponentType.ReadOnly<Game.Companies.ServiceAvailable>(),
                                ComponentType.ReadOnly<PrefabRef>(),
                                ComponentType.ReadOnly<Game.Economy.Resources>(),
                                ComponentType.ReadWrite<Game.Companies.TradeCost>(),
                            },

                        None =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Common.Deleted>(),
                                ComponentType.ReadOnly<Game.Tools.Temp>(),
                            },
                    });

            m_RestoreData =
                new NativeParallelHashMap<Entity, CommercialAttractionRestore>(
                    64,
                    Allocator.Persistent);

            // 0 = preventive shops, 1 = strong shops.
            m_Counts = new NativeArray<int>(2, Allocator.Persistent);

            RequireForUpdate(m_CompanyQuery);
        }

        protected override void OnDestroy()
        {
            m_RestoreJob.Complete();
            m_AdjustJob.Complete();

            if (m_RestoreData.IsCreated)
            {
                m_RestoreData.Dispose();
            }

            if (m_Counts.IsCreated)
            {
                m_Counts.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_RestoreJob.Complete();
            m_AdjustJob.Complete();

            m_RestoreData.Clear();
            m_Counts[0] = 0;
            m_Counts[1] = 0;

#if DEBUG
            m_LogFrameCount = 0;
#endif
        }

        protected override void OnUpdate()
        {
            // Restore from the previous MainLoop must be finished before the
            // same native storage is reused.
            m_RestoreJob.Complete();

#if DEBUG
            m_LogFrameCount++;

            if (m_LogFrameCount % 256 == 0)
            {
                LogUtils.Info(
                    "[NCF-ATTRACTION] " +
                    $"preventiveShops={m_Counts[0]} " +
                    $"strongShops={m_Counts[1]} " +
                    "discounts=10%/25%");
            }
#endif

            m_RestoreData.Clear();
            m_Counts[0] = 0;
            m_Counts[1] = 0;

            int companyCount = m_CompanyQuery.CalculateEntityCount();

            if (m_RestoreData.Capacity < companyCount)
            {
                m_RestoreData.Capacity = companyCount;
            }

            ApplyAttractionJob job =
                new()
                {
                    m_EntityType = SystemAPI.GetEntityTypeHandle(),

                    m_ServiceAvailableType =
                        SystemAPI.GetComponentTypeHandle<Game.Companies.ServiceAvailable>(true),

                    m_PrefabRefType =
                        SystemAPI.GetComponentTypeHandle<PrefabRef>(true),

                    m_ResourcesType =
                        SystemAPI.GetBufferTypeHandle<Game.Economy.Resources>(true),

                    m_TradeCostType =
                        SystemAPI.GetBufferTypeHandle<Game.Companies.TradeCost>(),

                    m_ServiceCompanyDatas =
                        SystemAPI.GetComponentLookup<Game.Companies.ServiceCompanyData>(true),

                    m_ProcessDatas =
                        SystemAPI.GetComponentLookup<IndustrialProcessData>(true),

                    m_ResourceDatas =
                        SystemAPI.GetComponentLookup<ResourceData>(true),

                    m_ResourcePrefabs = m_ResourceSystem.GetPrefabs(),

                    m_RestoreData = m_RestoreData,
                    m_Counts = m_Counts,
                };

            // This runs every MainLoop around PathfindSetupSystem, so keep the
            // all-commercial scan inside Burst.
            m_AdjustJob = job.ScheduleByRef(m_CompanyQuery, Dependency);
            Dependency = m_AdjustJob;

            m_ResourceSystem.AddPrefabsReader(m_AdjustJob);
        }

        internal void SetRestoreJob(JobHandle restoreJob)
        {
            m_RestoreJob = restoreJob;
        }

        [BurstCompile]
        private struct ApplyAttractionJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            [ReadOnly]
            public ComponentTypeHandle<Game.Companies.ServiceAvailable> m_ServiceAvailableType;

            [ReadOnly]
            public ComponentTypeHandle<PrefabRef> m_PrefabRefType;

            [ReadOnly]
            public BufferTypeHandle<Game.Economy.Resources> m_ResourcesType;

            public BufferTypeHandle<Game.Companies.TradeCost> m_TradeCostType;

            [ReadOnly]
            public ComponentLookup<Game.Companies.ServiceCompanyData> m_ServiceCompanyDatas;

            [ReadOnly]
            public ComponentLookup<IndustrialProcessData> m_ProcessDatas;

            [ReadOnly]
            public ComponentLookup<ResourceData> m_ResourceDatas;

            [ReadOnly]
            public ResourcePrefabs m_ResourcePrefabs;

            public NativeParallelHashMap<Entity, CommercialAttractionRestore> m_RestoreData;

            public NativeArray<int> m_Counts;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities =
                    chunk.GetNativeArray(m_EntityType);

                NativeArray<Game.Companies.ServiceAvailable> services =
                    chunk.GetNativeArray(ref m_ServiceAvailableType);

                NativeArray<PrefabRef> prefabs =
                    chunk.GetNativeArray(ref m_PrefabRefType);

                BufferAccessor<Game.Economy.Resources> resources =
                    chunk.GetBufferAccessor(ref m_ResourcesType);

                BufferAccessor<Game.Companies.TradeCost> tradeCosts =
                    chunk.GetBufferAccessor(ref m_TradeCostType);

                ChunkEntityEnumerator enumerator =
                    new(
                        useEnabledMask,
                        chunkEnabledMask,
                        chunk.Count);

                while (enumerator.NextEntityIndex(out int i))
                {
                    Entity companyPrefab = prefabs[i].m_Prefab;

                    if (!m_ServiceCompanyDatas.HasComponent(companyPrefab) ||
                        !m_ProcessDatas.HasComponent(companyPrefab))
                    {
                        continue;
                    }

                    Game.Companies.ServiceCompanyData serviceData =
                        m_ServiceCompanyDatas[companyPrefab];

                    if (serviceData.m_MaxService <= 0)
                    {
                        continue;
                    }

                    Resource resource =
                        m_ProcessDatas[companyPrefab].m_Output.m_Resource;

                    if (resource == Resource.NoResource ||
                        resource == Resource.Lodging ||
                        EconomyUtils.IsOfficeResource(resource))
                    {
                        continue;
                    }

                    Entity resourcePrefab = m_ResourcePrefabs[resource];

                    if (resourcePrefab == Entity.Null ||
                        !m_ResourceDatas.HasComponent(resourcePrefab) ||
                        m_ResourceDatas[resourcePrefab].m_IsLeisure)
                    {
                        continue;
                    }

                    // A shop without sellable stock cannot satisfy the shopper,
                    // so don't spend pathfinding preference on it.
                    if (EconomyUtils.GetResources(resource, resources[i]) <= 200)
                    {
                        continue;
                    }

                    int available =
                        math.clamp(
                            services[i].m_ServiceAvailable,
                            0,
                            serviceData.m_MaxService);

                    float unusedRatio =
                        (float)available /
                        serviceData.m_MaxService;

                    if (unusedRatio < kPreventiveRatio)
                    {
                        continue;
                    }

                    float marketPrice =
                        EconomyUtils.GetMarketPrice(
                            resource,
                            m_ResourcePrefabs,
                            ref m_ResourceDatas);

                    if (marketPrice <= 0f)
                    {
                        continue;
                    }

                    bool strong = unusedRatio > kStrongRatio;

                    float discount =
                        marketPrice *
                        (strong
                            ? kStrongDiscount
                            : kPreventiveDiscount);

                    DynamicBuffer<Game.Companies.TradeCost> costs =
                        tradeCosts[i];

                    for (int j = 0; j < costs.Length; j++)
                    {
                        Game.Companies.TradeCost tradeCost = costs[j];

                        if (tradeCost.m_Resource != resource)
                        {
                            continue;
                        }

                        CommercialAttractionRestore restore =
                            new()
                            {
                                m_Resource = resource,
                                m_BuyCost = tradeCost.m_BuyCost,
                            };

                        if (!m_RestoreData.TryAdd(entities[i], restore))
                        {
                            break;
                        }

                        // Only PathfindSetup sees this temporary discount.
                        tradeCost.m_BuyCost -= discount;
                        costs[j] = tradeCost;

                        if (strong)
                        {
                            m_Counts[1]++;
                        }
                        else
                        {
                            m_Counts[0]++;
                        }

                        break;
                    }
                }
            }
        }
    }

    public partial class CommercialAttractionRestoreSystem : GameSystemBase
    {
        private EntityQuery m_CompanyQuery;

        private CommercialAttractionPrepareSystem m_PrepareSystem = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrepareSystem =
                World.GetOrCreateSystemManaged<CommercialAttractionPrepareSystem>();

            m_CompanyQuery =
                GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Companies.CommercialCompany>(),
                                ComponentType.ReadWrite<Game.Companies.TradeCost>(),
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
            RestoreAttractionJob job =
                new()
                {
                    m_EntityType = SystemAPI.GetEntityTypeHandle(),

                    m_TradeCostType =
                        SystemAPI.GetBufferTypeHandle<Game.Companies.TradeCost>(),

                    m_RestoreData = m_PrepareSystem.RestoreData,
                };

            // PathfindSetupSystem must finish reading the temporary costs before
            // we restore the real values.
            JobHandle dependencies =
                JobHandle.CombineDependencies(
                    Dependency,
                    m_PrepareSystem.AdjustJob);

            JobHandle restoreJob =
                job.ScheduleByRef(
                    m_CompanyQuery,
                    dependencies);

            Dependency = restoreJob;

            m_PrepareSystem.SetRestoreJob(restoreJob);
        }

        [BurstCompile]
        private struct RestoreAttractionJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            public BufferTypeHandle<Game.Companies.TradeCost> m_TradeCostType;

            [ReadOnly]
            public NativeParallelHashMap<Entity, CommercialAttractionRestore> m_RestoreData;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities =
                    chunk.GetNativeArray(m_EntityType);

                BufferAccessor<Game.Companies.TradeCost> tradeCosts =
                    chunk.GetBufferAccessor(ref m_TradeCostType);

                ChunkEntityEnumerator enumerator =
                    new(
                        useEnabledMask,
                        chunkEnabledMask,
                        chunk.Count);

                while (enumerator.NextEntityIndex(out int i))
                {
                    if (!m_RestoreData.TryGetValue(
                            entities[i],
                            out CommercialAttractionRestore restore))
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Companies.TradeCost> costs =
                        tradeCosts[i];

                    for (int j = 0; j < costs.Length; j++)
                    {
                        Game.Companies.TradeCost tradeCost = costs[j];

                        if (tradeCost.m_Resource != restore.m_Resource)
                        {
                            continue;
                        }

                        tradeCost.m_BuyCost = restore.m_BuyCost;
                        costs[j] = tradeCost;

                        break;
                    }
                }
            }
        }
    }
}
