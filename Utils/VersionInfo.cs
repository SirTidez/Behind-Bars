using System;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Version information read from the remote GitHub project-version document.
    /// </summary>
    /// <remarks>
    /// The public field names, including snake_case casing, are the JSON wire
    /// contract consumed by the updater. Keep them aligned with
    /// <c>project_version.json</c>; this DTO has no validation or normalization.
    /// </remarks>
    [Serializable]
    public class VersionInfo
    {
        /// <summary>Remote version identifier compared with the local mod version.</summary>
        public string version = "";

        /// <summary>Human-readable release description.</summary>
        public string description = "";

        /// <summary>Release date string as supplied by the remote document.</summary>
        public string release_date = "";

        /// <summary>Optional URL for downloading the release.</summary>
        public string download_url = "";

        /// <summary>Optional URL for release notes/changelog.</summary>
        public string changelog_url = "";
    }

    /// <summary>
    /// Result data for an update-check operation.
    /// </summary>
    /// <remarks>A failed operation should set <see cref="Success"/> to
    /// <c>false</c> and may provide <see cref="ErrorMessage"/>. The latest
    /// version is nullable and the availability flag is meaningful only when a
    /// check completed successfully. This type is a data container; it does not
    /// perform the network request or version comparison itself.</remarks>
    public class UpdateCheckResult
    {
        /// <summary>Whether the completed check found a newer version.</summary>
        public bool UpdateAvailable { get; set; }

        /// <summary>Whether the check completed with usable version data.</summary>
        public bool Success { get; set; }

        /// <summary>The fetched version data, or <c>null</c> when unavailable.</summary>
        public VersionInfo? LatestVersion { get; set; }

        /// <summary>The local version used for comparison.</summary>
        public string CurrentVersion { get; set; } = "";

        /// <summary>Failure detail when the check did not succeed.</summary>
        public string ErrorMessage { get; set; } = "";
    }
}

