// <copyright file="VehicleBalanceSystem.State.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/VehicleBalanceSystem.State.cs
// Purpose: Vehicle-store pressure, corrective amount, and DEBUG logging.

#if DEBUG

using System;
using System.Globalization;

using CS2Shared.RiverMochi;

using Game.Economy;
using Game.Prefabs;
using Game.Simulation;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    public partial class VehicleBalanceSystem
    {
        // Same target that worked well for ordinary physical retail.
        private const float kVehicleTargetServiceRatio = 0.85f;

        // Spread the currently measured excess across one full pass of the
        // 16 household update buckets.
        private const int kVehicleCorrectionHorizonUpdates = 16;

        private const int kVehicleLogEveryUpdates = 256;

        private long m_VehicleServiceAvailable;
        private long m_VehicleMaxService;
        private long m_VehicleStock;

        private int m_VehicleShopCount;
        private int m_VehicleWarningCount;

        private long m_VehicleExcess;
        private long m_WorkingVehicleExcess;

        private long m_RemainingVehicleBudget;

        private void ResetVehicleState()
        {
            m_VehicleServiceAvailable = 0;
            m_VehicleMaxService = 0;
            m_VehicleStock = 0;

            m_VehicleShopCount = 0;
            m_VehicleWarningCount = 0;

            m_VehicleExcess = 0;
            m_WorkingVehicleExcess = 0;

            m_RemainingVehicleBudget = 0;
        }

        private void BuildVehiclePressure(
            ComponentLookup<
                Game.Companies.ServiceCompanyData>
                    serviceCompanyDatas,
            ComponentLookup<
                IndustrialProcessData>
                    industrialProcessDatas)
        {
            m_VehicleServiceAvailable = 0;
            m_VehicleMaxService = 0;
            m_VehicleStock = 0;

            m_VehicleShopCount = 0;
            m_VehicleWarningCount = 0;

            m_VehicleExcess = 0;

            BufferLookup<Game.Economy.Resources>
                resourcesLookup =
                    SystemAPI.GetBufferLookup<
                        Game.Economy.Resources>(
                            true);

            foreach ((
                RefRO<Game.Companies.ServiceAvailable>
                    serviceRef,
                RefRO<Game.Companies.CompanyNotifications>
                    notificationsRef,
                RefRO<PrefabRef>
                    prefabRef,
                Entity company) in
                SystemAPI
                    .Query<
                        RefRO<
                            Game.Companies.ServiceAvailable>,
                        RefRO<
                            Game.Companies.CompanyNotifications>,
                        RefRO<
                            PrefabRef>>()
                    .WithAll<
                        Game.Companies.CommercialCompany>()
                    .WithNone<
                        Game.Common.Deleted,
                        Game.Tools.Temp>()
                    .WithEntityAccess())
            {
                Entity companyPrefab =
                    prefabRef.ValueRO.m_Prefab;

                if (!serviceCompanyDatas.HasComponent(
                        companyPrefab) ||
                    !industrialProcessDatas.HasComponent(
                        companyPrefab))
                {
                    continue;
                }

                Resource outputResource =
                    industrialProcessDatas[
                        companyPrefab]
                        .m_Output.m_Resource;

                if (outputResource !=
                    Resource.Vehicles)
                {
                    continue;
                }

                Game.Companies.ServiceCompanyData
                    serviceData =
                        serviceCompanyDatas[
                            companyPrefab];

                if (serviceData.m_MaxService <= 0)
                {
                    continue;
                }

                int available =
                    math.clamp(
                        serviceRef.ValueRO
                            .m_ServiceAvailable,
                        0,
                        serviceData.m_MaxService);

                int stock = 0;

                if (resourcesLookup.HasBuffer(
                        company))
                {
                    stock =
                        EconomyUtils.GetResources(
                            Resource.Vehicles,
                            resourcesLookup[
                                company]);
                }

                m_VehicleShopCount++;

                m_VehicleServiceAvailable +=
                    available;

                m_VehicleMaxService +=
                    serviceData.m_MaxService;

                m_VehicleStock +=
                    stock;

                if (notificationsRef.ValueRO
                        .m_NoCustomersEntity !=
                    Entity.Null)
                {
                    m_VehicleWarningCount++;
                }

                // Match the normal warning's physical-stock gate.
                if (stock <= 200)
                {
                    continue;
                }

                int targetService =
                    (int)math.floor(
                        serviceData.m_MaxService *
                        kVehicleTargetServiceRatio);

                int excess =
                    math.max(
                        0,
                        available -
                            targetService);

                m_VehicleExcess +=
                    excess;
            }
        }

        private void PrepareVehicleBudget()
        {
            m_WorkingVehicleExcess =
                m_VehicleExcess;

            if (m_WorkingVehicleExcess <= 0)
            {
                m_RemainingVehicleBudget = 0;
                return;
            }

            m_RemainingVehicleBudget =
                (m_WorkingVehicleExcess +
                    kVehicleCorrectionHorizonUpdates -
                    1) /
                kVehicleCorrectionHorizonUpdates;

            if (m_RemainingVehicleBudget < 1)
            {
                m_RemainingVehicleBudget = 1;
            }
        }

        private bool TryCreateVehicleNeed(
            Entity householdEntity,
            Game.Citizens.Household household,
            Game.Buildings.PropertyRenter propertyRenter,
            float resourceDemandMultiplier,
            EconomyParameterData economyParameters,
            DynamicBuffer<Game.Citizens.HouseholdCitizen>
                citizens,
            DynamicBuffer<Game.Economy.Resources>
                resources,
            BufferLookup<Game.Vehicles.OwnedVehicle>
                ownedVehicles,
            BufferLookup<Game.Buildings.Renter>
                renterBuffers,
            ComponentLookup<ConsumptionData>
                consumptionDatas,
            ComponentLookup<PrefabRef>
                prefabRefs,
            ComponentLookup<Game.Citizens.Citizen>
                citizenDatas,
            ComponentLookup<ResourceData>
                resourceDatas,
            out int amount,
            out bool avoidedSpecialCarAmount)
        {
            amount = 0;
            avoidedSpecialCarAmount = false;

            if (citizens.Length == 0)
            {
                return false;
            }

            int spendableMoney =
                EconomyUtils.GetHouseholdSpendableMoney(
                    household,
                    resources,
                    ref renterBuffers,
                    ref consumptionDatas,
                    ref prefabRefs,
                    propertyRenter);

            if (spendableMoney <
                HouseholdBehaviorSystem
                    .kMinimumShoppingMoney)
            {
                return false;
            }

            Entity vehiclePrefab =
                m_ResourceSystem.GetPrefab(
                    Resource.Vehicles);

            if (vehiclePrefab == Entity.Null ||
                !resourceDatas.HasComponent(
                    vehiclePrefab))
            {
                return false;
            }

            ResourceData vehicleResourceData =
                resourceDatas[
                    vehiclePrefab];

            int disposableIncome =
                EconomyUtils.GetHouseholdDisposableIncome(
                    household,
                    propertyRenter);

            int affluenceIncome =
                EconomyUtils.GetAffluenceReferenceIncome(
                    economyParameters) *
                citizens.Length;

            int carCount = 0;

            if (ownedVehicles.HasBuffer(
                    householdEntity))
            {
                carCount =
                    ownedVehicles[
                        householdEntity].Length;
            }

            // Keep vanilla's age/income/car-based Vehicle preference as an
            // eligibility check. The corrective controller increases demand,
            // but does not assign Vehicles to a household that vanilla gives
            // zero Vehicle shopping weight.
            int vanillaVehicleWeight =
                HouseholdBehaviorSystem
                    .GetResourceShopWeightWithAge(
                        disposableIncome,
                        vehicleResourceData,
                        carCount,
                        leisureIncluded: false,
                        citizens,
                        ref citizenDatas,
                        affluenceIncome);

            if (vanillaVehicleWeight <= 0)
            {
                return false;
            }

            float marketPrice =
                EconomyUtils.GetMarketPrice(
                    vehicleResourceData);

            if (marketPrice <= 0f)
            {
                return false;
            }

            // This deliberately follows vanilla's generic Vehicle-resource
            // purchase path rather than its real-car purchase path.
            amount =
                math.clamp(
                    (int)(
                        (float)spendableMoney /
                        marketPrice),
                    0,
                    HouseholdBehaviorSystem
                        .kMaxHouseholdNeedAmount);

            amount =
                (int)(
                    amount *
                    resourceDemandMultiplier);

            if (amount <= 0)
            {
                return false;
            }

            long allowed =
                Math.Min(
                    m_RemainingVehicleBudget,
                    m_WorkingVehicleExcess);

            if (allowed <= 0)
            {
                return false;
            }

            if (amount > allowed)
            {
                amount =
                    (int)Math.Min(
                        int.MaxValue,
                        allowed);
            }

            if (amount <= 0)
            {
                return false;
            }

            // CRITICAL:
            //
            // ResourceBuyerSystem creates a real PersonalCar only when:
            //
            //     resource == Vehicles
            //     amount   == HouseholdBehaviorSystem.kCarAmount (50)
            //
            // Corrective demand must never accidentally hit that exact amount.
            if (amount ==
                HouseholdBehaviorSystem.kCarAmount)
            {
                amount--;

                avoidedSpecialCarAmount = true;
            }

            if (amount <= 0)
            {
                return false;
            }

            m_RemainingVehicleBudget -=
                amount;

            m_WorkingVehicleExcess -=
                amount;

            return true;
        }

        private void LogVehicleWindow()
        {
            float unused =
                m_VehicleMaxService > 0
                    ? 100f *
                      m_VehicleServiceAvailable /
                      m_VehicleMaxService
                    : 0f;

            LogUtils.Info(
                "[CWD-VEHICLE] " +
                $"updates={kVehicleLogEveryUpdates} " +
                $"shops={m_VehicleShopCount} " +
                $"warnings={m_VehicleWarningCount} " +
                $"unused=" +
                $"{unused.ToString("F1", CultureInfo.InvariantCulture)}% " +
                $"stock={m_VehicleStock} " +
                $"excess85={m_VehicleExcess} " +
                $"ready={m_WindowReady} " +
                $"emptyAfterRetail={m_WindowEmpty} " +
                $"injected={m_WindowInjected} " +
                $"injectedUnits={m_WindowInjectedUnits} " +
                $"avoidedCarAmount={m_WindowAvoidedCarAmount}");

            m_WindowReady = 0;
            m_WindowEmpty = 0;

            m_WindowInjected = 0;
            m_WindowInjectedUnits = 0;

            m_WindowAvoidedCarAmount = 0;
        }
    }
}

#endif
