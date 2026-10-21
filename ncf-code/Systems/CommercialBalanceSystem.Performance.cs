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
        private int m_PerfSkippedSnapshots;

        private void RecordSkippedSnapshot()
        {
            m_PerfSkippedSnapshots++;
        }

#if NCF_PERF_TEST
        // This is the same game value Advanced Simulation Speed displays.
        // Sample at real-time intervals, not simulation-frame intervals.
        private const int kSpeedSampleSeconds = 10;
        private long m_NextSpeedSampleTick;

        private void LogSpeedSample()
        {
            long now = Stopwatch.GetTimestamp();
            if (m_NextSpeedSampleTick == 0)
            {
                m_NextSpeedSampleTick = now +
                    (kSpeedSampleSeconds * Stopwatch.Frequency);
                return;
            }

            if (now < m_NextSpeedSampleTick)
            {
                return;
            }

            m_NextSpeedSampleTick = now +
                (kSpeedSampleSeconds * Stopwatch.Frequency);

            Game.Simulation.TimeSystem timeSystem =
                World.GetOrCreateSystemManaged<Game.Simulation.TimeSystem>();
            int minuteOfDay = (int)(timeSystem.normalizedTime * 1440f);
            int hour = (minuteOfDay / 60) % 24;
            int minute = minuteOfDay % 60;

            LogUtils.Info(
                "[NCF-SPEED] " +
                $"cityTime={hour:D2}:{minute:D2} " +
                $"selected={m_SimulationSystem.selectedSpeed:F2} " +
                $"actual={m_SimulationSystem.smoothSpeed:F3} " +
                $"frame={m_SimulationSystem.frameIndex}");
        }
#endif

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
            m_PerfSkippedSnapshots = 0;
        }

        private void RecordPerfUpdate(
            long start, long afterPressure, long afterSnapshot,
            int candidates, int visited, int injected)
        {
            long finished = Stopwatch.GetTimestamp();
            long correction = finished - afterSnapshot;

            m_PerfUpdates++;
            m_PerfPressureTicks += afterPressure - start;
            m_PerfSnapshotTicks += afterSnapshot - afterPressure;
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
                $"injected={m_PerfInjected} " +
                $"skippedSnapshots={m_PerfSkippedSnapshots} " +
                $"commercialWarnings={m_CommercialWarningCount} " +
                $"normalCorrectionCadence={kNormalCorrectionActive}/{kNormalCorrectionCycle} " +
                $"lowWarningThrottle={m_LowWarningThrottle}");
            ResetPerfCounters();
        }
    }
}
#endif
