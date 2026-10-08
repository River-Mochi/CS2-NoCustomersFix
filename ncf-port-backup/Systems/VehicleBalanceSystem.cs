// <copyright file="VehicleBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/VehicleBalanceSystem.cs
// Purpose: DEBUG-only corrective demand for commercial Vehicle stores.

#if DEBUG

using Colossal.Serialization.Entities;

using CS2Shared.RiverMochi;

using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;

using Unity.Entities;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Runs after the ordinary-retail demand controller.
    ///
    /// Uses households that were ready to shop before vanilla but still have
    /// no shopping need after vanilla and the ordinary-retail correction.
    ///
    /// Corrective Vehicle purchases intentionally never use vanilla's special
    /// car-purchase amount of 50, so this system does not manufacture extra
    /// personal cars.
    /// </summary>
    public partial class VehicleBalanceSystem : GameSystemBase
    {
        private SimulationSystem m_SimulationSystem = null!;
        private ResourceSystem m_ResourceSystem = null!;

        private CommercialBalancePrepareSystem
            m_PrepareSystem = null!;

        private int m_UpdateCount;

        private int m_WindowReady;
        private int m_WindowEmpty;
        private int m_WindowInjected;
        private long m_WindowInjectedUnits;

        private int m_WindowAvoidedCarAmount;

        private bool m_ConfigurationLogged;

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

            m_ResourceSystem =
                World.GetOrCreateSystemManaged<
                    ResourceSystem>();

            m_PrepareSystem =
                World.GetOrCreateSystemManaged<
                    CommercialBalancePrepareSystem>();
        }

        protected override void OnGameLoaded(
            Context serializationContext)
        {
            base.OnGameLoaded(
                serializationContext);

            ResetVehicleState();

            m_UpdateCount = 0;

            m_WindowReady = 0;
            m_WindowEmpty = 0;
            m_WindowInjected = 0;
            m_WindowInjectedUnits = 0;

            m_WindowAvoidedCarAmount = 0;

            m_ConfigurationLogged = false;
        }

        protected override void OnUpdate()
        {
            uint simulationFrame =
                m_SimulationSystem.frameIndex;

            if (!m_PrepareSystem.HasSnapshotFor(
                    simulationFrame))
            {
                return;
            }

            if (!SystemAPI.TryGetSingleton<
                    EconomyParameterData>(
                    out EconomyParameterData
                        economyParameters))
            {
                return;
            }

            float resourceDemandMultiplier = 1f;

            if (SystemAPI.TryGetSingleton<
                    Game.Prefabs.Modes.ModeSettingData>(
                    out Game.Prefabs.Modes.ModeSettingData
                        modeSetting) &&
                modeSetting.m_Enable)
            {
                resourceDemandMultiplier =
                    modeSetting
                        .m_ResourceDemandPerCitizenMultiplier;
            }

            ComponentLookup<
                Game.Companies.ServiceCompanyData>
                serviceCompanyDatas =
                    SystemAPI.GetComponentLookup<
                        Game.Companies.ServiceCompanyData>(
                            true);

            ComponentLookup<
                IndustrialProcessData>
                industrialProcessDatas =
                    SystemAPI.GetComponentLookup<
                        IndustrialProcessData>(
                            true);

            ComponentLookup<
                ResourceData>
                resourceDatas =
                    SystemAPI.GetComponentLookup<
                        ResourceData>(
                            true);

            ComponentLookup<
                Game.Citizens.Citizen>
                citizenDatas =
                    SystemAPI.GetComponentLookup<
                        Game.Citizens.Citizen>(
                            true);

            ComponentLookup<
                ConsumptionData>
                consumptionDatas =
                    SystemAPI.GetComponentLookup<
                        ConsumptionData>(
                            true);

            ComponentLookup<
                PrefabRef>
                prefabRefs =
                    SystemAPI.GetComponentLookup<
                        PrefabRef>(
                            true);

            BufferLookup<
                Game.Citizens.HouseholdCitizen>
                householdCitizens =
                    SystemAPI.GetBufferLookup<
                        Game.Citizens.HouseholdCitizen>(
                            true);

            BufferLookup<
                Game.Economy.Resources>
                householdResources =
                    SystemAPI.GetBufferLookup<
                        Game.Economy.Resources>(
                            true);

            BufferLookup<
                Game.Vehicles.OwnedVehicle>
                ownedVehicles =
                    SystemAPI.GetBufferLookup<
                        Game.Vehicles.OwnedVehicle>(
                            true);

            BufferLookup<
                Game.Buildings.Renter>
                renterBuffers =
                    SystemAPI.GetBufferLookup<
                        Game.Buildings.Renter>(
                            true);

            BuildVehiclePressure(
                serviceCompanyDatas,
                industrialProcessDatas);

            PrepareVehicleBudget();

            if (!m_ConfigurationLogged)
            {
                m_ConfigurationLogged = true;

                LogUtils.Info(
                    "[CWD-VEHICLE] ACTIVE DEBUG vehicle prototype. " +
                    "Vehicle shops above 85% unused Service receive " +
                    "controlled generic Vehicle demand. Existing vanilla " +
                    "needs are untouched. Corrective Vehicle purchases " +
                    "never use amount=50, so they cannot trigger vanilla's " +
                    "personal-car creation path.");
            }

            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    simulationFrame,
                    (uint)GetUpdateInterval(
                        SystemUpdatePhase.GameSimulation),
                    16);

            int ready = 0;
            int empty = 0;
            int injected = 0;
            long injectedUnits = 0;
            int avoidedCarAmount = 0;

            foreach ((
                RefRW<Game.Citizens.HouseholdNeed>
                    needRef,
                RefRO<Game.Citizens.Household>
                    householdRef,
                RefRO<Game.Buildings.PropertyRenter>
                    propertyRenterRef,
                Entity householdEntity) in
                SystemAPI
                    .Query<
                        RefRW<
                            Game.Citizens.HouseholdNeed>,
                        RefRO<
                            Game.Citizens.Household>,
                        RefRO<
                            Game.Buildings.PropertyRenter>>()
                    .WithAll<
                        Game.Citizens.HouseholdCitizen,
                        Game.Economy.Resources>()
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
                if (!m_PrepareSystem
                        .WasReadyBeforeVanilla(
                            householdEntity))
                {
                    continue;
                }

                ready++;

                Game.Citizens.HouseholdNeed need =
                    needRef.ValueRO;

                // Vanilla and the successful V4 retail controller always win.
                // Only use households that are still empty afterward.
                if (need.m_Resource !=
                    Resource.NoResource)
                {
                    continue;
                }

                empty++;

                if (m_RemainingVehicleBudget <= 0 ||
                    m_WorkingVehicleExcess <= 0)
                {
                    continue;
                }

                if (!householdCitizens.HasBuffer(
                        householdEntity) ||
                    !householdResources.HasBuffer(
                        householdEntity))
                {
                    continue;
                }

                if (!TryCreateVehicleNeed(
                        householdEntity,
                        householdRef.ValueRO,
                        propertyRenterRef.ValueRO,
                        resourceDemandMultiplier,
                        economyParameters,
                        householdCitizens[
                            householdEntity],
                        householdResources[
                            householdEntity],
                        ownedVehicles,
                        renterBuffers,
                        consumptionDatas,
                        prefabRefs,
                        citizenDatas,
                        resourceDatas,
                        out int amount,
                        out bool avoidedSpecialCarAmount))
                {
                    continue;
                }

                need.m_Resource =
                    Resource.Vehicles;

                need.m_Amount =
                    amount;

                needRef.ValueRW =
                    need;

                injected++;
                injectedUnits += amount;

                if (avoidedSpecialCarAmount)
                {
                    avoidedCarAmount++;
                }
            }

            m_UpdateCount++;

            m_WindowReady += ready;
            m_WindowEmpty += empty;

            m_WindowInjected += injected;
            m_WindowInjectedUnits +=
                injectedUnits;

            m_WindowAvoidedCarAmount +=
                avoidedCarAmount;

            if (m_UpdateCount %
                kVehicleLogEveryUpdates == 0)
            {
                LogVehicleWindow();
            }
        }
    }
}

#endif
