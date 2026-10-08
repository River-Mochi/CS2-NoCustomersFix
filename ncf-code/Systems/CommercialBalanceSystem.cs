// <copyright file="CommercialBalanceSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the MIT License. You may not use this file except in compliance with this License.
// See LICENSE file in the project root for full license information.
// This notice and the MIT License notice must be kept with
// all copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/CommercialBalanceSystem.cs
// Purpose: DEBUG-only prototype that adds controlled physical retail demand
// when commercial shops have excessive unused service.

#if DEBUG

using System;

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
    /// Runs immediately after vanilla HouseholdBehaviorSystem.
    ///
    /// V4 does not reroll normal vanilla shopping choices.
    /// It only adds a corrective physical retail need to households that were
    /// ready to shop before vanilla ran but still have no need afterward.
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

            BuildCommercialPressure(
                serviceCompanyDatas,
                industrialProcessDatas,
                resourceDatas);

            PrepareCorrectionBudget();

            if (!m_ConfigurationLogged)
            {
                m_ConfigurationLogged = true;

                LogUtils.Info(
                    "[CWD-BALANCE] ACTIVE DEBUG prototype v4. " +
                    "Existing vanilla shopping choices are untouched. " +
                    "V4 only adds physical retail demand when individual " +
                    "shops have Service above 85% and stock above 200. " +
                    "Correction is spread across 16 balance updates. " +
                    "Vehicles, leisure, and office resources are untouched.");
            }

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

                readyHouseholds++;

                Game.Citizens.HouseholdNeed need =
                    needRef.ValueRO;

                // V4 deliberately leaves every vanilla-created need alone.
                if (need.m_Resource !=
                        Resource.NoResource)
                {
                    vanillaCreatedNeeds++;
                    continue;
                }

                emptyAfterVanilla++;

                if (m_RemainingInjectionBudget <= 0 ||
                    m_TotalCorrectionUnits <= 0)
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

                if (!TryCreateCorrectiveNeed(
                        householdEntity,
                        simulationFrame,
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
                        out Resource targetResource,
                        out int amount))
                {
                    continue;
                }

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
            }

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

            if (m_UpdateCount %
                kLogEveryUpdates == 0)
            {
                LogBalanceWindow();
            }
        }
    }
}

#endif
