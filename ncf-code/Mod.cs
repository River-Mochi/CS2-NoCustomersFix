// <copyright file="Mod.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Mod.cs
// Purpose: No Customers Fix entrypoint and system scheduling.

namespace NoCustomersFix
{
    using System.Reflection;

    using Colossal.Logging;

    using CS2Shared.RiverMochi;

    using Game;
    using Game.Modding;
    using Game.Simulation;

    using NoCustomersFix.Systems;

    public sealed class Mod : IMod
    {
        public const string ModName = "No Customers Fix";
        public const string ModId = "NoCustomersFix";
        public const string ModTag = "[NCF]";

        public static readonly string ModVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.5.0";

        internal static readonly ILog s_Log =
            LogManager.GetLogger(ModId).SetShowsErrorsInUI(
#if DEBUG
                true
#else
                false
#endif
            );

        public void OnLoad(UpdateSystem updateSystem)
        {
            LogUtils.Configure(ModId, s_Log);

#if DEBUG
            LogUtils.Info($"{ModName} v{ModVersion} DEBUG loaded");
#else
            LogUtils.Info($"{ModName} v{ModVersion} loaded");
#endif

#if DEBUG
            updateSystem.UpdateBefore<CommercialBalancePrepareSystem,HouseholdBehaviorSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<CommercialBalanceSystem,HouseholdBehaviorSystem>(SystemUpdatePhase.GameSimulation);

            updateSystem.UpdateBefore<LeisureBalanceSystem,CitizenBehaviorSystem>(SystemUpdatePhase.GameSimulation);

            updateSystem.UpdateBefore<HotelBalanceSystem,TouristFindTargetSystem>(SystemUpdatePhase.GameSimulation);

            updateSystem.UpdateAfter<PathHealthSystem,CommercialBalanceSystem>(SystemUpdatePhase.GameSimulation);
#endif
        }

        public void OnDispose()
        {
        }
    }
}
