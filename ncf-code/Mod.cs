
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
     using System;
     using System.Reflection;
     using Colossal.Logging;
     using CS2Shared.RiverMochi;

     using Game;
     using Game.Modding;
     using Game.Simulation;

     using NoCustomersFix.Systems;
#if NCF_PERF_TEST
     using NoCustomersFix.Localization;
     using NoCustomersFix.Settings;
     using Colossal.IO.AssetDatabase;
     using Game.SceneFlow;
#endif

     public sealed class Mod : IMod
     {
         public const string ModName = "No Customers Fix";
         public const string ModId = "NoCustomersFix";

         public static readonly string ModVersion =
             Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.6.0";

         internal static readonly ILog s_Log =
             LogManager.GetLogger(ModId).SetShowsErrorsInUI(
 #if DEBUG
                 true
 #else
                 false
 #endif
             );

#if NCF_PERF_TEST
         private static BenchmarkSettings? s_BenchmarkSettings;

         // Read Options live, including changes made in a loaded city.
         internal static bool BalancingEnabled =>
             s_BenchmarkSettings?.EnableBalancing ?? true;

         internal static bool ProductionReliefEnabled =>
             BalancingEnabled &&
             (s_BenchmarkSettings?.EnableProductionRelief ?? true);

         internal static bool DeferHotelBookingToTourismOverhaul =>
             s_BenchmarkSettings?.DeferHotelBookingToTourismOverhaul ?? true;

         internal static void ApplyBenchmarkModeOnCityLoad()
         {
             LogUtils.Info(
                 "[NCF-BENCHMARK] city load mode=" +
                 (BalancingEnabled ? "ON (normal fixes + logging)" :
                     "OFF (logging only; all balancing updates skipped)") +
                 $" productionRelief={(ProductionReliefEnabled ? "ON" : "OFF")}" +
                 $" skipNCFHotelBookingsWhenTourismModActive={DeferHotelBookingToTourismOverhaul}");
             if (!BalancingEnabled)
             {
                 LogUtils.Info("[NCF-TO] NCF hotel bookings: DISABLED (NCF balancing OFF).");
             }
         }
#else
         internal static bool DeferHotelBookingToTourismOverhaul => true;
#endif

         public void OnLoad(UpdateSystem updateSystem)
         {
             LogUtils.Configure(ModId, s_Log);

#if DEBUG
            LogUtils.Info($"{ModName} v{ModVersion} DEBUG loaded");
#elif NCF_PERF_TEST
            LogUtils.Info($"{ModName} v{ModVersion} RELEASE PERF TEST loaded");
#else
            LogUtils.Info($"{ModName} v{ModVersion} RELEASE loaded");
#endif

#if NCF_PERF_TEST
             BenchmarkSettings benchmarkSettings = new(this);
             s_BenchmarkSettings = benchmarkSettings;
             try
             {
                 if (GameManager.instance?.localizationManager != null)
                 {
                     GameManager.instance.localizationManager.AddSource(
                         "en-US", new BenchmarkLocaleEN(benchmarkSettings));
                 }

                 AssetDatabase.global.LoadSettings(
                     ModId, benchmarkSettings, new BenchmarkSettings(this),
                     userSetting: true);
                 benchmarkSettings.RegisterInOptionsUI();
             }
             catch (Exception ex)
             {
                 LogUtils.Warn(
                     "[NCF-BENCHMARK] Options registration failed; " +
                     "benchmark defaults to balancing ON.", ex);
                 s_BenchmarkSettings = null;
             }

             LogUtils.Info(
                 "[NCF-BENCHMARK] Options loaded. Mode changes apply immediately.");
             updateSystem.UpdateBefore<BenchmarkSpeedSystem, CitizenBehaviorSystem>(
                 SystemUpdatePhase.GameSimulation);
#endif

#if NCF_PERF_TEST
             // Passive hourly commercial demand logging, including benchmark OFF.
             updateSystem.UpdateAfter<CommercialDemandDiagnosticSystem,CommercialDemandSystem>(
                 SystemUpdatePhase.GameSimulation);

             // Read-only land-value diagnostics: load-complete snapshot, rapid samples, hourly later.
             updateSystem.UpdateAt<LandValueDiagnosticSystem>(
                 SystemUpdatePhase.GameSimulation);

             // Resident household-needs snapshot, independent of InfoLoom.
             updateSystem.UpdateAt<HouseholdNeedDiagnosticSystem>(
                 SystemUpdatePhase.GameSimulation);
#endif

             // One read-only load-time snapshot, including normal public Release builds.
             updateSystem.UpdateAt<LoadedLandValueWarningSystem>(
                 SystemUpdatePhase.GameSimulation);

             updateSystem.UpdateBefore<CommercialBalancePrepareSystem,HouseholdBehaviorSystem>(SystemUpdatePhase.GameSimulation);
             updateSystem.UpdateAfter<CommercialBalanceSystem,HouseholdBehaviorSystem>(SystemUpdatePhase.GameSimulation);

             updateSystem.UpdateBefore<LeisureBalanceSystem,CitizenBehaviorSystem>(SystemUpdatePhase.GameSimulation);

             updateSystem.UpdateBefore<HotelBalanceSystem,TouristFindTargetSystem>(SystemUpdatePhase.GameSimulation);

             // Temporarily reduce production workforce, then restore it.
             updateSystem.UpdateBefore<CommercialProductionReliefPrepareSystem,ServiceCompanySystem>(SystemUpdatePhase.GameSimulation);
             updateSystem.UpdateAfter<CommercialProductionReliefRestoreSystem,ServiceCompanySystem>(SystemUpdatePhase.GameSimulation);

 #if DEBUG
             updateSystem.UpdateAfter<PathHealthSystem,CommercialBalanceSystem>(SystemUpdatePhase.GameSimulation);
 #endif
         }

         public void OnDispose()
         {
         }
     }
 }
