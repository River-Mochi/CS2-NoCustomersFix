// <copyright file="CommercialBalanceSystem.Logging.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the MIT License. You may not use this file except in compliance with this License.
// See LICENSE file in the project root for full license information.
// This notice and the MIT License notice must be kept with
// all copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/CommercialBalanceSystem.Logging.cs
// Purpose: DEBUG diagnostics for commercial demand-balance prototype.

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
            LogUtils.Info(
                "[CWD-BALANCE] " +
                "prototype=v4 " +
                $"updates={kLogEveryUpdates} " +
                $"ready={m_WindowReadyHouseholds} " +
                $"vanillaNeeds={m_WindowVanillaCreatedNeeds} " +
                $"emptyAfterVanilla={m_WindowEmptyAfterVanilla} " +
                $"injected={m_WindowInjectedNeeds} " +
                $"injectedUnits={m_WindowInjectedUnits} " +
                $"physicalWarnings={GetPhysicalWarningCount()} " +
                $"excess85={m_TotalExcessService}");

            StringBuilder details =
                new(4096);

            for (int i = 0;
                i < EconomyUtils.ResourceCount;
                i++)
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
                    .Append(
                        "[CWD-BALANCE-RESOURCE] ")
                    .Append("resource=")
                    .Append(resource)
                    .Append(" shops=")
                    .Append(
                        m_CompanyCount[i])
                    .Append(" warnings=")
                    .Append(
                        m_WarningCount[i])
                    .Append(" avgUnused=")
                    .Append(
                        (m_UnsoldRatio[i] * 100f)
                            .ToString(
                                "F1",
                                CultureInfo.InvariantCulture))
                    .Append('%')
                    .Append(" excess85=")
                    .Append(
                        m_ExcessService[i])
                    .Append(" injected=")
                    .Append(
                        m_WindowInjectedNeedsByResource[i])
                    .Append(" injectedUnits=")
                    .Append(
                        m_WindowInjectedUnitsByResource[i])
                    .AppendLine();
            }

            if (details.Length > 0)
            {
                LogUtils.Info(
                    details.ToString());
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

            m_WindowInjectedNeeds = 0;
            m_WindowInjectedUnits = 0;
        }

        private int GetPhysicalWarningCount()
        {
            int count = 0;

            for (int i = 0;
                i < m_WarningCount.Length;
                i++)
            {
                count +=
                    m_WarningCount[i];
            }

            return count;
        }
    }
}

#endif
