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
    /// Conversion: 1 real second = 1 game minute
    /// </summary>
    public class GameTimeManager
    {
        private static GameTimeManager? _instance;
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

        // Time conversion constants
        public const float REAL_SECONDS_PER_GAME_MINUTE = 1f;      // 1 real second = 1 game minute
        public const float GAME_SECONDS_PER_GAME_MINUTE = 60f;    // 60 game seconds = 1 game minute
        public const float GAME_MINUTES_PER_GAME_HOUR = 60f;      // 60 game minutes = 1 game hour
        public const float GAME_HOURS_PER_GAME_DAY = 24f;         // 24 game hours = 1 game day

        // Derived constants
        public const float REAL_SECONDS_PER_GAME_HOUR = 60f;      // 1 real minute = 1 game hour
        public const float REAL_SECONDS_PER_GAME_DAY = 1440f;    // 24 real minutes = 1 game day

        // Events
        public event Action<int>? OnGameHourChanged;      // Fires when game hour changes (0-23)
        public event Action<int>? OnGameMinuteChanged;    // Fires when game minute changes (0-59)
        public event Action<int>? OnGameDayChanged;       // Fires when game day changes (starts at 1)

        // Current game time state
        private int _currentGameDay = 1;
        private int _currentGameHour = 0;
        private int _currentGameMinute = 0;
        private float _lastUpdateTime = 0f;
        private bool _isInitialized = false;
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
        public static float RealSecondsToGameMinutes(float realSeconds)
        {
            return realSeconds / REAL_SECONDS_PER_GAME_MINUTE;
        }

        /// <summary>
        /// Convert game minutes to real-time seconds
        /// </summary>
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

                // Update every real-time second (which is 1 game minute)
                yield return new WaitForSeconds(REAL_SECONDS_PER_GAME_MINUTE);
            }
        }

        /// <summary>
        /// Shutdown the GameTimeManager
        /// </summary>
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

