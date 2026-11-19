// <copyright file="LoadedLandValueWarningSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// One read-only snapshot per city load, including normal public Release builds.
// It does not edit land values, road availability, companies or saved data.

using System;
using Colossal.Serialization.Entities;
using CS2Shared.RiverMochi;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    public partial class LoadedLandValueWarningSystem : GameSystemBase
    {
        private EntityQuery m_RoadQuery;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_ParametersQuery;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 8192;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ParametersQuery = GetEntityQuery(
                ComponentType.ReadOnly<LandValueParameterData>());

            m_RoadQuery = GetEntityQuery(new EntityQueryDesc
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

            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<BuildingCondition>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (mode != GameMode.Game || m_ParametersQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            try
            {
                ReadLoadedValues();
            }
            catch (Exception exception)
            {
                // Never make a diagnostic prevent a city from loading.
                LogUtils.Warn(
                    "[NCF-LAND-LOAD] Unable to read initial land-value snapshot.",
                    exception);
            }
        }

        private void ReadLoadedValues()
        {
            LandValueParameterData parameters =
                m_ParametersQuery.GetSingleton<LandValueParameterData>();
            EntityManager.CompleteDependencyBeforeRO<LandValue>();
            EntityManager.CompleteDependencyBeforeRO<ResourceAvailability>();
            EntityManager.CompleteDependencyBeforeRO<Building>();

            int roads = 0;
            float maxRoad = 0f;
            float maxCommercialBonus = 0f;
            Entity maxBonusEdge = Entity.Null;
            using (NativeArray<Entity> edges =
                m_RoadQuery.ToEntityArray(Allocator.Temp))
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

                    roads++;
                    maxRoad = Math.Max(maxRoad,
                        EntityManager.GetComponentData<LandValue>(edge).m_LandValue);
                    float serviceAverage =
                        (availability[1].m_Availability.x +
                         availability[1].m_Availability.y) * 0.5f;
                    float commercialBonus =
                        serviceAverage * parameters.m_CommercialServiceBonusMultiplier;
                    if (commercialBonus > maxCommercialBonus)
                    {
                        maxCommercialBonus = commercialBonus;
                        maxBonusEdge = edge;
                    }
                }
            }

            double sumBuildingRoadValue = 0d;
            int buildings = 0;
            using (NativeArray<Entity> buildingEntities =
                m_BuildingQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < buildingEntities.Length; i++)
                {
                    Entity road = EntityManager.GetComponentData<Building>(
                        buildingEntities[i]).m_RoadEdge;
                    if (!EntityManager.HasComponent<LandValue>(road))
                    {
                        continue;
                    }

                    sumBuildingRoadValue +=
                        EntityManager.GetComponentData<LandValue>(road).m_LandValue;
                    buildings++;
                }
            }

            double uiAverageCurrency =
                buildings > 0 ? 100d * sumBuildingRoadValue / buildings : 0d;
            // These are diagnostic thresholds, NOT land-value caps.
            bool abnormal = uiAverageCurrency > 250000d ||
                maxCommercialBonus > 10000f || maxRoad > 10000f;

            LogUtils.Info(
                "[NCF-LAND-LOAD] readOnly=true " +
                $"abnormal={abnormal} roads={roads} buildings={buildings} " +
                $"uiAvgApproxCurrency={uiAverageCurrency:F0} " +
                $"maxRoadValue={maxRoad:F1} " +
                $"commercialBonusMax={maxCommercialBonus:F1} " +
                $"maxBonusEdge={maxBonusEdge}");
        }

        protected override void OnUpdate()
        {
            // The snapshot is taken only in OnGameLoadingComplete.
        }
    }
}
