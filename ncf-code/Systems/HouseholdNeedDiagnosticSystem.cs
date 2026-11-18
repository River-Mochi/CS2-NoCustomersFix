// <copyright file="HouseholdNeedDiagnosticSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Developer-only, hourly resident-household snapshot. Reads vanilla ECS only.
#if NCF_PERF_TEST
using System;
using System.Diagnostics;
using Colossal.Serialization.Entities;
using CS2Shared.RiverMochi;
using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    public partial class HouseholdNeedDiagnosticSystem : GameSystemBase
    {
        private EntityQuery m_ResidentQuery;
        private TimeSystem m_TimeSystem = null!;
        private int m_LastHour = -1;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 256;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_ResidentQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Household>(),
                    ComponentType.ReadOnly<HouseholdNeed>(),
                    ComponentType.ReadOnly<HouseholdCitizen>(),
                    ComponentType.ReadOnly<Game.Economy.Resources>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<TouristHousehold>(),
                    ComponentType.ReadOnly<CommuterHousehold>(),
                    ComponentType.ReadOnly<MovingAway>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_LastHour = -1;
        }

        protected override void OnUpdate()
        {
            int hour = ((int)(m_TimeSystem.normalizedTime * 24f)) % 24;
            if (hour == m_LastHour)
            {
                return;
            }

            m_LastHour = hour;
            long started = Stopwatch.GetTimestamp();

            EntityManager.CompleteDependencyBeforeRO<Household>();
            EntityManager.CompleteDependencyBeforeRO<HouseholdNeed>();
            EntityManager.CompleteDependencyBeforeRO<Game.Economy.Resources>();

            int residentHouseholds = 0;
            int pendingNeed = 0;
            int pendingCommercial = 0;
            long pendingAmount = 0;
            int noNeed = 0;
            int noNeedWithGoods = 0;
            int noNeedWithoutGoods = 0;
            int goodsEmpty = 0;
            int moneyUnder200 = 0;
            int shoppedToday = 0;
            int shoppedPreviousDay = 0;
            int consuming = 0;
            int[] byResource = new int[EconomyUtils.ResourceCount];

            using (NativeArray<Entity> residents =
                m_ResidentQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < residents.Length; i++)
                {
                    Entity entity = residents[i];
                    Household household = EntityManager.GetComponentData<Household>(entity);
                    HouseholdNeed need = EntityManager.GetComponentData<HouseholdNeed>(entity);
                    DynamicBuffer<Game.Economy.Resources> resources =
                        EntityManager.GetBuffer<Game.Economy.Resources>(entity, true);

                    residentHouseholds++;
                    if (household.m_Resources <= 0)
                    {
                        goodsEmpty++;
                    }

                    if (household.m_ConsumptionPerDay > 0)
                    {
                        consuming++;
                    }

                    if (household.m_ShoppedValuePerDay > 0)
                    {
                        shoppedToday++;
                    }

                    if (household.m_ShoppedValueLastDay > 0)
                    {
                        shoppedPreviousDay++;
                    }

                    // Cash in the resource buffer is NOT household spendable income.
                    if (EconomyUtils.GetResources(Resource.Money, resources) < 200)
                    {
                        moneyUnder200++;
                    }

                    if (need.m_Resource == Resource.NoResource)
                    {
                        noNeed++;
                        if (household.m_Resources > 0)
                        {
                            noNeedWithGoods++;
                        }
                        else
                        {
                            noNeedWithoutGoods++;
                        }

                        continue;
                    }

                    if (need.m_Amount <= 0)
                    {
                        continue;
                    }

                    pendingNeed++;
                    pendingAmount += need.m_Amount;
                    if (EconomyUtils.IsCommercialResource(need.m_Resource))
                    {
                        pendingCommercial++;
                    }

                    int index = EconomyUtils.GetResourceIndex(need.m_Resource);
                    if (index >= 0 && index < byResource.Length)
                    {
                        byResource[index]++;
                    }
                }
            }

            // A compact ranked sample of pending needs, not a count of companies.
            string topNeeds = "";
            for (int rank = 0; rank < 5; rank++)
            {
                int bestIndex = -1;
                int bestCount = 0;
                for (int i = 0; i < byResource.Length; i++)
                {
                    if (byResource[i] > bestCount)
                    {
                        bestIndex = i;
                        bestCount = byResource[i];
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                topNeeds += (rank == 0 ? "" : ",") +
                    EconomyUtils.GetResource(bestIndex) + ":" + bestCount;
                byResource[bestIndex] = 0;
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000d /
                Stopwatch.Frequency;
            LogUtils.Info(
                "[NCF-HOUSEHOLDS] " +
                $"mode={(Mod.BalancingEnabled ? "ON" : "OFF")} hour={hour:D2} " +
                $"residentHouseholds={residentHouseholds} " +
                $"pendingNeed={pendingNeed} pendingCommercial={pendingCommercial} " +
                $"pendingAmount={pendingAmount} noNeed={noNeed} " +
                $"noNeedWithGoods={noNeedWithGoods} noNeedWithoutGoods={noNeedWithoutGoods} " +
                $"goodsEmpty={goodsEmpty} consuming={consuming} " +
                $"cashUnder200={moneyUnder200} " +
                $"shoppedToday={shoppedToday} shoppedPreviousDay={shoppedPreviousDay} " +
                $"topPendingNeeds={topNeeds} elapsedMs={elapsedMs:F1}");
        }
    }
}
#endif
