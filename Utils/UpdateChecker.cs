using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Behind_Bars.Helpers;
using MelonLoader;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Handles checking for mod updates from GitHub repository
    /// </summary>
    public static class UpdateChecker
    {
        private static MelonPreferences_Entry<long>? _lastUpdateCheckEntry;
        private static MelonPreferences_Entry<string>? _cachedLatestVersionEntry;
        private static MelonPreferences_Entry<bool>? _enableUpdateCheckingEntry;
        private static bool _hasCheckedThisSession = false; // Track if we've checked this game session

        /// <summary>
        /// Initialize update checker preferences (called from Core.cs)
        /// </summary>
        public static void InitializePreferences(
            MelonPreferences_Entry<long> lastUpdateCheckEntry,
            MelonPreferences_Entry<string> cachedLatestVersionEntry,
            MelonPreferences_Entry<bool> enableUpdateCheckingEntry)
        {
            _lastUpdateCheckEntry = lastUpdateCheckEntry;
            _cachedLatestVersionEntry = cachedLatestVersionEntry;
            _enableUpdateCheckingEntry = enableUpdateCheckingEntry;
        }

        /// <summary>
        /// Main coroutine for checking updates
        /// </summary>
        /// <param name="forceCheck">If true, bypasses cache and always checks</param>
        public static IEnumerator CheckForUpdatesAsync(bool forceCheck = false)
        {
            // Check if update checking is enabled
            if (_enableUpdateCheckingEntry?.Value == false)
            {
                ModLogger.Debug("Update checking is disabled");
                yield break;
            }

            // Always check on first load of game session (ignore cache)
            // Also check if forced or cache interval has passed
            bool shouldCheck = forceCheck || !_hasCheckedThisSession || ShouldCheckForUpdates();
            
            if (!shouldCheck)
            {
                ModLogger.Debug("Update check skipped - within cache interval");
                
                // Still check cached version for display
                string cachedVersion = _cachedLatestVersionEntry?.Value ?? "";
                if (!string.IsNullOrEmpty(cachedVersion))
                {
                    ModLogger.Debug($"Checking cached version: {cachedVersion} vs current: {Constants.MOD_VERSION}");
                    if (IsNewerVersion(Constants.MOD_VERSION, cachedVersion))
                    {
                        ModLogger.Info($"Cached update available: {cachedVersion}");
                        // Fetch fresh data to show notification
                        yield return FetchVersionFromGitHub((info) => 
                        {
                            if (info != null && !string.IsNullOrEmpty(info.version))
                            {
                                ShowUpdateNotificationIfNewer(info);
                            }
                        });
                    }
                }
                yield break;
            }

            ModLogger.Info("Checking for mod updates from GitHub...");
            ModLogger.Info($"Current mod version: {Constants.MOD_VERSION}");

            // Fetch version from GitHub
            VersionInfo? versionInfo = null;
            yield return FetchVersionFromGitHub((info) => { versionInfo = info; });

            if (versionInfo == null || string.IsNullOrEmpty(versionInfo.version))
            {
                ModLogger.Warn("Failed to fetch version information from GitHub");
                yield break;
            }

            ModLogger.Info($"Fetched version from GitHub: {versionInfo.version}");

            // Update cache
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_lastUpdateCheckEntry != null)
            {
                _lastUpdateCheckEntry.Value = currentTime;
            }
            if (_cachedLatestVersionEntry != null)
            {
                _cachedLatestVersionEntry.Value = versionInfo.version;
            }
            
            // Mark that we've checked this session
            _hasCheckedThisSession = true;

            // Compare versions and show notification if newer
            ShowUpdateNotificationIfNewer(versionInfo);
        }

        /// <summary>
        /// Compare versions and show notification if update is available
        /// </summary>
        private static void ShowUpdateNotificationIfNewer(VersionInfo versionInfo)
        {
            string currentVersion = Constants.MOD_VERSION;
            ModLogger.Info($"Comparing versions - Current: '{currentVersion}' vs Latest: '{versionInfo.version}'");
            
            bool updateAvailable = IsNewerVersion(currentVersion, versionInfo.version);

            if (updateAvailable)
            {
                ModLogger.Info($"✓ Update available! Current: {currentVersion}, Latest: {versionInfo.version}");
                
                // Show update notification UI
                if (Behind_Bars.UI.BehindBarsUIManager.Instance != null)
                {
                    Behind_Bars.UI.BehindBarsUIManager.Instance.ShowUpdateNotification(versionInfo);
                }
                else
                {
                    ModLogger.Warn("BehindBarsUIManager not initialized - cannot show update notification");
                }
            }
            else
            {
                ModLogger.Info($"Mod is up to date (version {currentVersion})");
            }
        }

        /// <summary>
        /// Fetch version information from GitHub
        /// </summary>
        private static IEnumerator FetchVersionFromGitHub(System.Action<VersionInfo?> callback)
        {
            string url = Constants.GITHUB_VERSION_URL;
            ModLogger.Debug($"Fetching version from: {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = 30;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                string jsonText = request.downloadHandler.text;
                ModLogger.Debug($"Received JSON response: {jsonText.Substring(0, Math.Min(200, jsonText.Length))}...");
                    
                VersionInfo versionInfo = ParseVersionInfo(jsonText);
                callback(versionInfo);
            }
            else
            {
                ModLogger.Warn($"Update check failed: {request.error} (HTTP {request.responseCode})");
                callback(null);
            }
        }

        /// <summary>
        /// Parse JSON response into VersionInfo object
        /// </summary>
        private static VersionInfo? ParseVersionInfo(string jsonText)
        {
            try
            {
                if (string.IsNullOrEmpty(jsonText))
                {
                    ModLogger.Error("JSON text is null or empty");
                    return null;
                }

                VersionInfo versionInfo = JsonUtility.FromJson<VersionInfo>(jsonText);
                
                if (string.IsNullOrEmpty(versionInfo.version))
                {
                    ModLogger.Error("Invalid version data - version field is empty");
                    return null;
                }

                ModLogger.Debug($"Parsed version info: {versionInfo.version}");
                return versionInfo;
            }
            catch (Exception e)
            {
                ModLogger.Error($"JSON parse error: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Compare two version strings supporting alpha/beta prefixes and suffixes
        /// Supports formats: "alpha-1.0.0", "1.0.0a", "beta-1.0.0", "1.0.0-rc1", "1.0.0"
        /// </summary>
        public static bool IsNewerVersion(string currentVersion, string latestVersion)
        {
            if (string.IsNullOrEmpty(currentVersion) || string.IsNullOrEmpty(latestVersion))
            {
                ModLogger.Warn($"Cannot compare versions - Current: '{currentVersion}', Latest: '{latestVersion}'");
                return false;
            }

            // Trim whitespace
            currentVersion = currentVersion.Trim();
            latestVersion = latestVersion.Trim();

            ModLogger.Debug($"Comparing versions - Current: '{currentVersion}' vs Latest: '{latestVersion}'");

            try
            {
                // Parse versions with prefix/suffix support
                var currentParsed = ParseVersionWithPrefix(currentVersion);
                var latestParsed = ParseVersionWithPrefix(latestVersion);
                
                ModLogger.Info($"Version comparison result:");
                ModLogger.Info($"  Current: {currentParsed.prefix}{currentParsed.version} (Prefix={currentParsed.prefix}, Version={currentParsed.version})");
                ModLogger.Info($"  Latest:  {latestParsed.prefix}{latestParsed.version} (Prefix={latestParsed.prefix}, Version={latestParsed.version})");
                
                // First compare prefixes (stable > rc > beta > alpha)
                int prefixComparison = ComparePrefixes(currentParsed.prefixType, latestParsed.prefixType);
                
                if (prefixComparison != 0)
                {
                    // Different release types
                    bool isNewer = prefixComparison < 0; // Latest has higher prefix rank
                    ModLogger.Info($"  Result: {(isNewer ? "NEWER - Update Available!" : "OLDER - No update needed")} (prefix comparison)");
                    return isNewer;
                }
                
                // Same prefix, compare numeric versions
                if (currentParsed.version != null && latestParsed.version != null)
                {
                    bool isNewer = latestParsed.version > currentParsed.version;
                    bool isEqual = latestParsed.version == currentParsed.version;
                    ModLogger.Info($"  Result: {(isNewer ? "NEWER - Update Available!" : (isEqual ? "SAME - No update needed" : "OLDER - No update needed"))} (version comparison)");
                    return isNewer;
                }
                else
                {
                    // Fallback to string comparison if parsing failed
                    int comparison = string.Compare(currentVersion, latestVersion, StringComparison.Ordinal);
                    bool isNewer = comparison < 0;
                    ModLogger.Info($"  Result: {(isNewer ? "NEWER" : "SAME/OLDER")} (string comparison fallback)");
                    return isNewer;
                }
            }
            catch (Exception e)
            {
                // Fallback to string comparison if version parsing fails
                ModLogger.Warn($"Version parsing failed ({e.Message}), using string comparison");
                int comparison = string.Compare(currentVersion, latestVersion, StringComparison.Ordinal);
                bool isNewer = comparison < 0;
                ModLogger.Info($"String comparison result: {currentVersion} vs {latestVersion} = {(isNewer ? "NEWER" : "SAME/OLDER")}");
                return isNewer;
            }
        }

        /// <summary>
        /// Parse version string with alpha/beta prefix support
        /// Returns (prefixType, prefix, version)
        /// </summary>
        private static (int prefixType, string prefix, Version? version) ParseVersionWithPrefix(string versionString)
        {
            if (string.IsNullOrEmpty(versionString))
            {
                return (0, "", null);
            }

            string prefix = "";
            string numericPart = versionString;
            int prefixType = 3; // Default to stable (highest priority)

            // Check for prefix formats: "alpha-1.0.0" or "beta-1.0.0"
            if (versionString.Contains("-"))
            {
                string[] parts = versionString.Split(new[] { '-' }, 2);
                string possiblePrefix = parts[0].ToLower();
                
                if (possiblePrefix == "alpha" || possiblePrefix == "a")
                {
                    prefix = "alpha-";
                    prefixType = 0;
                    numericPart = parts.Length > 1 ? parts[1] : parts[0];
                }
                else if (possiblePrefix == "beta" || possiblePrefix == "b")
                {
                    prefix = "beta-";
                    prefixType = 1;
                    numericPart = parts.Length > 1 ? parts[1] : parts[0];
                }
                else if (possiblePrefix == "rc")
                {
                    prefix = "rc-";
                    prefixType = 2;
                    numericPart = parts.Length > 1 ? parts[1] : parts[0];
                }
            }
            // Check for suffix formats: "1.0.0a" or "1.0.0alpha"
            else if (versionString.EndsWith("a") || versionString.EndsWith("alpha", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "alpha-";
                prefixType = 0;
                numericPart = versionString.TrimEnd('a', 'A').TrimEnd("alpha", "ALPHA", "Alpha");
            }
            else if (versionString.EndsWith("b") || versionString.EndsWith("beta", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "beta-";
                prefixType = 1;
                numericPart = versionString.TrimEnd('b', 'B').TrimEnd("beta", "BETA", "Beta");
            }

            // Try to parse the numeric part
            Version? version = null;
            try
            {
                version = new Version(numericPart);
            }
            catch
            {
                // If parsing fails, return what we have
            }

            return (prefixType, prefix, version);
        }

        /// <summary>
        /// Helper to trim strings (case-insensitive)
        /// </summary>
        private static string TrimEnd(this string str, params string[] suffixes)
        {
            foreach (var suffix in suffixes)
            {
                if (str.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return str.Substring(0, str.Length - suffix.Length);
                }
            }
            return str;
        }

        /// <summary>
        /// Compare prefix types
        /// Returns: -1 if current < latest, 0 if equal, 1 if current > latest
        /// Priority: stable (3) > rc (2) > beta (1) > alpha (0)
        /// </summary>
        private static int ComparePrefixes(int currentPrefixType, int latestPrefixType)
        {
            return currentPrefixType.CompareTo(latestPrefixType);
        }

        /// <summary>
        /// Check if enough time has passed since last update check
        /// </summary>
        private static bool ShouldCheckForUpdates()
        {
            if (_lastUpdateCheckEntry == null)
            {
                // No preference entry - allow check
                return true;
            }

            long lastCheck = _lastUpdateCheckEntry.Value;
            if (lastCheck == 0)
            {
                // Never checked before - allow check
                return true;
            }

            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long intervalSeconds = (long)(Constants.UPDATE_CHECK_INTERVAL_HOURS * 3600);
            long timeSinceLastCheck = currentTime - lastCheck;

            bool shouldCheck = timeSinceLastCheck > intervalSeconds;
            
            if (!shouldCheck)
            {
                long hoursRemaining = (intervalSeconds - timeSinceLastCheck) / 3600;
                ModLogger.Debug($"Update check skipped - {hoursRemaining} hours remaining in cache interval");
            }

            return shouldCheck;
        }

        /// <summary>
        /// Force an update check (bypasses cache) - for testing/debugging
        /// </summary>
        public static void ForceUpdateCheck()
        {
            _hasCheckedThisSession = false; // Reset session flag
            if (_lastUpdateCheckEntry != null)
            {
                _lastUpdateCheckEntry.Value = 0; // Reset cache
            }
            MelonLoader.MelonCoroutines.Start(CheckForUpdatesAsync(forceCheck: true));
        }

        /// <summary>
        /// Clear update cache - for testing/debugging
        /// </summary>
        public static void ClearUpdateCache()
        {
            if (_lastUpdateCheckEntry != null)
            {
                _lastUpdateCheckEntry.Value = 0;
            }
            if (_cachedLatestVersionEntry != null)
            {
                _cachedLatestVersionEntry.Value = "";
            }
            ModLogger.Info("Update cache cleared");
        }
    }
}

