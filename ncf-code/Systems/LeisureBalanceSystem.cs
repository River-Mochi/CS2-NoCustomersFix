// <copyright file="LeisureBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/LeisureBalanceSystem.cs
// Purpose: Adaptive vanilla leisure-demand correction for commercial NEC pressure.

#if DEBUG

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
    /// Adjusts vanilla leisure-seeking probability only while commercial
    /// Leisure NEC pressure is elevated.
    ///
    /// No trips, destinations, or paths are created by this system.
    /// Vanilla CitizenBehaviorSystem and LeisureSystem continue to handle
    /// leisure decisions, destination selection, pathfinding, and spending.
    /// </summary>
    public partial class LeisureBalanceSystem : GameSystemBase
    {
        private const int kWarningGoal = 5;
        private const int kWarningScanInterval = 256;
        private const int kReductionPerExcessWarning = 32;

        private EntityQuery m_LeisureParametersQuery;

        private Entity m_ParametersEntity = Entity.Null;

        private int m_BaselineFactor;
        private int m_LastAppliedFactor;

        private bool m_HasBaseline;
        private bool m_AppliedByUs;

        private int m_LastLoggedWarnings = -1;
        private int m_LastLoggedFactor = -1;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kWarningScanInterval;
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

            int warningCount =
                CountCommercialLeisureWarnings();

            int desiredFactor =
                CalculateDesiredFactor(warningCount);

            LogState(
                warningCount,
                currentFactor,
                desiredFactor);

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

        private int CalculateDesiredFactor(
            int warningCount)
        {
            if (warningCount <= kWarningGoal)
            {
                return m_BaselineFactor;
            }

            int minimumFactor =
                math.max(
                    1,
                    m_BaselineFactor / 2);

            int excessWarnings =
                warningCount - kWarningGoal;

            return math.max(
                minimumFactor,
                m_BaselineFactor -
                    (excessWarnings *
                        kReductionPerExcessWarning));
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

        private int CountCommercialLeisureWarnings()
        {
            ComponentLookup<IndustrialProcessData>
                processDatas =
                    SystemAPI.GetComponentLookup<
                        IndustrialProcessData>(true);

            int warnings = 0;

            foreach ((
                RefRO<Game.Companies.CompanyNotifications>
                    notificationsRef,
                RefRO<PrefabRef>
                    prefabRef) in
                SystemAPI
                    .Query<
                        RefRO<
                            Game.Companies.CompanyNotifications>,
                        RefRO<PrefabRef>>()
                    .WithAll<
                        Game.Companies.CommercialCompany>()
                    .WithNone<
                        Game.Common.Deleted,
                        Game.Tools.Temp>())
            {
                if (notificationsRef.ValueRO
                        .m_NoCustomersEntity ==
                    Entity.Null)
                {
                    continue;
                }

                Entity prefab =
                    prefabRef.ValueRO.m_Prefab;

                if (!processDatas.HasComponent(prefab))
                {
                    continue;
                }

                Resource resource =
                    processDatas[prefab]
                        .m_Output.m_Resource;

                if (resource == Resource.Entertainment ||
                    resource == Resource.Meals ||
                    resource == Resource.Recreation)
                {
                    warnings++;
                }
            }

            return warnings;
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

        private void LogState(
            int warningCount,
            int currentFactor,
            int desiredFactor)
        {
            if (m_LastLoggedWarnings == warningCount &&
                m_LastLoggedFactor == desiredFactor)
            {
                return;
            }

            m_LastLoggedWarnings =
                warningCount;

            m_LastLoggedFactor =
                desiredFactor;

            string mode =
                warningCount <= kWarningGoal
                    ? "vanilla"
                    : "adaptive";

            LogUtils.Info(
                "[NCF-LEISURE] " +
                $"mode={mode} " +
                $"warnings={warningCount} " +
                $"randomFactor={currentFactor}->{desiredFactor} " +
                $"baseline={m_BaselineFactor}");
        }

        private void ResetState()
        {
            m_ParametersEntity =
                Entity.Null;

            m_BaselineFactor =
                0;

            m_LastAppliedFactor =
                0;

            m_HasBaseline =
                false;

            m_AppliedByUs =
                false;

            m_LastLoggedWarnings =
                -1;

            m_LastLoggedFactor =
                -1;
        }
    }
}

#endif
