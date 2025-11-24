using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Behind_Bars.Helpers;
using Behind_Bars.Systems;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Utils.Saveable;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Persistence;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Persistence;
#endif

namespace Behind_Bars.Systems.Testing
{
    /// <summary>
    /// Test system for saving test data using Alt + letter keybinds.
    /// Provides unit testing capabilities for the saveable system.
    /// </summary>
    public class SaveableTestSystem : MonoBehaviour
    {
        private static SaveableTestSystem? _instance;
        public static SaveableTestSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("SaveableTestSystem");
                    _instance = go.AddComponent<SaveableTestSystem>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            // Ensure the GameObject is active so Update() is called
            gameObject.SetActive(true);
        }

        private void Update()
        {
            // Check for Alt + letter combinations
            // Only process if game is loaded to avoid errors
#if !MONO
            var loadManager = Il2CppScheduleOne.Persistence.LoadManager.Instance;
#else
            var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
#endif
            if (loadManager == null || !loadManager.IsGameLoaded)
                return;

            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                // Alt + S: Save all saveables
                if (Input.GetKeyDown(KeyCode.S))
                {
                    ModLogger.Info("[TEST] Alt+S pressed - Saving all saveables");
                    TestSaveAll();
                }
                // Alt + L: Load all saveables
                else if (Input.GetKeyDown(KeyCode.L))
                {
                    ModLogger.Info("[TEST] Alt+L pressed - Loading all saveables");
                    TestLoadAll();
                }
                // Alt + R: Save RapSheet test data
                else if (Input.GetKeyDown(KeyCode.R))
                {
                    ModLogger.Info("[TEST] Alt+R pressed - Saving RapSheet test data");
                    TestSaveRapSheet();
                }
                // Alt + P: Save ParoleRecord test data
                else if (Input.GetKeyDown(KeyCode.P))
                {
                    ModLogger.Info("[TEST] Alt+P pressed - Saving ParoleRecord test data");
                    TestSaveParoleRecord();
                }
                // Alt + D: Dump saveable info
                else if (Input.GetKeyDown(KeyCode.D))
                {
                    ModLogger.Info("[TEST] Alt+D pressed - Dumping saveable info");
                    DumpSaveableInfo();
                }
                // Alt + C: Clear this mod's save data only
                else if (Input.GetKeyDown(KeyCode.C))
                {
                    ModLogger.Info("[TEST] Alt+C pressed - Clearing Behind Bars save data");
                    ClearModSaveData();
                }
            }
        }

        /// <summary>
        /// Test saving all discovered saveables.
        /// </summary>
        private void TestSaveAll()
        {
            try
            {
                ModLogger.Info("=== TEST: Saving All Saveables ===");
                
#if !MONO
                var loadManager = Il2CppScheduleOne.Persistence.LoadManager.Instance;
#else
                var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
#endif
                if (loadManager == null || !loadManager.IsGameLoaded)
                {
                    ModLogger.Warn("Game not loaded - cannot save");
                    return;
                }

                string basePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables");
                Directory.CreateDirectory(basePath);

                int savedCount = 0;
                foreach (var saveable in SaveableAutoRegistry.GetRegisteredSaveables())
                {
                    try
                    {
                        string folder = saveable.GetType().Name;
                        string path = Path.Combine(basePath, folder);
                        Directory.CreateDirectory(path);

#if !MONO
                        Il2CppSystem.Collections.Generic.List<string> extra = new Il2CppSystem.Collections.Generic.List<string>();
#else
                        System.Collections.Generic.List<string> extra = new System.Collections.Generic.List<string>();
#endif
                        saveable.SaveInternal(path, ref extra);
                        ModLogger.Info($"✓ Saved {saveable.GetType().Name} to {path}");
                        savedCount++;
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"✗ Failed to save {saveable.GetType().Name}: {ex.Message}");
                    }
                }

                ModLogger.Info($"=== TEST COMPLETE: Saved {savedCount} saveables ===");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TestSaveAll failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Test loading all discovered saveables.
        /// </summary>
        private void TestLoadAll()
        {
            try
            {
                ModLogger.Info("=== TEST: Loading All Saveables ===");
                
#if !MONO
                var loadManager = Il2CppScheduleOne.Persistence.LoadManager.Instance;
#else
                var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
#endif
                if (loadManager == null || !loadManager.IsGameLoaded)
                {
                    ModLogger.Warn("Game not loaded - cannot load");
                    return;
                }

                string basePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables");
                
                int loadedCount = 0;
                foreach (var saveable in SaveableAutoRegistry.GetRegisteredSaveables())
                {
                    try
                    {
                        string folder = saveable.GetType().Name;
                        string path = Path.Combine(basePath, folder);

                        if (Directory.Exists(path))
                        {
                            saveable.LoadInternal(path);
                            ModLogger.Info($"✓ Loaded {saveable.GetType().Name} from {path}");
                            loadedCount++;
                        }
                        else
                        {
                            ModLogger.Warn($"✗ No save data found for {saveable.GetType().Name} at {path}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"✗ Failed to load {saveable.GetType().Name}: {ex.Message}");
                    }
                }

                ModLogger.Info($"=== TEST COMPLETE: Loaded {loadedCount} saveables ===");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TestLoadAll failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Test saving RapSheet with test data.
        /// </summary>
        private void TestSaveRapSheet()
        {
            try
            {
                ModLogger.Info("=== TEST: Saving RapSheet Test Data ===");
                
                var player = Player.Local;
                if (player == null)
                {
                    ModLogger.Warn("No local player - cannot create test RapSheet");
                    return;
                }

                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Warn("No RapSheet found for player - creating test one");
                    rapSheet = new RapSheet(player);
                }

                // Add some test data
                var testCrime = new CrimeInstance
                {
                    Description = "Test Crime",
                    Severity = 5f,
                    Timestamp = GameTimeManager.Instance.GetCurrentGameTimeInMinutes(),
                    Location = player.transform.position
                };
                rapSheet.AddCrime(testCrime);

                rapSheet.MarkChanged();
                
#if !MONO
                var loadManager = Il2CppScheduleOne.Persistence.LoadManager.Instance;
#else
                var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
#endif
                if (loadManager == null || !loadManager.IsGameLoaded)
                {
                    ModLogger.Warn("Game not loaded - cannot save");
                    return;
                }

                string basePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables");
                Directory.CreateDirectory(basePath);
                string path = Path.Combine(basePath, "RapSheet");
                Directory.CreateDirectory(path);

#if !MONO
                Il2CppSystem.Collections.Generic.List<string> extra = new Il2CppSystem.Collections.Generic.List<string>();
#else
                System.Collections.Generic.List<string> extra = new System.Collections.Generic.List<string>();
#endif
                rapSheet.SaveInternal(path, ref extra);
                
                ModLogger.Info($"✓ Saved RapSheet test data to {path}");
                ModLogger.Info($"  - Crimes: {rapSheet.CrimesCommited.Count}");
                ModLogger.Info($"  - Parole Records: {rapSheet.PastParoleRecords.Count}");
                ModLogger.Info("=== TEST COMPLETE ===");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TestSaveRapSheet failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Test saving ParoleRecord with test data.
        /// </summary>
        private void TestSaveParoleRecord()
        {
            try
            {
                ModLogger.Info("=== TEST: Saving ParoleRecord Test Data ===");
                
                var player = Player.Local;
                if (player == null)
                {
                    ModLogger.Warn("No local player - cannot create test ParoleRecord");
                    return;
                }

                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Warn("No RapSheet found - creating one first");
                    rapSheet = new RapSheet(player);
                }

                if (rapSheet.CurrentParoleRecord == null)
                {
                    rapSheet.CurrentParoleRecord = new ParoleRecord(player);
                }

                // Add test parole data
                rapSheet.CurrentParoleRecord.StartParole(1440f); // 24 hours in game minutes
                rapSheet.MarkChanged();

#if !MONO
                var loadManager = Il2CppScheduleOne.Persistence.LoadManager.Instance;
#else
                var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
#endif
                if (loadManager == null || !loadManager.IsGameLoaded)
                {
                    ModLogger.Warn("Game not loaded - cannot save");
                    return;
                }

                string basePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables");
                Directory.CreateDirectory(basePath);
                string path = Path.Combine(basePath, "RapSheet");
                Directory.CreateDirectory(path);

#if !MONO
                Il2CppSystem.Collections.Generic.List<string> extra = new Il2CppSystem.Collections.Generic.List<string>();
#else
                System.Collections.Generic.List<string> extra = new System.Collections.Generic.List<string>();
#endif
                rapSheet.SaveInternal(path, ref extra);
                
                ModLogger.Info($"✓ Saved ParoleRecord test data to {path}");
                ModLogger.Info($"  - Parole Active: {rapSheet.CurrentParoleRecord.IsOnParole()}");
                ModLogger.Info("=== TEST COMPLETE ===");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TestSaveParoleRecord failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Dump information about all discovered saveables.
        /// </summary>
        private void DumpSaveableInfo()
        {
            try
            {
                ModLogger.Info("=== SAVEABLE INFO DUMP ===");
                
                var saveables = SaveableAutoRegistry.GetRegisteredSaveables().ToList();
                ModLogger.Info($"Total saveables discovered: {saveables.Count}");
                
                foreach (var saveable in saveables)
                {
                    ModLogger.Info($"  - {saveable.GetType().FullName}");
                    ScheduleOne.Persistence.ISaveable gameInterface = saveable;
                    ModLogger.Info($"    Folder: {gameInterface.SaveFolderName}");
                    ModLogger.Info($"    File: {gameInterface.SaveFileName}");
                    ModLogger.Info($"    Save Under Folder: {gameInterface.ShouldSaveUnderFolder}");
                    ModLogger.Info($"    Has Changed: {saveable.HasChanged}");
                }
                
                ModLogger.Info("=== DUMP COMPLETE ===");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DumpSaveableInfo failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Clear only this mod's save data (for testing).
        /// Only deletes BehindBars folder, not other mods' data.
        /// </summary>
        private void ClearModSaveData()
        {
            try
            {
                ModLogger.Info("=== TEST: Clearing Behind Bars Save Data ===");
                
#if !MONO
                var loadManager = Il2CppScheduleOne.Persistence.LoadManager.Instance;
#else
                var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
#endif
                if (loadManager == null || !loadManager.IsGameLoaded)
                {
                    ModLogger.Warn("Game not loaded - cannot clear");
                    return;
                }

                // Only delete this mod's specific folder: Modded/Saveables/BehindBars
                string basePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables", "BehindBars");
                
                if (Directory.Exists(basePath))
                {
                    Directory.Delete(basePath, true);
                    ModLogger.Info($"✓ Deleted Behind Bars save data directory: {basePath}");
                    
                    // Also clear RapSheetManager cache
                    RapSheetManager.Instance.ClearCache();
                    ModLogger.Info("✓ Cleared RapSheetManager cache");
                }
                else
                {
                    ModLogger.Info("No Behind Bars save data directory found to delete");
                }
                
                ModLogger.Info("=== TEST COMPLETE ===");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ClearModSaveData failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}

