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
        // Correlations are process/scene-local and keyed by the best player identity
        // available. They are deliberately not a second persistence store.
        private readonly Dictionary<string, List<LedgerEntry>> _entriesByPlayer = new(StringComparer.Ordinal);

        private sealed class LedgerEntry
        {
            // NativeObjectKey is lifetime-local identity; IncidentId on Charge is the
            // persisted identity. The remaining fields retain diagnostic context for the
            // short correlation window.
            /// <summary>Process-local key for the native crime object.</summary>
            public string NativeObjectKey = string.Empty;
            /// <summary>Native runtime type name captured for diagnostics.</summary>
            public string NativeTypeName = string.Empty;
            /// <summary>Native display name captured for diagnostics.</summary>
            public string NativeCrimeName = string.Empty;
            /// <summary>Realtime expiration boundary for this correlation.</summary>
            public float ExpiresAt;
            /// <summary>Persisted/local charge associated with the native event.</summary>
            public CrimeInstance Charge = null!;
        }

        /// <summary>
        /// Records one native AddCrime event and assigns a persisted incident identity while
        /// retaining the native object key for later custody correlation.
        /// </summary>
        /// <param name="player">Player whose native crime collection emitted the event.</param>
        /// <param name="crime">Native crime object being correlated.</param>
        /// <param name="location">World location captured for the local charge.</param>
        /// <param name="severity">Severity captured for wanted and penalty calculations.</param>
        /// <param name="enhancement">Optional contextual enhancement attached to this charge.</param>
        /// <returns>The new local charge, or null when either required native object is absent.</returns>
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

        /// <summary>
        /// Resolves custody-time native crime quantities against the short-lived AddCrime
        /// ledger. Matching entries are reused; missing or expired entries become explicit
        /// native fallback charges so authoritative crimes are never silently discarded.
        /// </summary>
        /// <param name="player">Player entering custody.</param>
        /// <param name="crime">Native crime object reported at custody entry.</param>
        /// <param name="quantity">Number of native charges to resolve.</param>
        /// <param name="location">Location used for fallback charge instances.</param>
        /// <param name="severity">Severity used for fallback charge instances.</param>
        /// <returns>Exactly the requested number of charges when inputs are valid; otherwise an empty list.</returns>
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

            // Matching entries are read/reused, not consumed. The native custody quantity
            // is the authoritative bound for this call; repeated callers can resolve the
            // same short-lived correlation until it expires.
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

        /// <summary>Clears all scene-local native incident correlations.</summary>
        internal void Clear()
        {
            _entriesByPlayer.Clear();
        }

        private void PruneExpiredEntries()
        {
            // Cleanup is opportunistic at AddCrime/arrest-resolution boundaries. Expiry
            // uses Unity realtimeSinceStartup, so game-clock pauses do not extend it.
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

            // PlayerCode is preferred because display names can change; name is only a
            // compatibility fallback for runtimes/saves without the code.
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
