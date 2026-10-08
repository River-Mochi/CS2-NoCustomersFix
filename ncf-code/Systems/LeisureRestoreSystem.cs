// <copyright file="LeisureRestoreSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// File: Systems/Commercial/LeisureRestoreSystem.cs
// Purpose: Restores vanilla leisure parameters after CitizenBehaviorSystem.

#if DEBUG

using Game;
using Game.Prefabs;

using Unity.Entities;

namespace NoCustomersFix.Systems
{
    public partial class LeisureRestoreSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            if (!LeisureBalanceState.Applied)
            {
                return;
            }

            Entity entity = LeisureBalanceState.ParametersEntity;

            if (entity != Entity.Null &&
                EntityManager.Exists(entity) &&
                EntityManager.HasComponent<LeisureParametersData>(entity))
            {
                LeisureParametersData parameters =
                    EntityManager.GetComponentData<LeisureParametersData>(entity);

                parameters.m_LeisureRandomFactor =
                    LeisureBalanceState.OriginalRandomFactor;

                EntityManager.SetComponentData(entity, parameters);
            }

            LeisureBalanceState.Applied = false;
        }
    }
}

#endif
