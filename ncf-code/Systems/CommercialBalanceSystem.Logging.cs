// <copyright file="CommercialBalanceSystem.Logging.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalanceSystem.Logging.cs
// Purpose: Concise DEBUG diagnostics for combined NEC balancing.

#if DEBUG

using System;
using System.Globalization;
using System.Text;

using CS2Shared.RiverMochi;

using Game.Economy;

namespace NoCustomersFix.Systems
{
    public partial class CommercialBalanceSystem
    {
        private void LogBalanceWindow()
        {
            int retailWarnings = GetPhysicalWarningCount();

            int totalWarnings =
                retailWarnings +
                m_VehicleWarningCount;

            LogUtils.Info(
                "[NCF-BALANCE] " +
                $"updates={kLogEveryUpdates} " +
                $"pressureEvery={kPressureRefreshUpdates} " +
                $"ready={m_WindowReadyHouseholds} " +
                $"vanillaNeeds={m_WindowVanillaCreatedNeeds} " +
                $"emptyAfterVanilla={m_WindowEmptyAfterVanilla} " +
                $"fresh={m_WindowFreshCandidates} " +
                $"repeat={m_WindowRepeatCandidates} " +
                $"repeatAllowed={m_WindowRepeatAllowed} " +
                $"repeatPct={m_LastRepeatPercent} " +
                $"freshInjected={m_WindowFreshInjected} " +
                $"repeatInjected={m_WindowRepeatInjected} " +
                $"retailInjected={m_WindowInjectedNeeds} " +
                $"retailUnits={m_WindowInjectedUnits} " +
                $"vehicleInjected={m_WindowVehicleInjectedNeeds} " +
                $"vehicleUnits={m_WindowVehicleInjectedUnits} " +
                $"warnings={totalWarnings} " +
                $"retailWarnings={retailWarnings} " +
                $"vehicleWarnings={m_VehicleWarningCount} " +
                $"retailExcess82={m_TotalExcessService} " +
                $"vehicleExcess82={m_VehicleExcess} " +
                $"avoidedCarAmount={m_WindowAvoidedCarAmount}");

            StringBuilder details = new(4096);

            for (int i = 0; i < EconomyUtils.ResourceCount; i++)
            {
                if (m_CompanyCount[i] <= 0)
                {
                    continue;
                }

                Resource resource =
                    EconomyUtils.GetResource(i);

                bool interesting =
                    m_WarningCount[i] > 0 ||
                    m_ExcessService[i] > 0 ||
                    m_WindowInjectedNeedsByResource[i] > 0;

                if (!interesting)
                {
                    continue;
                }

                details
                    .Append("[NCF-BALANCE-RESOURCE] ")
                    .Append("resource=")
                    .Append(resource)
                    .Append(" shops=")
                    .Append(m_CompanyCount[i])
                    .Append(" warnings=")
                    .Append(m_WarningCount[i])
                    .Append(" avgUnused=")
                    .Append(
                        (m_UnsoldRatio[i] * 100f)
                            .ToString(
                                "F1",
                                CultureInfo.InvariantCulture))
                    .Append('%')
                    .Append(" excess82=")
                    .Append(m_ExcessService[i])
                    .Append(" injected=")
                    .Append(m_WindowInjectedNeedsByResource[i])
                    .Append(" injectedUnits=")
                    .Append(m_WindowInjectedUnitsByResource[i])
                    .AppendLine();
            }

            if (m_VehicleShopCount > 0 &&
                (m_VehicleWarningCount > 0 ||
                 m_VehicleExcess > 0 ||
                 m_WindowVehicleInjectedNeeds > 0))
            {
                float unused =
                    m_VehicleMaxService > 0
                        ? 100f *
                          m_VehicleServiceAvailable /
                          m_VehicleMaxService
                        : 0f;

                details
                    .Append("[NCF-BALANCE-RESOURCE] ")
                    .Append("resource=Vehicles")
                    .Append(" shops=")
                    .Append(m_VehicleShopCount)
                    .Append(" warnings=")
                    .Append(m_VehicleWarningCount)
                    .Append(" avgUnused=")
                    .Append(
                        unused.ToString(
                            "F1",
                            CultureInfo.InvariantCulture))
                    .Append('%')
                    .Append(" stock=")
                    .Append(m_VehicleStock)
                    .Append(" excess82=")
                    .Append(m_VehicleExcess)
                    .Append(" injected=")
                    .Append(m_WindowVehicleInjectedNeeds)
                    .Append(" injectedUnits=")
                    .Append(m_WindowVehicleInjectedUnits)
                    .AppendLine();
            }

            if (details.Length > 0)
            {
                LogUtils.Info(details.ToString());
            }

            Array.Clear(
                m_WindowInjectedNeedsByResource,
                0,
                m_WindowInjectedNeedsByResource.Length);

            Array.Clear(
                m_WindowInjectedUnitsByResource,
                0,
                m_WindowInjectedUnitsByResource.Length);

            m_WindowReadyHouseholds = 0;
            m_WindowVanillaCreatedNeeds = 0;
            m_WindowEmptyAfterVanilla = 0;

            m_WindowFreshCandidates = 0;
            m_WindowRepeatCandidates = 0;
            m_WindowRepeatAllowed = 0;
            m_WindowFreshInjected = 0;
            m_WindowRepeatInjected = 0;

            m_WindowInjectedNeeds = 0;
            m_WindowInjectedUnits = 0;

            m_WindowVehicleInjectedNeeds = 0;
            m_WindowVehicleInjectedUnits = 0;
            m_WindowAvoidedCarAmount = 0;
        }

        private int GetPhysicalWarningCount()
        {
            int count = 0;

            for (int i = 0; i < m_WarningCount.Length; i++)
            {
                count += m_WarningCount[i];
            }

            return count;
        }
    }
}

#endif
