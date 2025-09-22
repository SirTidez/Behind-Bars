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
    /// ScriptableObject-based voice database for jail NPCs
    /// Supports different voice types and randomized audio clips
    /// </summary>
    [System.Serializable]
    [CreateAssetMenu(fileName = "JailVoiceDatabase", menuName = "Behind Bars/Jail Voice Database")]
    public class JailVoiceDatabase : ScriptableObject
    {
        [Header("Database Settings")]
        [Range(0f, 2f)]
        public float globalVolumeMultiplier = 1f;

        [Header("Voice Entries")]
        public List<JailVoiceEntry> voiceEntries = new List<JailVoiceEntry>();

        [Header("Radio Effects")]
        public AudioClip radioBeepSound;
        public AudioClip radioStaticSound;
        public bool enableRadioEffects = true;

        [Header("Default Fallback Settings")]
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
        /// Get a random audio clip for a specific command type
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
        /// Create default voice entries for testing (without actual audio clips)
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
        /// Load voice clips from asset bundle or resources
        /// </summary>
        /// <param name="bundleName">Name of the asset bundle</param>
        /// <param name="assetPath">Path to voice assets</param>
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
        [Header("Command Configuration")]
        public JailNPCAudioController.GuardCommandType commandType;

        [Header("Audio Clips")]
        public AudioClip[] audioClips;

        [Header("Playback Settings")]
        [Range(0f, 2f)]
        public float volumeMultiplier = 1f;

        [Range(0.5f, 2f)]
        public float pitchVariation = 0.1f;

        public bool useRadioEffect = true;

        [Header("Fallback")]
        public EVOLineType fallbackEVOType = EVOLineType.Command;

        private AudioClip lastPlayedClip;

        /// <summary>
        /// Get a random audio clip, avoiding repetition
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
        /// Create a default voice database for testing
        /// </summary>
        /// <returns>Configured voice database</returns>
        public static JailVoiceDatabase CreateDefault()
        {
            var database = ScriptableObject.CreateInstance<JailVoiceDatabase>();
            database.Initialize();
            return database;
        }

        /// <summary>
        /// Create a voice database from asset bundle
        /// </summary>
        /// <param name="bundlePath">Path to the asset bundle</param>
        /// <returns>Voice database loaded from bundle</returns>
        public static JailVoiceDatabase CreateFromBundle(string bundlePath)
        {
            try
            {
                var database = ScriptableObject.CreateInstance<JailVoiceDatabase>();
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