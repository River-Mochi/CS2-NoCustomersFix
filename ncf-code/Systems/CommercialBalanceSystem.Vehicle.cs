// <copyright file="CommercialBalanceSystem.Vehicle.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalanceSystem.Vehicle.cs
// Purpose: Vehicle-store pressure and safe generic Vehicle correction.


using System;

using Game.Economy;
using Game.Prefabs;
using Game.Simulation;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    public partial class CommercialBalanceSystem
    {
        private long m_VehicleServiceAvailable;
        private long m_VehicleMaxService;
        private long m_VehicleStock;

        private int m_VehicleShopCount;
        private int m_VehicleWarningCount;

        private long m_VehicleExcess;
        private long m_WorkingVehicleExcess;

        private long m_PerUpdateVehicleBudget;
        private long m_RemainingVehicleBudget;

        private void ResetVehicleState()
        {
            ResetVehiclePressure();

            m_WorkingVehicleExcess = 0;

            m_PerUpdateVehicleBudget = 0;
            m_RemainingVehicleBudget = 0;
        }

        private void ResetVehiclePressure()
        {
            m_VehicleServiceAvailable = 0;
            m_VehicleMaxService = 0;
            m_VehicleStock = 0;

            m_VehicleShopCount = 0;
            m_VehicleWarningCount = 0;

            m_VehicleExcess = 0;
        }

        private void RecordVehiclePressure(
            int available,
            int maxService,
            int stock,
            bool hasWarning)
        {
            m_VehicleShopCount++;

            m_VehicleServiceAvailable +=
                available;

            m_VehicleMaxService +=
                maxService;

            m_VehicleStock +=
                stock;

            if (hasWarning)
            {
                m_VehicleWarningCount++;
            }

            if (stock <= 200)
            {
                return;
            }

            int targetService =
                (int)math.floor(
                    maxService *
                    kTargetServiceRatio);

            int excess =
                math.max(
                    0,
                    available - targetService);

            m_VehicleExcess +=
                excess;
        }

        private void PrepareVehicleCorrectionWindow()
        {
            m_WorkingVehicleExcess =
                m_VehicleExcess;

            m_PerUpdateVehicleBudget =
                CalculatePerUpdateBudget(
                    m_WorkingVehicleExcess);
        }

        private void BeginVehicleCorrectionUpdate()
        {
            m_RemainingVehicleBudget =
                Math.Min(
                    m_PerUpdateVehicleBudget,
                    m_WorkingVehicleExcess);
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
                resourceDatas[vehiclePrefab];

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

            // Vanilla interprets an exact Vehicle need of 50 as the special
            // personal-car purchase path. Corrective NEC demand must never
            // accidentally create extra personal cars.
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
    }
}

