// <copyright file="CommercialDemandDiagnosticSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Passive diagnostics compiled only into NCFPerfTest builds.
#if NCF_PERF_TEST
using System;
using System.Diagnostics;

using Colossal.Serialization.Entities;
using CS2Shared.RiverMochi;

using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;

using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace NoCustomersFix.Systems
{
    public partial class CommercialDemandDiagnosticSystem : GameSystemBase
    {
        private SimulationSystem m_SimulationSystem = null!;
        private TimeSystem m_TimeSystem = null!;
        private CountCompanyDataSystem m_CompanyDataSystem = null!;
        private CommercialDemandSystem m_DemandSystem = null!;
        private EntityQuery m_PropertyQuery;
        private int m_LastHour = -1;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 256;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_CompanyDataSystem = World.GetOrCreateSystemManaged<CountCompanyDataSystem>();
            m_DemandSystem = World.GetOrCreateSystemManaged<CommercialDemandSystem>();

            // Match vanilla CommercialDemandSystem's eligible property filters.
            m_PropertyQuery = GetEntityQuery(
                ComponentType.ReadOnly<CommercialProperty>(),
                ComponentType.ReadOnly<PropertyOnMarket>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Abandoned>(),
                ComponentType.Exclude<Destroyed>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Condemned>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_LastHour = -1;
        }

        protected override void OnUpdate()
        {
            int hour = ((int)(m_TimeSystem.normalizedTime * 24f)) % 24;
            if (hour == m_LastHour)
            {
                return;
            }

            m_LastHour = hour;
            long started = Stopwatch.GetTimestamp();

            // Read the actual vanilla resource-demand and company totals.
            CountCompanyDataSystem.CommercialCompanyDatas companies =
                m_CompanyDataSystem.GetCommercialCompanyDatas(out JobHandle companyDeps);
            NativeArray<int> companyDemand =
                m_DemandSystem.GetResourceDemands(out JobHandle demandDeps);
            NativeArray<int> buildingDemand =
                m_DemandSystem.GetBuildingDemands(out JobHandle buildingDeps);

            JobHandle.CombineDependencies(
                companyDeps,
                JobHandle.CombineDependencies(demandDeps, buildingDeps)).Complete();

            using NativeArray<int> warnings =
                new(EconomyUtils.ResourceCount, Allocator.Temp);
            using NativeArray<int> vacant =
                new(EconomyUtils.ResourceCount, Allocator.Temp);

            ComponentLookup<IndustrialProcessData> processes =
                SystemAPI.GetComponentLookup<IndustrialProcessData>(true);
            ComponentLookup<BuildingPropertyData> buildingProperties =
                SystemAPI.GetComponentLookup<BuildingPropertyData>(true);
            ComponentLookup<CommercialCompany> commercialCompanies =
                SystemAPI.GetComponentLookup<CommercialCompany>(true);
            ComponentLookup<PrefabRef> prefabs =
                SystemAPI.GetComponentLookup<PrefabRef>(true);
            BufferLookup<Renter> renters =
                SystemAPI.GetBufferLookup<Renter>(true);

            int warningCompanies = 0;
            foreach ((
                RefRO<CompanyNotifications> notification,
                RefRO<PrefabRef> prefab) in
                SystemAPI.Query<RefRO<CompanyNotifications>, RefRO<PrefabRef>>()
                    .WithAll<CommercialCompany, ServiceAvailable>()
                    .WithNone<Deleted, Temp>())
            {
                if (notification.ValueRO.m_NoCustomersEntity == Entity.Null ||
                    !processes.HasComponent(prefab.ValueRO.m_Prefab))
                {
                    continue;
                }

                Resource resource =
                    processes[prefab.ValueRO.m_Prefab].m_Output.m_Resource;
                if (resource == Resource.NoResource)
                {
                    continue;
                }

                warnings[EconomyUtils.GetResourceIndex(resource)]++;
                warningCompanies++;
            }

            int propertiesChecked = 0;
            using (NativeArray<Entity> properties =
                m_PropertyQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < properties.Length; i++)
                {
                    Entity property = properties[i];
                    propertiesChecked++;

                    if (!prefabs.HasComponent(property) ||
                        !buildingProperties.HasComponent(prefabs[property].m_Prefab))
                    {
                        continue;
                    }

                    bool occupied = false;
                    if (renters.HasBuffer(property))
                    {
                        DynamicBuffer<Renter> propertyRenters = renters[property];
                        for (int j = 0; j < propertyRenters.Length; j++)
                        {
                            if (commercialCompanies.HasComponent(
                                propertyRenters[j].m_Renter))
                            {
                                occupied = true;
                                break;
                            }
                        }
                    }

                    if (occupied)
                    {
                        continue;
                    }

                    Resource allowedSold =
                        buildingProperties[prefabs[property].m_Prefab].m_AllowedSold;
                    ResourceIterator iterator = ResourceIterator.GetIterator();
                    while (iterator.Next())
                    {
                        if ((allowedSold & iterator.resource) != Resource.NoResource)
                        {
                            vacant[EconomyUtils.GetResourceIndex(iterator.resource)]++;
                        }
                    }
                }
            }

            int loggedResources = 0;
            ResourceIterator resources = ResourceIterator.GetIterator();
            while (resources.Next())
            {
                Resource resource = resources.resource;
                if (!EconomyUtils.IsCommercialResource(resource))
                {
                    continue;
                }

                int index = EconomyUtils.GetResourceIndex(resource);
                int companyCount = companies.m_ServiceCompanies[index];
                int unplaced = companies.m_ServicePropertyless[index];
                int shops = Math.Max(0, companyCount - unplaced);

                if (companyCount == 0 && companyDemand[index] == 0 &&
                    buildingDemand[index] == 0 && vacant[index] == 0)
                {
                    continue;
                }

                int available = companies.m_CurrentAvailables[index];
                int capacity = companies.m_TotalAvailables[index];
                float unused = capacity > 0 ? 100f * available / capacity : 0f;

                LogUtils.Info(
                    "[NCF-DEMAND] " +
                    $"mode={(Mod.BalancingEnabled ? "ON" : "OFF")} " +
                    $"hour={hour:D2} resource={resource} " +
                    $"shops={shops} warnings={warnings[index]} " +
                    $"unused={unused:F1}% " +
                    $"companyDemand={companyDemand[index]} " +
                    $"buildingDemand={buildingDemand[index]} " +
                    $"vacant={vacant[index]} unplaced={unplaced}");
                loggedResources++;
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - started) *
                1000.0 / Stopwatch.Frequency;

            LogUtils.Info(
                "[NCF-DEMAND-SUMMARY] " +
                $"mode={(Mod.BalancingEnabled ? "ON" : "OFF")} " +
                $"hour={hour:D2} frame={m_SimulationSystem.frameIndex} " +
                $"resources={loggedResources} warnings={warningCompanies} " +
                $"propertiesChecked={propertiesChecked} elapsedMs={elapsedMs:F2}");
        }
    }
}
#endif
