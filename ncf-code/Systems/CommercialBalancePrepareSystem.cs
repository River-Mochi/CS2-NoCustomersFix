// <copyright file="CommercialBalancePrepareSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalancePrepareSystem.cs
// Purpose: Snapshot households eligible for post-vanilla NEC correction.

#if DEBUG

using Colossal.Serialization.Entities;

using Game;
using Game.Economy;
using Game.Simulation;

using Unity.Collections;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Records households that enter vanilla HouseholdBehaviorSystem with no
    /// existing shopping need and no remaining household-consumption resources.
    ///
    /// NCF may use only these households if vanilla still leaves them without
    /// a need after HouseholdBehaviorSystem runs.
    /// </summary>
    public partial class CommercialBalancePrepareSystem : GameSystemBase
    {
        private NativeHashSet<Entity> m_ReadyHouseholds;

        private SimulationSystem m_SimulationSystem = null!;

        private uint m_CapturedSimulationFrame = uint.MaxValue;

        internal bool HasSnapshotFor(uint simulationFrame)
        {
            return m_CapturedSimulationFrame == simulationFrame;
        }

        internal bool WasReadyBeforeVanilla(Entity household)
        {
            return m_ReadyHouseholds.Contains(household);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 262144 /
                (HouseholdBehaviorSystem.kUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem =
                World.GetOrCreateSystemManaged<SimulationSystem>();

            // Only one of the 16 household update buckets is captured at once.
            // NativeHashSet avoids the managed allocation/GC path used by the
            // original CWD prototype.
            m_ReadyHouseholds =
                new NativeHashSet<Entity>(
                    32768,
                    Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            if (m_ReadyHouseholds.IsCreated)
            {
                m_ReadyHouseholds.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoaded(
            Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_ReadyHouseholds.Clear();
            m_CapturedSimulationFrame = uint.MaxValue;
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
                RefRO<Game.Citizens.HouseholdNeed> needRef,
                RefRO<Game.Citizens.Household> householdRef,
                Entity household) in
                SystemAPI
                    .Query<
                        RefRO<Game.Citizens.HouseholdNeed>,
                        RefRO<Game.Citizens.Household>>()
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

                // Match vanilla's important early-out. Households still consuming
                // previously purchased goods do not become the extra extra corrective shoppers.
                if (householdRef.ValueRO.m_Resources > 0)
                {
                    continue;
                }

                m_ReadyHouseholds.Add(household);
            }

            m_CapturedSimulationFrame =
                simulationFrame;
        }
    }
}

#endif
