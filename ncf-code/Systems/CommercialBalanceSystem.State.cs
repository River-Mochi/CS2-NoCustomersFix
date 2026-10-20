// <copyright file="CommercialBalanceSystem.State.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalanceSystem.State.cs
// Purpose: Cached commercial NEC pressure and correction budgets.

using System;

using Game.Economy;
using Game.Prefabs;

using Unity.Entities;
using Unity.Mathematics;

namespace NoCustomersFix.Systems
{
    public partial class CommercialBalanceSystem
    {
        // Vanilla NEC appears at roughly >90% unused Service.
        // Keep some safety margin below that threshold.
        private const float kTargetServiceRatio = 0.82f;

        // Recheck actual company pressure every four correction updates.
        // removes most full-commercial scans while staying responsive.
        private const int kPressureRefreshUpdates = 4;

        // Spread measured excess across a complete set of the 16 household
        // update buckets rather than creating all corrective demand at once.
        private const int kCorrectionHorizonUpdates = 16;

#if DEBUG
        private const int kLogEveryUpdates = 256;
#endif

#if DEBUG
        private readonly long[] m_ServiceAvailable =
            new long[EconomyUtils.ResourceCount];

        private readonly long[] m_MaxService =
            new long[EconomyUtils.ResourceCount];
#endif

        private readonly long[] m_ExcessService =
            new long[EconomyUtils.ResourceCount];

        private readonly long[] m_WorkingCorrectionUnits =
            new long[EconomyUtils.ResourceCount];

        // Cache unchanged resource prefabs between company-pressure scans.
        private readonly Resource[] m_CachedRetailResources =
            new Resource[EconomyUtils.ResourceCount];
        private readonly ResourceData[] m_CachedRetailData =
            new ResourceData[EconomyUtils.ResourceCount];
        private int m_CachedRetailCount;

#if DEBUG
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
#endif

        private long m_TotalExcessService;
        private long m_TotalCorrectionUnits;

        private long m_PerUpdateInjectionBudget;
        private long m_RemainingInjectionBudget;

        private int m_PressureUpdateIndex;

        private void ResetBalanceState()
        {
#if DEBUG
            Array.Clear(
                m_ServiceAvailable,
                0,
                m_ServiceAvailable.Length);

            Array.Clear(
                m_MaxService,
                0,
                m_MaxService.Length);
#endif

            Array.Clear(
                m_ExcessService,
                0,
                m_ExcessService.Length);

            Array.Clear(
                m_WorkingCorrectionUnits,
                0,
                m_WorkingCorrectionUnits.Length);

#if DEBUG
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
#endif

            m_TotalExcessService = 0;
            m_TotalCorrectionUnits = 0;

            m_PerUpdateInjectionBudget = 0;
            m_RemainingInjectionBudget = 0;

            m_PressureUpdateIndex = 0;
            m_CachedRetailCount = 0;

            ResetVehicleState();
        }

        private void RefreshCommercialPressure(
            ComponentLookup<Game.Companies.ServiceCompanyData>
                serviceCompanyDatas,
            ComponentLookup<IndustrialProcessData>
                industrialProcessDatas,
            ComponentLookup<ResourceData>
                resourceDatas)
        {
            if (m_PressureUpdateIndex == 0)
            {
                BuildCommercialPressure(
                    serviceCompanyDatas,
                    industrialProcessDatas,
                    resourceDatas);

                RefreshCachedRetailResources(ref resourceDatas);
                PrepareCorrectionWindow();
            }

            BeginCorrectionUpdate();

            m_PressureUpdateIndex++;

            if (m_PressureUpdateIndex >=
                kPressureRefreshUpdates)
            {
                m_PressureUpdateIndex = 0;
            }
        }

        // ResourceIterator order is preserved for deterministic selection.
        // Only the live remaining-correction units change per household.
        private void RefreshCachedRetailResources(
            ref ComponentLookup<ResourceData> resourceDatas)
        {
            m_CachedRetailCount = 0;
            ResourceIterator iterator = ResourceIterator.GetIterator();

            while (iterator.Next())
            {
                Resource resource = iterator.resource;
                if (resource == Resource.NoResource ||
                    resource == Resource.Vehicles ||
                    EconomyUtils.IsOfficeResource(resource))
                {
                    continue;
                }

                Entity prefab = m_ResourceSystem.GetPrefab(resource);
                if (prefab == Entity.Null ||
                    !resourceDatas.HasComponent(prefab))
                {
                    continue;
                }

                ResourceData data = resourceDatas[prefab];
                if (data.m_IsLeisure)
                {
                    continue;
                }

                m_CachedRetailResources[m_CachedRetailCount] = resource;
                m_CachedRetailData[m_CachedRetailCount] = data;
                m_CachedRetailCount++;
            }
        }

        private void BuildCommercialPressure(
            ComponentLookup<Game.Companies.ServiceCompanyData>
                serviceCompanyDatas,
            ComponentLookup<IndustrialProcessData>
                industrialProcessDatas,
            ComponentLookup<ResourceData>
                resourceDatas)
        {
#if DEBUG
            Array.Clear(
                m_ServiceAvailable,
                0,
                m_ServiceAvailable.Length);

            Array.Clear(
                m_MaxService,
                0,
                m_MaxService.Length);
#endif

            Array.Clear(
                m_ExcessService,
                0,
                m_ExcessService.Length);

#if DEBUG
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
#endif

            m_TotalExcessService = 0;

            ResetVehiclePressure();

            BufferLookup<Game.Economy.Resources>
                resourcesLookup =
                    SystemAPI.GetBufferLookup<
                        Game.Economy.Resources>(true);

            foreach ((
                RefRO<Game.Companies.ServiceAvailable> serviceRef,
                RefRO<Game.Companies.CompanyNotifications>
                    notificationsRef,
                RefRO<PrefabRef> prefabRef,
                Entity company) in
                SystemAPI
                    .Query<
                        RefRO<Game.Companies.ServiceAvailable>,
                        RefRO<Game.Companies.CompanyNotifications>,
                        RefRO<PrefabRef>>()
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

                Game.Companies.ServiceCompanyData serviceData =
                    serviceCompanyDatas[companyPrefab];

                if (serviceData.m_MaxService <= 0)
                {
                    continue;
                }

                Resource resource =
                    industrialProcessDatas[companyPrefab]
                        .m_Output.m_Resource;

                int available =
                    math.clamp(
                        serviceRef.ValueRO.m_ServiceAvailable,
                        0,
                        serviceData.m_MaxService);

                int stock = 0;

                if (resourcesLookup.HasBuffer(company))
                {
                    stock =
                        EconomyUtils.GetResources(
                            resource,
                            resourcesLookup[company]);
                }

#if DEBUG
                bool hasWarning =
                    notificationsRef.ValueRO
                        .m_NoCustomersEntity !=
                    Entity.Null;
#else
                const bool hasWarning = false;
#endif

                // Vehicles use the same company scan but retain their special
                // generic-Vehicle purchase handling.
                if (resource == Resource.Vehicles)
                {
                    RecordVehiclePressure(
                        available,
                        serviceData.m_MaxService,
                        stock,
                        hasWarning);

                    continue;
                }

                if (!IsPhysicalRetailResource(
                        resource,
                        ref resourceDatas))
                {
                    continue;
                }

                int resourceIndex =
                    EconomyUtils.GetResourceIndex(resource);

                if (resourceIndex < 0 ||
                    resourceIndex >= m_ExcessService.Length)
                {
                    continue;
                }

#if DEBUG
                m_ServiceAvailable[resourceIndex] +=
                    available;

                m_MaxService[resourceIndex] +=
                    serviceData.m_MaxService;

                m_CompanyCount[resourceIndex]++;

                if (hasWarning)
                {
                    m_WarningCount[resourceIndex]++;
                }
#endif

                // Match the physical-stock side of vanilla NEC.
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
                        available - targetService);

                if (excess <= 0)
                {
                    continue;
                }

                m_ExcessService[resourceIndex] +=
                    excess;

                m_TotalExcessService +=
                    excess;
            }

#if DEBUG
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
#endif
        }

        private void PrepareCorrectionWindow()
        {
            Array.Copy(
                m_ExcessService,
                m_WorkingCorrectionUnits,
                m_ExcessService.Length);

            m_TotalCorrectionUnits =
                m_TotalExcessService;

            m_PerUpdateInjectionBudget =
                CalculatePerUpdateBudget(
                    m_TotalCorrectionUnits);

            PrepareVehicleCorrectionWindow();
        }

        private void BeginCorrectionUpdate()
        {
            m_RemainingInjectionBudget =
                Math.Min(
                    m_PerUpdateInjectionBudget,
                    m_TotalCorrectionUnits);

            BeginVehicleCorrectionUpdate();
        }

        private static long CalculatePerUpdateBudget(
            long correctionUnits)
        {
            if (correctionUnits <= 0)
            {
                return 0;
            }

            long budget =
                (correctionUnits +
                    kCorrectionHorizonUpdates -
                    1) /
                kCorrectionHorizonUpdates;

            return Math.Max(1, budget);
        }

        private bool IsPhysicalRetailResource(
            Resource resource,
            ref ComponentLookup<ResourceData>
                resourceDatas)
        {
            if (resource == Resource.NoResource ||
                resource == Resource.Vehicles ||
                EconomyUtils.IsOfficeResource(resource))
            {
                return false;
            }

            Entity resourcePrefab =
                m_ResourceSystem.GetPrefab(resource);

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

