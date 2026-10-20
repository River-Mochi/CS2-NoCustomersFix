// <copyright file="CommercialProductionReliefSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialProductionReliefSystem.cs
// Purpose: Prevent excessive commercial service production before NEC develops.

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
    internal struct CommercialProductionEmployeeRestore
    {
        public Entity m_Company;
        public int m_Index;
        public Game.Companies.Employee m_Employee;
    }

    /// <summary>
    /// Temporarily reduces workforce seen by vanilla production calculation.
    /// </summary>
    public partial class CommercialProductionReliefPrepareSystem : GameSystemBase
    {
        private const float kPreventiveRatio = 0.82f;
        private const float kStrongRatio = 0.90f;

        private EntityQuery m_CompanyQuery;
        private SimulationSystem m_SimulationSystem = null!;

        private NativeQueue<CommercialProductionEmployeeRestore> m_RestoreEntries;
#if DEBUG
        private NativeArray<int> m_Counts;
#endif

        private JobHandle m_AdjustJob;
        private JobHandle m_RestoreJob;

#if DEBUG
        private int m_UpdateCount;
        private int m_WindowPreventiveCompanies;
        private int m_WindowStrongCompanies;
#endif

        internal NativeQueue<CommercialProductionEmployeeRestore> RestoreEntries => m_RestoreEntries;

        internal JobHandle AdjustJob => m_AdjustJob;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
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
                                ComponentType.ReadOnly<Game.Companies.ServiceAvailable>(),
                                ComponentType.ReadOnly<PrefabRef>(),
                                ComponentType.ReadOnly<Game.Economy.Resources>(),
                                ComponentType.ReadWrite<Game.Companies.Employee>(),
                                ComponentType.ReadOnly<UpdateFrame>(),
                            },

                        None =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Common.Deleted>(),
                                ComponentType.ReadOnly<Game.Tools.Temp>(),
                            },
                    });

            m_RestoreEntries =
                new NativeQueue<CommercialProductionEmployeeRestore>(
                    Allocator.Persistent);

#if DEBUG
            // 0 = preventive 82–90%, 1 = strong >90%.
            m_Counts = new NativeArray<int>(2, Allocator.Persistent);
#endif

            RequireForUpdate(m_CompanyQuery);
        }

        protected override void OnDestroy()
        {
            m_RestoreJob.Complete();
            m_AdjustJob.Complete();

            if (m_RestoreEntries.IsCreated)
            {
                m_RestoreEntries.Dispose();
            }

#if DEBUG
            if (m_Counts.IsCreated)
            {
                m_Counts.Dispose();
            }
#endif

            base.OnDestroy();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_RestoreJob.Complete();
            m_AdjustJob.Complete();

            m_RestoreEntries.Clear();
#if DEBUG
            m_Counts[0] = 0;
            m_Counts[1] = 0;
#endif

#if DEBUG
            m_UpdateCount = 0;
            m_WindowPreventiveCompanies = 0;
            m_WindowStrongCompanies = 0;
#endif
        }

        protected override void OnUpdate()
        {
            // The previous ServiceCompany pass must be completely restored
            // before the shared queue is reused.
            m_RestoreJob.Complete();

#if DEBUG
            m_WindowPreventiveCompanies += m_Counts[0];
            m_WindowStrongCompanies += m_Counts[1];

            m_UpdateCount++;

            if (m_UpdateCount % 256 == 0)
            {
                LogUtils.Info(
                    "[NCF-PRODUCTION] " +
                    $"updates=256 preventive={m_WindowPreventiveCompanies} " +
                    $"strong={m_WindowStrongCompanies} " +
                    "curve=82-90:~50% workforce, >90:0% workforce");

                m_WindowPreventiveCompanies = 0;
                m_WindowStrongCompanies = 0;
            }
#endif

            m_RestoreEntries.Clear();
#if DEBUG
            m_Counts[0] = 0;
            m_Counts[1] = 0;
#endif

            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    m_SimulationSystem.frameIndex,
                    (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation),
                    16);

            m_CompanyQuery.SetSharedComponentFilter(new UpdateFrame(updateFrame));

            ApplyProductionReliefJob job =
                new()
                {
                    m_EntityType = SystemAPI.GetEntityTypeHandle(),

                    m_ServiceAvailableType =
                        SystemAPI.GetComponentTypeHandle<Game.Companies.ServiceAvailable>(true),

                    m_PrefabRefType =
                        SystemAPI.GetComponentTypeHandle<PrefabRef>(true),

                    m_ResourcesType =
                        SystemAPI.GetBufferTypeHandle<Game.Economy.Resources>(true),

                    m_EmployeeType =
                        SystemAPI.GetBufferTypeHandle<Game.Companies.Employee>(),

                    m_ServiceCompanyDatas =
                        SystemAPI.GetComponentLookup<Game.Companies.ServiceCompanyData>(true),

                    m_ProcessDatas =
                        SystemAPI.GetComponentLookup<IndustrialProcessData>(true),

                    m_RestoreEntries = m_RestoreEntries,
#if DEBUG
                    m_Counts = m_Counts,
#endif
                };

            // The company bucket scan can be large; keep it inside Burst.
            m_AdjustJob = job.ScheduleByRef(m_CompanyQuery, Dependency);
            Dependency = m_AdjustJob;
        }

        internal void SetRestoreJob(JobHandle restoreJob)
        {
            m_RestoreJob = restoreJob;
        }

        [BurstCompile]
        private struct ApplyProductionReliefJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            [ReadOnly]
            public ComponentTypeHandle<Game.Companies.ServiceAvailable> m_ServiceAvailableType;

            [ReadOnly]
            public ComponentTypeHandle<PrefabRef> m_PrefabRefType;

            [ReadOnly]
            public BufferTypeHandle<Game.Economy.Resources> m_ResourcesType;

            public BufferTypeHandle<Game.Companies.Employee> m_EmployeeType;

            [ReadOnly]
            public ComponentLookup<Game.Companies.ServiceCompanyData> m_ServiceCompanyDatas;

            [ReadOnly]
            public ComponentLookup<IndustrialProcessData> m_ProcessDatas;

            public NativeQueue<CommercialProductionEmployeeRestore> m_RestoreEntries;

#if DEBUG
            public NativeArray<int> m_Counts;
#endif

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

                BufferAccessor<Game.Companies.Employee> employees =
                    chunk.GetBufferAccessor(ref m_EmployeeType);

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

                    Resource resource =
                        m_ProcessDatas[prefab].m_Output.m_Resource;

                    // Hotels also receive production relief.
                    // HotelBalanceSystem separately handles room occupancy.
                    if (resource == Resource.NoResource ||
                        EconomyUtils.IsOfficeResource(resource))
                    {
                        continue;
                    }


                    // Match vanilla's physical-stock side of NEC.
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

                    bool strong = unusedRatio > kStrongRatio;

                    DynamicBuffer<Game.Companies.Employee> companyEmployees =
                        employees[i];

                    if (companyEmployees.Length == 0)
                    {
                        continue;
                    }

                    bool changed = false;

                    for (int j = 0; j < companyEmployees.Length; j++)
                    {
                        // In the preventive range, vanilla sees roughly half
                        // the workforce. Strong NEC pressure sees none.
                        if (!strong && (j & 1) == 0)
                        {
                            continue;
                        }

                        Game.Companies.Employee employee =
                            companyEmployees[j];

                        if (employee.m_Worker == Entity.Null)
                        {
                            continue;
                        }

                        m_RestoreEntries.Enqueue(
                            new CommercialProductionEmployeeRestore
                            {
                                m_Company = entities[i],
                                m_Index = j,
                                m_Employee = employee,
                            });

                        // GetWorkforce ignores an Employee whose worker is not
                        // a valid Citizen. The real entry is restored immediately
                        // after ServiceCompanySystem finishes.
                        employee.m_Worker = Entity.Null;
                        companyEmployees[j] = employee;

                        changed = true;
                    }

                    if (!changed)
                    {
                        continue;
                    }

#if DEBUG
                    if (strong)
                    {
                        m_Counts[1]++;
                    }
                    else
                    {
                        m_Counts[0]++;
                    }
#endif
                }
            }
        }
    }

    public partial class CommercialProductionReliefRestoreSystem : GameSystemBase
    {
        private CommercialProductionReliefPrepareSystem m_PrepareSystem = null!;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 262144 / (EconomyUtils.kCompanyUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrepareSystem =
                World.GetOrCreateSystemManaged<CommercialProductionReliefPrepareSystem>();
        }

        protected override void OnUpdate()
        {
            RestoreProductionReliefJob job =
                new()
                {
                    m_Employees =
                        SystemAPI.GetBufferLookup<Game.Companies.Employee>(),

                    m_RestoreEntries =
                        m_PrepareSystem.RestoreEntries,
                };

            // Dependency also includes vanilla's Employee-buffer reader, so
            // restoration happens only after ServiceCompanySystem is finished.
            JobHandle dependencies =
                JobHandle.CombineDependencies(
                    Dependency,
                    m_PrepareSystem.AdjustJob);

            JobHandle restoreJob =
                job.Schedule(dependencies);

            Dependency = restoreJob;

            m_PrepareSystem.SetRestoreJob(restoreJob);
        }

        [BurstCompile]
        private struct RestoreProductionReliefJob : IJob
        {
            public BufferLookup<Game.Companies.Employee> m_Employees;

            public NativeQueue<CommercialProductionEmployeeRestore> m_RestoreEntries;

            public void Execute()
            {
                while (m_RestoreEntries.TryDequeue(
                           out CommercialProductionEmployeeRestore restore))
                {
                    if (!m_Employees.HasBuffer(restore.m_Company))
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Companies.Employee> employees =
                        m_Employees[restore.m_Company];

                    if (restore.m_Index < 0 ||
                        restore.m_Index >= employees.Length)
                    {
                        continue;
                    }

                    employees[restore.m_Index] = restore.m_Employee;
                }
            }
        }
    }
}
