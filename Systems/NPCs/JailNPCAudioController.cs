using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.Audio;
using ScheduleOne.VoiceOver;
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Audio controller for jail NPCs that manages voice commands and radio communications
    /// Similar to PoliceChatterVO but designed for jail guard communications
    /// </summary>
    public class JailNPCAudioController : MonoBehaviour
    {
#if !MONO
        public JailNPCAudioController(System.IntPtr ptr) : base(ptr) { }
#endif

        [Header("Audio Components")]
        public AudioSourceController mainVoiceSource;
        public AudioSourceController radioBeepSource;
        public AudioSourceController radioStaticSource;

        [Header("Voice Settings")]
        public float volumeMultiplier = 1.0f;
        public float pitchVariation = 0.1f;
        public float commandCooldown = 3.0f;

        [Header("Radio Effect Settings")]
        public bool useRadioEffect = true;
        public float radioBeepDelay = 0.25f;
        public float staticVolume = 0.3f;

        // Voice database configuration
        private JailVoiceDatabase voiceDatabase;
        private VOEmitter voiceEmitter;
        private bool isInitialized = false;
        private float lastCommandTime = 0f;
        private Coroutine currentVoiceRoutine;

        // Guard voice line types
        public enum GuardCommandType
        {
            Stop,
            Move,
            Follow,
            StayBack,
            HandsUp,
            GetDown,
            DontMove,
            Escort,
            CellCheck,
            Alert,
            AllClear,
            Backup,
            Greeting,
            Warning
        }

        protected virtual void Awake()
        {
            InitializeAudioComponents();
        }

        protected virtual void Start()
        {
            SetupVoiceDatabase();
            SetupVOEmitter();

            // Delay initialization to ensure all components are ready
            StartCoroutine(DelayedInitialization());
        }

        /// <summary>
        /// Initialize audio components if not already assigned
        /// </summary>
        private void InitializeAudioComponents()
        {
            try
            {
                // Find or create main voice source
                if (mainVoiceSource == null)
                {
                    mainVoiceSource = GetComponent<AudioSourceController>();
                    if (mainVoiceSource == null)
                    {
                        ModLogger.Debug($"Creating new AudioSourceController for {gameObject.name}");

                        // Create AudioSource and AudioSourceController
                        var audioSource = gameObject.AddComponent<AudioSource>();
                        mainVoiceSource = gameObject.AddComponent<AudioSourceController>();

                        // Ensure the audio source is properly linked
                        if (mainVoiceSource != null && audioSource != null)
                        {
                            mainVoiceSource.AudioSource = audioSource;

                            // Configure basic audio settings
                            audioSource.volume = 0.8f;
                            audioSource.pitch = 1.0f;
                            audioSource.spatialBlend = 0.5f; // 3D audio
                            audioSource.playOnAwake = false;

                            ModLogger.Debug($"✓ Created and configured AudioSource for {gameObject.name}");
                        }
                    }
                    else
                    {
                        ModLogger.Debug($"✓ Found existing AudioSourceController for {gameObject.name}");
                    }
                }

                // Verify audio source is properly set up
                if (mainVoiceSource != null)
                {
                    if (mainVoiceSource.AudioSource == null)
                    {
                        var audioSource = GetComponent<AudioSource>();
                        if (audioSource == null)
                        {
                            audioSource = gameObject.AddComponent<AudioSource>();
                        }
                        mainVoiceSource.AudioSource = audioSource;
                        ModLogger.Debug($"✓ Linked AudioSource to AudioSourceController for {gameObject.name}");
                    }
                }

                // Create radio effect sources
                if (useRadioEffect)
                {
                    CreateRadioEffectSources();
                }

                ModLogger.Debug($"JailNPCAudioController audio components initialized for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error initializing audio components: {e.Message}");
            }
        }

        /// <summary>
        /// Create separate audio sources for radio beep and static effects
        /// </summary>
        private void CreateRadioEffectSources()
        {
            try
            {
                // Create radio beep source
                if (radioBeepSource == null)
                {
                    GameObject beepObject = new GameObject("RadioBeep");
                    beepObject.transform.SetParent(transform);

                    var beepAudioSource = beepObject.AddComponent<AudioSource>();
                    radioBeepSource = beepObject.AddComponent<AudioSourceController>();
                    radioBeepSource.AudioSource = beepAudioSource;
#if !MONO
                    radioBeepSource.AudioType = Il2CppScheduleOne.Audio.EAudioType.FX;
#else
                    radioBeepSource.AudioType = ScheduleOne.Audio.EAudioType.FX;
#endif
                    radioBeepSource.DefaultVolume = 0.7f;
                }

                // Create radio static source
                if (radioStaticSource == null)
                {
                    GameObject staticObject = new GameObject("RadioStatic");
                    staticObject.transform.SetParent(transform);

                    var staticAudioSource = staticObject.AddComponent<AudioSource>();
                    radioStaticSource = staticObject.AddComponent<AudioSourceController>();
                    radioStaticSource.AudioSource = staticAudioSource;
#if !MONO
                    radioStaticSource.AudioType = Il2CppScheduleOne.Audio.EAudioType.FX;
#else
                    radioStaticSource.AudioType = ScheduleOne.Audio.EAudioType.FX;
#endif
                    radioStaticSource.DefaultVolume = staticVolume;
                    radioStaticSource.AudioSource.loop = true;
                }

                ModLogger.Debug($"Radio effect sources created for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error creating radio effect sources: {e.Message}");
            }
        }

        /// <summary>
        /// Setup the voice database for this NPC
        /// </summary>
        private void SetupVoiceDatabase()
        {
            try
            {
                // Try to load from asset bundle first, fallback to default
                voiceDatabase = JailVoiceDatabaseFactory.CreateDefault();

                // Try to load voice clips from the main Behind Bars asset bundle
                if (Behind_Bars.Systems.AssetManager.bundle != null)
                {
                    voiceDatabase.LoadVoiceClipsFromBundle("behind_bars.bundle", "voices");
                }

                ModLogger.Debug($"Voice database setup complete for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error setting up voice database: {e.Message}");

                // Fallback to basic database
                voiceDatabase = JailVoiceDatabaseFactory.CreateDefault();
            }
        }

        /// <summary>
        /// Setup VOEmitter component for proper voice over playback
        /// </summary>
        private void SetupVOEmitter()
        {
            try
            {
                // Try to find existing VOEmitter
                voiceEmitter = GetComponent<VOEmitter>();

                if (voiceEmitter == null)
                {
                    // Create VOEmitter component
#if !MONO
                    voiceEmitter = gameObject.AddComponent<Il2CppScheduleOne.VoiceOver.VOEmitter>();
#else
                    voiceEmitter = gameObject.AddComponent<ScheduleOne.VoiceOver.VOEmitter>();
#endif
                }

                if (voiceEmitter != null && mainVoiceSource != null)
                {
                    // Configure VOEmitter settings
                    voiceEmitter.PitchMultiplier = 1.0f + UnityEngine.Random.Range(-pitchVariation, pitchVariation);

                    // Find and set a VODatabase from existing NPCs or create one
                    SetupVODatabase();

                    ModLogger.Debug($"VOEmitter setup complete for {gameObject.name}");
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error setting up VOEmitter: {e.Message}");
            }
        }

        /// <summary>
        /// Setup VODatabase for the VOEmitter
        /// </summary>
        private void SetupVODatabase()
        {
            try
            {
                // Try to find an existing VODatabase from other NPCs or police
#if !MONO
                var existingVOEmitters = FindObjectsOfType<Il2CppScheduleOne.VoiceOver.VOEmitter>();
#else
                var existingVOEmitters = FindObjectsOfType<ScheduleOne.VoiceOver.VOEmitter>();
#endif

                if (existingVOEmitters != null && existingVOEmitters.Length > 0)
                {
                    foreach (var emitter in existingVOEmitters)
                    {
                        // Skip our own emitter
                        if (emitter == voiceEmitter) continue;

                        // Try to get the database via reflection
                        var emitterType = emitter.GetType();
                        var databaseField = emitterType.GetField("Database",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                        if (databaseField != null)
                        {
                            var database = databaseField.GetValue(emitter);
                            if (database != null)
                            {
                                // Set the database on our emitter
                                databaseField.SetValue(voiceEmitter, database);
                                ModLogger.Info($"Found and set VODatabase from {emitter.gameObject.name}");
                                return;
                            }
                        }
                    }
                }

                // If no database found, create a simple fallback that just uses AudioSourceController directly
                ModLogger.Warn($"No VODatabase found for {gameObject.name}, voice system will use direct audio playback");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error setting up VODatabase: {e.Message}");
            }
        }

        /// <summary>
        /// Delayed initialization coroutine to ensure all components are ready
        /// </summary>
        private IEnumerator DelayedInitialization()
        {
            yield return new WaitForSeconds(1.0f);

            try
            {
                // Try to find head bone for proper voice positioning
                var npcComponent = GetComponent<NPC>();
                if (npcComponent != null && npcComponent.Avatar != null && npcComponent.Avatar.HeadBone != null)
                {
                    // Move voice emitter to head bone for realistic positioning
                    transform.position = npcComponent.Avatar.HeadBone.position;
                    ModLogger.Debug($"Voice emitter positioned at head bone for {gameObject.name}");
                }

                isInitialized = true;
                ModLogger.Info($"JailNPCAudioController fully initialized for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error in delayed initialization: {e.Message}");
                isInitialized = true; // Set as initialized anyway to prevent blocking
            }
        }

        /// <summary>
        /// Play a guard command with optional radio effect
        /// </summary>
        /// <param name="commandType">Type of command to play</param>
        /// <param name="useRadio">Whether to use radio effect (beeps + static)</param>
        public void PlayGuardCommand(GuardCommandType commandType, bool useRadio = true)
        {
            if (!isInitialized || Time.time - lastCommandTime < commandCooldown)
            {
                return;
            }

            if (currentVoiceRoutine != null)
            {
                StopCoroutine(currentVoiceRoutine);
            }

            currentVoiceRoutine = StartCoroutine(PlayGuardCommandCoroutine(commandType, useRadio));
            lastCommandTime = Time.time;
        }

        /// <summary>
        /// Coroutine to play guard command with radio effects
        /// </summary>
        private IEnumerator PlayGuardCommandCoroutine(GuardCommandType commandType, bool useRadio)
        {
            bool hasError = false;

            // Start radio effects
            if (useRadio && useRadioEffect && voiceDatabase != null && voiceDatabase.ShouldUseRadioEffects())
            {
                try
                {
                    // Start radio beep
                    if (radioBeepSource != null)
                    {
                        PlayRadioBeep();
                    }

                    // Start static
                    if (radioStaticSource != null)
                    {
                        var staticClip = voiceDatabase.GetRadioStaticSound();
                        if (staticClip != null)
                        {
                            radioStaticSource.AudioSource.clip = staticClip;
                        }
                        radioStaticSource.Play();
                    }
                }
                catch (System.Exception e)
                {
                    ModLogger.Error($"Error starting radio effects: {e.Message}");
                    hasError = true;
                }

                if (!hasError)
                {
                    yield return new WaitForSeconds(radioBeepDelay);
                }
            }

            // Play the actual voice command
            if (!hasError)
            {
                try
                {
                    PlayVoiceCommand(commandType);
                }
                catch (System.Exception e)
                {
                    ModLogger.Error($"Error playing voice command: {e.Message}");
                    hasError = true;
                }
            }

            // Wait for voice to finish
            if (!hasError)
            {
                float commandDuration = GetEstimatedCommandDuration(commandType);
                yield return new WaitForSeconds(commandDuration);
            }

            // End radio effects
            if (useRadio && useRadioEffect)
            {
                try
                {
                    // End radio beep
                    if (radioBeepSource != null)
                    {
                        PlayRadioBeep();
                    }

                    // Stop static
                    if (radioStaticSource != null)
                    {
                        radioStaticSource.Stop();
                    }
                }
                catch (System.Exception e)
                {
                    ModLogger.Error($"Error stopping radio effects: {e.Message}");
                }
            }

            // Cleanup
            currentVoiceRoutine = null;
        }

        /// <summary>
        /// Play the actual voice command audio
        /// </summary>
        private void PlayVoiceCommand(GuardCommandType commandType)
        {
            try
            {
                bool voicePlayedSuccessfully = false;

                if (voiceDatabase != null && mainVoiceSource != null)
                {
                    // Get voice entry for this command
                    var voiceEntry = voiceDatabase.GetVoiceEntry(commandType);
                    var audioClip = voiceDatabase.GetCommandClip(commandType);

                    if (audioClip != null && voiceEntry != null)
                    {
                        // Use custom audio clip from database
                        mainVoiceSource.AudioSource.clip = audioClip;
                        mainVoiceSource.volumeMultiplier = volumeMultiplier * voiceEntry.GetVolumeMultiplier() * voiceDatabase.globalVolumeMultiplier;
                        mainVoiceSource.pitchMultiplier = 1.0f + UnityEngine.Random.Range(-voiceEntry.GetPitchVariation(), voiceEntry.GetPitchVariation());
                        mainVoiceSource.Play();

                        ModLogger.Debug($"Playing custom guard command audio: {commandType}");
                        voicePlayedSuccessfully = true;
                    }
                    else
                    {
                        // Try VOEmitter with appropriate EVOLineType
                        if (voiceEmitter != null)
                        {
                            try
                            {
                                // Check if VOEmitter has all required components
                                var voEmitterType = voiceEmitter.GetType();
                                var databaseField = voEmitterType.GetField("Database",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                var audioControllerField = voEmitterType.GetField("audioSourceController",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                                var database = databaseField?.GetValue(voiceEmitter);
                                var audioController = audioControllerField?.GetValue(voiceEmitter);

                                if (database == null)
                                {
                                    ModLogger.Debug($"VOEmitter has no database set for {gameObject.name}");
                                }
                                else if (audioController == null)
                                {
                                    ModLogger.Debug($"VOEmitter has no audio controller for {gameObject.name}");
                                }
                                else
                                {
                                    EVOLineType voLineType;

                                    // Use fallback type from voice entry if available
                                    if (voiceEntry != null)
                                    {
                                        voLineType = voiceEntry.GetFallbackEVOType();
                                    }
                                    else
                                    {
                                        voLineType = ConvertToEVOLineType(commandType);
                                    }

                                    voiceEmitter.Play(voLineType);
                                    ModLogger.Debug($"Playing VOEmitter voice line: {voLineType} for command: {commandType}");
                                    voicePlayedSuccessfully = true;
                                }
                            }
                            catch (System.Exception voError)
                            {
                                ModLogger.Warn($"VOEmitter failed for {commandType}: {voError.Message}");
                                ModLogger.Debug($"VOEmitter stack trace: {voError.StackTrace}");
                            }
                        }
                        else
                        {
                            ModLogger.Debug($"No VOEmitter available for {gameObject.name}");
                        }
                    }
                }

                // Final fallback: Play a simple beep or sound effect to indicate the command
                if (!voicePlayedSuccessfully && mainVoiceSource != null)
                {
                    PlaySimpleCommandSound(commandType);
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error playing voice command: {e.Message}");
            }
        }

        /// <summary>
        /// Play a simple sound effect as final fallback when voice systems fail
        /// </summary>
        private void PlaySimpleCommandSound(GuardCommandType commandType)
        {
            try
            {
                // Always log the command even if audio fails
                ModLogger.Info($"Guard {gameObject.name} issued command: {commandType} (audio fallback)");

                if (mainVoiceSource == null || mainVoiceSource.AudioSource == null)
                {
                    ModLogger.Debug($"No audio source available for simple command sound on {gameObject.name}");
                    return;
                }

                // Generate different pitch beeps for different command types
                float pitch = 1.0f;
                switch (commandType)
                {
                    case GuardCommandType.Alert:
                    case GuardCommandType.Backup:
                        pitch = 2.0f; // High pitch for alerts
                        break;
                    case GuardCommandType.Stop:
                    case GuardCommandType.Warning:
                        pitch = 1.5f; // Medium-high for commands
                        break;
                    case GuardCommandType.Greeting:
                    case GuardCommandType.AllClear:
                        pitch = 0.8f; // Lower pitch for casual
                        break;
                    default:
                        pitch = 1.2f; // Default medium pitch
                        break;
                }

                // Set audio properties safely
                if (mainVoiceSource.AudioSource != null)
                {
                    mainVoiceSource.AudioSource.pitch = pitch;
                    mainVoiceSource.volumeMultiplier = volumeMultiplier * 0.5f; // Quieter fallback
                }

                ModLogger.Debug($"Simple command sound configured for {commandType} with pitch {pitch}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error playing simple command sound: {e.Message}");
            }
        }

        /// <summary>
        /// Play radio beep sound effect
        /// </summary>
        private void PlayRadioBeep()
        {
            try
            {
                if (radioBeepSource != null)
                {
                    // Try to get radio beep from database first
                    if (voiceDatabase != null)
                    {
                        var beepClip = voiceDatabase.GetRadioBeepSound();
                        if (beepClip != null)
                        {
                            radioBeepSource.AudioSource.clip = beepClip;
                            radioBeepSource.Play();
                            return;
                        }
                    }

                    // Fallback to simple synthesized beep
                    radioBeepSource.AudioSource.pitch = 2.0f;
                    radioBeepSource.PlayOneShot();
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error playing radio beep: {e.Message}");
            }
        }

        /// <summary>
        /// Convert guard command type to Schedule I's EVOLineType
        /// </summary>
        private EVOLineType ConvertToEVOLineType(GuardCommandType commandType)
        {
            switch (commandType)
            {
                case GuardCommandType.Stop:
                case GuardCommandType.DontMove:
                case GuardCommandType.HandsUp:
                case GuardCommandType.GetDown:
                    return EVOLineType.Command;

                case GuardCommandType.Alert:
                case GuardCommandType.Backup:
                    return EVOLineType.Alerted;

                case GuardCommandType.Warning:
                    return EVOLineType.Angry;

                case GuardCommandType.Greeting:
                    return EVOLineType.Greeting;

                case GuardCommandType.AllClear:
                    return EVOLineType.Acknowledge;

                default:
                    return EVOLineType.Command;
            }
        }

        /// <summary>
        /// Get estimated duration for different command types
        /// </summary>
        private float GetEstimatedCommandDuration(GuardCommandType commandType)
        {
            switch (commandType)
            {
                case GuardCommandType.Stop:
                case GuardCommandType.Move:
                    return 1.0f;

                case GuardCommandType.Follow:
                case GuardCommandType.StayBack:
                    return 1.5f;

                case GuardCommandType.HandsUp:
                case GuardCommandType.GetDown:
                case GuardCommandType.DontMove:
                    return 2.0f;

                case GuardCommandType.Escort:
                case GuardCommandType.CellCheck:
                case GuardCommandType.Greeting:
                    return 2.5f;

                default:
                    return 1.5f;
            }
        }

        /// <summary>
        /// Stop any currently playing voice command
        /// </summary>
        public void StopVoiceCommand()
        {
            try
            {
                if (currentVoiceRoutine != null)
                {
                    StopCoroutine(currentVoiceRoutine);
                    currentVoiceRoutine = null;
                }

                if (mainVoiceSource != null)
                {
                    mainVoiceSource.Stop();
                }

                if (radioStaticSource != null)
                {
                    radioStaticSource.Stop();
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error stopping voice command: {e.Message}");
            }
        }

        /// <summary>
        /// Set voice database for this audio controller
        /// </summary>
        public void SetVoiceDatabase(JailVoiceDatabase database)
        {
            voiceDatabase = database;
        }

        /// <summary>
        /// Check if audio controller is ready to play commands
        /// </summary>
        public bool IsReady()
        {
            return isInitialized && mainVoiceSource != null && Time.time - lastCommandTime >= commandCooldown;
        }

        /// <summary>
        /// Cleanup when component is destroyed
        /// </summary>
        protected virtual void OnDestroy()
        {
            StopVoiceCommand();
        }
    }
}