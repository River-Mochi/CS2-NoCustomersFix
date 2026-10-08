// <copyright file="CommercialBalanceSystem.State.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/CommercialBalanceSystem.State.cs
// Purpose: Commercial pressure calculation for demand-balance prototype.

#if DEBUG

using System;

using Game.Economy;
using Game.Prefabs;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    public partial class CommercialBalanceSystem
    {
        // Keep shops comfortably below the vanilla ~90% warning threshold.
        //
        // Vanilla production already begins throttling at 80%, so 85% gives
        // vanilla production control room while leaving a warning buffer.
        private const float kTargetServiceRatio = 0.85f;

        // Spread the currently measured Service excess over one complete set
        // of the 16 household update buckets instead of dumping all corrective
        // demand into a single update.
        private const int kCorrectionHorizonUpdates = 16;

        private const int kLogEveryUpdates = 256;

        private readonly long[] m_ServiceAvailable =
            new long[EconomyUtils.ResourceCount];

        private readonly long[] m_MaxService =
            new long[EconomyUtils.ResourceCount];

        // Exact Service units currently sitting above the 85% target.
        //
        // This is calculated shop-by-shop, not from a category average.
        private readonly long[] m_ExcessService =
            new long[EconomyUtils.ResourceCount];

        // Working copy reduced as corrective needs are created this update.
        private readonly long[] m_WorkingCorrectionUnits =
            new long[EconomyUtils.ResourceCount];

        private readonly float[] m_UnsoldRatio =
            new float[EconomyUtils.ResourceCount];

        private readonly int[] m_CompanyCount =
            new int[EconomyUtils.ResourceCount];

        private readonly int[] m_WarningCount =
            new int[EconomyUtils.ResourceCount];

        private readonly int[] m_WindowInjectedNeedsByResource =
            new int[EconomyUtils.ResourceCount];

        private readonly long[] m_WindowInjectedUnitsByResource =
            new long[EconomyUtils.ResourceCount];

        private long m_TotalExcessService;
        private long m_TotalCorrectionUnits;

        private long m_RemainingInjectionBudget;

        private void ResetBalanceState()
        {
            Array.Clear(
                m_ServiceAvailable,
                0,
                m_ServiceAvailable.Length);

            Array.Clear(
                m_MaxService,
                0,
                m_MaxService.Length);

            Array.Clear(
                m_ExcessService,
                0,
                m_ExcessService.Length);

            Array.Clear(
                m_WorkingCorrectionUnits,
                0,
                m_WorkingCorrectionUnits.Length);

            Array.Clear(
                m_UnsoldRatio,
                0,
                m_UnsoldRatio.Length);

            Array.Clear(
                m_CompanyCount,
                0,
                m_CompanyCount.Length);

            Array.Clear(
                m_WarningCount,
                0,
                m_WarningCount.Length);

            Array.Clear(
                m_WindowInjectedNeedsByResource,
                0,
                m_WindowInjectedNeedsByResource.Length);

            Array.Clear(
                m_WindowInjectedUnitsByResource,
                0,
                m_WindowInjectedUnitsByResource.Length);

            m_TotalExcessService = 0;
            m_TotalCorrectionUnits = 0;
            m_RemainingInjectionBudget = 0;
        }

        private void BuildCommercialPressure(
            ComponentLookup<
                Game.Companies.ServiceCompanyData>
                    serviceCompanyDatas,
            ComponentLookup<
                IndustrialProcessData>
                    industrialProcessDatas,
            ComponentLookup<
                ResourceData>
                    resourceDatas)
        {
            Array.Clear(
                m_ServiceAvailable,
                0,
                m_ServiceAvailable.Length);

            Array.Clear(
                m_MaxService,
                0,
                m_MaxService.Length);

            Array.Clear(
                m_ExcessService,
                0,
                m_ExcessService.Length);

            Array.Clear(
                m_CompanyCount,
                0,
                m_CompanyCount.Length);

            Array.Clear(
                m_WarningCount,
                0,
                m_WarningCount.Length);

            Array.Clear(
                m_UnsoldRatio,
                0,
                m_UnsoldRatio.Length);

            m_TotalExcessService = 0;

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

                Game.Companies.ServiceCompanyData
                    serviceData =
                        serviceCompanyDatas[
                            companyPrefab];

                if (serviceData.m_MaxService <= 0)
                {
                    continue;
                }

                Resource resource =
                    industrialProcessDatas[
                        companyPrefab]
                        .m_Output.m_Resource;

                if (!IsPhysicalRetailResource(
                        resource,
                        ref resourceDatas))
                {
                    continue;
                }

                int resourceIndex =
                    EconomyUtils.GetResourceIndex(
                        resource);

                if (resourceIndex < 0 ||
                    resourceIndex >=
                        m_MaxService.Length)
                {
                    continue;
                }

                int available =
                    math.clamp(
                        serviceRef.ValueRO
                            .m_ServiceAvailable,
                        0,
                        serviceData.m_MaxService);

                m_ServiceAvailable[
                    resourceIndex] +=
                        available;

                m_MaxService[
                    resourceIndex] +=
                        serviceData.m_MaxService;

                m_CompanyCount[
                    resourceIndex]++;

                if (notificationsRef.ValueRO
                        .m_NoCustomersEntity !=
                    Entity.Null)
                {
                    m_WarningCount[
                        resourceIndex]++;
                }

                // Match the important stock side of vanilla's warning check.
                // A shop at <=200 output stock cannot currently show the
                // normal No Customers warning, so do not manufacture extra
                // demand specifically to clear it.
                int stock = 0;

                if (resourcesLookup.HasBuffer(
                        company))
                {
                    stock =
                        EconomyUtils.GetResources(
                            resource,
                            resourcesLookup[
                                company]);
                }

                if (stock <= 200)
                {
                    continue;
                }

                int targetService =
                    (int)math.floor(
                        serviceData.m_MaxService *
                        kTargetServiceRatio);

                int excess =
                    math.max(
                        0,
                        available -
                            targetService);

                if (excess <= 0)
                {
                    continue;
                }

                m_ExcessService[
                    resourceIndex] +=
                        excess;

                m_TotalExcessService +=
                    excess;
            }

            for (int i = 0;
                i < m_UnsoldRatio.Length;
                i++)
            {
                if (m_MaxService[i] <= 0)
                {
                    continue;
                }

                m_UnsoldRatio[i] =
                    math.saturate(
                        (float)m_ServiceAvailable[i] /
                        m_MaxService[i]);
            }
        }

        private void PrepareCorrectionBudget()
        {
            Array.Copy(
                m_ExcessService,
                m_WorkingCorrectionUnits,
                m_ExcessService.Length);

            m_TotalCorrectionUnits =
                m_TotalExcessService;

            if (m_TotalCorrectionUnits <= 0)
            {
                m_RemainingInjectionBudget = 0;
                return;
            }

            // Ceiling division so a small remaining problem still receives
            // some corrective demand.
            m_RemainingInjectionBudget =
                (m_TotalCorrectionUnits +
                    kCorrectionHorizonUpdates -
                    1) /
                kCorrectionHorizonUpdates;

            if (m_RemainingInjectionBudget < 1)
            {
                m_RemainingInjectionBudget = 1;
            }
        }

        private bool IsPhysicalRetailResource(
            Resource resource,
            ref ComponentLookup<ResourceData>
                resourceDatas)
        {
            if (resource == Resource.NoResource ||
                resource == Resource.Vehicles ||
                EconomyUtils.IsOfficeResource(
                    resource))
            {
                return false;
            }

            Entity resourcePrefab =
                m_ResourceSystem.GetPrefab(
                    resource);

            if (resourcePrefab == Entity.Null ||
                !resourceDatas.HasComponent(
                    resourcePrefab))
            {
                return false;
            }

            return !resourceDatas[
                resourcePrefab].m_IsLeisure;
        }
    }
}

#endif
