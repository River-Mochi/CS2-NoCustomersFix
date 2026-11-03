// <copyright file="BenchmarkSettings.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Developer-only Options toggle. Not compiled into public Release builds.
#if NCF_PERF_TEST
using Colossal.IO.AssetDatabase;
using CS2Shared.RiverMochi;
using Game.Modding;
using Game.Settings;

namespace NoCustomersFix.Settings
{
    [FileLocation("ModsSettings/NoCustomersFix/Benchmark")]
    [SettingsUITabOrder("Benchmark")]
    [SettingsUIGroupOrder("Mode")]
    public class BenchmarkSettings : ModSetting
    {
        public BenchmarkSettings(IMod mod)
            : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection("Benchmark", "Mode")]
        [SettingsUISetter(typeof(BenchmarkSettings), nameof(OnBalancingChanged))]
        public bool EnableBalancing { get; set; }

        private void OnBalancingChanged(bool enabled)
        {
            LogUtils.Info(
                "[NCF-BENCHMARK] toggle changed to " +
                (enabled ? "ON" : "OFF") + " (applies immediately)");
        }

        public override void SetDefaults()
        {
            EnableBalancing = true;
        }
    }
}
#endif
