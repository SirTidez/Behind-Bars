using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Behind_Bars.Helpers;
using Behind_Bars.Utils.Saveable;
using Behind_Bars.Systems.CrimeTracking;
#if !MONO
using S1Persistence = Il2CppScheduleOne.Persistence;
using S1Loaders = Il2CppScheduleOne.Persistence.Loaders;
using S1Datas = Il2CppScheduleOne.Persistence.Datas;
using ListString = Il2CppSystem.Collections.Generic.List<string>;
#else
using S1Persistence = ScheduleOne.Persistence;
using S1Loaders = ScheduleOne.Persistence.Loaders;
using S1Datas = ScheduleOne.Persistence.Datas;
using ListString = System.Collections.Generic.List<string>;
#endif

namespace Behind_Bars.Harmony
{
    /// <summary>
    /// INTERNAL: Save/Load pipeline for mod-registered Saveables not tied to base entities.
    /// Writes to Modded/Saveables and restores on load. Cross-compatible for Mono/IL2CPP.
    /// </summary>
    [HarmonyPatch]
    internal static class SaveablePatches
    {
        [HarmonyPatch(typeof(S1Persistence.SaveManager), "Save", new Type[] { typeof(string) })]
        [HarmonyPostfix]
        private static void SaveManager_Save_Postfix(string saveFolderPath)
        {
            try
            {
                // Ensure top-level Modded paths are whitelisted so the game's cleanup doesn't delete our files
                var saveManager = S1Persistence.SaveManager.Instance;
                if (saveManager == null)
                    return;

                string approvedModded = "Modded";
                string approvedFolder = Path.Combine("Modded", "Saveables");
                if (!saveManager.ApprovedBaseLevelPaths.Contains(approvedModded))
                    saveManager.ApprovedBaseLevelPaths.Add(approvedModded);
                if (!saveManager.ApprovedBaseLevelPaths.Contains(approvedFolder))
                    saveManager.ApprovedBaseLevelPaths.Add(approvedFolder);

                string basePath = Path.Combine(saveFolderPath, "Modded", "Saveables");
                Directory.CreateDirectory(basePath);
                
                ModLogger.Debug($"[SAVEABLE] Starting save process for save folder: {saveFolderPath}");
                
                // Save auto-discovered singleton saveables
                int autoDiscoveredCount = 0;
                foreach (var saveable in SaveableAutoRegistry.GetRegisteredSaveables())
                {
                    try
                    {
                        ListString extra = new ListString();
                        string folder = saveable.GetType().Name;
                        string path = Path.Combine(basePath, folder);
                        Directory.CreateDirectory(path);
                        saveable.SaveInternal(path, ref extra);
                        ModLogger.Debug($"[SAVEABLE] Saved {saveable.GetType().Name} to {path}");
                        autoDiscoveredCount++;
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"[SAVEABLE] Error saving {saveable.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                
                // Save per-player saveables (RapSheet, etc.) that are excluded from auto-discovery
                int perPlayerCount = 0;
                try
                {
                    var rapSheetManager = RapSheetManager.Instance;
                    if (rapSheetManager != null)
                    {
                        var allRapSheets = rapSheetManager.GetAllRapSheets().ToList();
                        ModLogger.Debug($"[SAVEABLE] Found {allRapSheets.Count} RapSheet(s) to save");
                        
                        foreach (var rapSheet in allRapSheets)
                        {
                            try
                            {
                                if (rapSheet == null)
                                    continue;
                                
                                // Only save if it has changed
                                if (!rapSheet.HasChanged)
                                {
                                    ModLogger.Debug($"[SAVEABLE] Skipping RapSheet for {rapSheet.FullName ?? "Unknown"} - no changes");
                                    continue;
                                }
                                
                                // Use the SaveFolderName from RapSheet via ISaveable interface to determine the save path
                                ScheduleOne.Persistence.ISaveable saveableInterface = rapSheet;
                                string rapSheetFolder = saveableInterface.SaveFolderName;
                                string rapSheetPath = Path.Combine(basePath, rapSheetFolder);
                                Directory.CreateDirectory(rapSheetPath);
                                
                                List<string> extra = new List<string>();
                                rapSheet.SaveInternal(rapSheetPath, ref extra);
                                
                                // Mark as unchanged after successful save
                                rapSheet.MarkUnchanged();
                                
                                ModLogger.Debug($"[SAVEABLE] Saved RapSheet for {rapSheet.FullName ?? "Unknown"} to {rapSheetPath}");
                                perPlayerCount++;
                            }
                            catch (Exception ex)
                            {
                                ModLogger.Error($"[SAVEABLE] Error saving RapSheet for {rapSheet?.FullName ?? "Unknown"}: {ex.Message}\n{ex.StackTrace}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[SAVEABLE] Error saving per-player saveables: {ex.Message}\n{ex.StackTrace}");
                }
                
                ModLogger.Debug($"[SAVEABLE] Save process complete - Auto-discovered: {autoDiscoveredCount}, Per-player: {perPlayerCount}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"[SAVEABLE] SaveManager_Save_Postfix failed: {e.Message}\n{e.StackTrace}");
            }
        }

        [HarmonyPatch(typeof(S1Loaders.NPCsLoader), "Load")]
        [HarmonyPostfix]
        private static void AfterBaseLoaders(string mainPath)
        {
            try
            {
                var loadManager = S1Persistence.LoadManager.Instance;
                if (loadManager == null)
                    return;

                string basePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables");
                
                // Use automatic discovery instead of manual registry
                foreach (var saveable in SaveableAutoRegistry.GetRegisteredSaveables())
                {
                    try
                    {
                        string folder = saveable.GetType().Name;
                        string path = Path.Combine(basePath, folder);

                        if (Directory.Exists(path))
                        {
                            // Existing save data found -> load
                            saveable.LoadInternal(path);
                            ModLogger.Debug($"[SAVEABLE] Loaded {saveable.GetType().Name} from {path}");
                        }
                        else
                        {
                            // No save data yet for this save -> initialize once after full game load
                            void InitializeOnLoadComplete()
                            {
                                try
                                {
                                    EventHelper.RemoveListener(InitializeOnLoadComplete, loadManager.onLoadComplete);
                                    ((IRegisterable)saveable).CreateInternal();
                                    ModLogger.Debug($"[SAVEABLE] Initialized {saveable.GetType().Name} after load complete");
                                }
                                catch (Exception e)
                                {
                                    ModLogger.Error($"[SAVEABLE] InitializeOnLoadComplete failed for {saveable.GetType().Name}: {e.Message}\n{e.StackTrace}");
                                }
                            }
                            EventHelper.AddListener(InitializeOnLoadComplete, loadManager.onLoadComplete);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"[SAVEABLE] Error loading {saveable.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"[SAVEABLE] AfterBaseLoaders failed: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}

