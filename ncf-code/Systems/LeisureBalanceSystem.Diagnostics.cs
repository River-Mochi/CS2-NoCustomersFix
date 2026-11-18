// <copyright file="LeisureBalanceSystem.Diagnostics.cs" company="River-Mochi">
// Copyright (c) 2026 River-Mochi. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later,
// with the Cities: Skylines II Linking Exception.
// See LICENSE and LICENSE-EXCEPTION in the project root.
// This notice MUST be kept with copies or substantial portions of this code.
// ================= </copyright> ======================

// Reuses the existing Leisure scan for hourly Entertainment, Recreation,
// Meals, and Lodging warning details. No new ECS query or simulation writes.
#if NCF_PERF_TEST
using CS2Shared.RiverMochi;
using Game.Economy;
using Game.Simulation;
using Unity.Entities;

namespace NoCustomersFix.Systems
{
    public partial class LeisureBalanceSystem
    {
        private const int kMaxTargetWarningsPerResource = 12;

        private TimeSystem m_TargetTimeSystem = null!;
        private int m_LastTargetDiagnosticHour = -1;
        private int m_TargetDiagnosticHour;
        private bool m_RecordTargetDiagnostics;
        private int m_TargetEntertainmentLogged;
        private int m_TargetRecreationLogged;
        private int m_TargetMealsLogged;
        private int m_TargetLodgingLogged;

        private void ResetTargetDiagnostics()
        {
            m_LastTargetDiagnosticHour = -1;
            m_RecordTargetDiagnostics = false;
            m_TargetEntertainmentLogged = 0;
            m_TargetRecreationLogged = 0;
            m_TargetMealsLogged = 0;
            m_TargetLodgingLogged = 0;
        }

        private void BeginTargetDiagnostics()
        {
            m_TargetDiagnosticHour =
                ((int)(m_TargetTimeSystem.normalizedTime * 24f)) % 24;
            m_RecordTargetDiagnostics =
                m_TargetDiagnosticHour != m_LastTargetDiagnosticHour;

            if (!m_RecordTargetDiagnostics)
            {
                return;
            }

            m_LastTargetDiagnosticHour = m_TargetDiagnosticHour;
            m_TargetEntertainmentLogged = 0;
            m_TargetRecreationLogged = 0;
            m_TargetMealsLogged = 0;
            m_TargetLodgingLogged = 0;
        }

        private void LogTargetWarning(
            Resource resource,
            Entity company,
            Entity building,
            int serviceAvailable,
            int maxService,
            int stock,
            int freeRooms,
            int occupiedRooms)
        {
            if (!m_RecordTargetDiagnostics)
            {
                return;
            }

            if (resource == Resource.Entertainment)
            {
                if (m_TargetEntertainmentLogged++ >= kMaxTargetWarningsPerResource)
                {
                    return;
                }
            }
            else if (resource == Resource.Recreation)
            {
                if (m_TargetRecreationLogged++ >= kMaxTargetWarningsPerResource)
                {
                    return;
                }
            }
            else if (resource == Resource.Meals)
            {
                if (m_TargetMealsLogged++ >= kMaxTargetWarningsPerResource)
                {
                    return;
                }
            }
            else if (resource == Resource.Lodging)
            {
                if (m_TargetLodgingLogged++ >= kMaxTargetWarningsPerResource)
                {
                    return;
                }
            }
            else
            {
                return;
            }

            float unused = maxService > 0
                ? 100f * serviceAvailable / maxService
                : 0f;

            string roomDetails = resource == Resource.Lodging
                ? $" freeRooms={freeRooms} occupiedRooms={occupiedRooms}"
                : string.Empty;

            LogUtils.Info(
                "[NCF-NEC-TARGET] " +
                $"hour={m_TargetDiagnosticHour:D2} resource={resource} " +
                $"building={building.Index}:{building.Version} " +
                $"company={company.Index}:{company.Version} " +
                $"unused={unused:F1}% stock={stock}" +
                roomDetails);
        }

        private void EndTargetDiagnostics(
            LeisurePressure pressure,
            int currentFactor,
            int desiredFactor)
        {
            if (!m_RecordTargetDiagnostics)
            {
                return;
            }

            LogUtils.Info(
                "[NCF-LEISURE-STATE] " +
                $"hour={m_TargetDiagnosticHour:D2} " +
                $"factorBaseline={m_BaselineFactor} " +
                $"factorBefore={currentFactor} factorDesired={desiredFactor} " +
                $"eligible={pressure.EligibleCount} " +
                $"medium={pressure.MediumCount} strong={pressure.StrongCount} " +
                $"entWarnings={pressure.EntertainmentWarnings} " +
                $"recWarnings={pressure.RecreationWarnings} " +
                $"mealsWarnings={pressure.MealsWarnings} " +
                $"lodgingWarnings={pressure.LodgingWarnings} " +
                $"loggedEnt={System.Math.Min(m_TargetEntertainmentLogged, kMaxTargetWarningsPerResource)} " +
                $"loggedRec={System.Math.Min(m_TargetRecreationLogged, kMaxTargetWarningsPerResource)} " +
                $"loggedMeals={System.Math.Min(m_TargetMealsLogged, kMaxTargetWarningsPerResource)} " +
                $"loggedLodging={System.Math.Min(m_TargetLodgingLogged, kMaxTargetWarningsPerResource)}");

            m_RecordTargetDiagnostics = false;
        }
    }
}
#endif
