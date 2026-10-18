// <copyright file="CommercialBalanceSystem.Selection.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/CommercialBalanceSystem.Selection.cs
// Purpose: Select and create corrective physical retail shopping needs.


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
        private bool TryCreateCorrectiveNeed(
            Entity householdEntity,
            uint simulationFrame,
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
            out Resource targetResource,
            out int amount)
        {
            targetResource =
                Resource.NoResource;

            amount = 0;

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

            targetResource =
                SelectCorrectiveResource(
                    householdEntity,
                    simulationFrame,
                    disposableIncome,
                    affluenceIncome,
                    carCount,
                    citizens,
                    ref citizenDatas,
                    ref resourceDatas);

            if (targetResource ==
                Resource.NoResource)
            {
                return false;
            }

            int resourceIndex =
                EconomyUtils.GetResourceIndex(
                    targetResource);

            if (resourceIndex < 0 ||
                resourceIndex >=
                    m_WorkingCorrectionUnits.Length ||
                m_WorkingCorrectionUnits[
                    resourceIndex] <= 0)
            {
                return false;
            }

            Entity resourcePrefab =
                m_ResourceSystem.GetPrefab(
                    targetResource);

            if (resourcePrefab == Entity.Null ||
                !resourceDatas.HasComponent(
                    resourcePrefab))
            {
                return false;
            }

            float marketPrice =
                EconomyUtils.GetMarketPrice(
                    resourceDatas[
                        resourcePrefab]);

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
                    m_RemainingInjectionBudget,
                    m_WorkingCorrectionUnits[
                        resourceIndex]);

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

            m_RemainingInjectionBudget -=
                amount;

            m_WorkingCorrectionUnits[
                resourceIndex] -=
                    amount;

            m_TotalCorrectionUnits =
                Math.Max(
                    0,
                    m_TotalCorrectionUnits -
                        amount);

            return true;
        }

        /// <summary>
        /// Selects only among physical retail resources that currently have
        /// actual Service units above the 82% target.
        ///
        /// Pressure comes mainly from excess Service units. Vanilla
        /// household preference is retained as a soft weighting with sqrt().
        /// Stops huge vanilla preference differences from completely
        /// overwhelming the commercial correction.
        /// </summary>
        private Resource SelectCorrectiveResource(
            Entity household,
            uint simulationFrame,
            int disposableIncome,
            int affluenceIncome,
            int carCount,
            DynamicBuffer<Game.Citizens.HouseholdCitizen>
                citizens,
            ref ComponentLookup<Game.Citizens.Citizen>
                citizenDatas,
            ref ComponentLookup<ResourceData>
                resourceDatas)
        {
            float totalScore = 0f;

            ResourceIterator iterator =
                ResourceIterator.GetIterator();

            while (iterator.Next())
            {
                Resource resource =
                    iterator.resource;

                int resourceIndex =
                    EconomyUtils.GetResourceIndex(
                        resource);

                if (resourceIndex < 0 ||
                    resourceIndex >=
                        m_WorkingCorrectionUnits.Length ||
                    m_WorkingCorrectionUnits[
                        resourceIndex] <= 0)
                {
                    continue;
                }

                if (!IsPhysicalRetailResource(
                        resource,
                        ref resourceDatas))
                {
                    continue;
                }

                Entity resourcePrefab =
                    m_ResourceSystem.GetPrefab(
                        resource);

                ResourceData resourceData =
                    resourceDatas[
                        resourcePrefab];

                int vanillaWeight =
                    HouseholdBehaviorSystem
                        .GetResourceShopWeightWithAge(
                            disposableIncome,
                            resourceData,
                            carCount,
                            leisureIncluded: false,
                            citizens,
                            ref citizenDatas,
                            affluenceIncome);

                if (vanillaWeight <= 0)
                {
                    continue;
                }

                float preference =
                    math.sqrt(
                        (float)vanillaWeight);

                float score =
                    m_WorkingCorrectionUnits[
                        resourceIndex] *
                    preference;

                totalScore +=
                    score;
            }

            if (totalScore <= 0f ||
                !math.isfinite(totalScore))
            {
                return Resource.NoResource;
            }

            uint seed =
                unchecked(
                    ((uint)household.Index *
                        747796405u) ^
                    ((uint)household.Version *
                        2891336453u) ^
                    (simulationFrame *
                        277803737u) ^
                    0xC4D4A11u);

            if (seed == 0)
            {
                seed = 1;
            }

            Unity.Mathematics.Random random =
                new(seed);

            float selection =
                random.NextFloat(
                    0f,
                    totalScore);

            iterator =
                ResourceIterator.GetIterator();

            while (iterator.Next())
            {
                Resource resource =
                    iterator.resource;

                int resourceIndex =
                    EconomyUtils.GetResourceIndex(
                        resource);

                if (resourceIndex < 0 ||
                    resourceIndex >=
                        m_WorkingCorrectionUnits.Length ||
                    m_WorkingCorrectionUnits[
                        resourceIndex] <= 0)
                {
                    continue;
                }

                if (!IsPhysicalRetailResource(
                        resource,
                        ref resourceDatas))
                {
                    continue;
                }

                Entity resourcePrefab =
                    m_ResourceSystem.GetPrefab(
                        resource);

                ResourceData resourceData =
                    resourceDatas[
                        resourcePrefab];

                int vanillaWeight =
                    HouseholdBehaviorSystem
                        .GetResourceShopWeightWithAge(
                            disposableIncome,
                            resourceData,
                            carCount,
                            leisureIncluded: false,
                            citizens,
                            ref citizenDatas,
                            affluenceIncome);

                if (vanillaWeight <= 0)
                {
                    continue;
                }

                float preference =
                    math.sqrt(
                        (float)vanillaWeight);

                float score =
                    m_WorkingCorrectionUnits[
                        resourceIndex] *
                    preference;

                if (selection < score)
                {
                    return resource;
                }

                selection -=
                    score;
            }

            return Resource.NoResource;
        }
    }
}

