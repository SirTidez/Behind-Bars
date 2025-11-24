using System;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Version information from GitHub repository
    /// </summary>
    [Serializable]
    public class VersionInfo
    {
        public string version = "";
        public string description = "";
        public string release_date = "";
        public string download_url = "";
        public string changelog_url = "";
    }

    /// <summary>
    /// Result of update check operation
    /// </summary>
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public bool Success { get; set; }
        public VersionInfo? LatestVersion { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }
}

