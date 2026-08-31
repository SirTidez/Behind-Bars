using System;
using UnityEngine;
using MelonLoader;
using System.Collections;
using Behind_Bars.Helpers;

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Manages game time tracking and conversion
    /// Tracks game time based on Unity's Time.time and fires events for time changes
    /// Conversion: 1 Unity-scaled second = 1 fallback game minute
    /// </summary>
    /// <remarks>
    /// This is the mod's lightweight fallback clock. It derives its value from Unity's
    /// scaled <see cref="Time.time"/> and therefore follows the game's time scale; it is
    /// not a wall-clock/real-time timer. The Schedule I native <c>TimeManager</c> remains
    /// the authoritative source where a system needs the game's persisted schedule.
    /// </remarks>
    public class GameTimeManager
    {
        private static GameTimeManager? _instance;

        /// <summary>
        /// Gets the lazily-created process-wide fallback clock instance.
        /// </summary>
        public static GameTimeManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new GameTimeManager();
                }
                return _instance;
            }
        }

        // Time conversion constants. These describe this fallback clock's scaled-time
        // convention, not the cadence of the native Schedule I calendar.
        /// <summary>Number of scaled Unity seconds represented by one fallback game minute.</summary>
        public const float REAL_SECONDS_PER_GAME_MINUTE = 1f;      // 1 Unity-scaled second = 1 game minute
        /// <summary>Number of game seconds represented by one fallback game minute.</summary>
        public const float GAME_SECONDS_PER_GAME_MINUTE = 60f;    // 60 game seconds = 1 game minute
        /// <summary>Number of fallback game minutes in one game hour.</summary>
        public const float GAME_MINUTES_PER_GAME_HOUR = 60f;      // 60 game minutes = 1 game hour
        /// <summary>Number of fallback game hours in one game day.</summary>
        public const float GAME_HOURS_PER_GAME_DAY = 24f;         // 24 game hours = 1 game day

        // Derived constants, expressed in the same scaled Unity-second convention.
        /// <summary>Number of scaled Unity seconds represented by one fallback game hour.</summary>
        public const float REAL_SECONDS_PER_GAME_HOUR = 60f;      // 1 Unity-scaled minute = 1 game hour
        /// <summary>Number of scaled Unity seconds represented by one fallback game day.</summary>
        public const float REAL_SECONDS_PER_GAME_DAY = 1440f;    // 24 Unity-scaled minutes = 1 game day

        // Events are edge notifications from the once-per-minute sample. They do not replay
        // skipped intermediate values if Unity time advances by more than one game minute.
        /// <summary>Raised when the sampled fallback game hour changes; the argument is 0-23.</summary>
        public event Action<int>? OnGameHourChanged;      // Fires when game hour changes (0-23)
        /// <summary>Raised when the sampled fallback game minute changes; the argument is 0-59.</summary>
        public event Action<int>? OnGameMinuteChanged;    // Fires when game minute changes (0-59)
        /// <summary>Raised when the sampled fallback game day changes; day numbering starts at 1.</summary>
        public event Action<int>? OnGameDayChanged;       // Fires when game day changes (starts at 1)

        // Current sampled fallback time state. The calendar components are derived from
        // Time.time; _lastUpdateTime is retained as the last sample for diagnostics.
        private int _currentGameDay = 1;
        private int _currentGameHour = 0;
        private int _currentGameMinute = 0;
        private float _lastUpdateTime = 0f;
        private bool _isInitialized = false;
        // MelonCoroutines returns an interop handle whose concrete type differs by runtime.
        private object? _updateCoroutine;

        /// <summary>
        /// Initialize the GameTimeManager and start tracking time
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                ModLogger.Warn("GameTimeManager already initialized");
                return;
            }

            _lastUpdateTime = Time.time;
            _isInitialized = true;

            // Start update coroutine
            _updateCoroutine = MelonCoroutines.Start(UpdateGameTime());

            ModLogger.Info("GameTimeManager initialized");
        }

        /// <summary>
        /// Get the current game time in game minutes since start
        /// </summary>
        /// <remarks>
        /// The value is derived directly from Unity's scaled <see cref="Time.time"/> and
        /// resets with the process/session; it is not the native persisted calendar.
        /// </remarks>
        public float GetCurrentGameTimeInMinutes()
        {
            return Time.time / REAL_SECONDS_PER_GAME_MINUTE;
        }

        /// <summary>
        /// Get the current game time in game hours since start
        /// </summary>
        public float GetCurrentGameTimeInHours()
        {
            return GetCurrentGameTimeInMinutes() / GAME_MINUTES_PER_GAME_HOUR;
        }

        /// <summary>
        /// Get the current game time in game days since start
        /// </summary>
        public float GetCurrentGameTimeInDays()
        {
            return GetCurrentGameTimeInHours() / GAME_HOURS_PER_GAME_DAY;
        }

        /// <summary>
        /// Get the current game day (starts at 1)
        /// </summary>
        public int GetCurrentGameDay()
        {
            return _currentGameDay;
        }

        /// <summary>
        /// Get the current game hour (0-23)
        /// </summary>
        public int GetCurrentGameHour()
        {
            return _currentGameHour;
        }

        /// <summary>
        /// Get the current game minute (0-59)
        /// </summary>
        public int GetCurrentGameMinute()
        {
            return _currentGameMinute;
        }

        /// <summary>
        /// Convert real-time seconds to game minutes
        /// </summary>
        /// <remarks>
        /// “Real-time” here means seconds in the fallback conversion convention. Callers
        /// using Unity waits still observe Unity's scaled time, not an unscaled stopwatch.
        /// </remarks>
        public static float RealSecondsToGameMinutes(float realSeconds)
        {
            return realSeconds / REAL_SECONDS_PER_GAME_MINUTE;
        }

        /// <summary>
        /// Convert game minutes to real-time seconds
        /// </summary>
        /// <remarks>
        /// This is a numeric conversion for the fallback clock. It does not guarantee
        /// wall-clock elapsed time when Unity's time scale is changed or paused.
        /// </remarks>
        public static float GameMinutesToRealSeconds(float gameMinutes)
        {
            return gameMinutes * REAL_SECONDS_PER_GAME_MINUTE;
        }

        /// <summary>
        /// Convert game minutes to game hours
        /// </summary>
        public static float GameMinutesToGameHours(float gameMinutes)
        {
            return gameMinutes / GAME_MINUTES_PER_GAME_HOUR;
        }

        /// <summary>
        /// Convert game hours to game minutes
        /// </summary>
        public static float GameHoursToGameMinutes(float gameHours)
        {
            return gameHours * GAME_MINUTES_PER_GAME_HOUR;
        }

        /// <summary>
        /// Convert game days to game minutes
        /// </summary>
        public static float GameDaysToGameMinutes(float gameDays)
        {
            return gameDays * GAME_HOURS_PER_GAME_DAY * GAME_MINUTES_PER_GAME_HOUR;
        }

        /// <summary>
        /// Convert game minutes to game days
        /// </summary>
        public static float GameMinutesToGameDays(float gameMinutes)
        {
            return gameMinutes / (GAME_HOURS_PER_GAME_DAY * GAME_MINUTES_PER_GAME_HOUR);
        }

        /// <summary>
        /// Format game minutes into a human-readable string (e.g., "2d 3h 45m")
        /// </summary>
        public static string FormatGameTime(float gameMinutes)
        {
            if (gameMinutes <= 0)
            {
                return "0m";
            }

            int days = Mathf.FloorToInt(gameMinutes / (GAME_HOURS_PER_GAME_DAY * GAME_MINUTES_PER_GAME_HOUR));
            int hours = Mathf.FloorToInt((gameMinutes % (GAME_HOURS_PER_GAME_DAY * GAME_MINUTES_PER_GAME_HOUR)) / GAME_MINUTES_PER_GAME_HOUR);
            int minutes = Mathf.FloorToInt(gameMinutes % GAME_MINUTES_PER_GAME_HOUR);

            if (days > 0)
            {
                return $"{days}d {hours}h {minutes}m";
            }
            else if (hours > 0)
            {
                return $"{hours}h {minutes}m";
            }
            else
            {
                return $"{minutes}m";
            }
        }

        /// <summary>
        /// Coroutine that updates game time and fires events
        /// </summary>
        /// <remarks>
        /// Each pass samples <see cref="Time.time"/>, derives the calendar components, and
        /// emits only the component changes observed at that sample. The wait is a scaled
        /// <see cref="WaitForSeconds"/> interval, so pausing or changing time scale also
        /// changes when this coroutine samples and raises events.
        /// </remarks>
        private IEnumerator UpdateGameTime()
        {
            while (true)
            {
                float currentRealTime = Time.time;
                float gameMinutes = GetCurrentGameTimeInMinutes();

                // Calculate current game day, hour, minute
                int newGameDay = Mathf.FloorToInt(GameMinutesToGameDays(gameMinutes)) + 1;
                float dayMinutes = gameMinutes % (GAME_HOURS_PER_GAME_DAY * GAME_MINUTES_PER_GAME_HOUR);
                int newGameHour = Mathf.FloorToInt(dayMinutes / GAME_MINUTES_PER_GAME_HOUR);
                int newGameMinute = Mathf.FloorToInt(dayMinutes % GAME_MINUTES_PER_GAME_HOUR);

                // Check for day change
                if (newGameDay != _currentGameDay)
                {
                    _currentGameDay = newGameDay;
                    OnGameDayChanged?.Invoke(_currentGameDay);
                }

                // Check for hour change
                if (newGameHour != _currentGameHour)
                {
                    _currentGameHour = newGameHour;
                    OnGameHourChanged?.Invoke(_currentGameHour);
                }

                // Check for minute change
                if (newGameMinute != _currentGameMinute)
                {
                    _currentGameMinute = newGameMinute;
                    OnGameMinuteChanged?.Invoke(_currentGameMinute);
                }

                _lastUpdateTime = currentRealTime;

                // Update every scaled Unity second (which is 1 fallback game minute).
                yield return new WaitForSeconds(REAL_SECONDS_PER_GAME_MINUTE);
            }
        }

        /// <summary>
        /// Shutdown the GameTimeManager
        /// </summary>
        /// <remarks>
        /// Shutdown stops the sampling coroutine and marks the manager uninitialized. It
        /// intentionally leaves the singleton and event delegate lists intact; callers that
        /// own event subscriptions must remove them before discarding their listeners.
        /// </remarks>
        public void Shutdown()
        {
            if (!_isInitialized)
            {
                return;
            }

            if (_updateCoroutine != null)
            {
                MelonCoroutines.Stop(_updateCoroutine);
                _updateCoroutine = null;
            }

            _isInitialized = false;
            ModLogger.Info("GameTimeManager shut down");
        }
    }
}

