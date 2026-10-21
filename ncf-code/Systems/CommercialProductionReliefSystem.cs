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
        private const float kPreventiveRatio = 0.80f;
        private const float kStrongRatio = 0.84f;

        private EntityQuery m_CompanyQuery;
        private SimulationSystem m_SimulationSystem = null!;

        private NativeQueue<CommercialProductionEmployeeRestore> m_RestoreEntries;
#if DEBUG || NCF_PERF_TEST
        // Per-pass Burst counters: preventive companies, strong companies,
        // masked employees, restored employees, skipped restorations.
        private NativeArray<int> m_Counts;
#endif

        private JobHandle m_AdjustJob;
        private JobHandle m_RestoreJob;

#if DEBUG
        private int m_UpdateCount;
        private int m_WindowPreventiveCompanies;
        private int m_WindowStrongCompanies;
#endif

#if DEBUG || NCF_PERF_TEST
        private int m_PerfProductionUpdates;
        private long m_PerfProductionWaitTicks;
        private long m_PerfProductionPrepareTicks;
        private long m_PerfPreventiveCompanies;
        private long m_PerfStrongCompanies;
        private long m_PerfMaskedEmployees;
        private long m_PerfRestoredEmployees;
        private long m_PerfSkippedRestorations;
#endif

        internal NativeQueue<CommercialProductionEmployeeRestore> RestoreEntries => m_RestoreEntries;

        internal JobHandle AdjustJob => m_AdjustJob;
#if DEBUG || NCF_PERF_TEST
        internal NativeArray<int> PerfCounters => m_Counts;
#endif

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

#if DEBUG || NCF_PERF_TEST
            // 0 = preventive companies; 1 = strong companies;
            // 2 = masked employees; 3 = restored; 4 = skipped restores.
            m_Counts = new NativeArray<int>(5, Allocator.Persistent);
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

#if DEBUG || NCF_PERF_TEST
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
#if DEBUG || NCF_PERF_TEST
            for (int index = 0; index < m_Counts.Length; index++)
            {
                m_Counts[index] = 0;
            }
#endif

#if DEBUG
            m_UpdateCount = 0;
            m_WindowPreventiveCompanies = 0;
            m_WindowStrongCompanies = 0;
#endif
#if DEBUG || NCF_PERF_TEST
            m_PerfProductionUpdates = 0;
            m_PerfProductionWaitTicks = 0;
            m_PerfProductionPrepareTicks = 0;
            m_PerfPreventiveCompanies = 0;
            m_PerfStrongCompanies = 0;
            m_PerfMaskedEmployees = 0;
            m_PerfRestoredEmployees = 0;
            m_PerfSkippedRestorations = 0;
#endif
        }

        protected override void OnUpdate()
        {
#if DEBUG || NCF_PERF_TEST
            long perfStarted = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            // The previous ServiceCompany pass must be completely restored
            // before the shared queue is reused.
            m_RestoreJob.Complete();
#if DEBUG || NCF_PERF_TEST
            long perfWaitFinished = System.Diagnostics.Stopwatch.GetTimestamp();

            // Jobs are complete: previous pass counters are safe to read.
            m_PerfPreventiveCompanies += m_Counts[0];
            m_PerfStrongCompanies += m_Counts[1];
            m_PerfMaskedEmployees += m_Counts[2];
            m_PerfRestoredEmployees += m_Counts[3];
            m_PerfSkippedRestorations += m_Counts[4];
#endif

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
                    "curve=retail 80-84:~50%, hotel 80-84:~40%, >=84:0% workforce");

                m_WindowPreventiveCompanies = 0;
                m_WindowStrongCompanies = 0;
            }
#endif

            m_RestoreEntries.Clear();
#if DEBUG || NCF_PERF_TEST
            for (int index = 0; index < m_Counts.Length; index++)
            {
                m_Counts[index] = 0;
            }
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
#if DEBUG || NCF_PERF_TEST
                    m_Counts = m_Counts,
#endif
                };

            // The company bucket scan can be large; keep it inside Burst.
            m_AdjustJob = job.ScheduleByRef(m_CompanyQuery, Dependency);
            Dependency = m_AdjustJob;

#if DEBUG || NCF_PERF_TEST
            long perfFinished = System.Diagnostics.Stopwatch.GetTimestamp();
            m_PerfProductionUpdates++;
            m_PerfProductionWaitTicks += perfWaitFinished - perfStarted;
            m_PerfProductionPrepareTicks += perfFinished - perfWaitFinished;

            if (m_PerfProductionUpdates >= 256)
            {
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                LogUtils.Info(
                    "[NCF-PERF-PRODUCTION] " +
                    $"updates={m_PerfProductionUpdates} " +
                    $"restoreWaitMs={(m_PerfProductionWaitTicks * ms):F1} " +
                    $"prepareScheduleMs={(m_PerfProductionPrepareTicks * ms):F1} " +
                    $"preventiveAt={kPreventiveRatio:F2} strongAt={kStrongRatio:F2} " +
                    $"preventiveCompanies={m_PerfPreventiveCompanies} " +
                    $"strongCompanies={m_PerfStrongCompanies} " +
                    $"maskedEmployees={m_PerfMaskedEmployees} " +
                    $"restoredEmployees={m_PerfRestoredEmployees} " +
                    $"skippedRestorations={m_PerfSkippedRestorations}");
                m_PerfProductionUpdates = 0;
                m_PerfProductionWaitTicks = 0;
                m_PerfProductionPrepareTicks = 0;
                m_PerfPreventiveCompanies = 0;
                m_PerfStrongCompanies = 0;
                m_PerfMaskedEmployees = 0;
                m_PerfRestoredEmployees = 0;
                m_PerfSkippedRestorations = 0;
            }
#endif
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

#if DEBUG || NCF_PERF_TEST
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

                    bool isHotel = resource == Resource.Lodging;
                    bool strong = unusedRatio >= kStrongRatio;

                    DynamicBuffer<Game.Companies.Employee> companyEmployees =
                        employees[i];

                    if (companyEmployees.Length == 0)
                    {
                        continue;
                    }

                    bool changed = false;

                    for (int j = 0; j < companyEmployees.Length; j++)
                    {
                        // Preventive: ~40% hotel workforce, ~50% retail.
                        // Strong: temporarily stop all Service production.
                        if (!strong)
                        {
                            bool keepWorker = isHotel
                                ? (j % 10) < 4
                                : (j & 1) == 0;

                            if (keepWorker)
                            {
                                continue;
                            }
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
#if DEBUG || NCF_PERF_TEST
                        m_Counts[2]++;
#endif
                        changed = true;
                    }

                    if (!changed)
                    {
                        continue;
                    }

#if DEBUG || NCF_PERF_TEST
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
#if DEBUG || NCF_PERF_TEST
                    m_Counts = m_PrepareSystem.PerfCounters,
#endif
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
#if DEBUG || NCF_PERF_TEST
            public NativeArray<int> m_Counts;
#endif

            public void Execute()
            {
                while (m_RestoreEntries.TryDequeue(
                           out CommercialProductionEmployeeRestore restore))
                {
                    if (!m_Employees.HasBuffer(restore.m_Company))
                    {
#if DEBUG || NCF_PERF_TEST
                        m_Counts[4]++;
#endif
                        continue;
                    }

                    DynamicBuffer<Game.Companies.Employee> employees =
                        m_Employees[restore.m_Company];

                    if (restore.m_Index < 0 ||
                        restore.m_Index >= employees.Length)
                    {
#if DEBUG || NCF_PERF_TEST
                        m_Counts[4]++;
#endif
                        continue;
                    }

                    employees[restore.m_Index] = restore.m_Employee;
#if DEBUG || NCF_PERF_TEST
                    m_Counts[3]++;
#endif
                }
            }
        }
    }
}
