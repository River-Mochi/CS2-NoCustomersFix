// <copyright file="LeisureRestoreSystem.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the MIT License. You may not use this file except in compliance with this License.
// See LICENSE file in the project root for full license information.
// This notice and the MIT License notice must be kept with
// all copies or substantial portions of this code.
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
