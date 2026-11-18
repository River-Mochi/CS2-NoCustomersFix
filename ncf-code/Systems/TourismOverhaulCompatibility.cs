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
    internal static class TourismOverhaulCompatibility
    {
        private const string kAssemblyName = "TourismOverhaul";
        private const string kSystemName = "TourismOverhaul.Systems.TouristTargetSearchSystem";

        private static Type? s_ReplacementType;
        private static bool s_Discovered;
        private static World? s_LastWorld;
        private static int s_LastDecision = -1;

        internal static bool IsActive(World world)
        {
            if (!s_Discovered)
            {
                s_Discovered = true;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == kAssemblyName)
                    {
                        s_ReplacementType = assemblies[i].GetType(kSystemName, false);
                        break;
                    }
                }
            }

            if (s_ReplacementType == null)
            {
                return false;
            }

            // Never GetOrCreate a foreign system: its OnCreate may change vanilla behavior.
            GameSystemBase? replacement =
                world.GetExistingSystemManaged(s_ReplacementType) as GameSystemBase;
            TouristFindTargetSystem? native =
                world.GetExistingSystemManaged<TouristFindTargetSystem>();

            // TO's search is authoritative only while it actually replaces vanilla.
            return replacement != null && replacement.Enabled &&
                native != null && !native.Enabled;
        }

        internal static bool ShouldDeferHotelBooking(World world)
        {
            bool active = IsActive(world);
            bool defer = active && Mod.DeferHotelBookingToTourismOverhaul;

            if (s_LastWorld != world)
            {
                s_LastWorld = world;
                s_LastDecision = -1;
            }

            int decision = defer ? 2 : (active ? 1 : 0);
            if (decision != s_LastDecision)
            {
                s_LastDecision = decision;
                if (active)
                {
                    LogUtils.Info(defer
                        ? "[NCF-TO] NCF hotel bookings: SKIPPED (Tourism Overhaul tourist search active)."
                        : "[NCF-TO] NCF hotel bookings: ENABLED (Tourism Overhaul active; test setting OFF).");
                }
                else if (decision == 0 && s_ReplacementType != null)
                {
                    LogUtils.Info(
                        "[NCF-TO] NCF hotel bookings: ENABLED (Tourism Overhaul tourist search inactive).");
                }
            }

            return defer;
        }
    }
}
