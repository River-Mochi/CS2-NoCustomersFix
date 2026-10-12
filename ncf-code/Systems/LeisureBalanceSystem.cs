// <copyright file="LeisureBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/LeisureBalanceSystem.cs
// Purpose: Preventive adaptive vanilla Leisure demand for commercial NEC pressure.

using Colossal.Serialization.Entities;

using CS2Shared.RiverMochi;

using Game;
using Game.Economy;
using Game.Prefabs;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Preventively adjusts vanilla Leisure demand when Entertainment,
    /// Meals, or Recreation businesses approach NEC pressure.
    ///
    /// NCF does not select destinations and does not create paths.
    /// Vanilla CitizenBehaviorSystem, LeisureSystem, and pathfinding continue
    /// to handle Leisure decisions, destination selection, travel, and spending.
    /// </summary>
    public partial class LeisureBalanceSystem : GameSystemBase
    {
        // Begin helping well before vanilla's ~90% unused-Service NEC trigger.
        private const float kMediumPressureRatio = 0.82f;
        private const float kStrongPressureRatio = 0.89f;

        // Match vanilla's physical-stock requirement for NEC.
        private const int kMinimumStock = 200;

        private const int kPressureScanInterval = 256;

        private EntityQuery m_LeisureParametersQuery;

        private Entity m_ParametersEntity = Entity.Null;

        private int m_BaselineFactor;
        private int m_LastAppliedFactor;

        private bool m_HasBaseline;
        private bool m_AppliedByUs;

#if DEBUG
        private int m_LastLoggedMedium = -1;
        private int m_LastLoggedStrong = -1;
        private int m_LastLoggedFactor = -1;
#endif

        private struct LeisurePressure
        {
            public int MediumCount;
            public int StrongCount;
            public int WarningCount;
            public int EntertainmentWarnings;
            public int MealsWarnings;
            public int RecreationWarnings;
            public int LodgingWarnings;
            public float MaxUnusedRatio;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kPressureScanInterval;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_LeisureParametersQuery =
                GetEntityQuery(
                    ComponentType.ReadWrite<LeisureParametersData>());

            RequireForUpdate(m_LeisureParametersQuery);
        }

        protected override void OnGameLoaded(
            Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            ResetState();
        }

        protected override void OnDestroy()
        {
            RestoreBaselineIfOwned();

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (m_LeisureParametersQuery.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity parametersEntity =
                m_LeisureParametersQuery.GetSingletonEntity();

            LeisureParametersData parameters =
                EntityManager.GetComponentData<LeisureParametersData>(
                    parametersEntity);

            int currentFactor =
                parameters.m_LeisureRandomFactor;

            RefreshBaseline(
                parametersEntity,
                currentFactor);

            if (!m_HasBaseline ||
                m_BaselineFactor <= 0)
            {
                return;
            }

            LeisurePressure pressure =
                ScanLeisurePressure();

            int desiredFactor =
                CalculateDesiredFactor(pressure);

#if DEBUG
            LogState(
                pressure,
                currentFactor,
                desiredFactor);
#endif

            if (desiredFactor == currentFactor)
            {
                if (desiredFactor == m_BaselineFactor)
                {
                    m_AppliedByUs = false;
                }

                m_LastAppliedFactor =
                    currentFactor;

                return;
            }

            parameters.m_LeisureRandomFactor =
                desiredFactor;

            EntityManager.SetComponentData(
                parametersEntity,
                parameters);

            m_LastAppliedFactor =
                desiredFactor;

            m_AppliedByUs =
                desiredFactor != m_BaselineFactor;
        }

        private int CalculateDesiredFactor(LeisurePressure pressure)
        {
            if (pressure.StrongCount > 0)
            {
                int strongFactor = m_BaselineFactor / 2;
                int extraStrong = math.max(0, pressure.StrongCount - 5);
                int step = math.max(1, m_BaselineFactor / 32);

                strongFactor -= extraStrong * step;

                return math.max(m_BaselineFactor / 4, strongFactor);
            }

            if (pressure.MediumCount > 0)
            {
                return math.max(1, (m_BaselineFactor * 3) / 4);
            }

            return m_BaselineFactor;
        }

        private LeisurePressure ScanLeisurePressure()
        {
            ComponentLookup<
                Game.Companies.ServiceCompanyData>
                serviceCompanyDatas =
                    SystemAPI.GetComponentLookup<
                        Game.Companies.ServiceCompanyData>(
                            true);

            ComponentLookup<IndustrialProcessData>
                processDatas =
                    SystemAPI.GetComponentLookup<
                        IndustrialProcessData>(
                            true);

            BufferLookup<Game.Economy.Resources>
                resources =
                    SystemAPI.GetBufferLookup<
                        Game.Economy.Resources>(
                            true);

            LeisurePressure pressure = default;

            foreach ((
                RefRO<Game.Companies.ServiceAvailable>
                    serviceRef,
                RefRO<Game.Companies.CompanyNotifications>
                    notificationsRef,
                RefRO<PrefabRef>
                    prefabRef,
                Entity company) in
                SystemAPI
                    .Query<
                        RefRO<Game.Companies.ServiceAvailable>,
                        RefRO<Game.Companies.CompanyNotifications>,
                        RefRO<PrefabRef>>()
                    .WithAll<
                        Game.Companies.CommercialCompany>()
                    .WithNone<
                        Game.Common.Deleted,
                        Game.Tools.Temp>()
                    .WithEntityAccess())
            {
                Entity prefab =
                    prefabRef.ValueRO.m_Prefab;

                if (!serviceCompanyDatas.HasComponent(prefab) ||
                    !processDatas.HasComponent(prefab) ||
                    !resources.HasBuffer(company))
                {
                    continue;
                }

                Resource resource =
                    processDatas[prefab]
                        .m_Output.m_Resource;

                bool hasWarning = notificationsRef.ValueRO.m_NoCustomersEntity != Entity.Null;

                if (resource == Resource.Lodging)
                {
                    if (hasWarning)
                    {
                        pressure.LodgingWarnings++;
                    }

                    continue;
                }

                if (resource != Resource.Entertainment &&
                    resource != Resource.Meals &&
                    resource != Resource.Recreation)
                {
                    continue;
                }



                int stock =
                    EconomyUtils.GetResources(
                        resource,
                        resources[company]);

                if (stock <= kMinimumStock)
                {
                    continue;
                }

                Game.Companies.ServiceCompanyData
                    serviceData =
                        serviceCompanyDatas[prefab];

                if (serviceData.m_MaxService <= 0)
                {
                    continue;
                }

                float unusedRatio =
                    math.saturate(
                        (float)serviceRef.ValueRO.m_ServiceAvailable /
                        serviceData.m_MaxService);

                pressure.MaxUnusedRatio =
                    math.max(
                        pressure.MaxUnusedRatio,
                        unusedRatio);


                if (hasWarning)
                {
                    pressure.WarningCount++;

                    if (resource == Resource.Entertainment)
                    {
                        pressure.EntertainmentWarnings++;
                    }
                    else if (resource == Resource.Meals)
                    {
                        pressure.MealsWarnings++;
                    }
                    else
                    {
                        pressure.RecreationWarnings++;
                    }
                }



                if (unusedRatio >=
                    kStrongPressureRatio)
                {
                    pressure.StrongCount++;
                }
                else if (unusedRatio >=
                    kMediumPressureRatio)
                {
                    pressure.MediumCount++;
                }
            }

            return pressure;
        }

        private void RefreshBaseline(
            Entity parametersEntity,
            int currentFactor)
        {
            if (!m_HasBaseline ||
                parametersEntity != m_ParametersEntity)
            {
                m_ParametersEntity =
                    parametersEntity;

                m_BaselineFactor =
                    currentFactor;

                m_LastAppliedFactor =
                    currentFactor;

                m_HasBaseline =
                    true;

                m_AppliedByUs =
                    false;

                return;
            }

            if (m_AppliedByUs)
            {
                if (currentFactor ==
                    m_LastAppliedFactor)
                {
                    return;
                }

                // Another mod changed the value after NCF applied its value.
                // Treat that value as the new baseline instead of overwriting it.
                m_BaselineFactor =
                    currentFactor;

                m_LastAppliedFactor =
                    currentFactor;

                m_AppliedByUs =
                    false;

                return;
            }

            if (currentFactor !=
                m_BaselineFactor)
            {
                // NCF is inactive, so an external change becomes the new baseline.
                m_BaselineFactor =
                    currentFactor;

                m_LastAppliedFactor =
                    currentFactor;
            }
        }

        private void RestoreBaselineIfOwned()
        {
            if (!m_HasBaseline ||
                !m_AppliedByUs ||
                m_ParametersEntity == Entity.Null ||
                !EntityManager.Exists(m_ParametersEntity) ||
                !EntityManager.HasComponent<
                    LeisureParametersData>(
                        m_ParametersEntity))
            {
                return;
            }

            LeisureParametersData parameters =
                EntityManager.GetComponentData<
                    LeisureParametersData>(
                        m_ParametersEntity);

            // Restore only if the value is still the one NCF applied.
            // Never overwrite a later change made by another mod.
            if (parameters.m_LeisureRandomFactor ==
                m_LastAppliedFactor)
            {
                parameters.m_LeisureRandomFactor =
                    m_BaselineFactor;

                EntityManager.SetComponentData(
                    m_ParametersEntity,
                    parameters);
            }

            m_AppliedByUs = false;
        }

#if DEBUG
        private void LogState(
            LeisurePressure pressure,
            int currentFactor,
            int desiredFactor)
        {
            if (m_LastLoggedMedium ==
                    pressure.MediumCount &&
                m_LastLoggedStrong ==
                    pressure.StrongCount &&
                m_LastLoggedFactor ==
                    desiredFactor)
            {
                return;
            }

            m_LastLoggedMedium =
                pressure.MediumCount;

            m_LastLoggedStrong =
                pressure.StrongCount;

            m_LastLoggedFactor =
                desiredFactor;

            string mode =
                pressure.StrongCount > 0
                    ? "strong"
                    : pressure.MediumCount > 0
                        ? "medium"
                        : "vanilla";

            LogUtils.Info(
                "[NCF-LEISURE] " +
                $"mode={mode} " +
                $"medium={pressure.MediumCount} " +
                $"strong={pressure.StrongCount} " +
                $"warnings={pressure.WarningCount} " +
                $"ent={pressure.EntertainmentWarnings} " +
                $"meals={pressure.MealsWarnings} " +
                $"rec={pressure.RecreationWarnings} " +
                $"lodging={pressure.LodgingWarnings} " +
                $"maxUnused={pressure.MaxUnusedRatio * 100f:F1}% " +
                $"randomFactor={currentFactor}->{desiredFactor} " +
                $"baseline={m_BaselineFactor}");

        }
#endif

        private void ResetState()
        {
            m_ParametersEntity = Entity.Null;

            m_BaselineFactor = 0;

            m_LastAppliedFactor = 0;

            m_HasBaseline = false;

            m_AppliedByUs = false;

#if DEBUG
            m_LastLoggedMedium =
                -1;

            m_LastLoggedStrong =
                -1;

            m_LastLoggedFactor =
                -1;
#endif
        }
    }
}
