// <copyright file="LeisureBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/LeisureBalanceSystem.cs
// Purpose: DEBUG-only vanilla leisure-demand correction.

#if DEBUG

using Colossal.Serialization.Entities;

using CS2Shared.RiverMochi;

using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    internal static class LeisureBalanceState
    {
        internal static bool Applied;
        internal static Entity ParametersEntity;
        internal static int OriginalRandomFactor;
    }

    /// <summary>
    /// Temporarily increases vanilla leisure-seeking probability when
    /// commercial Leisure NEC warnings remain high.
    ///
    /// No trips or destinations are created here. CitizenBehaviorSystem
    /// continues to decide which eligible citizens begin leisure, and
    /// vanilla LeisureSystem/pathfinding handles destination selection,
    /// travel, spending, and Service consumption.
    /// </summary>
    public partial class LeisureBalanceSystem :
        GameSystemBase
    {
        private const int kWarningGoal = 5;

        private const int kWarningScanInterval = 256;

        private const int kReductionPerExcessWarning = 32;

        private SimulationSystem m_SimulationSystem = null!;

        private EntityQuery m_LeisureParametersQuery;

        private uint m_LastWarningScanFrame;

        private int m_CachedWarningCount;

        private int m_LastLoggedWarnings = -1;

        private int m_LastLoggedFactor = -1;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem =
                World.GetOrCreateSystemManaged<
                    SimulationSystem>();

            m_LeisureParametersQuery =
                GetEntityQuery(
                    ComponentType.ReadWrite<
                        LeisureParametersData>());

            RequireForUpdate(
                m_LeisureParametersQuery);

            m_LastWarningScanFrame =
                uint.MaxValue;
        }

        protected override void OnGameLoaded(
            Context serializationContext)
        {
            base.OnGameLoaded(
                serializationContext);

            LeisureBalanceState.Applied =
                false;

            LeisureBalanceState.ParametersEntity =
                Entity.Null;

            LeisureBalanceState.OriginalRandomFactor =
                0;

            m_LastWarningScanFrame =
                uint.MaxValue;

            m_CachedWarningCount =
                0;

            m_LastLoggedWarnings =
                -1;

            m_LastLoggedFactor =
                -1;
        }

        protected override void OnUpdate()
        {
            RestoreStaleState();

            if (m_LeisureParametersQuery
                    .CalculateEntityCount() != 1)
            {
                return;
            }

            uint frame =
                m_SimulationSystem.frameIndex;

            if (m_LastWarningScanFrame ==
                    uint.MaxValue ||
                frame - m_LastWarningScanFrame >=
                    kWarningScanInterval)
            {
                m_LastWarningScanFrame =
                    frame;

                m_CachedWarningCount =
                    CountCommercialLeisureWarnings();
            }

            if (m_CachedWarningCount <=
                kWarningGoal)
            {
                return;
            }

            Entity parametersEntity =
                m_LeisureParametersQuery
                    .GetSingletonEntity();

            LeisureParametersData parameters =
                EntityManager.GetComponentData<
                    LeisureParametersData>(
                        parametersEntity);

            int originalFactor =
                parameters.m_LeisureRandomFactor;

            if (originalFactor <= 1)
            {
                return;
            }

            // At high NEC pressure, allow at most a 2x increase
            // in vanilla leisure-seeking probability.
            int minimumFactor =
                math.max(
                    1,
                    originalFactor / 2);

           int excessWarnings =
                m_CachedWarningCount - kWarningGoal;

            int adjustedFactor =
                math.max(
                    minimumFactor,
                    originalFactor -
                        (excessWarnings *
                            kReductionPerExcessWarning));

            if (adjustedFactor >=
                originalFactor)
            {
                return;
            }

            LeisureBalanceState.Applied =
                true;

            LeisureBalanceState.ParametersEntity =
                parametersEntity;

            LeisureBalanceState.OriginalRandomFactor =
                originalFactor;

            parameters.m_LeisureRandomFactor =
                adjustedFactor;

            EntityManager.SetComponentData(
                parametersEntity,
                parameters);

            if (m_LastLoggedWarnings !=
                    m_CachedWarningCount ||
                m_LastLoggedFactor !=
                    adjustedFactor)
            {
                m_LastLoggedWarnings =
                    m_CachedWarningCount;

                m_LastLoggedFactor =
                    adjustedFactor;

                LogUtils.Info(
                    "[CWD-LEISURE] " +
                    "prototype=v3 " +
                    "mode=vanilla-demand " +
                    $"warnings={m_CachedWarningCount} " +
                    $"randomFactor={originalFactor}->{adjustedFactor}");
            }
        }

        private int CountCommercialLeisureWarnings()
        {
            ComponentLookup<
                IndustrialProcessData>
                processDatas =
                    SystemAPI.GetComponentLookup<
                        IndustrialProcessData>(
                            true);

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
                        RefRO<
                            PrefabRef>>()
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

                if (!processDatas.HasComponent(
                        prefab))
                {
                    continue;
                }

                Resource resource =
                    processDatas[prefab]
                        .m_Output.m_Resource;

                if (resource ==
                        Resource.Entertainment ||
                    resource ==
                        Resource.Meals ||
                    resource ==
                        Resource.Recreation)
                {
                    warnings++;
                }
            }

            return warnings;
        }

        private void RestoreStaleState()
        {
            if (!LeisureBalanceState.Applied)
            {
                return;
            }

            Entity entity =
                LeisureBalanceState
                    .ParametersEntity;

            if (entity != Entity.Null &&
                EntityManager.Exists(entity) &&
                EntityManager.HasComponent<
                    LeisureParametersData>(
                        entity))
            {
                LeisureParametersData parameters =
                    EntityManager.GetComponentData<
                        LeisureParametersData>(
                            entity);

                parameters.m_LeisureRandomFactor =
                    LeisureBalanceState
                        .OriginalRandomFactor;

                EntityManager.SetComponentData(
                    entity,
                    parameters);
            }

            LeisureBalanceState.Applied =
                false;
        }
    }
}

#endif
