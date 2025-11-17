namespace Behind_Bars.Helpers
{
    public static class Constants
    {
        /// <summary>
        /// Mod information
        /// </summary>
        public const string MOD_ID = "Behind_Bars";
        public const string MOD_NAME = "Behind Bars";
        public const string MOD_AUTHOR = "SirTidez";
        public const string MOD_VERSION = "alpha-1.0.2";
        public const string MOD_DESCRIPTION = "Expands the after-arrest experience in Schedule I with jail, bail, court, and parole systems";

        /// <summary>
        /// MelonPreferences configuration
        /// </summary>
        public const string PREF_CATEGORY = "Behind_Bars";
        
        /// <summary>
        /// Jail system constants
        /// Note: Jail times are now in game minutes (see GameTimeManager for conversion)
        /// </summary>
        public const float DEFAULT_MIN_JAIL_TIME = 5f;  // 5 seconds for testing (deprecated - use game time)
        public const float DEFAULT_MAX_JAIL_TIME = 30f; // 30 seconds max for testing (deprecated - use game time)
        public const float DEFAULT_JAIL_TIME_MULTIPLIER = 1.0f;
        
        /// <summary>
        /// Game time constants (see GameTimeManager for full documentation)
        /// 1 real second = 1 game minute
        /// 1 real minute = 1 game hour
        /// 24 real minutes = 1 game day
        /// </summary>
        public const float REAL_SECONDS_PER_GAME_MINUTE = 1f;      // 1 real second = 1 game minute
        public const float GAME_SECONDS_PER_GAME_MINUTE = 60f;     // 60 game seconds = 1 game minute
        public const float GAME_MINUTES_PER_GAME_HOUR = 60f;       // 60 game minutes = 1 game hour
        public const float GAME_HOURS_PER_GAME_DAY = 24f;          // 24 game hours = 1 game day
        public const float REAL_SECONDS_PER_GAME_HOUR = 60f;       // 1 real minute = 1 game hour
        public const float REAL_SECONDS_PER_GAME_DAY = 1440f;      // 24 real minutes = 1 game day
        
        /// <summary>
        /// Sentence constraints (in game minutes)
        /// </summary>
        public const float MIN_SENTENCE_GAME_MINUTES = 120f;         // Minimum sentence: 2 game hours (120 game minutes)
        public const float MAX_SENTENCE_GAME_MINUTES = 7200f;       // Maximum sentence: 5 game days (7200 game minutes)
        
        /// <summary>
        /// Bail system constants
        /// </summary>
        public const float DEFAULT_BAIL_MULTIPLIER = 2.5f;
        public const float DEFAULT_NEGOTIATION_RANGE = 0.2f;
        
        /// <summary>
        /// Bail system key bindings
        /// </summary>
        public const UnityEngine.KeyCode BAIL_PAYMENT_KEY = UnityEngine.KeyCode.B;
        
        /// <summary>
        /// Court system constants
        /// </summary>
        public const float DEFAULT_NEGOTIATION_TIME = 60f;
        public const float MIN_NEGOTIATION_AMOUNT = 0.5f;
        
        /// <summary>
        /// Parole system constants
        /// </summary>
        public const float DEFAULT_PAROLE_DURATION = 600f;
        public const float DEFAULT_SEARCH_INTERVAL_MIN = 30f;
        public const float DEFAULT_SEARCH_INTERVAL_MAX = 120f;
        public const float DEFAULT_SEARCH_RADIUS = 50f;
        
        /// <summary>
        /// Multiplayer support
        /// </summary>
        public const bool ENABLE_MULTIPLAYER_BAIL = true;
        public const bool ENABLE_FRIEND_BAIL_PAYMENT = true;
        
        /// <summary>
        /// Debug and testing
        /// </summary>
        public const bool ENABLE_DEBUG_LOGGING = true;
        public const bool DEBUG_LOGGING = false;
        public const bool ENABLE_TEST_MODE = false;
        
        /// <summary>
        /// Update checking constants
        /// </summary>
        public const string GITHUB_USERNAME = "SirTidez";
        public const string GITHUB_REPO = "Behind-Bars";
        public const string GITHUB_BRANCH = "parole-development";
        public const string VERSION_FILE_PATH = "project_version.json";
        public const float UPDATE_CHECK_INTERVAL_HOURS = 24f;
        public const bool ENABLE_UPDATE_CHECKING = true;

        /// <summary>
        /// Build GitHub raw URL for version file
        /// </summary>
        public static string GITHUB_VERSION_URL => 
            $"https://raw.githubusercontent.com/{GITHUB_USERNAME}/{GITHUB_REPO}/{GITHUB_BRANCH}/{VERSION_FILE_PATH}";
    }
}