// <copyright file="PathHealthSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/PathHealthSystem.cs
// Purpose: Lightweight DEBUG-only path-health diagnostics.

#if DEBUG

using CS2Shared.RiverMochi;

using Game;
using Game.Economy;
using Game.Pathfind;

using Unity.Entities;

namespace NoCustomersFix.Systems
{
    /// <summary>
    /// Samples existing vanilla path state.
    ///
    /// This system never creates, retries, modifies, or removes a path.
    /// It exists only to detect growing Failed/Stuck/Pending populations while
    /// NCF is being validated.
    /// </summary>
    public partial class PathHealthSystem :
        GameSystemBase
    {
        private const int kScanInterval = 1024;
        private const int kLogEveryScans = 4;

        private int m_ScanCount;

        private int m_PeakPending;
        private int m_PeakFailed;
        private int m_PeakStuck;

        private int m_PeakShoppingPending;
        private int m_PeakShoppingFailed;

        private int m_PeakLeisurePending;
        private int m_PeakLeisureFailed;

        private int m_LastFailed = -1;
        private int m_LastStuck = -1;

        public override int GetUpdateInterval(
            SystemUpdatePhase phase)
        {
            return kScanInterval;
        }

        protected override void OnUpdate()
        {
            ComponentLookup<
                Game.Citizens.HouseholdMember>
                householdMembers =
                    SystemAPI.GetComponentLookup<
                        Game.Citizens.HouseholdMember>(
                            true);

            ComponentLookup<
                Game.Citizens.HouseholdNeed>
                householdNeeds =
                    SystemAPI.GetComponentLookup<
                        Game.Citizens.HouseholdNeed>(
                            true);

            ComponentLookup<
                Game.Citizens.Leisure>
                leisure =
                    SystemAPI.GetComponentLookup<
                        Game.Citizens.Leisure>(
                            true);

            int pending = 0;
            int failed = 0;
            int stuck = 0;

            int shoppingPending = 0;
            int shoppingFailed = 0;
            int shoppingStuck = 0;
            int shoppingNegativeCost = 0;

            int leisurePending = 0;
            int leisureFailed = 0;
            int leisureStuck = 0;

            foreach ((
                RefRO<PathInformation> pathRef,
                Entity entity) in
                SystemAPI
                    .Query<RefRO<PathInformation>>()
                    .WithNone<
                        Game.Common.Deleted,
                        Game.Tools.Temp>()
                    .WithEntityAccess())
            {
                PathInformation path =
                    pathRef.ValueRO;

                bool isPending =
                    (path.m_State &
                        PathFlags.Pending) != 0;

                bool isFailed =
                    (path.m_State &
                        PathFlags.Failed) != 0;

                bool isStuck =
                    (path.m_State &
                        PathFlags.Stuck) != 0;

                if (isPending)
                {
                    pending++;
                }

                if (isFailed)
                {
                    failed++;
                }

                if (isStuck)
                {
                    stuck++;
                }

                bool isShopping = false;

                if (householdMembers.HasComponent(
                        entity))
                {
                    Entity household =
                        householdMembers[
                            entity].m_Household;

                    if (household != Entity.Null &&
                        householdNeeds.HasComponent(
                            household) &&
                        householdNeeds[
                            household].m_Resource !=
                            Resource.NoResource)
                    {
                        isShopping = true;
                    }
                }

                if (isShopping)
                {
                    if (isPending)
                    {
                        shoppingPending++;
                    }

                    if (isFailed)
                    {
                        shoppingFailed++;
                    }

                    if (isStuck)
                    {
                        shoppingStuck++;
                    }

                    // ResourceBuyerSystem treats negative total cost as a
                    // shopping-path failure signal.
                    if (path.m_TotalCost < 0f)
                    {
                        shoppingNegativeCost++;
                    }
                }

                if (leisure.HasComponent(entity))
                {
                    if (isPending)
                    {
                        leisurePending++;
                    }

                    if (isFailed)
                    {
                        leisureFailed++;
                    }

                    if (isStuck)
                    {
                        leisureStuck++;
                    }
                }
            }

            m_PeakPending =
                System.Math.Max(
                    m_PeakPending,
                    pending);

            m_PeakFailed =
                System.Math.Max(
                    m_PeakFailed,
                    failed);

            m_PeakStuck =
                System.Math.Max(
                    m_PeakStuck,
                    stuck);

            m_PeakShoppingPending =
                System.Math.Max(
                    m_PeakShoppingPending,
                    shoppingPending);

            m_PeakShoppingFailed =
                System.Math.Max(
                    m_PeakShoppingFailed,
                    shoppingFailed);

            m_PeakLeisurePending =
                System.Math.Max(
                    m_PeakLeisurePending,
                    leisurePending);

            m_PeakLeisureFailed =
                System.Math.Max(
                    m_PeakLeisureFailed,
                    leisureFailed);

            m_ScanCount++;

            bool failureStateChanged =
                failed != m_LastFailed ||
                stuck != m_LastStuck;

            m_LastFailed =
                failed;

            m_LastStuck =
                stuck;

            if (!failureStateChanged &&
                m_ScanCount %
                    kLogEveryScans != 0)
            {
                return;
            }

            LogUtils.Info(
                "[NCF-PATH] " +
                $"pending={pending} " +
                $"failed={failed} " +
                $"stuck={stuck} " +
                $"shoppingPending={shoppingPending} " +
                $"shoppingFailed={shoppingFailed} " +
                $"shoppingStuck={shoppingStuck} " +
                $"shoppingNegCost={shoppingNegativeCost} " +
                $"leisurePending={leisurePending} " +
                $"leisureFailed={leisureFailed} " +
                $"leisureStuck={leisureStuck} " +
                $"peakPending={m_PeakPending} " +
                $"peakFailed={m_PeakFailed} " +
                $"peakStuck={m_PeakStuck} " +
                $"peakShoppingPending={m_PeakShoppingPending} " +
                $"peakShoppingFailed={m_PeakShoppingFailed} " +
                $"peakLeisurePending={m_PeakLeisurePending} " +
                $"peakLeisureFailed={m_PeakLeisureFailed}");
        }
    }
}

#endif
