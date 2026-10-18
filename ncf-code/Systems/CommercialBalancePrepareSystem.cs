// <copyright file="CommercialBalancePrepareSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalancePrepareSystem.cs
// Purpose: Burst snapshot of households eligible for post-vanilla NEC correction runs as IJobChunk

using Colossal.Serialization.Entities;

using Game;
using Game.Simulation;

using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Captures resident households that enter vanilla HouseholdBehaviorSystem
    /// without an existing shopping need.
    ///
    /// Vanilla runs after this snapshot and gets first chance to consume existing
    /// household resources or create its own shopping need.
    /// CommercialBalanceSystem then rechecks the household after vanilla.
    /// </summary>
    public partial class CommercialBalancePrepareSystem : GameSystemBase
    {
        private EntityQuery m_HouseholdQuery;
        private NativeList<Entity> m_ReadyHouseholds;

        private SimulationSystem m_SimulationSystem = null!;

        private JobHandle m_SnapshotJob;
        private uint m_CapturedSimulationFrame = uint.MaxValue;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 262144 /
                (HouseholdBehaviorSystem.kUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_HouseholdQuery =
                GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Citizens.HouseholdNeed>(),
                                ComponentType.ReadOnly<Game.Citizens.Household>(),
                                ComponentType.ReadOnly<Game.Citizens.HouseholdCitizen>(),
                                ComponentType.ReadOnly<Game.Economy.Resources>(),
                                ComponentType.ReadOnly<Game.Buildings.PropertyRenter>(),
                                ComponentType.ReadOnly<UpdateFrame>(),
                            },

                        None =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Game.Citizens.TouristHousehold>(),
                                ComponentType.ReadOnly<Game.Citizens.HomelessHousehold>(),
                                ComponentType.ReadOnly<Game.Agents.MovingAway>(),
                                ComponentType.ReadOnly<Game.Common.Deleted>(),
                                ComponentType.ReadOnly<Game.Tools.Temp>(),
                            },
                    });

            m_ReadyHouseholds = new NativeList<Entity>(1024, Allocator.Persistent);

            RequireForUpdate(m_HouseholdQuery);
        }

        protected override void OnDestroy()
        {
            m_SnapshotJob.Complete();

            if (m_ReadyHouseholds.IsCreated)
            {
                m_ReadyHouseholds.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_SnapshotJob.Complete();
            m_ReadyHouseholds.Clear();
            m_CapturedSimulationFrame = uint.MaxValue;
        }

        protected override void OnUpdate()
        {
            m_SnapshotJob.Complete();
            m_ReadyHouseholds.Clear();

            uint simulationFrame = m_SimulationSystem.frameIndex;

            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    simulationFrame,
                    (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation),
                    16);

            m_HouseholdQuery.SetSharedComponentFilter(new UpdateFrame(updateFrame));

            int candidateCount = m_HouseholdQuery.CalculateEntityCount();

            if (m_ReadyHouseholds.Capacity < candidateCount)
            {
                m_ReadyHouseholds.Capacity = candidateCount;
            }

            CaptureReadyHouseholdsJob job =
                new()
                {
                    m_EntityType = SystemAPI.GetEntityTypeHandle(),

                    m_HouseholdNeedType =
                        SystemAPI.GetComponentTypeHandle<Game.Citizens.HouseholdNeed>(
                            isReadOnly: true),

                    m_ReadyHouseholds = m_ReadyHouseholds,
                };

            m_SnapshotJob = job.ScheduleByRef(m_HouseholdQuery, Dependency);
            Dependency = m_SnapshotJob;

            m_CapturedSimulationFrame = simulationFrame;
        }

        internal NativeArray<Entity> CompleteAndGetSnapshot(uint simulationFrame)
        {
            if (m_CapturedSimulationFrame != simulationFrame)
            {
                return default;
            }

            m_SnapshotJob.Complete();

            return m_ReadyHouseholds.AsArray();
        }

        [BurstCompile]
        private struct CaptureReadyHouseholdsJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            [ReadOnly]
            public ComponentTypeHandle<Game.Citizens.HouseholdNeed> m_HouseholdNeedType;

            public NativeList<Entity> m_ReadyHouseholds;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);

                NativeArray<Game.Citizens.HouseholdNeed> needs =
                    chunk.GetNativeArray(ref m_HouseholdNeedType);

                ChunkEntityEnumerator enumerator =
                    new(
                        useEnabledMask,
                        chunkEnabledMask,
                        chunk.Count);

                while (enumerator.NextEntityIndex(out int i))
                {
                    // Never compete with a shopping need that already existed
                    // before vanilla HouseholdBehaviorSystem ran.
                    if (needs[i].m_Resource != Game.Economy.Resource.NoResource)
                    {
                        continue;
                    }

                    // Do NOT reject households here for existing resources.
                    // Vanilla runs next and gets first chance to consume them.
                    m_ReadyHouseholds.AddNoResize(entities[i]);
                }
            }
        }
    }
}
