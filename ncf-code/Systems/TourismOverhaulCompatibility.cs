// <copyright file="TourismOverhaulCompatibility.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Optional Tourism Overhaul integration. No Harmony or hard assembly dependency.
using System;
using System.Reflection;
using CS2Shared.RiverMochi;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    // Optional hotel-booking handoff. Never loads or instantiates a foreign system.
    internal static class TourismOverhaulCompatibility
    {
        private const string kTourismOverhaulAssembly = "TourismOverhaul";
        private const string kTourismOverhaulSystem =
            "TourismOverhaul.Systems.TouristTargetSearchSystem";
        private const string kCustomTourismAssembly = "CustomizeIt";
        private const string kCustomTourismSystem =
            "CustomizeIt.Systems.TouristTransitArrivalSystem";

        private static Type? s_TourismOverhaulType;
        private static Type? s_CustomTourismType;
        private static bool s_Discovered;
        private static World? s_LastWorld;
        private static int s_LastDecision = -1;

        private static void Discover()
        {
            if (s_Discovered)
            {
                return;
            }

            s_Discovered = true;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                string? name = assemblies[i].GetName().Name;
                if (name == kTourismOverhaulAssembly)
                {
                    s_TourismOverhaulType =
                        assemblies[i].GetType(kTourismOverhaulSystem, false);
                }
                else if (name == kCustomTourismAssembly)
                {
                    s_CustomTourismType =
                        assemblies[i].GetType(kCustomTourismSystem, false);
                }
            }
        }

        internal static bool IsActive(World world)
        {
            Discover();
            if (s_TourismOverhaulType == null)
            {
                return false;
            }

            GameSystemBase? replacement =
                world.GetExistingSystemManaged(s_TourismOverhaulType) as GameSystemBase;
            TouristFindTargetSystem? native =
                world.GetExistingSystemManaged<TouristFindTargetSystem>();

            // TO controls the normal tourist search only after it disables vanilla.
            return replacement != null && replacement.Enabled &&
                native != null && !native.Enabled;
        }

        internal static bool IsCustomTourismActive(World world)
        {
            Discover();
            if (s_CustomTourismType == null)
            {
                return false;
            }

            // Custom Tourism runs this additional 16-frame pre-booking system,
            // while vanilla TouristFindTargetSystem may remain enabled.
            GameSystemBase? arrivalSystem =
                world.GetExistingSystemManaged(s_CustomTourismType) as GameSystemBase;
            return arrivalSystem != null && arrivalSystem.Enabled;
        }

        internal static bool ShouldDeferHotelBooking(World world)
        {
            bool tourismOverhaul = IsActive(world);
            bool customTourism = IsCustomTourismActive(world);
            bool detected = tourismOverhaul || customTourism;
            bool defer = detected && Mod.DeferHotelBookingToTourismOverhaul;

            if (s_LastWorld != world)
            {
                s_LastWorld = world;
                s_LastDecision = -1;
            }

            int decision = (tourismOverhaul ? 1 : 0) +
                (customTourism ? 2 : 0) + (defer ? 4 : 0);
            if (decision != s_LastDecision)
            {
                s_LastDecision = decision;
                if (detected)
                {
                    string provider = tourismOverhaul && customTourism
                        ? "Tourism Overhaul and Custom Tourism"
                        : (tourismOverhaul ? "Tourism Overhaul" : "Custom Tourism");
                    LogUtils.Info(
                        "[NCF-TO] NCF hotel bookings: " +
                        (defer ? "SKIPPED" : "ENABLED (test override)") +
                        " (" + provider + " tourist booking system active).");
                    if (tourismOverhaul && customTourism)
                    {
                        LogUtils.Warn(
                            "[NCF-TO] Both tourism mods have active hotel-booking systems; " +
                            "NCF defers, but cannot resolve conflicts between those mods.");
                    }
                }
                else if (s_TourismOverhaulType != null ||
                    s_CustomTourismType != null)
                {
                    LogUtils.Info(
                        "[NCF-TO] NCF hotel bookings: ENABLED " +
                        "(no external tourist booking system active).");
                }
            }

            return defer;
        }
    }
}
