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
using Game.Notifications;
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
        private EntityQuery m_HighRentIconQuery;
        private EntityQuery m_BuildingConfigQuery;
        private LandValueSystem m_LandValueSystem = null!;
        private TimeSystem m_TimeSystem = null!;
        private SimulationSystem m_SimulationSystem = null!;
        private int m_LastHour = -1;
        private uint m_FirstSampleFrame;
        private int m_RapidSampleIndex;

        // Frame offsets from the first update after load (~182 frames per game minute).
        // Capture the opening seconds, then roughly 10, 20, and 30 game minutes.
        private static readonly uint[] s_LoadOffsets =
            { 0u, 16u, 64u, 256u, 1824u, 3648u, 5472u };

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            // Cheap early exit outside the few snapshot frames.
            // 16 frames catches a short spike much sooner than hourly scans.
            return 16;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_LandValueSystem = World.GetOrCreateSystemManaged<LandValueSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_ParametersQuery = GetEntityQuery(
                ComponentType.ReadOnly<LandValueParameterData>());

            // Match City Watchdog's High Rent icon count using vanilla ECS.
            m_BuildingConfigQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingConfigurationData>());
            m_HighRentIconQuery = GetEntityQuery(
                ComponentType.ReadOnly<Icon>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());

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
            m_RapidSampleIndex = 0;
            m_FirstSampleFrame = 0u;
        }

        protected override void OnUpdate()
        {
            if (m_ParametersQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            uint frame = m_SimulationSystem.frameIndex;
            int minuteOfDay = (int)(m_TimeSystem.normalizedTime * 1440f);
            int hour = (minuteOfDay / 60) % 24;
            bool rapid = m_RapidSampleIndex < s_LoadOffsets.Length;
            if (rapid)
            {
                if (m_RapidSampleIndex == 0)
                {
                    m_FirstSampleFrame = frame;
                }
                else if (frame - m_FirstSampleFrame <
                    s_LoadOffsets[m_RapidSampleIndex])
                {
                    return;
                }

                m_RapidSampleIndex++;
            }
            else if (hour == m_LastHour)
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

            // Snapshot reads only: initial load burst, then once per game hour.
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
            int highRentFlags = 0;
            EntityManager.CompleteDependencyBeforeRO<Building>();
            using (NativeArray<Entity> buildings =
                m_BuildingQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Building building =
                        EntityManager.GetComponentData<Building>(buildings[i]);
                    if ((building.m_Flags & BuildingFlags.HighRentWarning) != BuildingFlags.None)
                    {
                        highRentFlags++;
                    }

                    Entity roadEdge = building.m_RoadEdge;
                    if (!EntityManager.HasComponent<LandValue>(roadEdge))
                    {
                        continue;
                    }

                    uiLandValueSum +=
                        EntityManager.GetComponentData<LandValue>(roadEdge).m_LandValue;
                    uiBuildingCount++;
                }
            }

            // City Watchdog's High Rent number counts notification entities.
            // Count the same icon prefab directly; no dependency on CWD.
            int highRentIcons = 0;
            if (!m_BuildingConfigQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.CompleteDependencyBeforeRO<PrefabRef>();
                Entity highRentPrefab =
                    m_BuildingConfigQuery.GetSingleton<BuildingConfigurationData>()
                        .m_HighRentNotification;
                using (NativeArray<Entity> icons =
                    m_HighRentIconQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < icons.Length; i++)
                    {
                        if (EntityManager.GetComponentData<PrefabRef>(icons[i]).m_Prefab ==
                            highRentPrefab)
                        {
                            highRentIcons++;
                        }
                    }
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
            bool toSearchActive = TourismOverhaulCompatibility.IsActive(World);
            string hotelBookingState = !Mod.BalancingEnabled
                ? "DISABLED_NCF_OFF"
                : (toSearchActive && Mod.DeferHotelBookingToTourismOverhaul
                    ? "SKIPPED" : "ENABLED");
            string sampleKind = rapid ? (m_RapidSampleIndex == 1
                ? "loadFirstUpdate" : "loadFollowup") : "hourly";

            LogUtils.Info(
                "[NCF-LAND] " +
                $"mode={(Mod.BalancingEnabled ? "ON" : "OFF")} " +
                $"productionRelief={(Mod.ProductionReliefEnabled ? "ON" : "OFF")} " +
                $"toSearchActive={toSearchActive} " +
                $"skipNCFHotelWhenTOActive={Mod.DeferHotelBookingToTourismOverhaul} " +
                $"hotelBookings={hotelBookingState} " +
                $"sample={sampleKind} " +
                $"loadElapsedFrames={(rapid ? frame - m_FirstSampleFrame : 0u)} " +
                $"hour={hour:D2} minute={minuteOfDay % 60:D2} " +
                $"frame={frame} highRentIcons={highRentIcons} " +
                $"highRentBuildingFlags={highRentFlags} " +
                $"mapAvgAllCells={(map.IsCreated && map.Length > 0 ? mapSum / map.Length : 0d):F1} " +
                $"mapMax={mapMax:F1} cellsOver100k={highCells} " +
                $"uiAverageLandValue={(uiBuildingCount > 0 ? uiLandValueSum / uiBuildingCount : 0d):F1} " +
                $"uiAvgApproxCurrency={(uiBuildingCount > 0 ? 100d * uiLandValueSum / uiBuildingCount : 0d):F0} " +
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
