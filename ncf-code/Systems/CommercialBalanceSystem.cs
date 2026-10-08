// <copyright file="CommercialBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalanceSystem.cs
// Purpose: Single post-vanilla physical retail + Vehicle NEC correction pass.

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
    /// Runs after vanilla HouseholdBehaviorSystem.
    ///
    /// Vanilla-created shopping needs always win. NCF only uses households
    /// that were eligible before vanilla and remain without a shopping need
    /// afterward.
    ///
    /// Ordinary physical retail is attempted first. If it cannot use that
    /// household, Vehicle correction may use it second.
    /// </summary>
    public partial class CommercialBalanceSystem : GameSystemBase
    {
        private SimulationSystem m_SimulationSystem = null!;
        private ResourceSystem m_ResourceSystem = null!;

        private CommercialBalancePrepareSystem
            m_PrepareSystem = null!;

        private int m_UpdateCount;

        private int m_WindowReadyHouseholds;
        private int m_WindowVanillaCreatedNeeds;
        private int m_WindowEmptyAfterVanilla;

        private int m_WindowInjectedNeeds;
        private long m_WindowInjectedUnits;

        private int m_WindowVehicleInjectedNeeds;
        private long m_WindowVehicleInjectedUnits;
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

            ResetBalanceState();

            m_UpdateCount = 0;

            m_WindowReadyHouseholds = 0;
            m_WindowVanillaCreatedNeeds = 0;
            m_WindowEmptyAfterVanilla = 0;

            m_WindowInjectedNeeds = 0;
            m_WindowInjectedUnits = 0;

            m_WindowVehicleInjectedNeeds = 0;
            m_WindowVehicleInjectedUnits = 0;
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

            RefreshCommercialPressure(
                serviceCompanyDatas,
                industrialProcessDatas,
                resourceDatas);

            if (!m_ConfigurationLogged)
            {
                m_ConfigurationLogged = true;

                LogUtils.Info(
                    "[NCF-BALANCE] ACTIVE DEBUG. " +
                    "Single post-vanilla household pass handles normal " +
                    "physical retail first and Vehicle stores second. " +
                    "Company pressure is cached for 4 correction updates. " +
                    "Existing vanilla shopping needs are untouched.");
            }

            // Nothing needs correction: do not scan the household bucket.
            if (m_RemainingInjectionBudget <= 0 &&
                m_RemainingVehicleBudget <= 0)
            {
                FinishUpdate(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0);

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

            uint updateFrame =
                SimulationUtils.GetUpdateFrameWithInterval(
                    simulationFrame,
                    (uint)GetUpdateInterval(
                        SystemUpdatePhase.GameSimulation),
                    16);

            int readyHouseholds = 0;
            int vanillaCreatedNeeds = 0;
            int emptyAfterVanilla = 0;

            int injectedNeeds = 0;
            long injectedUnits = 0;

            int vehicleInjectedNeeds = 0;
            long vehicleInjectedUnits = 0;
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
                        RefRW<Game.Citizens.HouseholdNeed>,
                        RefRO<Game.Citizens.Household>,
                        RefRO<Game.Buildings.PropertyRenter>>()
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
                // Major prototype performance fix:
                // stop inspecting households as soon as all useful corrective
                // demand for this update has been assigned.
                if (m_RemainingInjectionBudget <= 0 &&
                    m_RemainingVehicleBudget <= 0)
                {
                    break;
                }

                if (!m_PrepareSystem
                        .WasReadyBeforeVanilla(
                            householdEntity))
                {
                    continue;
                }

                readyHouseholds++;

                Game.Citizens.HouseholdNeed need =
                    needRef.ValueRO;

                // Vanilla always wins.
                if (need.m_Resource !=
                    Resource.NoResource)
                {
                    vanillaCreatedNeeds++;
                    continue;
                }

                emptyAfterVanilla++;

                if (!householdCitizens.HasBuffer(
                        householdEntity) ||
                    !householdResources.HasBuffer(
                        householdEntity))
                {
                    continue;
                }

                DynamicBuffer<
                    Game.Citizens.HouseholdCitizen>
                    citizens =
                        householdCitizens[
                            householdEntity];

                DynamicBuffer<
                    Game.Economy.Resources>
                    resources =
                        householdResources[
                            householdEntity];

                // Ordinary physical retail gets first opportunity.
                if (m_RemainingInjectionBudget > 0 &&
                    m_TotalCorrectionUnits > 0 &&
                    TryCreateCorrectiveNeed(
                        householdEntity,
                        simulationFrame,
                        householdRef.ValueRO,
                        propertyRenterRef.ValueRO,
                        resourceDemandMultiplier,
                        economyParameters,
                        citizens,
                        resources,
                        ownedVehicles,
                        renterBuffers,
                        consumptionDatas,
                        prefabRefs,
                        citizenDatas,
                        resourceDatas,
                        out Resource targetResource,
                        out int amount))
                {
                    need.m_Resource =
                        targetResource;

                    need.m_Amount =
                        amount;

                    needRef.ValueRW =
                        need;

                    injectedNeeds++;
                    injectedUnits += amount;

                    int resourceIndex =
                        EconomyUtils.GetResourceIndex(
                            targetResource);

                    if (resourceIndex >= 0 &&
                        resourceIndex <
                            m_WindowInjectedNeedsByResource
                                .Length)
                    {
                        m_WindowInjectedNeedsByResource[
                            resourceIndex]++;

                        m_WindowInjectedUnitsByResource[
                            resourceIndex] +=
                                amount;
                    }

                    continue;
                }

                // Only households still unused by vanilla and ordinary retail
                // may receive Vehicle-store correction.
                if (m_RemainingVehicleBudget <= 0 ||
                    m_WorkingVehicleExcess <= 0)
                {
                    continue;
                }

                if (!TryCreateVehicleNeed(
                        householdEntity,
                        householdRef.ValueRO,
                        propertyRenterRef.ValueRO,
                        resourceDemandMultiplier,
                        economyParameters,
                        citizens,
                        resources,
                        ownedVehicles,
                        renterBuffers,
                        consumptionDatas,
                        prefabRefs,
                        citizenDatas,
                        resourceDatas,
                        out int vehicleAmount,
                        out bool avoidedSpecialCarAmount))
                {
                    continue;
                }

                need.m_Resource =
                    Resource.Vehicles;

                need.m_Amount =
                    vehicleAmount;

                needRef.ValueRW =
                    need;

                vehicleInjectedNeeds++;
                vehicleInjectedUnits +=
                    vehicleAmount;

                if (avoidedSpecialCarAmount)
                {
                    avoidedCarAmount++;
                }
            }

            FinishUpdate(
                readyHouseholds,
                vanillaCreatedNeeds,
                emptyAfterVanilla,
                injectedNeeds,
                injectedUnits,
                vehicleInjectedNeeds,
                vehicleInjectedUnits,
                avoidedCarAmount);
        }

        private void FinishUpdate(
            int readyHouseholds,
            int vanillaCreatedNeeds,
            int emptyAfterVanilla,
            int injectedNeeds,
            long injectedUnits,
            int vehicleInjectedNeeds,
            long vehicleInjectedUnits,
            int avoidedCarAmount)
        {
            m_UpdateCount++;

            m_WindowReadyHouseholds +=
                readyHouseholds;

            m_WindowVanillaCreatedNeeds +=
                vanillaCreatedNeeds;

            m_WindowEmptyAfterVanilla +=
                emptyAfterVanilla;

            m_WindowInjectedNeeds +=
                injectedNeeds;

            m_WindowInjectedUnits +=
                injectedUnits;

            m_WindowVehicleInjectedNeeds +=
                vehicleInjectedNeeds;

            m_WindowVehicleInjectedUnits +=
                vehicleInjectedUnits;

            m_WindowAvoidedCarAmount +=
                avoidedCarAmount;

            if (m_UpdateCount %
                kLogEveryUpdates == 0)
            {
                LogBalanceWindow();
            }
        }
    }
}

#endif
