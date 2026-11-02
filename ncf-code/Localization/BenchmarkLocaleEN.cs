// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under GPL-3.0-or-later with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.

#if NCF_PERF_TEST
using System.Collections.Generic;
using Colossal;
using NoCustomersFix.Settings;

namespace NoCustomersFix.Localization
{
    public sealed class BenchmarkLocaleEN : IDictionarySource
    {
        private readonly BenchmarkSettings m_Settings;

        public BenchmarkLocaleEN(BenchmarkSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "No Customers Fix (Perf Test)" },
                { m_Settings.GetOptionTabLocaleID("Benchmark"), "Benchmark" },
                { m_Settings.GetOptionGroupLocaleID("Mode"), "Test Mode" },
                { m_Settings.GetOptionLabelLocaleID(nameof(BenchmarkSettings.EnableBalancing)),
                    "NCF balancing enabled" },
                { m_Settings.GetOptionDescLocaleID(nameof(BenchmarkSettings.EnableBalancing)),
                    "ON: normal NCF fixes and speed logging. OFF: speed logging only, no NCF fixes. " +
                    "Restart the game after changing this option. Load the same original save for both tests." },
            };
        }

        public void Unload()
        {
        }
    }
}
#endif
