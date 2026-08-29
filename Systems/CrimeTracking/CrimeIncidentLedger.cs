using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
#if !MONO
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.Law;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Keeps short-lived, per-player correlations between native crime submissions and
    /// Behind Bars context such as the victim being law enforcement. Native crimes do
    /// not expose a stable save identifier, so this ledger assigns one at the AddCrime
    /// seam and uses the original native object key through arrest capture.
    /// </summary>
    internal sealed class CrimeIncidentLedger
    {
        private readonly Dictionary<string, List<LedgerEntry>> _entriesByPlayer = new(StringComparer.Ordinal);

        private sealed class LedgerEntry
        {
            public string NativeObjectKey = string.Empty;
            public string NativeTypeName = string.Empty;
            public string NativeCrimeName = string.Empty;
            public float ExpiresAt;
            public CrimeInstance Charge = null!;
        }

        internal CrimeInstance RecordNativeCrime(
            Player player,
            Crime crime,
            Vector3 location,
            float severity,
            CrimeEnhancement enhancement = null)
        {
            if (player == null || crime == null)
            {
                return null;
            }

            PruneExpiredEntries();

            var charge = new CrimeInstance(crime, location, severity)
            {
                IncidentId = Guid.NewGuid().ToString("N"),
                Source = "Native"
            };

            if (enhancement != null && enhancement.Kind != CrimeEnhancementKind.None)
            {
                charge.AddEnhancement(enhancement);
            }

            string playerKey = GetPlayerKey(player);
            if (!_entriesByPlayer.TryGetValue(playerKey, out var entries))
            {
                entries = new List<LedgerEntry>();
                _entriesByPlayer[playerKey] = entries;
            }

            entries.Add(new LedgerEntry
            {
                NativeObjectKey = GetNativeObjectKey(crime),
                NativeTypeName = crime.GetType().FullName ?? crime.GetType().Name,
                NativeCrimeName = crime.CrimeName ?? string.Empty,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(1f, Core.CrimeIncidentRetentionSeconds),
                Charge = charge
            });

            ModLogger.Debug($"[Charge Ledger] Recorded native incident={charge.IncidentId} type={charge.GetCrimeTypeName()} enhancement={enhancement?.Kind ?? CrimeEnhancementKind.None}");
            return charge;
        }

        internal List<CrimeInstance> ResolveArrestCharges(Player player, Crime crime, int quantity, Vector3 location, float severity)
        {
            var resolved = new List<CrimeInstance>();
            if (player == null || crime == null || quantity <= 0)
            {
                return resolved;
            }

            PruneExpiredEntries();
            string playerKey = GetPlayerKey(player);
            string nativeObjectKey = GetNativeObjectKey(crime);
            _entriesByPlayer.TryGetValue(playerKey, out var entries);

            var matchingEntries = entries?
                .Where(entry => string.Equals(entry.NativeObjectKey, nativeObjectKey, StringComparison.Ordinal))
                .Take(quantity)
                .ToList() ?? new List<LedgerEntry>();

            foreach (var entry in matchingEntries)
            {
                resolved.Add(entry.Charge);
            }

            // A native crime can predate Behind Bars loading or be emitted by an unhooked
            // game path. Preserve it as an authoritative base charge rather than dropping it.
            for (int i = matchingEntries.Count; i < quantity; i++)
            {
                var fallback = new CrimeInstance(crime, location, severity)
                {
                    IncidentId = Guid.NewGuid().ToString("N"),
                    Source = "Native"
                };
                resolved.Add(fallback);
                ModLogger.Debug($"[Charge Ledger] Created arrest-capture fallback incident={fallback.IncidentId} type={fallback.GetCrimeTypeName()}");
            }

            return resolved;
        }

        internal void Clear()
        {
            _entriesByPlayer.Clear();
        }

        private void PruneExpiredEntries()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var playerKey in _entriesByPlayer.Keys.ToList())
            {
                var entries = _entriesByPlayer[playerKey];
                entries.RemoveAll(entry => entry == null || entry.Charge == null || entry.ExpiresAt < now);
                if (entries.Count == 0)
                {
                    _entriesByPlayer.Remove(playerKey);
                }
            }
        }

        private static string GetPlayerKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return !string.IsNullOrWhiteSpace(player.PlayerCode) ? player.PlayerCode : player.name ?? string.Empty;
        }

        private static string GetNativeObjectKey(Crime crime)
        {
            if (crime == null)
            {
                return string.Empty;
            }

            // GetHashCode is stable for the lifetime of this native object on both runtimes.
            // The key is intentionally scene-local; the assigned incident ID is persisted.
            return $"{crime.GetType().FullName}:{crime.GetHashCode()}";
        }
    }
}
