// <copyright file="HotelBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/HotelBalanceSystem.cs
// Purpose: Balance existing tourist lodging demand across failing hotels.


using System.Collections.Generic;

using Colossal.Serialization.Entities;

using Unity.Collections;

using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    public partial class HotelBalanceSystem : GameSystemBase
    {
        private EntityQuery m_LodgingSeekerQuery;
        private CommercialBalanceSystem m_CommercialBalanceSystem = null!;
        private int m_HotelUpdateIndex;
        private struct HotelPressure
        {
            public Entity Hotel;
            public Entity Building;
            public int ExcessService;
            public int FreeRooms;
            public int RoomDeficit;
        }

        private struct HotelAssignment
        {
            public Entity Household;
            public Entity Building;
        }

        private const float kTargetServiceRatio = 0.85f;
        private const float kTargetOccupiedRatio = 0.11f;
        private const int kMaxAssignmentsPerUpdate = 16;
        // Once no hotel needs correction, check every fourth eligible update.
        // Each update is 64 simulation frames, so the longest extra delay
        // before seeing new pressure is only 192 simulation frames.
        private const int kEmptyHotelRescanStride = 4;
        private int m_EmptyHotelSkipsRemaining;

        private readonly List<HotelPressure> m_Hotels = new();
        private readonly List<HotelAssignment> m_Assignments = new();

#if NCF_PERF_TEST
        private const int kPerfHotelLogUpdates = 256;
        private int m_PerfHotelUpdates;
        private long m_PerfHotelScanTicks;
        private long m_PerfHotelBookingTicks;
        private long m_PerfHotelPlaybackTicks;
        private long m_PerfHotelCandidates;
        private long m_PerfHotelBookings;
        private int m_PerfHotelSkippedScans;
        private int m_PerfHotelCadenceSkips;
        private int m_PerfHotelLowWarningSkips;

        private void RecordHotelPerf(long scanTicks, long bookingTicks, long playbackTicks, int hotelCandidates, int bookings)
        {
            m_PerfHotelUpdates++;
            m_PerfHotelScanTicks += scanTicks;
            m_PerfHotelBookingTicks += bookingTicks;
            m_PerfHotelPlaybackTicks += playbackTicks;
            m_PerfHotelCandidates += hotelCandidates;
            m_PerfHotelBookings += bookings;
            if (m_PerfHotelUpdates < kPerfHotelLogUpdates)
            {
                return;
            }

            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            CS2Shared.RiverMochi.LogUtils.Info(
                "[NCF-PERF-HOTEL] " +
                $"updates={m_PerfHotelUpdates} " +
                $"scanMs={(m_PerfHotelScanTicks * ms):F1} " +
                $"bookingMs={(m_PerfHotelBookingTicks * ms):F1} " +
                $"playbackMs={(m_PerfHotelPlaybackTicks * ms):F1} " +
                $"candidateHotels={m_PerfHotelCandidates} " +
                $"bookings={m_PerfHotelBookings} " +
                $"skippedEmptyScans={m_PerfHotelSkippedScans} " +
                $"skippedCadence={m_PerfHotelCadenceSkips} " +
                $"skippedLowWarning={m_PerfHotelLowWarningSkips}");
            m_PerfHotelUpdates = 0;
            m_PerfHotelScanTicks = 0;
            m_PerfHotelBookingTicks = 0;
            m_PerfHotelPlaybackTicks = 0;
            m_PerfHotelCandidates = 0;
            m_PerfHotelBookings = 0;
            m_PerfHotelSkippedScans = 0;
            m_PerfHotelCadenceSkips = 0;
            m_PerfHotelLowWarningSkips = 0;
        }
#endif


        protected override void OnCreate()
        {
            base.OnCreate();

            m_LodgingSeekerQuery =
                GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<TouristHousehold>(),
                                ComponentType.ReadOnly<LodgingSeeker>(),
                            },

                        None =
                            new ComponentType[]
                            {
                                ComponentType.ReadOnly<Target>(),
                                ComponentType.ReadOnly<MovingAway>(),
                                ComponentType.ReadOnly<Game.Common.Deleted>(),
                                ComponentType.ReadOnly<Game.Tools.Temp>(),
                                ComponentType.ReadOnly<PathInformation>(),
                            },
                    });

            RequireForUpdate(
                m_LodgingSeekerQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 64;
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_EmptyHotelSkipsRemaining = 0;
            m_HotelUpdateIndex = 0;
#if NCF_PERF_TEST
            m_PerfHotelUpdates = 0;
            m_PerfHotelScanTicks = 0;
            m_PerfHotelBookingTicks = 0;
            m_PerfHotelPlaybackTicks = 0;
            m_PerfHotelCandidates = 0;
            m_PerfHotelBookings = 0;
            m_PerfHotelSkippedScans = 0;
            m_PerfHotelCadenceSkips = 0;
            m_PerfHotelLowWarningSkips = 0;
#endif
        }

        protected override void OnUpdate()
        {
#if NCF_PERF_TEST
            if (!Mod.BalancingEnabled)
            {
                return;
            }
#endif
            // Resolve lazily and reuse the existing company-pressure scan.
            if (m_CommercialBalanceSystem == null)
            {
                m_CommercialBalanceSystem =
                    World.GetOrCreateSystemManaged<CommercialBalanceSystem>();
            }

            // Hotel placement is optional help. Avoid repeatedly assigning
            // tourists for the last few warnings at the cost of sim speed.
            // Vanilla hotel booking continues at its normal frequency.
            CommercialBalanceSystem activeBalance =
                HouseholdBehaviorCompatibility.IsReplacementActive
                    ? World.GetOrCreateSystemManaged<ReplacementCommercialBalanceSystem>()
                    : m_CommercialBalanceSystem;
            bool lowWarning = activeBalance.IsLowWarningMode;
            int cadence = lowWarning ? 4 : 2;

            if (m_HotelUpdateIndex++ % cadence != 0)
            {
#if NCF_PERF_TEST
                m_PerfHotelCadenceSkips++;

                if (lowWarning)
                {
                    m_PerfHotelLowWarningSkips++;
                }

                RecordHotelPerf(0, 0, 0, 0, 0);
#endif
                return;
            }

            if (m_EmptyHotelSkipsRemaining > 0)
            {
                m_EmptyHotelSkipsRemaining--;
#if NCF_PERF_TEST
                m_PerfHotelSkippedScans++;
                RecordHotelPerf(0, 0, 0, 0, 0);
#endif
                return;
            }
#if NCF_PERF_TEST
            long scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            ComponentLookup<ServiceCompanyData> serviceCompanyDatas =
                SystemAPI.GetComponentLookup<ServiceCompanyData>(true);

            ComponentLookup<IndustrialProcessData> industrialProcessDatas =
                SystemAPI.GetComponentLookup<IndustrialProcessData>(true);

            ComponentLookup<Building> buildings =
                SystemAPI.GetComponentLookup<Building>(true);

            BufferLookup<Game.Economy.Resources> resources =
                SystemAPI.GetBufferLookup<Game.Economy.Resources>(true);

            BufferLookup<Renter> renters =
                SystemAPI.GetBufferLookup<Renter>();

            m_Hotels.Clear();
            m_Assignments.Clear();

            foreach ((
                RefRO<LodgingProvider> lodgingRef,
                RefRO<ServiceAvailable> serviceRef,
                RefRO<PrefabRef> prefabRef,
                RefRO<PropertyRenter> propertyRenterRef,
                Entity hotelEntity) in
                SystemAPI.Query<
                    RefRO<LodgingProvider>,
                    RefRO<ServiceAvailable>,
                    RefRO<PrefabRef>,
                    RefRO<PropertyRenter>>()
                .WithAll<CommercialCompany>()
                .WithNone<Game.Common.Deleted, Game.Tools.Temp>()
                .WithEntityAccess())
            {
                Entity prefab = prefabRef.ValueRO.m_Prefab;

                if (!serviceCompanyDatas.HasComponent(prefab) ||
                    !industrialProcessDatas.HasComponent(prefab))
                {
                    continue;
                }

                IndustrialProcessData process = industrialProcessDatas[prefab];

                if (process.m_Output.m_Resource != Resource.Lodging)
                {
                    continue;
                }

                Entity building = propertyRenterRef.ValueRO.m_Property;

                if (building == Entity.Null ||
                    !buildings.HasComponent(building))
                {
                    continue;
                }

                ServiceCompanyData serviceData = serviceCompanyDatas[prefab];

                int freeRooms = lodgingRef.ValueRO.m_FreeRooms;

                if (serviceData.m_MaxService <= 0 ||
                    freeRooms <= 0 ||
                    !renters.HasBuffer(hotelEntity))
                {
                    continue;
                }

                int stock = 0;

                if (resources.HasBuffer(hotelEntity))
                {
                    stock = EconomyUtils.GetResources(
                        Resource.Lodging,
                        resources[hotelEntity]);
                }

                // Match the physical-stock side of vanilla NEC.
                if (stock <= 200)
                {
                    continue;
                }

                int targetService =
                    (int)math.floor(serviceData.m_MaxService * kTargetServiceRatio);

                int excessService =
                    math.max(0, serviceRef.ValueRO.m_ServiceAvailable - targetService);

                if (excessService <= 0)
                {
                    continue;
                }

                int occupiedRooms = renters[hotelEntity].Length;
                int totalRooms = occupiedRooms + freeRooms;

                if (totalRooms <= 0)
                {
                    continue;
                }

                int targetOccupiedRooms =
                    (int)math.ceil(totalRooms * kTargetOccupiedRatio);

                int roomDeficit =
                    math.max(0, targetOccupiedRooms - occupiedRooms);

                if (roomDeficit <= 0)
                {
                    continue;
                }

                m_Hotels.Add(new HotelPressure
                {
                    Hotel = hotelEntity,
                    Building = building,
                    ExcessService = excessService,
                    FreeRooms = freeRooms,
                    RoomDeficit = roomDeficit,
                });
            }

#if NCF_PERF_TEST
            long scanFinished = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            if (m_Hotels.Count == 0)
            {
                m_EmptyHotelSkipsRemaining = kEmptyHotelRescanStride - 1;
#if NCF_PERF_TEST
                RecordHotelPerf(scanFinished - scanStart, 0, 0, 0, 0);
#endif
                return;
            }

            ComponentLookup<LodgingProvider> lodgingProviders =
                SystemAPI.GetComponentLookup<LodgingProvider>();

            int assignments = 0;

            foreach ((
                RefRW<TouristHousehold> touristRef,
                Entity householdEntity) in
                SystemAPI.Query<RefRW<TouristHousehold>>()
                .WithAll<LodgingSeeker>()
                .WithNone<Target, MovingAway, Game.Common.Deleted>()
                .WithNone<Game.Tools.Temp, PathInformation>()
                .WithEntityAccess())
            {
                if (assignments >= kMaxAssignmentsPerUpdate)
                {
                    break;
                }

                TouristHousehold tourist = touristRef.ValueRO;

                if (tourist.m_Hotel != Entity.Null)
                {
                    continue;
                }

                int hotelIndex = SelectHotel();

                if (hotelIndex < 0)
                {
                    break;
                }

                HotelPressure pressure = m_Hotels[hotelIndex];

                if (!renters.HasBuffer(pressure.Hotel) ||
                    !lodgingProviders.HasComponent(pressure.Hotel))
                {
                    continue;
                }

                LodgingProvider lodging = lodgingProviders[pressure.Hotel];

                if (lodging.m_FreeRooms <= 0)
                {
                    pressure.FreeRooms = 0;
                    pressure.RoomDeficit = 0;
                    m_Hotels[hotelIndex] = pressure;
                    continue;
                }

                // Mirror vanilla HotelReserveJob.
                renters[pressure.Hotel].Add(new Renter
                {
                    m_Renter = householdEntity,
                });

                lodging.m_FreeRooms--;
                lodgingProviders[pressure.Hotel] = lodging;

                tourist.m_Hotel = pressure.Hotel;
                touristRef.ValueRW = tourist;

                // Keep our in-memory pressure correct for later assignments
                // during this same update.
                pressure.FreeRooms--;
                pressure.RoomDeficit =
                    math.max(0, pressure.RoomDeficit - 1);

                m_Hotels[hotelIndex] = pressure;

                m_Assignments.Add(new HotelAssignment
                {
                    Household = householdEntity,
                    Building = pressure.Building,
                });

                assignments++;
            }

            // Queue structural changes after the query and play them back
            // immediately. This keeps same-update behavior before vanilla's
            // TouristFindTargetSystem while batching individual sync points.
#if NCF_PERF_TEST
            long playbackTicks = 0;
#endif
            if (m_Assignments.Count > 0)
            {
                using (EntityCommandBuffer commandBuffer = new(Allocator.Temp))
                {
                    for (int i = 0; i < m_Assignments.Count; i++)
                    {
                        HotelAssignment assignment = m_Assignments[i];

                        if (EntityManager.HasComponent<LodgingSeeker>(assignment.Household))
                        {
                            commandBuffer.RemoveComponent<LodgingSeeker>(assignment.Household);
                        }

                        if (!EntityManager.HasComponent<Target>(assignment.Household))
                        {
                            commandBuffer.AddComponent(
                                assignment.Household,
                                new Target
                                {
                                    m_Target = assignment.Building,
                                });
                        }
                    }
#if NCF_PERF_TEST
                    long playbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                    commandBuffer.Playback(EntityManager);
#if NCF_PERF_TEST
                    playbackTicks = System.Diagnostics.Stopwatch.GetTimestamp() - playbackStart;
#endif
                }
            }
#if NCF_PERF_TEST
            RecordHotelPerf(
                scanFinished - scanStart,
                System.Diagnostics.Stopwatch.GetTimestamp() - scanFinished,
                playbackTicks,
                m_Hotels.Count,
                assignments);
#endif
        }

        private int SelectHotel()
        {
            int selected = -1;
            int bestRoomDeficit = int.MaxValue;
            int bestExcessService = 0;

            for (int i = 0; i < m_Hotels.Count; i++)
            {
                HotelPressure hotel = m_Hotels[i];

                if (hotel.FreeRooms <= 0 ||
                    hotel.RoomDeficit <= 0)
                {
                    continue;
                }

                if (hotel.RoomDeficit < bestRoomDeficit ||
                    (hotel.RoomDeficit == bestRoomDeficit &&
                     hotel.ExcessService > bestExcessService))
                {
                    bestRoomDeficit = hotel.RoomDeficit;
                    bestExcessService = hotel.ExcessService;
                    selected = i;
                }
            }

            return selected;
        }
    }
}

