// <copyright file="CommercialBalanceSystem.Performance.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/CommercialBalanceSystem.Performance.cs
// Purpose: Opt-in and Debug large-city performance measurements.

#if DEBUG || NCF_PERF_TEST
using System;
using System.Diagnostics;
using CS2Shared.RiverMochi;

namespace NoCustomersFix.Systems
{
    public partial class CommercialBalanceSystem
    {
        private const int kPerfLogUpdates = 256;

        private int m_PerfUpdates;
        private long m_PerfSnapshotTicks;
        private long m_PerfPressureTicks;
        private long m_PerfCorrectionTicks;
        private long m_PerfLongestCorrection;
        private long m_PerfCandidates;
        private long m_PerfVisited;
        private long m_PerfInjected;

        private void ResetPerfCounters()
        {
            m_PerfUpdates = 0;
            m_PerfSnapshotTicks = 0;
            m_PerfPressureTicks = 0;
            m_PerfCorrectionTicks = 0;
            m_PerfLongestCorrection = 0;
            m_PerfCandidates = 0;
            m_PerfVisited = 0;
            m_PerfInjected = 0;
        }

        private void RecordPerfUpdate(
            long start, long afterSnapshot, long afterPressure,
            int candidates, int visited, int injected)
        {
            long finished = Stopwatch.GetTimestamp();
            long correction = finished - afterPressure;

            m_PerfUpdates++;
            m_PerfSnapshotTicks += afterSnapshot - start;
            m_PerfPressureTicks += afterPressure - afterSnapshot;
            m_PerfCorrectionTicks += correction;
            m_PerfLongestCorrection = Math.Max(
                m_PerfLongestCorrection, correction);
            m_PerfCandidates += candidates;
            m_PerfVisited += visited;
            m_PerfInjected += injected;

            if (m_PerfUpdates < kPerfLogUpdates)
            {
                return;
            }

            double millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
            LogUtils.Info(
                "[NCF-PERF] " +
                $"updates={m_PerfUpdates} " +
                $"snapshotMs={(m_PerfSnapshotTicks * millisecondsPerTick):F1} " +
                $"pressureMs={(m_PerfPressureTicks * millisecondsPerTick):F1} " +
                $"correctionMs={(m_PerfCorrectionTicks * millisecondsPerTick):F1} " +
                $"maxCorrectionMs={(m_PerfLongestCorrection * millisecondsPerTick):F2} " +
                $"candidates={m_PerfCandidates} " +
                $"visited={m_PerfVisited} " +
                $"injected={m_PerfInjected}");
            ResetPerfCounters();
        }
    }
}
#endif
