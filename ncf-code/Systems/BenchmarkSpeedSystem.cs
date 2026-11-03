// <copyright file="BenchmarkSpeedSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Separate passive logger: runs with NCF balancing ON or OFF.
#if NCF_PERF_TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Serialization.Entities;
using CS2Shared.RiverMochi;
using Game;
using Game.Simulation;

namespace NoCustomersFix.Systems
{
    public partial class BenchmarkSpeedSystem : GameSystemBase
    {
        private const int kSampleSeconds = 10;
        private readonly List<float> m_HourlySpeeds = new();
        private SimulationSystem m_SimulationSystem = null!;
        private TimeSystem m_TimeSystem = null!;
        private long m_NextSampleTick;
        private int m_SampledHour = -1;
        private bool m_SampledBalancingEnabled = true;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 64;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            WriteHourlySummary();
            m_HourlySpeeds.Clear();
            m_SampledHour = -1;
            m_SampledBalancingEnabled = Mod.BalancingEnabled;
            m_NextSampleTick = 0;
            Mod.ApplyBenchmarkModeOnCityLoad();
        }

        protected override void OnDestroy()
        {
            WriteHourlySummary();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            long now = Stopwatch.GetTimestamp();
            if (m_NextSampleTick == 0)
            {
                m_NextSampleTick = now + kSampleSeconds * Stopwatch.Frequency;
                return;
            }

            if (now < m_NextSampleTick)
            {
                return;
            }

            m_NextSampleTick = now + kSampleSeconds * Stopwatch.Frequency;

            int minuteOfDay = (int)(m_TimeSystem.normalizedTime * 1440f);
            int hour = (minuteOfDay / 60) % 24;
            int minute = minuteOfDay % 60;
            float selected = m_SimulationSystem.selectedSpeed;
            float actual = m_SimulationSystem.smoothSpeed;
            bool balancingEnabled = Mod.BalancingEnabled;
            string mode = balancingEnabled ? "ON" : "OFF";

            // A live switch starts a new sample window; never mix ON and OFF.
            if (m_SampledHour != hour ||
                m_SampledBalancingEnabled != balancingEnabled)
            {
                WriteHourlySummary();
                m_SampledHour = hour;
                m_SampledBalancingEnabled = balancingEnabled;
            }

            // Compare hourly averages at the same requested ASS target.
            // Exclude pauses, loading, and accidental 1x selections.
            if (selected >= 7.99f && selected <= 8.01f)
            {
                m_HourlySpeeds.Add(actual);
            }

            LogUtils.Info(
                "[NCF-SPEED] " +
                $"mode={mode} cityTime={hour:D2}:{minute:D2} " +
                $"selected={selected:F2} actual={actual:F3} " +
                $"frame={m_SimulationSystem.frameIndex}");
        }

        private void WriteHourlySummary()
        {
            if (m_SampledHour < 0 || m_HourlySpeeds.Count == 0)
            {
                return;
            }

            float[] sorted = m_HourlySpeeds.ToArray();
            Array.Sort(sorted);

            double sum = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                sum += sorted[i];
            }

            int n = sorted.Length;
            double median = n % 2 == 0
                ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
                : sorted[n / 2];

            LogUtils.Info(
                "[NCF-SPEED-HOUR] " +
                $"mode={(m_SampledBalancingEnabled ? "ON" : "OFF")} " +
                $"hour={m_SampledHour:D2} selected=8.00 " +
                $"samples={n} avg={sum / n:F3} median={median:F3} " +
                $"min={sorted[0]:F3} max={sorted[n - 1]:F3}");
            m_HourlySpeeds.Clear();
        }
    }
}
#endif
