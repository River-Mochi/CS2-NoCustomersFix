// <copyright file="HotelBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/HotelBalanceSystem.cs
// Purpose: DEBUG-only balancing of existing tourist demand across hotels.

#if DEBUG

using System.Collections.Generic;

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

        private readonly List<HotelPressure> m_Hotels = new();
        private readonly List<HotelAssignment> m_Assignments = new();

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 64;
        }

        protected override void OnUpdate()
        {
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

            if (m_Hotels.Count == 0)
            {
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

            // Do structural changes after the SystemAPI.Query iteration.
            // Vanilla successful reservation removes LodgingSeeker and leaves
            // the household targeted at the selected physical hotel building.
            for (int i = 0; i < m_Assignments.Count; i++)
            {
                HotelAssignment assignment = m_Assignments[i];

                if (EntityManager.HasComponent<LodgingSeeker>(assignment.Household))
                {
                    EntityManager.RemoveComponent<LodgingSeeker>(assignment.Household);
                }

                if (!EntityManager.HasComponent<Target>(assignment.Household))
                {
                    EntityManager.AddComponentData(
                        assignment.Household,
                        new Target
                        {
                            m_Target = assignment.Building,
                        });
                }
            }
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

#endif
