namespace Behind_Bars.Helpers
{
    /// <summary>
    /// Compile-time identity, policy, timing, and feature defaults for Behind Bars.
    /// </summary>
    /// <remarks>
    /// These values are not a live configuration schema. In particular, the
    /// fallback time constants describe the conversion used by
    /// <see cref="Behind_Bars.Systems.GameTimeManager"/> and should not be read as a claim
    /// that every native Schedule I timer uses the same clock. Some policy
    /// values are retained defaults with no current direct consumer.
    /// </remarks>
    public static class Constants
    {
        /// <summary>
        /// Stable MelonLoader mod identifier.
        /// </summary>
        public const string MOD_ID = "Behind_Bars";

        /// <summary>The display name of the mod.</summary>
        public const string MOD_NAME = "Behind Bars";

        /// <summary>The credited mod author name.</summary>
        public const string MOD_AUTHOR = "SirTidez";

        /// <summary>The packaged mod version advertised to MelonLoader and the updater.</summary>
        /// <remarks>This literal is maintained separately from the remote
        /// version document; the updater compares the two at runtime.</remarks>
        public const string MOD_VERSION = "alpha-1.2.0";

        /// <summary>The short description advertised by the mod metadata.</summary>
        public const string MOD_DESCRIPTION = "Expands the after-arrest experience in Schedule I with jail, bail, court, and parole systems";

        /// <summary>
        /// MelonPreferences category name used by the mod.
        /// </summary>
        public const string PREF_CATEGORY = "Behind_Bars";

        /// <summary>
        /// Legacy/testing minimum jail time value.
        /// </summary>
        /// <remarks>Value is retained for compatibility and is currently used
        /// by <c>JailSystem</c>'s minimum-time alias, but it is not the active
        /// sentence timing source. Its historical testing interpretation is
        /// five seconds; use the game-time conversion for new gameplay timers.</remarks>
        public const float DEFAULT_MIN_JAIL_TIME = 5f;

        /// <summary>Legacy/testing maximum jail time value.</summary>
        /// <remarks>Value is retained for compatibility and is currently used
        /// by <c>JailSystem</c>'s maximum-time alias, but it is not the active
        /// sentence timing source. Its historical testing interpretation is
        /// thirty seconds; use the game-time conversion for new gameplay timers.</remarks>
        public const float DEFAULT_MAX_JAIL_TIME = 30f;

        /// <summary>Default multiplier retained for jail-time policy calculations.</summary>
        /// <remarks>Consumers define the applicable time unit; this value is
        /// not itself a conversion to native game time.</remarks>
        public const float DEFAULT_JAIL_TIME_MULTIPLIER = 1.0f;

        /// <summary>
        /// Number of Unity-scaled real seconds in one fallback game minute.
        /// </summary>
        public const float REAL_SECONDS_PER_GAME_MINUTE = 1f;

        /// <summary>Number of game-clock seconds in one game minute.</summary>
        public const float GAME_SECONDS_PER_GAME_MINUTE = 60f;

        /// <summary>Number of game minutes in one game hour.</summary>
        public const float GAME_MINUTES_PER_GAME_HOUR = 60f;

        /// <summary>Number of game hours in one game day.</summary>
        public const float GAME_HOURS_PER_GAME_DAY = 24f;

        /// <summary>Number of Unity-scaled real seconds in one fallback game hour.</summary>
        public const float REAL_SECONDS_PER_GAME_HOUR = 60f;

        /// <summary>Number of Unity-scaled real seconds in one fallback game day.</summary>
        public const float REAL_SECONDS_PER_GAME_DAY = 1440f;
        
        /// <summary>
        /// Minimum sentence duration, in fallback game minutes (two game hours).
        /// </summary>
        public const float MIN_SENTENCE_GAME_MINUTES = 120f;

        /// <summary>
        /// Maximum sentence duration, in fallback game minutes (five game days).
        /// </summary>
        public const float MAX_SENTENCE_GAME_MINUTES = 7200f;

        /// <summary>
        /// Default multiplier applied by bail policy consumers.</summary>
        /// <remarks>The multiplier has no universal currency unit; callers
        /// determine the amount to which it is applied.</remarks>
        public const float DEFAULT_BAIL_MULTIPLIER = 2.5f;

        /// <summary>
        /// Default negotiation range expressed as a fractional amount (0.2 = 20 percent).
        /// </summary>
        public const float DEFAULT_NEGOTIATION_RANGE = 0.2f;
        
        /// <summary>
        /// Key used by the bail UI for payment.</summary>
        public const UnityEngine.KeyCode BAIL_PAYMENT_KEY = UnityEngine.KeyCode.B;
        
        /// <summary>
        /// Declared default negotiation duration for court policy consumers.
        /// </summary>
        /// <remarks>The value is a bare float; its time unit is defined by the
        /// consuming court flow rather than by this constants class.</remarks>
        public const float DEFAULT_NEGOTIATION_TIME = 60f;

        /// <summary>
        /// Minimum negotiation amount as a fractional value (0.5 = 50 percent).
        /// </summary>
        public const float MIN_NEGOTIATION_AMOUNT = 0.5f;
        
        /// <summary>
        /// Declared default parole duration for parole policy consumers.
        /// </summary>
        /// <remarks>This bare float's unit is defined by the consuming parole
        /// flow; it is not automatically converted here.</remarks>
        public const float DEFAULT_PAROLE_DURATION = 600f;

        /// <summary>Lower bound for a parole search interval, in the consuming flow's time unit.</summary>
        public const float DEFAULT_SEARCH_INTERVAL_MIN = 30f;

        /// <summary>Upper bound for a parole search interval, in the consuming flow's time unit.</summary>
        public const float DEFAULT_SEARCH_INTERVAL_MAX = 120f;

        /// <summary>Default parole search radius, in Unity world units.</summary>
        public const float DEFAULT_SEARCH_RADIUS = 50f;
        
        /// <summary>
        /// Enables multiplayer bail support in feature-gated consumers.
        /// </summary>
        public const bool ENABLE_MULTIPLAYER_BAIL = true;

        /// <summary>Enables friend-funded bail payments in feature-gated consumers.</summary>
        public const bool ENABLE_FRIEND_BAIL_PAYMENT = true;
        
        /// <summary>
        /// Retained compile-time debug-logging flag; the current Core
        /// preference has its own default and does not read this constant.</summary>
        public const bool ENABLE_DEBUG_LOGGING = true;

        /// <summary>
        /// Legacy debug logging flag retained for compatibility.
        /// </summary>
        public const bool DEBUG_LOGGING = false;

        /// <summary>Disables test mode by default.</summary>
        public const bool ENABLE_TEST_MODE = false;
        
        /// <summary>
        /// GitHub account that hosts the remote version document.</summary>
        public const string GITHUB_USERNAME = "SirTidez";

        /// <summary>GitHub repository that hosts the remote version document.</summary>
        public const string GITHUB_REPO = "Behind-Bars";

        /// <summary>Git branch from which the remote version document is read.</summary>
        public const string GITHUB_BRANCH = "parole-development";

        /// <summary>Repository-relative path of the remote version document.</summary>
        public const string VERSION_FILE_PATH = "project_version.json";

        /// <summary>Interval between update checks, in real hours.</summary>
        public const float UPDATE_CHECK_INTERVAL_HOURS = 24f;

        /// <summary>Enables the update-checking feature.</summary>
        public const bool ENABLE_UPDATE_CHECKING = true;

        /// <summary>
        /// Builds the GitHub raw URL for the configured version file.
        /// </summary>
        /// <returns>A URL assembled from the configured account, repository,
        /// branch, and repository-relative path. This property performs no
        /// network request.</returns>
        public static string GITHUB_VERSION_URL => 
            $"https://raw.githubusercontent.com/{GITHUB_USERNAME}/{GITHUB_REPO}/{GITHUB_BRANCH}/{VERSION_FILE_PATH}";
    }
}
