// <copyright file="LandValueDiagnosticSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Developer-only, read-only diagnostics. No land-value or company mutations.
#if NCF_PERF_TEST
using System;
using System.Diagnostics;
using Colossal.Serialization.Entities;
using CS2Shared.RiverMochi;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace NoCustomersFix.Systems
{
    public partial class LandValueDiagnosticSystem : GameSystemBase
    {
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_CompanyQuery;
        private EntityQuery m_ParametersQuery;
        private LandValueSystem m_LandValueSystem = null!;
        private TimeSystem m_TimeSystem = null!;
        private SimulationSystem m_SimulationSystem = null!;
        private int m_LastHour = -1;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 256;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_LandValueSystem = World.GetOrCreateSystemManaged<LandValueSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_ParametersQuery = GetEntityQuery(
                ComponentType.ReadOnly<LandValueParameterData>());

            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<LandValue>(),
                    ComponentType.ReadOnly<ResourceAvailability>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // Mirrors LandValueInfoviewUISystem's Average Land Value query.
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<BuildingCondition>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_CompanyQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CommercialCompany>(),
                    ComponentType.ReadOnly<ServiceAvailable>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_LastHour = -1;
        }

        protected override void OnUpdate()
        {
            if (m_ParametersQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int minuteOfDay = (int)(m_TimeSystem.normalizedTime * 1440f);
            int hour = (minuteOfDay / 60) % 24;
            if (hour == m_LastHour)
            {
                return;
            }

            m_LastHour = hour;
            long start = Stopwatch.GetTimestamp();
            LandValueParameterData parameters =
                m_ParametersQuery.GetSingleton<LandValueParameterData>();

            // The map is owned by the vanilla game; finish its writer before reading.
            NativeArray<LandValueCell> map =
                m_LandValueSystem.GetMap(true, out JobHandle mapDependencies);
            mapDependencies.Complete();

            double mapSum = 0d;
            float mapMax = 0f;
            int highCells = 0;
            if (map.IsCreated)
            {
                for (int i = 0; i < map.Length; i++)
                {
                    float value = map[i].m_LandValue;
                    mapSum += value;
                    mapMax = Math.Max(mapMax, value);
                    if (value > 100000f)
                    {
                        highCells++;
                    }
                }
            }

            // Diagnostic reads, once per game hour only.
            EntityManager.CompleteDependencyBeforeRO<LandValue>();
            EntityManager.CompleteDependencyBeforeRO<ResourceAvailability>();
            EntityManager.CompleteDependencyBeforeRO<ServiceAvailable>();
            EntityManager.CompleteDependencyBeforeRO<PrefabRef>();
            EntityManager.CompleteDependencyBeforeRO<ServiceCompanyData>();
            EntityManager.CompleteDependencyBeforeRO<IndustrialProcessData>();

            int edgeCount = 0;
            double roadSum = 0d;
            float roadMax = 0f;
            float roadServiceAtMax = 0f;
            float maxCommercialBonus = 0f;
            Entity maxRoadEntity = Entity.Null;
            Entity maxBonusEntity = Entity.Null;
            using (NativeArray<Entity> edges = m_EdgeQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i];
                    DynamicBuffer<ResourceAvailability> availability =
                        EntityManager.GetBuffer<ResourceAvailability>(edge, true);
                    if (availability.Length <= 1)
                    {
                        continue;
                    }

                    float road = EntityManager.GetComponentData<LandValue>(edge).m_LandValue;
                    float serviceAvailable =
                        (availability[1].m_Availability.x +
                         availability[1].m_Availability.y) * 0.5f;
                    float commercialBonus =
                        serviceAvailable * parameters.m_CommercialServiceBonusMultiplier;

                    edgeCount++;
                    roadSum += road;
                    if (road > roadMax)
                    {
                        roadMax = road;
                        roadServiceAtMax = commercialBonus;
                        maxRoadEntity = edge;
                    }

                    if (commercialBonus > maxCommercialBonus)
                    {
                        maxCommercialBonus = commercialBonus;
                        maxBonusEntity = edge;
                    }
                }
            }

            // Match vanilla LandValueInfoviewUISystem: average the road-edge
            // LandValue of every eligible building that has a valid road edge.
            double uiLandValueSum = 0d;
            int uiBuildingCount = 0;
            EntityManager.CompleteDependencyBeforeRO<Building>();
            using (NativeArray<Entity> buildings =
                m_BuildingQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity roadEdge =
                        EntityManager.GetComponentData<Building>(buildings[i]).m_RoadEdge;
                    if (!EntityManager.HasComponent<LandValue>(roadEdge))
                    {
                        continue;
                    }

                    uiLandValueSum +=
                        EntityManager.GetComponentData<LandValue>(roadEdge).m_LandValue;
                    uiBuildingCount++;
                }
            }

            int companyCount = 0;
            int over90 = 0;
            int overMax = 0;
            int hotelCount = 0;
            int leisureCount = 0;
            long stockTotal = 0;
            int maxStock = 0;
            int maxCapacity = 0;
            float maxStockRatio = 0f;
            Entity maxStockCompany = Entity.Null;
            using (NativeArray<Entity> companies = m_CompanyQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < companies.Length; i++)
                {
                    Entity company = companies[i];
                    Entity prefab =
                        EntityManager.GetComponentData<PrefabRef>(company).m_Prefab;
                    if (!EntityManager.HasComponent<ServiceCompanyData>(prefab))
                    {
                        continue;
                    }

                    int stock =
                        EntityManager.GetComponentData<ServiceAvailable>(company).m_ServiceAvailable;
                    int capacity =
                        EntityManager.GetComponentData<ServiceCompanyData>(prefab).m_MaxService;

                    companyCount++;
                    stockTotal += stock;
                    if (stock > maxStock)
                    {
                        maxStock = stock;
                        maxCapacity = capacity;
                        maxStockCompany = company;
                    }

                    if (capacity > 0)
                    {
                        float ratio = (float)stock / capacity;
                        maxStockRatio = Math.Max(maxStockRatio, ratio);
                        if (ratio > 0.9f)
                        {
                            over90++;
                        }

                        if (stock >= capacity)
                        {
                            overMax++;
                        }
                    }

                    if (EntityManager.HasComponent<LeisureProviderData>(prefab))
                    {
                        leisureCount++;
                    }

                    if (EntityManager.HasComponent<IndustrialProcessData>(prefab) &&
                        EntityManager.GetComponentData<IndustrialProcessData>(prefab)
                            .m_Output.m_Resource == Game.Economy.Resource.Lodging)
                    {
                        hotelCount++;
                    }
                }
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d /
                Stopwatch.Frequency;
            LogUtils.Info(
                "[NCF-LAND] " +
                $"mode={(Mod.BalancingEnabled ? "ON" : "OFF")} " +
                $"productionRelief={(Mod.ProductionReliefEnabled ? "ON" : "OFF")} " +
                $"toSearchActive={TourismOverhaulCompatibility.IsActive(World)} " +
                $"deferHotelToTO={Mod.DeferHotelBookingToTourismOverhaul} " +
                $"hour={hour:D2} frame={m_SimulationSystem.frameIndex} " +
                $"mapAvgAllCells={(map.IsCreated && map.Length > 0 ? mapSum / map.Length : 0d):F1} " +
                $"mapMax={mapMax:F1} cellsOver100k={highCells} " +
                $"uiAverageLandValue={(uiBuildingCount > 0 ? uiLandValueSum / uiBuildingCount : 0d):F1} " +
                $"uiBuildings={uiBuildingCount} " +
                $"roads={edgeCount} roadAvg={(edgeCount > 0 ? roadSum / edgeCount : 0d):F1} " +
                $"roadMax={roadMax:F1} maxRoadEdge={maxRoadEntity} " +
                $"commercialBonusAtMaxRoad={roadServiceAtMax:F1} " +
                $"commercialBonusMax={maxCommercialBonus:F1} maxBonusEdge={maxBonusEntity} " +
                $"commercialMultiplier={parameters.m_CommercialServiceBonusMultiplier:F2} " +
                $"attractivenessMultiplier={parameters.m_AttractivenessBonusMultiplier:F2} " +
                $"commonBonusCap={parameters.m_CommonFactorMaxBonus:F1} " +
                $"elapsedMs={elapsedMs:F1}");

            LogUtils.Info(
                "[NCF-LAND-STOCK] " +
                $"hour={hour:D2} companies={companyCount} hotels={hotelCount} " +
                $"leisure={leisureCount} totalStock={stockTotal} " +
                $"stockMax={maxStock} stockMaxCapacity={maxCapacity} " +
                $"stockMaxCompany={maxStockCompany} maxStockRatio={maxStockRatio:F2} " +
                $"over90pct={over90} atOrOverCapacity={overMax}");
        }
    }
}
#endif
