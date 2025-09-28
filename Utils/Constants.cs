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
        public const string MOD_VERSION = "1.0.0";
        public const string MOD_DESCRIPTION = "Expands the after-arrest experience in Schedule I with jail, bail, court, and probation systems";

        /// <summary>
        /// MelonPreferences configuration
        /// </summary>
        public const string PREF_CATEGORY = "Behind_Bars";
        
        /// <summary>
        /// Jail system constants
        /// </summary>
        public const float DEFAULT_MIN_JAIL_TIME = 5f;  // 5 seconds for testing
        public const float DEFAULT_MAX_JAIL_TIME = 30f; // 30 seconds max for testing
        public const float DEFAULT_JAIL_TIME_MULTIPLIER = 1.0f;
        
        /// <summary>
        /// Bail system constants
        /// </summary>
        public const float DEFAULT_BAIL_MULTIPLIER = 2.5f;
        public const float DEFAULT_NEGOTIATION_RANGE = 0.2f;
        
        /// <summary>
        /// Court system constants
        /// </summary>
        public const float DEFAULT_NEGOTIATION_TIME = 60f;
        public const float MIN_NEGOTIATION_AMOUNT = 0.5f;
        
        /// <summary>
        /// Probation system constants
        /// </summary>
        public const float DEFAULT_PROBATION_DURATION = 600f;
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
    }
}