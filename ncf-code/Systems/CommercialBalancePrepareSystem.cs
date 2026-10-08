// <copyright file="CommercialBalancePrepareSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the MIT License. You may not use this file except in compliance with this License.
// See LICENSE file in the project root for full license information.
// This notice and the MIT License notice must be kept with
// all copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/CommercialBalancePrepareSystem.cs
// Purpose: DEBUG-only snapshot immediately before vanilla household shopping.

#if DEBUG

using System.Collections.Generic;

using Colossal.Serialization.Entities;

using Game;
using Game.Economy;
using Game.Simulation;

using Unity.Entities;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Records households that enter vanilla HouseholdBehaviorSystem with no
    /// existing shopping need and no remaining household-consumption resources.
    ///
    /// If one of these households still has no need after vanilla runs, V4 may
    /// use it for controlled corrective physical shopping demand.
    /// </summary>
    public partial class CommercialBalancePrepareSystem
        : GameSystemBase
    {
        private readonly HashSet<Entity>
            m_ReadyHouseholds = new();

        private SimulationSystem
            m_SimulationSystem = null!;

        private uint m_CapturedSimulationFrame =
            uint.MaxValue;

        internal bool HasSnapshotFor(
            uint simulationFrame)
        {
            return m_CapturedSimulationFrame ==
                simulationFrame;
        }

        internal bool WasReadyBeforeVanilla(
            Entity household)
        {
            return m_ReadyHouseholds.Contains(
                household);
        }

        public override int GetUpdateInterval(
            SystemUpdatePhase phase)
        {
            return 262144 /
                (HouseholdBehaviorSystem.kUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem =
                World.GetOrCreateSystemManaged<
                    SimulationSystem>();
        }

        protected override void OnGameLoaded(
            Context serializationContext)
        {
            base.OnGameLoaded(
                serializationContext);

            m_ReadyHouseholds.Clear();

            m_CapturedSimulationFrame =
                uint.MaxValue;
        }

        protected override void OnUpdate()
        {
            m_ReadyHouseholds.Clear();

            uint simulationFrame =
                m_SimulationSystem.frameIndex;

            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    simulationFrame,
                    (uint)GetUpdateInterval(
                        SystemUpdatePhase.GameSimulation),
                    16);

            foreach ((
                RefRO<Game.Citizens.HouseholdNeed>
                    needRef,
                RefRO<Game.Citizens.Household>
                    householdRef,
                Entity household) in
                SystemAPI
                    .Query<
                        RefRO<
                            Game.Citizens.HouseholdNeed>,
                        RefRO<
                            Game.Citizens.Household>>()
                    .WithAll<
                        Game.Citizens.HouseholdCitizen,
                        Game.Economy.Resources,
                        Game.Buildings.PropertyRenter>()
                    .WithNone<
                        Game.Citizens.TouristHousehold,
                        Game.Citizens.HomelessHousehold,
                        Game.Agents.MovingAway>()
                    .WithNone<
                        Game.Common.Deleted,
                        Game.Tools.Temp>()
                    .WithSharedComponentFilter(
                        new UpdateFrame(updateFrame))
                    .WithEntityAccess())
            {
                if (needRef.ValueRO.m_Resource !=
                    Resource.NoResource)
                {
                    continue;
                }

                // Vanilla exits early while households still have previously
                // purchased resources to consume. Do not turn those households
                // into extra shoppers.
                if (householdRef.ValueRO.m_Resources >
                    0)
                {
                    continue;
                }

                m_ReadyHouseholds.Add(
                    household);
            }

            m_CapturedSimulationFrame =
                simulationFrame;
        }
    }
}

#endif
