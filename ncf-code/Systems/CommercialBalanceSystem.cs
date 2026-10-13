// <copyright file="CommercialBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalanceSystem.cs
// Purpose: Post-vanilla physical retail + Vehicle NEC correction.

using System;

using Colossal.Serialization.Entities;

using CS2Shared.RiverMochi;

using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;

using Unity.Collections;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Adds corrective commercial demand after vanilla HouseholdBehaviorSystem.
    /// </summary>
    public partial class CommercialBalanceSystem : GameSystemBase
    {
        private SimulationSystem m_SimulationSystem = null!;
        private ResourceSystem m_ResourceSystem = null!;
        private CommercialBalancePrepareSystem m_PrepareSystem = null!;

        private int m_UpdateCount;

        private int m_WindowReadyHouseholds;
        private int m_WindowVanillaCreatedNeeds;
        private int m_WindowEmptyAfterVanilla;
        private int m_WindowExtraNeedCap;

        private int m_WindowInjectedNeeds;
        private long m_WindowInjectedUnits;

        private int m_WindowVehicleInjectedNeeds;
        private long m_WindowVehicleInjectedUnits;
        private int m_WindowAvoidedCarAmount;

#if DEBUG
        private int m_WindowFreshCandidates;
        private int m_WindowRepeatCandidates;
        private int m_WindowRepeatAllowed;
        private int m_WindowFreshInjected;
        private int m_WindowRepeatInjected;
        private int m_LastRepeatPercent;
#endif

        private bool m_ConfigurationLogged;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 262144 / (HouseholdBehaviorSystem.kUpdatesPerDay * 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_PrepareSystem = World.GetOrCreateSystemManaged<CommercialBalancePrepareSystem>();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            ResetBalanceState();

            m_UpdateCount = 0;

            m_WindowReadyHouseholds = 0;
            m_WindowVanillaCreatedNeeds = 0;
            m_WindowEmptyAfterVanilla = 0;
            m_WindowExtraNeedCap = 0;

            m_WindowInjectedNeeds = 0;
            m_WindowInjectedUnits = 0;

            m_WindowVehicleInjectedNeeds = 0;
            m_WindowVehicleInjectedUnits = 0;
            m_WindowAvoidedCarAmount = 0;

#if DEBUG
            m_WindowFreshCandidates = 0;
            m_WindowRepeatCandidates = 0;
            m_WindowRepeatAllowed = 0;
            m_WindowFreshInjected = 0;
            m_WindowRepeatInjected = 0;
            m_LastRepeatPercent = 100;
#endif

            m_ConfigurationLogged = false;
        }

        protected override void OnUpdate()
        {
            uint simulationFrame = m_SimulationSystem.frameIndex;

            NativeArray<Entity> readyHouseholds =
                m_PrepareSystem.CompleteAndGetSnapshot(simulationFrame);

            if (!readyHouseholds.IsCreated)
            {
                return;
            }

            ComponentLookup<Game.Companies.ServiceCompanyData> serviceCompanyDatas =
                SystemAPI.GetComponentLookup<Game.Companies.ServiceCompanyData>(true);

            ComponentLookup<IndustrialProcessData> industrialProcessDatas =
                SystemAPI.GetComponentLookup<IndustrialProcessData>(true);

            ComponentLookup<ResourceData> resourceDatas =
                SystemAPI.GetComponentLookup<ResourceData>(true);

            RefreshCommercialPressure(
                serviceCompanyDatas,
                industrialProcessDatas,
                resourceDatas);

#if DEBUG
            m_LastRepeatPercent = 100;

            if (!m_ConfigurationLogged)
            {
                m_ConfigurationLogged = true;

                LogUtils.Info(
                    "[NCF-BALANCE] ACTIVE DEBUG. " +
                    "Vanilla runs first. Repeat shoppers remain eligible. " +
                    "Extra NCF shopping needs are capped at vanilla's " +
                    "current new-need rate to prevent queue saturation.");
            }
#endif

            if (m_RemainingInjectionBudget <= 0 &&
                m_RemainingVehicleBudget <= 0)
            {
                FinishUpdate(
                    readyHouseholds.Length,
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

            if (!SystemAPI.TryGetSingleton<EconomyParameterData>(
                    out EconomyParameterData economyParameters))
            {
                return;
            }

            float resourceDemandMultiplier = 1f;

            if (SystemAPI.TryGetSingleton<Game.Prefabs.Modes.ModeSettingData>(
                    out Game.Prefabs.Modes.ModeSettingData modeSetting) &&
                modeSetting.m_Enable)
            {
                resourceDemandMultiplier =
                    modeSetting.m_ResourceDemandPerCitizenMultiplier;
            }

            ComponentLookup<Game.Citizens.HouseholdNeed> householdNeeds =
                SystemAPI.GetComponentLookup<Game.Citizens.HouseholdNeed>();

            ComponentLookup<Game.Citizens.Household> households =
                SystemAPI.GetComponentLookup<Game.Citizens.Household>(true);

            ComponentLookup<Game.Buildings.PropertyRenter> propertyRenters =
                SystemAPI.GetComponentLookup<Game.Buildings.PropertyRenter>(true);

            ComponentLookup<Game.Citizens.Citizen> citizenDatas =
                SystemAPI.GetComponentLookup<Game.Citizens.Citizen>(true);

            ComponentLookup<ConsumptionData> consumptionDatas =
                SystemAPI.GetComponentLookup<ConsumptionData>(true);

            ComponentLookup<PrefabRef> prefabRefs =
                SystemAPI.GetComponentLookup<PrefabRef>(true);

            BufferLookup<Game.Citizens.HouseholdCitizen> householdCitizens =
                SystemAPI.GetBufferLookup<Game.Citizens.HouseholdCitizen>(true);

            BufferLookup<Game.Economy.Resources> householdResources =
                SystemAPI.GetBufferLookup<Game.Economy.Resources>(true);

            BufferLookup<Game.Vehicles.OwnedVehicle> ownedVehicles =
                SystemAPI.GetBufferLookup<Game.Vehicles.OwnedVehicle>(true);

            BufferLookup<Game.Buildings.Renter> renterBuffers =
                SystemAPI.GetBufferLookup<Game.Buildings.Renter>(true);

            int readyCount = readyHouseholds.Length;
            int vanillaCreatedNeeds = 0;
            int emptyAfterVanilla = 0;

            // First pass is intentionally lightweight. Vanilla's actual new-need
            // rate becomes backpressure for how many extra NCF needs we may add.
            for (int i = 0; i < readyHouseholds.Length; i++)
            {
                Entity householdEntity = readyHouseholds[i];

                if (!householdNeeds.HasComponent(householdEntity))
                {
                    continue;
                }

                if (householdNeeds[householdEntity].m_Resource != Resource.NoResource)
                {
                    vanillaCreatedNeeds++;
                }
                else
                {
                    emptyAfterVanilla++;
                }
            }

            // NCF can at most double vanilla's current new shopping-need rate.
            // This still adds substantial demand without flooding HouseholdNeed.
            int extraNeedCap = Math.Max(1, vanillaCreatedNeeds);
            int extraNeedsCreated = 0;

            int injectedNeeds = 0;
            long injectedUnits = 0;

            int vehicleInjectedNeeds = 0;
            long vehicleInjectedUnits = 0;
            int avoidedCarAmount = 0;

            for (int i = 0; i < readyHouseholds.Length; i++)
            {
                if ((m_RemainingInjectionBudget <= 0 &&
                     m_RemainingVehicleBudget <= 0) ||
                    extraNeedsCreated >= extraNeedCap)
                {
                    break;
                }

                Entity householdEntity = readyHouseholds[i];

                if (!householdNeeds.HasComponent(householdEntity) ||
                    !households.HasComponent(householdEntity) ||
                    !propertyRenters.HasComponent(householdEntity) ||
                    !householdCitizens.HasBuffer(householdEntity) ||
                    !householdResources.HasBuffer(householdEntity))
                {
                    continue;
                }

                Game.Citizens.HouseholdNeed need =
                    householdNeeds[householdEntity];

                // Vanilla always wins.
                if (need.m_Resource != Resource.NoResource)
                {
                    continue;
                }

                Game.Citizens.Household household =
                    households[householdEntity];

                // Vanilla consumes previous purchases before allowing another
                // shopping cycle. Check this after vanilla had its update.
                if (household.m_Resources > 0)
                {
                    continue;
                }

                bool repeatShopper =
                    household.m_ShoppedValuePerDay != 0;

#if DEBUG
                if (repeatShopper)
                {
                    m_WindowRepeatCandidates++;
                    m_WindowRepeatAllowed++;
                }
                else
                {
                    m_WindowFreshCandidates++;
                }
#endif

                Game.Buildings.PropertyRenter propertyRenter =
                    propertyRenters[householdEntity];

                DynamicBuffer<Game.Citizens.HouseholdCitizen> citizens =
                    householdCitizens[householdEntity];

                DynamicBuffer<Game.Economy.Resources> resources =
                    householdResources[householdEntity];

                if (m_RemainingInjectionBudget > 0 &&
                    m_TotalCorrectionUnits > 0 &&
                    TryCreateCorrectiveNeed(
                        householdEntity,
                        simulationFrame,
                        household,
                        propertyRenter,
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
                    need.m_Resource = targetResource;
                    need.m_Amount = amount;

                    householdNeeds[householdEntity] = need;

                    injectedNeeds++;
                    injectedUnits += amount;
                    extraNeedsCreated++;

#if DEBUG
                    if (repeatShopper)
                    {
                        m_WindowRepeatInjected++;
                    }
                    else
                    {
                        m_WindowFreshInjected++;
                    }
#endif

                    int resourceIndex =
                        EconomyUtils.GetResourceIndex(targetResource);

                    if (resourceIndex >= 0 &&
                        resourceIndex < m_WindowInjectedNeedsByResource.Length)
                    {
                        m_WindowInjectedNeedsByResource[resourceIndex]++;
                        m_WindowInjectedUnitsByResource[resourceIndex] += amount;
                    }

                    continue;
                }

                if (m_RemainingVehicleBudget <= 0 ||
                    m_WorkingVehicleExcess <= 0)
                {
                    continue;
                }

                if (!TryCreateVehicleNeed(
                        householdEntity,
                        household,
                        propertyRenter,
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

                need.m_Resource = Resource.Vehicles;
                need.m_Amount = vehicleAmount;

                householdNeeds[householdEntity] = need;

                vehicleInjectedNeeds++;
                vehicleInjectedUnits += vehicleAmount;
                extraNeedsCreated++;

#if DEBUG
                if (repeatShopper)
                {
                    m_WindowRepeatInjected++;
                }
                else
                {
                    m_WindowFreshInjected++;
                }
#endif

                if (avoidedSpecialCarAmount)
                {
                    avoidedCarAmount++;
                }
            }

            FinishUpdate(
                readyCount,
                vanillaCreatedNeeds,
                emptyAfterVanilla,
                extraNeedCap,
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
            int extraNeedCap,
            int injectedNeeds,
            long injectedUnits,
            int vehicleInjectedNeeds,
            long vehicleInjectedUnits,
            int avoidedCarAmount)
        {
            m_UpdateCount++;

            m_WindowReadyHouseholds += readyHouseholds;
            m_WindowVanillaCreatedNeeds += vanillaCreatedNeeds;
            m_WindowEmptyAfterVanilla += emptyAfterVanilla;
            m_WindowExtraNeedCap += extraNeedCap;

            m_WindowInjectedNeeds += injectedNeeds;
            m_WindowInjectedUnits += injectedUnits;

            m_WindowVehicleInjectedNeeds += vehicleInjectedNeeds;
            m_WindowVehicleInjectedUnits += vehicleInjectedUnits;
            m_WindowAvoidedCarAmount += avoidedCarAmount;

#if DEBUG
            if (m_UpdateCount % kLogEveryUpdates == 0)
            {
                LogBalanceWindow();
            }
#endif
        }
    }
}
