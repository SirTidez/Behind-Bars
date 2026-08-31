using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.VoiceOver;
#else
using ScheduleOne.VoiceOver;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Runtime voice database for jail NPCs. MONO stores it as a ScriptableObject asset; IL2CPP uses a plain
    /// managed-compatible instance, so callers must treat the database as runtime-owned on both targets.
    /// Supports command-specific clips, fallback voice types, and randomized selection.
    /// </summary>
    [System.Serializable]
#if MONO
    [CreateAssetMenu(fileName = "JailVoiceDatabase", menuName = "Behind Bars/Jail Voice Database")]
#endif
#if MONO
    public class JailVoiceDatabase : ScriptableObject
#else
    public class JailVoiceDatabase
#endif
    {
#if MONO
        [Header("Database Settings")]
        [Range(0f, 2f)]
#endif
        /// <summary>Global multiplier applied to command-entry volume.</summary>
        public float globalVolumeMultiplier = 1f;

#if MONO
        [Header("Voice Entries")]
#endif
        /// <summary>Command entries keyed by <see cref="JailNPCAudioController.GuardCommandType"/>.</summary>
        public List<JailVoiceEntry> voiceEntries = new List<JailVoiceEntry>();

#if MONO
        [Header("Radio Effects")]
#endif
        /// <summary>Optional clip used for radio start/stop beeps.</summary>
        public AudioClip radioBeepSound;
        /// <summary>Optional looping clip used for radio static.</summary>
        public AudioClip radioStaticSound;
        /// <summary>Whether the audio controller may use radio effects.</summary>
        public bool enableRadioEffects = true;

#if MONO
        [Header("Default Fallback Settings")]
#endif
        /// <summary>Legacy fallback preference retained for serialized compatibility; current callers select VO explicitly.</summary>
        public bool useScheduleOneVOFallback = true;

        /// <summary>
        /// Initialize the voice database with default entries if empty
        /// </summary>
        public void Initialize()
        {
            if (voiceEntries.Count == 0)
            {
                CreateDefaultVoiceEntries();
            }

            ModLogger.Debug($"JailVoiceDatabase initialized with {voiceEntries.Count} voice entries");
        }

        /// <summary>
        /// Gets a random clip for a command when its entry has a populated clip array. A missing entry returns
        /// null; the current implementation assumes an existing entry's array is non-null, so null arrays must
        /// be prevented by entry initialization until the getter contract is hardened.
        /// </summary>
        /// <param name="commandType">Type of guard command</param>
        /// <returns>Audio clip or null if not found</returns>
        public AudioClip GetCommandClip(JailNPCAudioController.GuardCommandType commandType)
        {
            var entry = GetVoiceEntry(commandType);
            if (entry != null && entry.audioClips.Length > 0)
            {
                return entry.GetRandomClip();
            }

            return null;
        }

        /// <summary>
        /// Get voice entry for a specific command type
        /// </summary>
        /// <param name="commandType">Type of guard command</param>
        /// <returns>Voice entry or null if not found</returns>
        public JailVoiceEntry GetVoiceEntry(JailNPCAudioController.GuardCommandType commandType)
        {
            foreach (var entry in voiceEntries)
            {
                if (entry.commandType == commandType)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// Add or update a voice entry
        /// </summary>
        /// <param name="commandType">Command type</param>
        /// <param name="clips">Audio clips for this command</param>
        /// <param name="volumeMultiplier">Volume multiplier for this command</param>
        public void SetVoiceEntry(JailNPCAudioController.GuardCommandType commandType, AudioClip[] clips, float volumeMultiplier = 1f)
        {
            var existingEntry = GetVoiceEntry(commandType);
            if (existingEntry != null)
            {
                existingEntry.audioClips = clips;
                existingEntry.volumeMultiplier = volumeMultiplier;
            }
            else
            {
                var newEntry = new JailVoiceEntry
                {
                    commandType = commandType,
                    audioClips = clips,
                    volumeMultiplier = volumeMultiplier,
                    useRadioEffect = true
                };
                voiceEntries.Add(newEntry);
            }
        }

        /// <summary>
        /// Creates the runtime default command table without audio clips. This is used by the normal factory,
        /// not only tests; bundle loading may subsequently replace the empty arrays.
        /// </summary>
        private void CreateDefaultVoiceEntries()
        {
            var defaultCommands = new[]
            {
                JailNPCAudioController.GuardCommandType.Stop,
                JailNPCAudioController.GuardCommandType.Move,
                JailNPCAudioController.GuardCommandType.Follow,
                JailNPCAudioController.GuardCommandType.StayBack,
                JailNPCAudioController.GuardCommandType.HandsUp,
                JailNPCAudioController.GuardCommandType.GetDown,
                JailNPCAudioController.GuardCommandType.DontMove,
                JailNPCAudioController.GuardCommandType.Escort,
                JailNPCAudioController.GuardCommandType.CellCheck,
                JailNPCAudioController.GuardCommandType.Alert,
                JailNPCAudioController.GuardCommandType.AllClear,
                JailNPCAudioController.GuardCommandType.Backup,
                JailNPCAudioController.GuardCommandType.Greeting,
                JailNPCAudioController.GuardCommandType.Warning
            };

            foreach (var command in defaultCommands)
            {
                var entry = new JailVoiceEntry
                {
                    commandType = command,
                    audioClips = new AudioClip[0], // Empty for now
                    volumeMultiplier = GetDefaultVolumeForCommand(command),
                    useRadioEffect = ShouldUseRadioEffect(command),
                    fallbackEVOType = ConvertCommandToEVOType(command)
                };

                voiceEntries.Add(entry);
            }

            ModLogger.Debug("Created default voice entries for JailVoiceDatabase");
        }

        /// <summary>
        /// Get default volume multiplier for different command types
        /// </summary>
        private float GetDefaultVolumeForCommand(JailNPCAudioController.GuardCommandType command)
        {
            switch (command)
            {
                case JailNPCAudioController.GuardCommandType.Alert:
                case JailNPCAudioController.GuardCommandType.Backup:
                case JailNPCAudioController.GuardCommandType.Stop:
                    return 1.2f; // Louder for urgent commands

                case JailNPCAudioController.GuardCommandType.Greeting:
                case JailNPCAudioController.GuardCommandType.AllClear:
                    return 0.8f; // Quieter for casual commands

                default:
                    return 1.0f; // Normal volume
            }
        }

        /// <summary>
        /// Determine if a command should use radio effect
        /// </summary>
        private bool ShouldUseRadioEffect(JailNPCAudioController.GuardCommandType command)
        {
            switch (command)
            {
                case JailNPCAudioController.GuardCommandType.Alert:
                case JailNPCAudioController.GuardCommandType.Backup:
                case JailNPCAudioController.GuardCommandType.AllClear:
                    return true; // Radio commands

                case JailNPCAudioController.GuardCommandType.Greeting:
                    return false; // Face-to-face greeting

                default:
                    return true; // Most commands use radio
            }
        }

        /// <summary>
        /// Convert guard command to Schedule I EVOLineType for fallback
        /// </summary>
        private EVOLineType ConvertCommandToEVOType(JailNPCAudioController.GuardCommandType command)
        {
            switch (command)
            {
                case JailNPCAudioController.GuardCommandType.Stop:
                case JailNPCAudioController.GuardCommandType.DontMove:
                case JailNPCAudioController.GuardCommandType.HandsUp:
                case JailNPCAudioController.GuardCommandType.GetDown:
                case JailNPCAudioController.GuardCommandType.Move:
                case JailNPCAudioController.GuardCommandType.Follow:
                case JailNPCAudioController.GuardCommandType.StayBack:
                case JailNPCAudioController.GuardCommandType.Escort:
                case JailNPCAudioController.GuardCommandType.SpreadThem:
                    return EVOLineType.Command;

                case JailNPCAudioController.GuardCommandType.Alert:
                case JailNPCAudioController.GuardCommandType.Backup:
                    return EVOLineType.Alerted;

                case JailNPCAudioController.GuardCommandType.Warning:
                    return EVOLineType.Angry;

                case JailNPCAudioController.GuardCommandType.Greeting:
                    return EVOLineType.Greeting;

                case JailNPCAudioController.GuardCommandType.AllClear:
                    return EVOLineType.Acknowledge;

                case JailNPCAudioController.GuardCommandType.CellCheck:
                    return EVOLineType.Question;

                default:
                    return EVOLineType.Command;
            }
        }

        /// <summary>
        /// Placeholder for asset-bundle clip loading. The current implementation logs the request but does not
        /// load or assign clips, so callers must not treat this method as proof that bundle audio is available.
        /// </summary>
        /// <param name="bundleName">Name of the asset bundle</param>
        /// <param name="assetPath">Requested asset path; currently retained for the future loader and unused.</param>
        public void LoadVoiceClipsFromBundle(string bundleName, string assetPath = "voices")
        {
            try
            {
                // This would load actual audio clips from an asset bundle
                // For now, this is a placeholder for future implementation
                ModLogger.Debug($"Loading voice clips from bundle: {bundleName} (placeholder)");

                // Example of how this might work:
                // var bundle = AssetBundle.LoadFromFile(bundleName);
                // var clips = bundle.LoadAllAssets<AudioClip>();
                // AssignClipsToCommands(clips);
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error loading voice clips from bundle: {e.Message}");
            }
        }

        /// <summary>
        /// Get radio beep sound effect
        /// </summary>
        /// <returns>Radio beep audio clip</returns>
        public AudioClip GetRadioBeepSound()
        {
            return radioBeepSound;
        }

        /// <summary>
        /// Get radio static sound effect
        /// </summary>
        /// <returns>Radio static audio clip</returns>
        public AudioClip GetRadioStaticSound()
        {
            return radioStaticSound;
        }

        /// <summary>
        /// Check if radio effects are enabled
        /// </summary>
        /// <returns>True if radio effects should be used</returns>
        public bool ShouldUseRadioEffects()
        {
            return enableRadioEffects;
        }
    }

    /// <summary>
    /// Individual voice entry for a specific command type
    /// </summary>
    [System.Serializable]
    public class JailVoiceEntry
    {
#if MONO
        [Header("Command Configuration")]
#endif
        /// <summary>Command category represented by this entry.</summary>
        public JailNPCAudioController.GuardCommandType commandType;

#if MONO
        [Header("Audio Clips")]
#endif
        /// <summary>Candidate clips; empty arrays produce no custom audio, while null must be avoided by initialization.</summary>
        public AudioClip[] audioClips;

#if MONO
        [Header("Playback Settings")]
        [Range(0f, 2f)]
#endif
        /// <summary>Per-command volume multiplier.</summary>
        public float volumeMultiplier = 1f;

#if MONO
        [Range(0.5f, 2f)]
#endif
        /// <summary>Random pitch range around neutral pitch.</summary>
        public float pitchVariation = 0.1f;

        /// <summary>Whether the command should use radio effects when selected.</summary>
        public bool useRadioEffect = true;

#if MONO
        [Header("Fallback")]
#endif
        /// <summary>Schedule I voice-line category used when native command audio is unavailable.</summary>
        public EVOLineType fallbackEVOType = EVOLineType.Command;

        /// <summary>Last selected clip used for best-effort adjacent-repeat avoidance.</summary>
        private AudioClip lastPlayedClip;

        /// <summary>
        /// Gets a random clip and makes up to five additional attempts to avoid repeating the last clip. This
        /// is best-effort only; a repeated clip remains valid when random selection does not change.
        /// </summary>
        /// <returns>Random audio clip</returns>
        public AudioClip GetRandomClip()
        {
            if (audioClips == null || audioClips.Length == 0)
            {
                return null;
            }

            AudioClip selectedClip = audioClips[UnityEngine.Random.Range(0, audioClips.Length)];

            // Avoid playing the same clip twice in a row if there are multiple clips
            int attempts = 0;
            while (selectedClip == lastPlayedClip && audioClips.Length > 1 && attempts < 5)
            {
                selectedClip = audioClips[UnityEngine.Random.Range(0, audioClips.Length)];
                attempts++;
            }

            lastPlayedClip = selectedClip;
            return selectedClip;
        }

        /// <summary>
        /// Get the fallback EVOLineType for Schedule I's voice system
        /// </summary>
        /// <returns>EVOLineType for fallback</returns>
        public EVOLineType GetFallbackEVOType()
        {
            return fallbackEVOType;
        }

        /// <summary>
        /// Check if this command should use radio effects
        /// </summary>
        /// <returns>True if radio effects should be used</returns>
        public bool ShouldUseRadioEffect()
        {
            return useRadioEffect;
        }

        /// <summary>
        /// Get volume multiplier for this command
        /// </summary>
        /// <returns>Volume multiplier</returns>
        public float GetVolumeMultiplier()
        {
            return volumeMultiplier;
        }

        /// <summary>
        /// Get pitch variation for this command
        /// </summary>
        /// <returns>Pitch variation amount</returns>
        public float GetPitchVariation()
        {
            return pitchVariation;
        }
    }

    /// <summary>
    /// Static factory for creating voice databases
    /// </summary>
    public static class JailVoiceDatabaseFactory
    {
        /// <summary>
        /// Creates and initializes the runtime default database. MONO uses a ScriptableObject instance while
        /// IL2CPP uses a plain managed-compatible object; neither path supplies real clips by itself.
        /// </summary>
        /// <returns>Configured voice database</returns>
        public static JailVoiceDatabase CreateDefault()
        {
            JailVoiceDatabase database;
#if MONO
            database = ScriptableObject.CreateInstance<JailVoiceDatabase>();
#else
            database = new JailVoiceDatabase();
#endif
            if (database == null)
            {
                ModLogger.Error("Failed to create JailVoiceDatabase instance");
                return null;
            }
            database.Initialize();
            return database;
        }

        /// <summary>
        /// Creates a default database and invokes the current placeholder bundle-loading hook. The returned
        /// database is not guaranteed to contain bundle clips until that loader is implemented.
        /// </summary>
        /// <param name="bundlePath">Path to the asset bundle</param>
        /// <returns>Initialized runtime database, possibly still containing only default empty entries.</returns>
        public static JailVoiceDatabase CreateFromBundle(string bundlePath)
        {
            try
            {
                JailVoiceDatabase database;
#if MONO
                database = ScriptableObject.CreateInstance<JailVoiceDatabase>();
#else
                database = new JailVoiceDatabase();
#endif
                if (database == null)
                {
                    ModLogger.Error("Failed to create JailVoiceDatabase instance from bundle");
                    return CreateDefault();
                }
                database.Initialize();
                database.LoadVoiceClipsFromBundle(bundlePath);
                return database;
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error creating voice database from bundle: {e.Message}");
                return CreateDefault();
            }
        }
    }
}
