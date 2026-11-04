// <copyright file="HouseholdBehaviorCompatibility.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Optional MapExt / EconomyEX household integration. No hard DLL dependency.
using System;
using System.Reflection;
using CS2Shared.RiverMochi;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    internal static class HouseholdBehaviorCompatibility
    {
        private static readonly string[] s_AssemblyNames =
        {
            "MapExt2",
            "EconomyEX",
        };

        private static readonly string[] s_SystemNames =
        {
            "MapExtPDX.EcoShared.HouseholdBehaviorSystemMod",
            "EconomyEX.Systems.HouseholdBehaviorSystemMod",
        };

        private static Type? s_ReplacementType;
        private static GameSystemBase? s_Replacement;
        private static World? s_World;
        private static bool s_Discovered;
        private static bool s_Registered;
        private static int s_ReplacementInterval = 128;

        internal static int ReplacementInterval => s_ReplacementInterval;
        internal static bool NeedsRegistration => s_ReplacementType != null && !s_Registered;

        internal static bool IsReplacementActive =>
            s_Registered && s_Replacement != null &&
            s_Replacement.Enabled &&
            s_World?.GetExistingSystemManaged<HouseholdBehaviorSystem>()?.Enabled == false;

        internal static void TryRegister(World world)
        {
            if (!s_Discovered)
            {
                s_Discovered = true;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

                // MapExt takes priority because standalone EconomyEX yields to it.
                for (int i = 0; i < s_AssemblyNames.Length; i++)
                {
                    for (int j = 0; j < assemblies.Length; j++)
                    {
                        if (assemblies[j].GetName().Name != s_AssemblyNames[i])
                        {
                            continue;
                        }

                        s_ReplacementType = assemblies[j].GetType(s_SystemNames[i], false);
                        if (s_ReplacementType != null)
                        {
                            break;
                        }
                    }

                    if (s_ReplacementType != null)
                    {
                        break;
                    }
                }
            }

            if (s_ReplacementType == null || (s_Registered && s_World == world))
            {
                return;
            }

            // Do not use GetOrCreate here: the replacement's OnCreate disables
            // vanilla even when the player's EconomyEx option is turned off.
            GameSystemBase? replacement =
                world.GetExistingSystemManaged(s_ReplacementType) as GameSystemBase;
            if (replacement == null)
            {
                return;
            }

            try
            {
                UpdateSystem updateSystem =
                    world.GetOrCreateSystemManaged<UpdateSystem>();

                // The scheduler matches both position AND interval. Merely
                // moving NCF's 64-frame vanilla systems is insufficient.
                s_ReplacementInterval =
                    replacement.GetUpdateInterval(SystemUpdatePhase.GameSimulation);

                MethodInfo before = FindAnchorMethod("UpdateBefore");
                MethodInfo after = FindAnchorMethod("UpdateAfter");

                before.MakeGenericMethod(
                        typeof(ReplacementCommercialBalancePrepareSystem),
                        s_ReplacementType)
                    .Invoke(updateSystem, new object[] { SystemUpdatePhase.GameSimulation });

                after.MakeGenericMethod(
                        typeof(ReplacementCommercialBalanceSystem),
                        s_ReplacementType)
                    .Invoke(updateSystem, new object[] { SystemUpdatePhase.GameSimulation });

                s_World = world;
                s_Replacement = replacement;
                s_Registered = true;
                LogUtils.Info(
                    "[NCF-COMPAT] Registered " + s_ReplacementType.FullName +
                    " household before/after hooks; interval=" + s_ReplacementInterval);
            }
            catch (Exception ex)
            {
                LogUtils.WarnOnce(
                    "ncf-household-compat-registration",
                    () => "[NCF-COMPAT] Failed to register replacement household hooks: " + ex);
            }
        }

        private static MethodInfo FindAnchorMethod(string name)
        {
            foreach (MethodInfo method in typeof(UpdateSystem).GetMethods(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name == name &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 2 &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(SystemUpdatePhase))
                {
                    return method;
                }
            }

            throw new MissingMethodException("UpdateSystem." + name + "<T, TTarget>");
        }
    }

    // Only created when an already-existing MapExt/EconomyEX replacement is found.
    // They inherit NCF's tested correction logic and use the same household batch
    // and interval as the active replacement; they never run in vanilla mode.
    public partial class ReplacementCommercialBalancePrepareSystem :
        CommercialBalancePrepareSystem
    {
        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return HouseholdBehaviorCompatibility.ReplacementInterval;
        }

        protected override CommercialBalanceSystem ResolveBalanceSystem()
        {
            return World.GetOrCreateSystemManaged<ReplacementCommercialBalanceSystem>();
        }

        protected override bool ShouldRunWithActiveHouseholdSystem()
        {
            return HouseholdBehaviorCompatibility.IsReplacementActive;
        }
    }

    public partial class ReplacementCommercialBalanceSystem : CommercialBalanceSystem
    {
        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return HouseholdBehaviorCompatibility.ReplacementInterval;
        }

        protected override CommercialBalancePrepareSystem ResolvePrepareSystem()
        {
            return World.GetOrCreateSystemManaged<ReplacementCommercialBalancePrepareSystem>();
        }

        protected override bool ShouldRunWithActiveHouseholdSystem()
        {
            return HouseholdBehaviorCompatibility.IsReplacementActive;
        }
    }
}
