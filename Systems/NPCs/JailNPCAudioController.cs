using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.NPCs;
#else
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
#else

        [Header("Audio Components")]
#endif
        /// <summary>Primary source used for custom guard voice clips.</summary>
        public AudioSource mainVoiceSource;
        /// <summary>Short source used for radio start/stop beeps.</summary>
        public AudioSource radioBeepSource;
        /// <summary>Looping source used for radio static while a command is spoken.</summary>
        public AudioSource radioStaticSource;

#if MONO
        [Header("Voice Settings")]
#endif
        /// <summary>Multiplier applied to custom voice clip volume.</summary>
        public float volumeMultiplier = 1.0f;
        /// <summary>Random pitch range applied around a neutral pitch.</summary>
        public float pitchVariation = 0.1f;
        /// <summary>Minimum Unity seconds between accepted guard commands.</summary>
        public float commandCooldown = 3.0f;

#if MONO
        [Header("Radio Effect Settings")]
#endif
        /// <summary>Whether commands may use the radio beep/static presentation path.</summary>
        public bool useRadioEffect = true;
        /// <summary>Delay between the radio start beep/static and the voice command, in Unity seconds.</summary>
        public float radioBeepDelay = 0.25f;
        /// <summary>Default volume for the generated radio static source.</summary>
        public float staticVolume = 0.3f;

        // Voice database configuration
        /// <summary>Command clip database selected during startup.</summary>
        private JailVoiceDatabase voiceDatabase;
        /// <summary>Optional native VOEmitter used after custom clips are unavailable.</summary>
        private VOEmitter voiceEmitter;
        /// <summary>True after delayed component positioning has completed or failed closed.</summary>
        private bool isInitialized = false;
        /// <summary>Unity-time timestamp of the last accepted guard command.</summary>
        private float lastCommandTime = 0f;
        /// <summary>Opaque global handle for the active command/radio coroutine.</summary>
        private Coroutine currentVoiceRoutine;
        /// <summary>Opaque global handle for delayed startup completion.</summary>
        private Coroutine delayedInitializationRoutine;

        /// <summary>Semantic guard command categories used to select clips and radio presentation.</summary>
        public enum GuardCommandType
        {
            /// <summary>Stop instruction.</summary>
            Stop,
            /// <summary>Move instruction.</summary>
            Move,
            /// <summary>Follow instruction.</summary>
            Follow,
            /// <summary>Stay-back instruction.</summary>
            StayBack,
            /// <summary>Hands-up instruction.</summary>
            HandsUp,
            /// <summary>Get-down instruction.</summary>
            GetDown,
            /// <summary>Don't-move instruction.</summary>
            DontMove,
            /// <summary>Escort instruction.</summary>
            Escort,
            /// <summary>Cell-check patrol announcement.</summary>
            CellCheck,
            /// <summary>Alert/incident response announcement.</summary>
            Alert,
            /// <summary>All-clear announcement.</summary>
            AllClear,
            /// <summary>Backup request.</summary>
            Backup,
            /// <summary>Greeting.</summary>
            Greeting,
            /// <summary>Warning instruction.</summary>
            Warning,
            /// <summary>Instruction to spread prisoners apart.</summary>
            SpreadThem
        }

        /// <summary>Resolves or creates audio sources before delayed voice setup.</summary>
        protected virtual void Awake()
        {
            InitializeAudioComponents();
        }

        /// <summary>
        /// Creates the command database and optional native VOEmitter, then defers final readiness so native
        /// avatar/audio references have time to hydrate.
        /// </summary>
        protected virtual void Start()
        {
            SetupVoiceDatabase();
            SetupVOEmitter();

            // Delay initialization to ensure all components are ready
            delayedInitializationRoutine = MelonCoroutines.Start(DelayedInitialization()) as Coroutine;
            //StartCoroutine(DelayedInitialization());
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
                    mainVoiceSource = GetComponent<AudioSource>();
                    if (mainVoiceSource == null)
                    {
                        ModLogger.Debug($"Creating new AudioSource for {gameObject.name}");
                        mainVoiceSource = gameObject.AddComponent<AudioSource>();
                    }
                }

                if (mainVoiceSource != null)
                {
                    mainVoiceSource.volume = 0.8f;
                    mainVoiceSource.pitch = 1.0f;
                    mainVoiceSource.spatialBlend = 0.5f;
                    mainVoiceSource.playOnAwake = false;
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

                    radioBeepSource = beepObject.AddComponent<AudioSource>();
                    if (radioBeepSource != null)
                    {
                        radioBeepSource.playOnAwake = false;
                        radioBeepSource.volume = 0.7f;
                        radioBeepSource.spatialBlend = 0.5f;
                    }
                }

                // Create radio static source
                if (radioStaticSource == null)
                {
                    GameObject staticObject = new GameObject("RadioStatic");
                    staticObject.transform.SetParent(transform);

                    radioStaticSource = staticObject.AddComponent<AudioSource>();
                    if (radioStaticSource != null)
                    {
                        radioStaticSource.playOnAwake = false;
                        radioStaticSource.volume = staticVolume;
                        radioStaticSource.spatialBlend = 0.5f;
                        radioStaticSource.loop = true;
                    }
                }

                ModLogger.Debug($"Radio effect sources created for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error creating radio effect sources: {e.Message}");
            }
        }

        /// <summary>
        /// Creates the default runtime voice database. Bundle loading is currently a reduced/placeholder path;
        /// if it remains unavailable, command playback falls through to VOEmitter or diagnostic audio paths.
        /// </summary>
        private void SetupVoiceDatabase()
        {
            try
            {
                // Try to load from asset bundle first, fallback to default
                voiceDatabase = JailVoiceDatabaseFactory.CreateDefault();
                if (voiceDatabase == null)
                {
                    ModLogger.Warn("Voice database factory returned null - voice commands will use fallback only");
                    return;
                }

                if (Behind_Bars.Core.CachedJailBundle != null)
                {
                    voiceDatabase.LoadVoiceClipsFromBundle("behind_bars", "voices");
                    // The loader is currently a reduced placeholder; default command entries remain authoritative.
                    ModLogger.Debug("Voice clips loading temporarily disabled - testing bundle redundancy");
                }

                ModLogger.Debug($"Voice database setup complete for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error setting up voice database: {e.Message}");

                // Fallback to basic database
                voiceDatabase = JailVoiceDatabaseFactory.CreateDefault();
                if (voiceDatabase == null)
                {
                    ModLogger.Warn("Voice database fallback creation failed");
                }
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
                    voiceEmitter.SetRuntimePitchMultiplier(1.0f + UnityEngine.Random.Range(-pitchVariation, pitchVariation));

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
        /// Copies an existing native VOEmitter database through its private field when available. Missing or
        /// inaccessible databases are allowed to fall through to direct audio playback.
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
                                ModLogger.Debug($"Found and set VODatabase from {emitter.gameObject.name}");
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
        /// Completes delayed readiness after native components hydrate. The current implementation positions
        /// this component at the avatar head bone; errors are logged and readiness is still marked true so the
        /// command surface does not remain permanently blocked.
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
                ModLogger.Debug($"JailNPCAudioController fully initialized for {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error in delayed initialization: {e.Message}");
                isInitialized = true; // Set as initialized anyway to prevent blocking
            }
        }

        /// <summary>
        /// Accepts a command when cooldown/readiness permits, replacing any prior command routine. The global
        /// Melon coroutine handle is stored for cancellation; on IL2CPP this cast is an interop-sensitive seam.
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
                MelonCoroutines.Stop(currentVoiceRoutine);
            }

            currentVoiceRoutine = (Coroutine)MelonCoroutines.Start(PlayGuardCommandCoroutine(commandType, useRadio));
            //currentVoiceRoutine = StartCoroutine(PlayGuardCommandCoroutine(commandType, useRadio));
            lastCommandTime = Time.time;
        }

        /// <summary>
        /// Plays optional radio effects, selects the command fallback chain, waits an estimated duration, and
        /// stops radio effects. The estimate is not clip-length introspection and cleanup clears the handle.
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
                            radioStaticSource.clip = staticClip;
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
        /// Attempts custom database audio first, then native VOEmitter playback, then the diagnostic simple
        /// command path. A fallback indicates reduced presentation, not a replacement native voice graph.
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
                        mainVoiceSource.clip = audioClip;
                        mainVoiceSource.volume = volumeMultiplier * voiceEntry.GetVolumeMultiplier() * voiceDatabase.globalVolumeMultiplier;
                        mainVoiceSource.pitch = 1.0f + UnityEngine.Random.Range(-voiceEntry.GetPitchVariation(), voiceEntry.GetPitchVariation());
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
        /// Records the final reduced audio fallback and configures pitch/volume. The current implementation
        /// does not call <see cref="AudioSource.Play"/> because no fallback clip is assigned.
        /// </summary>
        private void PlaySimpleCommandSound(GuardCommandType commandType)
        {
            try
            {
                // Always log the command even if audio fails
                ModLogger.Info($"Guard {gameObject.name} issued command: {commandType} (audio fallback)");

                if (mainVoiceSource == null)
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
                mainVoiceSource.pitch = pitch;
                mainVoiceSource.volume = volumeMultiplier * 0.5f;

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
                            radioBeepSource.clip = beepClip;
                            radioBeepSource.Play();
                            return;
                        }
                    }

                    radioBeepSource.pitch = 2.0f;
                    if (radioBeepSource.clip != null)
                    {
                        radioBeepSource.Play();
                    }
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
        /// Returns a scheduler estimate for radio cleanup timing; it is not the actual duration of a selected
        /// voice clip.
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

        /// <summary>Stops the global command routine and active voice/static sources.</summary>
        public void StopVoiceCommand()
        {
            try
            {
                if (currentVoiceRoutine != null)
                {
                    MelonCoroutines.Stop(currentVoiceRoutine);
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

        /// <summary>Replaces the command database used for future voice selection.</summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void SetVoiceDatabase(JailVoiceDatabase database)
        {
            voiceDatabase = database;
        }

        /// <summary>Returns whether delayed initialization and cooldown permit a command.</summary>
        public bool IsReady()
        {
            return isInitialized && mainVoiceSource != null && Time.time - lastCommandTime >= commandCooldown;
        }

        /// <summary>Stops command/audio coroutines and releases pending delayed initialization on destruction.</summary>
        protected virtual void OnDestroy()
        {
            StopVoiceCommand();
            if (delayedInitializationRoutine != null)
            {
                MelonCoroutines.Stop(delayedInitializationRoutine);
                delayedInitializationRoutine = null;
            }
        }
    }
}
