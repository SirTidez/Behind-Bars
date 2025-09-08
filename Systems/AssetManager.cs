using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;
#if !MONO
using Il2CppFishNet.Object;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.PlayerScripts;
#else

#endif

namespace Behind_Bars.Systems
{
    public class AssetManager
    {
#if !MONO
        public static Il2CppAssetBundle bundle;
#else
        public static AssetBundle bundle;
#endif

        public void Init()
        {
            try
            {
                bundle = AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars.bundle");

                if (bundle != null)
                {
                    ModLogger.Debug("AssetBundle loaded successfully.");

                    // Optionally list all asset names in the bundle for verification
                    string[] assetNames = bundle.GetAllAssetNames();
                    ModLogger.Debug($"Assets in bundle: {string.Join(", ", assetNames)}");

#if MONO
                    var meshes = bundle.LoadAllAssets<GameObject>();
#else
                    var meshes = bundle.LoadAllAssets(Il2CppType.Of<GameObject>());
#endif
                    ModLogger.Debug($"Loaded {meshes.Length} meshes from the bundle.");
                    for (int i = 0; i < meshes.Length; i++)
                    {
#if !MONO
                        ModLogger.Debug($"  mesh[{i}]={(meshes[i].TryCast<GameObject>()?.name ?? "<null>")}");
#else
                        ModLogger.Debug($"  mesh[{i}]={meshes[i]?.name ?? "<null>"}");
#endif
                    }
                }
                else
                {
                    ModLogger.Error("Failed to load AssetBundle.");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Exception occurred while loading AssetBundle: {e.Message}");
            }
        }

        /// <summary>
        /// Generic method to spawn any type of furniture asset
        /// </summary>
        /// <typeparam name="T">The type of asset to spawn (must implement ISpawnableAsset)</typeparam>
        /// <param name="furnitureType">The furniture type to spawn</param>
        /// <returns>The spawned asset or null if failed</returns>
        public T SpawnAsset<T>(FurnitureType furnitureType) where T : class, ISpawnableAsset
        {
            try
            {
                var asset = AssetFactory.CreateAsset(furnitureType);
                if (asset is T typedAsset)
                {
                    ModLogger.Debug($"Successfully spawned {furnitureType} as {typeof(T).Name}");
                    return typedAsset;
                }
                else
                {
                    ModLogger.Error($"Failed to cast spawned asset to {typeof(T).Name}");
                    return null;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning {furnitureType}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Non-generic method to spawn assets (returns ISpawnableAsset)
        /// </summary>
        /// <param name="furnitureType">The furniture type to spawn</param>
        /// <returns>The spawned asset or null if failed</returns>
        public ISpawnableAsset SpawnAsset(FurnitureType furnitureType)
        {
            return AssetFactory.CreateAsset(furnitureType);
        }

        public void TestToiletSinkSystem()
        {
            ModLogger.Debug("Testing ToiletSink management system...");
            
            // Spawn a toilet sink using generic method
            var sink = SpawnAsset<ToiletSink>(FurnitureType.ToiletSink);
            if (sink != null)
            {
                ModLogger.Debug($"Successfully spawned toilet sink: {sink.GetDebugInfo()}");
                ModLogger.Debug($"Total toilet sinks: {ToiletSinkManager.GetToiletSinkCount()}");
            }
            else
            {
                ModLogger.Error("Failed to spawn toilet sink");
            }
        }

        public void TestBunkBedSystem()
        {
            ModLogger.Debug("Testing BunkBed management system...");
            
            // Spawn a bunk bed using generic method
            var bed = SpawnAsset<BunkBed>(FurnitureType.BunkBed);
            if (bed != null)
            {
                ModLogger.Debug($"Successfully spawned bunk bed: {bed.GetDebugInfo()}");
                ModLogger.Debug($"Total bunk beds: {BunkBedManager.GetBunkBedCount()}");
            }
            else
            {
                ModLogger.Error("Failed to spawn bunk bed");
            }
        }

        public void Unload()
        {
            if (bundle != null)
            {
                bundle.Unload(true);
                bundle = null;
                ModLogger.Debug("AssetBundle unloaded successfully.");
            }
        }

        /// <summary>
        /// Registers a new asset type with the factory system
        /// </summary>
        /// <param name="furnitureType">The new furniture type to register</param>
        /// <param name="spawnPoint">Default spawn point</param>
        /// <param name="prefabPath">Path to the prefab in the asset bundle</param>
        /// <param name="layerName">Layer name for the asset</param>
        /// <param name="colliderSize">Size of the collider</param>
        /// <param name="materialColor">Default material color</param>
        public void RegisterAssetType(FurnitureType furnitureType, Vector3 spawnPoint, string prefabPath, 
            string layerName, Vector3 colliderSize, Color materialColor)
        {
            AssetFactory.RegisterAssetType(furnitureType, spawnPoint, prefabPath, layerName, colliderSize, materialColor);
        }

        /// <summary>
        /// Gets all registered asset types
        /// </summary>
        /// <returns>List of registered furniture types</returns>
        public List<FurnitureType> GetRegisteredAssetTypes()
        {
            return AssetFactory.GetRegisteredAssetTypes();
        }

        /// <summary>
        /// Spawns all registered asset types at their default locations
        /// </summary>
        public void SpawnAllRegisteredAssets()
        {
            var assetTypes = GetRegisteredAssetTypes();
            ModLogger.Info($"Spawning {assetTypes.Count} registered asset types...");
            
            foreach (var assetType in assetTypes)
            {
                var asset = SpawnAsset(assetType);
                if (asset != null)
                {
                    ModLogger.Info($"Successfully spawned {assetType}");
                }
                else
                {
                    ModLogger.Warn($"Failed to spawn {assetType}");
                }
            }
        }

        /// <summary>
        /// Example of how to add a new asset type:
        /// 
        /// 1. Add the new type to the FurnitureType enum:
        ///    public enum FurnitureType { BunkBed, ToiletSink, NewAsset }
        /// 
        /// 2. Create the asset class implementing ISpawnableAsset:
        ///    public sealed class NewAsset : MonoBehaviour, ISpawnableAsset { ... }
        /// 
        /// 3. Create the manager class following the same pattern as BunkBedManager
        /// 
        /// 4. Register the asset type:
        ///    AssetManager.RegisterAssetType(FurnitureType.NewAsset, 
        ///        new Vector3(0, 0, 0), "Assets/Jail/NewAsset.prefab", 
        ///        "New_Asset", new Vector3(1, 1, 1), Color.white);
        /// 
        /// 5. Add the creation logic to AssetFactory.CreateAsset method
        /// 
        /// 6. Use it: var asset = AssetManager.SpawnAsset<NewAsset>(FurnitureType.NewAsset);
        /// </summary>

        /// <summary>
        /// Creates a material with the specified color using Universal Render Pipeline shader
        /// </summary>
        /// <param name="color">The base color for the material</param>
        /// <returns>A new material with the specified color</returns>
        public static Material CreateMaterial(Color color)
        {
            try
            {
                // Create material using Universal Render Pipeline shader
                Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                
                if (material != null)
                {
                    // Set base color
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    
                    // Set default values for other properties
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.0f);
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.5f);
                    
                    // Set source properties
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0.0f); // 0 = Opaque, 1 = Transparent
                    if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 2.0f); // 2 = Back face culling
                    if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0.0f);
                    
                    ModLogger.Debug($"Created material with color: {color}");
                    return material;
                }
                else
                {
                    ModLogger.Error("Failed to create material - shader not found");
                    return null;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating material: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a material with the specified texture and color using Universal Render Pipeline shader
        /// </summary>
        /// <param name="texture">The base map texture for the material</param>
        /// <param name="color">The tint color for the material</param>
        /// <returns>A new material with the specified texture and color</returns>
        public static Material CreateMaterial(Texture2D texture, Color color)
        {
            try
            {
                // Create material using Universal Render Pipeline shader
                Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                
                if (material != null)
                {
                    // Set base map texture
                    if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                    
                    // Set base color (acts as tint)
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    
                    // Set default values for other properties
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.7f);
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.5f);
                    
                    // Set source properties
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0.0f); // 0 = Opaque, 1 = Transparent
                    if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0.0f);
                    
                    ModLogger.Debug($"Created material with texture: {texture?.name ?? "None"} and color: {color}");
                    return material;
                }
                else
                {
                    ModLogger.Error("Failed to create material - shader not found");
                    return null;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating material: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a metal material with the specified color and metallic properties using Universal Render Pipeline shader
        /// </summary>
        /// <param name="color">The base color for the material</param>
        /// <param name="metallic">The metallic value (0.0 to 1.0)</param>
        /// <returns>A new metal material with the specified properties</returns>
        public static Material CreateMetalMaterial(Color color, float metallic)
        {
            try
            {
                // Create material using Universal Render Pipeline shader
                Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                
                if (material != null)
                {
                    // Set base color
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    
                    // Set metallic properties
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", Mathf.Clamp01(metallic));
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.8f); // Higher smoothness for metal
                    
                    // Set source properties
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0.0f); // 0 = Opaque, 1 = Transparent
                    if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0.0f);
                    
                    ModLogger.Debug($"Created metal material with color: {color} and metallic: {metallic}");
                    return material;
                }
                else
                {
                    ModLogger.Error("Failed to create metal material - shader not found");
                    return null;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating metal material: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a cloth material with the specified color using Universal Render Pipeline shader
        /// This method is identical to CreateMaterial(Color color) for now, but can be customized later
        /// </summary>
        /// <param name="color">The base color for the material</param>
        /// <returns>A new cloth material with the specified color</returns>
        public static Material CreateClothMaterial(Color color)
        {
            try
            {
                // Create material using Universal Render Pipeline shader
                Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                
                if (material != null)
                {
                    // Set base color
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    
                    // Set default values for other properties
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.0f);
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
                    
                    // Set source properties
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0.0f); // 0 = Opaque, 1 = Transparent
                    if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0.0f);
                    
                    ModLogger.Debug($"Created cloth material with color: {color}");
                    return material;
                }
                else
                {
                    ModLogger.Error("Failed to create cloth material - shader not found");
                    return null;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating cloth material: {e.Message}");
                return null;
            }
        }
    }

    public enum FurnitureType
    {
        BunkBed,
        ToiletSink,
        CommonRoomTable,
        CellTable,
        Jail
    }

    /// <summary>
    /// Base interface for all spawnable furniture assets
    /// </summary>
    public interface ISpawnableAsset
    {
        bool IsInitialized { get; }
        Vector3 Position { get; }
        string GetDebugInfo();
        void DestroyAsset();
    }

    /// <summary>
    /// Configuration for asset spawning
    /// </summary>
    public class AssetSpawnConfig
    {
        public Vector3 SpawnPoint { get; set; }
        public string PrefabPath { get; set; }
        public string LayerName { get; set; }
        public Vector3 ColliderSize { get; set; }
        public Color MaterialColor { get; set; }

        public AssetSpawnConfig(Vector3 spawnPoint, string prefabPath, string layerName, Vector3 colliderSize, Color materialColor)
        {
            SpawnPoint = spawnPoint;
            PrefabPath = prefabPath;
            LayerName = layerName;
            ColliderSize = colliderSize;
            MaterialColor = materialColor;
        }
    }

    /// <summary>
    /// Factory for creating different types of assets
    /// </summary>
    public static class AssetFactory
    {
        private static readonly Dictionary<FurnitureType, AssetSpawnConfig> _spawnConfigs = new()
        {
            {
                FurnitureType.BunkBed,
                new AssetSpawnConfig(
                    new Vector3(-55f, -2.5f, -58f),
                    "Assets/Jail/BunkBed.prefab",
                    "Bunk_Bed",
                    new Vector3(2f, 2f, 4f),
                    new Color(0.7f, 0.7f, 0.7f, 1.0f)
                )
            },
            {
                FurnitureType.ToiletSink,
                new AssetSpawnConfig(
                    new Vector3(-58f, -2.5f, -57f),
                    "Assets/Jail/ToiletSink.prefab",
                    "Toilet_Sink",
                    new Vector3(1f, 1f, 1f),
                    new Color(0.75f, 0.75f, 0.75f, 0.9f)
                )
            },
            {
                FurnitureType.CommonRoomTable,
                new AssetSpawnConfig(
                    new Vector3(-60f, -2.5f, -55f),
                    "Assets/Jail/CommonRoomTable.prefab",
                    "Common_Room_Table",
                    new Vector3(3f, 1f, 2f),
                    new Color(0.75f, 0.75f, 0.75f, 0.9f)
                )
            },
            {
                FurnitureType.CellTable,
                new AssetSpawnConfig(
                    new Vector3(-62f, -2.5f, -52f),
                    "Assets/Jail/CellTable.prefab",
                    "Cell_Table",
                    new Vector3(2f, 1f, 1.5f),
                    new Color(0.75f, 0.75f, 0.75f, 0.9f)
                )
            },
            {
                FurnitureType.Jail,
                new AssetSpawnConfig(
                    new Vector3(66.5362f, 8.5001f, -220.6056f),
                    "Jail",
                    "Jail",
                    new Vector3(10f, 10f, 10f),
                    new Color(0.6f, 0.6f, 0.6f, 1.0f)
                )
            }
        };

        public static AssetSpawnConfig GetSpawnConfig(FurnitureType furnitureType)
        {
            if (_spawnConfigs.TryGetValue(furnitureType, out var config))
            {
                return config;
            }
            
            ModLogger.Error($"No spawn configuration found for furniture type: {furnitureType}");
            return null;
        }

        public static void AddSpawnConfig(FurnitureType furnitureType, AssetSpawnConfig config)
        {
            if (_spawnConfigs.ContainsKey(furnitureType))
            {
                ModLogger.Warn($"Overwriting existing spawn configuration for {furnitureType}");
            }
            
            _spawnConfigs[furnitureType] = config;
            ModLogger.Debug($"Added spawn configuration for {furnitureType}");
        }

        public static ISpawnableAsset CreateAsset(FurnitureType furnitureType)
        {
            var config = GetSpawnConfig(furnitureType);
            if (config == null) return null;

            return furnitureType switch
            {
                FurnitureType.BunkBed => BunkBedManager.CreateBunkBed(config.SpawnPoint, config),
                FurnitureType.ToiletSink => ToiletSinkManager.CreateToiletSink(config.SpawnPoint, config),
                FurnitureType.CommonRoomTable => CommonRoomTableManager.CreateCommonRoomTable(config.SpawnPoint, config),
                FurnitureType.CellTable => CellTableManager.CreateCellTable(config.SpawnPoint, config),
                FurnitureType.Jail => JailManager.CreateJail(config.SpawnPoint, config),
                _ => null
            };
        }

        /// <summary>
        /// Registers a new asset type with the factory
        /// </summary>
        /// <param name="furnitureType">The new furniture type to register</param>
        /// <param name="spawnPoint">Default spawn point</param>
        /// <param name="prefabPath">Path to the prefab in the asset bundle</param>
        /// <param name="layerName">Layer name for the asset</param>
        /// <param name="colliderSize">Size of the collider</param>
        /// <param name="materialColor">Default material color</param>
        public static void RegisterAssetType(FurnitureType furnitureType, Vector3 spawnPoint, string prefabPath, 
            string layerName, Vector3 colliderSize, Color materialColor)
        {
            var config = new AssetSpawnConfig(spawnPoint, prefabPath, layerName, colliderSize, materialColor);
            AddSpawnConfig(furnitureType, config);
            ModLogger.Info($"Registered new asset type: {furnitureType}");
        }

        /// <summary>
        /// Gets all registered asset types
        /// </summary>
        /// <returns>List of registered furniture types</returns>
        public static List<FurnitureType> GetRegisteredAssetTypes()
        {
            return _spawnConfigs.Keys.ToList();
        }
    }

#if MONO
    public sealed class BunkBedManager : MonoBehaviour
#else
    public sealed class BunkBedManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private static BunkBedManager _instance;
        private static readonly List<BunkBed> _bunkBeds = new();
        private static bool _isInitialized = false;

        public static BunkBedManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("BunkBedManager");
                    _instance = go.AddComponent<BunkBedManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public static BunkBed CreateBunkBed(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            if (_instance == null)
            {
                Instance.Initialize();
            }

            // Check if we already have a bunk bed at this location
            foreach (var existingBed in _bunkBeds)
            {
                if (Vector3.Distance(existingBed.transform.position, spawnLocation) < 0.1f)
                {
                    ModLogger.Debug($"Bunk bed already exists at location {spawnLocation}");
                    return existingBed;
                }
            }

            // Create new bunk bed
            var bunkBed = Instance.SpawnBunkBed(spawnLocation, config);
            if (bunkBed != null)
            {
                _bunkBeds.Add(bunkBed);
                ModLogger.Debug($"Created new bunk bed at {spawnLocation}. Total beds: {_bunkBeds.Count}");
            }

            return bunkBed;
        }

        public static void RemoveBunkBed(BunkBed bunkBed)
        {
            if (_bunkBeds.Remove(bunkBed))
            {
                if (bunkBed != null && bunkBed.gameObject != null)
                {
                    Destroy(bunkBed.gameObject);
                }
                ModLogger.Debug($"Removed bunk bed. Total beds: {_bunkBeds.Count}");
            }
        }

        public static void ClearAllBunkBeds()
        {
            foreach (var bed in _bunkBeds.ToList())
            {
                RemoveBunkBed(bed);
            }
        }

        public static List<BunkBed> GetAllBunkBeds()
        {
            return new List<BunkBed>(_bunkBeds);
        }

        public static int GetBunkBedCount()
        {
            return _bunkBeds.Count;
        }

        private void Initialize()
        {
            if (_isInitialized) return;
            
            ModLogger.Debug("BunkBedManager initialized");
            _isInitialized = true;
        }

        private BunkBed SpawnBunkBed(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return null;
                }

                // Use config if provided, otherwise use defaults
                var prefabPath = config?.PrefabPath ?? "Assets/Jail/JailBunkBeds.prefab";
                var layerName = config?.LayerName ?? "Bunk_Bed";
                var colliderSize = config?.ColliderSize ?? new Vector3(2f, 2f, 4f);
                var materialColor = config?.MaterialColor ?? new Color(0.5f, 0.5f, 0.5f, 1.0f);

                // Load the prefab
                var prefab = AssetManager.bundle.LoadAsset<GameObject>(prefabPath);
                if (prefab == null)
                {
                    ModLogger.Error("Failed to load BunkBed prefab from bundle");
                    
                    // Debug: List all available prefabs
                    var allPrefabs = AssetManager.bundle.LoadAllAssets<GameObject>();
                    ModLogger.Debug($"Available prefabs in bundle:");
                    foreach (var p in allPrefabs)
                    {
                        ModLogger.Debug($"  {p.name}");
                    }
                    return null;
                }

                ModLogger.Debug($"Successfully loaded prefab: {prefab.name}");
                
                // Debug: Check prefab components
                var prefabMeshFilter = prefab.GetComponent<MeshFilter>();
                var prefabMeshRenderer = prefab.GetComponent<MeshRenderer>();
                ModLogger.Debug($"Prefab has MeshFilter: {prefabMeshFilter != null}, MeshRenderer: {prefabMeshRenderer != null}");
                if (prefabMeshFilter != null)
                {
                    ModLogger.Debug($"Prefab mesh: {prefabMeshFilter.sharedMesh?.name ?? "null"}");
                }

                // Create material using the new CreateMaterial method with the configured color
                var material = AssetManager.CreateClothMaterial(new Color(0.02f, 0.2f, 0f, 1f));
                var material1 = AssetManager.CreateMetalMaterial(materialColor, 0.9f);
                if (material == null)
                {
                    ModLogger.Error("Failed to create material for BunkBed");
                    return null;
                }

                ModLogger.Debug($"Successfully created material: {material.name}");

                // Instantiate the prefab instead of creating a new GameObject
                var bunkBedGO = Object.Instantiate(prefab);
                bunkBedGO.name = "BunkBed";
                
                ModLogger.Debug($"Instantiated prefab: {bunkBedGO.name}, Active: {bunkBedGO.activeInHierarchy}, ActiveSelf: {bunkBedGO.activeSelf}");
                
                // Ensure required components exist
                var meshRenderer = bunkBedGO.GetComponent<MeshRenderer>();
                var meshFilter = bunkBedGO.GetComponent<MeshFilter>();
                var boxCollider = bunkBedGO.GetComponent<BoxCollider>();
                
                ModLogger.Debug($"Instantiated object components - MeshFilter: {meshFilter != null}, MeshRenderer: {meshRenderer != null}, BoxCollider: {boxCollider != null}");
                
                // Add missing components if they don't exist
                if (meshRenderer == null)
                {
                    meshRenderer = bunkBedGO.AddComponent<MeshRenderer>();
                    ModLogger.Debug("Added MeshRenderer component");
                }
                
                if (meshFilter == null)
                {
                    meshFilter = bunkBedGO.AddComponent<MeshFilter>();
                    ModLogger.Debug("Added MeshFilter component");
                }
                
                if (boxCollider == null)
                {
                    boxCollider = bunkBedGO.AddComponent<BoxCollider>();
                    ModLogger.Debug("Added BoxCollider component");
                }

                // Set position
                bunkBedGO.transform.position = spawnLocation;

                // Find parent container
                GameObject map = GameObject.Find("Map");
                if (map != null)
                {
                    bunkBedGO.transform.SetParent(map.transform, false);
                }
                else
                {
                    ModLogger.Warn("Map container not found, spawning at world position");
                }

                // Apply material to all renderers in the prefab
                var allRenderers = bunkBedGO.GetComponentsInChildren<Renderer>(true);
                ModLogger.Debug($"Found {allRenderers.Length} renderers in prefab");
                
                foreach (var renderer in allRenderers)
                {
                    if (renderer != null)
                    {
#if MONO
                        List<Material> materials = new List<Material>();
#else
                        Il2CppSystem.Collections.Generic.List<Material> materials = new Il2CppSystem.Collections.Generic.List<Material>();
#endif
                        materials.Add(material);
                        materials.Add(material1);
                        renderer.SetMaterials(materials);
                        //renderer.sharedMaterial = material;
                        ModLogger.Debug($"Applied material to renderer: {renderer.name}");
                        
                        // Debug: Check if material was applied correctly
                        if (renderer.sharedMaterial != null)
                        {
                            ModLogger.Debug($"  Material applied: {renderer.sharedMaterial.name}, Shader: {renderer.sharedMaterial.shader?.name ?? "null"}");
                        }
                        else
                        {
                            ModLogger.Warn($"  Failed to apply material to renderer: {renderer.name}");
                        }
                    }
                }

                // Add the BunkBed component
                var bunkBed = bunkBedGO.AddComponent<BunkBed>();

                // Initialize the BunkBed component
                bunkBed.Initialize(spawnLocation, material, colliderSize);

                // Set layer
                SetLayerRecursively(bunkBedGO.transform, LayerMask.NameToLayer(layerName));

                // Final visibility check
                ModLogger.Debug($"Final object state:");
                ModLogger.Debug($"  Active: {bunkBedGO.activeInHierarchy}, ActiveSelf: {bunkBedGO.activeSelf}");
                ModLogger.Debug($"  Position: {bunkBedGO.transform.position}");
                ModLogger.Debug($"  Layer: {bunkBedGO.layer} ({LayerMask.LayerToName(bunkBedGO.layer)})");
                ModLogger.Debug($"  MeshFilter has mesh: {meshFilter.sharedMesh != null}, MeshRenderer has material: {meshRenderer.sharedMaterial != null}");
                ModLogger.Debug($"  MeshRenderer enabled: {meshRenderer.enabled}");
                ModLogger.Debug($"  MeshRenderer visible: {meshRenderer.isVisible}");
                
                // Force visibility and check for issues
                ForceObjectVisibility(bunkBedGO);
                
                return bunkBed;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning bunk bed: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        private void ForceObjectVisibility(GameObject obj)
        {
            try
            {
                // Ensure the object is active
                obj.SetActive(true);
                
                // Check all renderers and ensure they're enabled
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                        ModLogger.Debug($"Enabled renderer: {renderer.name}");
                        
                        // Check if the renderer has a material
                        if (renderer.sharedMaterial == null)
                        {
                            ModLogger.Warn($"Renderer {renderer.name} has no material!");
                        }
                        else
                        {
                            ModLogger.Debug($"Renderer {renderer.name} material: {renderer.sharedMaterial.name}");
                        }
                    }
                }
                
                // Check if the object is in the camera's culling mask
                var camera = Camera.main;
                if (camera != null)
                {
                    var layerMask = camera.cullingMask;
                    var objectLayer = 1 << obj.layer;
                    if ((layerMask & objectLayer) == 0)
                    {
                        ModLogger.Warn($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is not in camera culling mask!");
                    }
                    else
                    {
                        ModLogger.Debug($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is visible to camera");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error forcing object visibility: {e.Message}");
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null) return;

            root.gameObject.layer = layer;

            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = root.GetChild(i);
                SetLayerRecursively(child, layer);
            }
        }
    }

#if MONO
    public sealed class BunkBed : MonoBehaviour, ISpawnableAsset
#else
    public sealed class BunkBed(IntPtr ptr) : MonoBehaviour(ptr), ISpawnableAsset
#endif
    {
        private bool _initialized = false;
        private Vector3 _currentPosition;
        private Material _assignedMaterial;
        private MeshRenderer _meshRenderer;
        private BoxCollider _boxCollider;

        public bool IsInitialized => _initialized;
        public Vector3 Position => _currentPosition;

        public void Initialize(Vector3 spawnLocation, Material material, Vector3? colliderSize = null)
        {
            if (_initialized) return;

            _currentPosition = spawnLocation;
            _assignedMaterial = material;
            
            // Get component references
            _meshRenderer = GetComponent<MeshRenderer>();
            _boxCollider = GetComponent<BoxCollider>();

            if (_meshRenderer == null)
            {
                ModLogger.Error("MeshRenderer component not found on BunkBed");
                return;
            }

            if (_boxCollider == null)
            {
                ModLogger.Error("BoxCollider component not found on BunkBed");
                return;
            }

            // Apply material
            _meshRenderer.sharedMaterial = _assignedMaterial;

            // Set up collider properties
            _boxCollider.isTrigger = false;
            _boxCollider.size = colliderSize ?? new Vector3(2f, 2f, 4f); // Use provided size or default

            _initialized = true;
            ModLogger.Debug($"BunkBed initialized at {_currentPosition}");
        }

        public void SetPosition(Vector3 newPosition)
        {
            if (!_initialized) return;

            _currentPosition = newPosition;
            transform.position = _currentPosition;
            ModLogger.Debug($"BunkBed moved to {_currentPosition}");
        }

        public void SetMaterial(Material newMaterial)
        {
            if (!_initialized || _meshRenderer == null) return;

            _assignedMaterial = newMaterial;
            _meshRenderer.sharedMaterial = _assignedMaterial;
            ModLogger.Debug($"BunkBed material changed to {newMaterial.name}");
        }

        public void DestroyBunkBed()
        {
            if (!_initialized) return;

            BunkBedManager.RemoveBunkBed(this);
            _initialized = false;
        }

        public void DestroyAsset()
        {
            DestroyBunkBed();
        }

        public string GetDebugInfo()
        {
            if (!_initialized)
                return "BunkBed not initialized";
            
            return $"BunkBed at {_currentPosition}, Material: {_assignedMaterial?.name ?? "None"}, " +
                   $"MeshRenderer: {_meshRenderer != null}, BoxCollider: {_boxCollider != null}";
        }

        private void OnDestroy()
        {
            if (_initialized)
            {
                BunkBedManager.RemoveBunkBed(this);
            }
        }
    }

#if MONO
    public sealed class ToiletSinkManager : MonoBehaviour
#else
    public sealed class ToiletSinkManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private static ToiletSinkManager _instance;
        private static readonly List<ToiletSink> _toiletSinks = new();
        private static bool _isInitialized = false;

        public static ToiletSinkManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ToiletSinkManager");
                    _instance = go.AddComponent<ToiletSinkManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public static ToiletSink CreateToiletSink(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            if (_instance == null)
            {
                Instance.Initialize();
            }

            // Check if we already have a toilet sink at this location
            foreach (var existingSink in _toiletSinks)
            {
                if (Vector3.Distance(existingSink.transform.position, spawnLocation) < 0.1f)
                {
                    ModLogger.Debug($"Toilet sink already exists at location {spawnLocation}");
                    return existingSink;
                }
            }

            // Create new toilet sink
            var toiletSink = Instance.SpawnToiletSink(spawnLocation, config);
            if (toiletSink != null)
            {
                _toiletSinks.Add(toiletSink);
                ModLogger.Debug($"Created new toilet sink at {spawnLocation}. Total sinks: {_toiletSinks.Count}");
            }

            return toiletSink;
        }

        public static void RemoveToiletSink(ToiletSink toiletSink)
        {
            if (_toiletSinks.Remove(toiletSink))
            {
                if (toiletSink != null && toiletSink.gameObject != null)
                {
                    Destroy(toiletSink.gameObject);
                }
                ModLogger.Debug($"Removed toilet sink. Total sinks: {_toiletSinks.Count}");
            }
        }

        public static void ClearAllToiletSinks()
        {
            foreach (var sink in _toiletSinks.ToList())
            {
                RemoveToiletSink(sink);
            }
        }

        public static List<ToiletSink> GetAllToiletSinks()
        {
            return new List<ToiletSink>(_toiletSinks);
        }

        public static int GetToiletSinkCount()
        {
            return _toiletSinks.Count;
        }

        private void Initialize()
        {
            if (_isInitialized) return;
            
            ModLogger.Debug("ToiletSinkManager initialized");
            _isInitialized = true;
        }

        private ToiletSink SpawnToiletSink(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return null;
                }

                // Use config if provided, otherwise use defaults
                var prefabPath = config?.PrefabPath ?? "Assets/Jail/ToiletSink.prefab";
                var layerName = config?.LayerName ?? "Toilet_Sink";
                var colliderSize = config?.ColliderSize ?? new Vector3(1f, 1f, 1f);
                var materialColor = config?.MaterialColor ?? new Color(0.5f, 0.5f, 0.5f, 1.0f);

                // Load the prefab
                var prefab = AssetManager.bundle.LoadAsset<GameObject>(prefabPath);
                if (prefab == null)
                {
                    ModLogger.Error("Failed to load ToiletSink prefab from bundle");
                    
                    // Debug: List all available prefabs
                    var allPrefabs = AssetManager.bundle.LoadAllAssets<GameObject>();
                    ModLogger.Debug($"Available prefabs in bundle:");
                    foreach (var p in allPrefabs)
                    {
                        ModLogger.Debug($"  {p.name}");
                    }
                    return null;
                }

                ModLogger.Debug($"Successfully loaded prefab: {prefab.name}");
                
                // Debug: Check prefab components
                var prefabMeshFilter = prefab.GetComponent<MeshFilter>();
                var prefabMeshRenderer = prefab.GetComponent<MeshRenderer>();
                ModLogger.Debug($"Prefab has MeshFilter: {prefabMeshFilter != null}, MeshRenderer: {prefabMeshRenderer != null}");
                if (prefabMeshFilter != null)
                {
                    ModLogger.Debug($"Prefab mesh: {prefabMeshFilter.sharedMesh?.name ?? "null"}");
                }

                // Create material using the new CreateMaterial method with the configured color
                var material = AssetManager.CreateMetalMaterial(materialColor, 0.75f);
                var material1 = AssetManager.CreateMetalMaterial(new Color(0f, 0f, 0f, 1f), 1f);
                if (material == null || material1 == null)
                {
                    ModLogger.Error("Failed to create material for ToiletSink");
                    return null;
                }

                ModLogger.Debug($"Successfully created material: {material.name}");

                // Instantiate the prefab instead of creating a new GameObject
                var toiletSinkGO = Object.Instantiate(prefab);
                toiletSinkGO.name = "ToiletSink";
                
                ModLogger.Debug($"Instantiated prefab: {toiletSinkGO.name}, Active: {toiletSinkGO.activeInHierarchy}, ActiveSelf: {toiletSinkGO.activeSelf}");
                
                // Ensure required components exist
                var meshRenderer = toiletSinkGO.GetComponent<MeshRenderer>();
                var meshFilter = toiletSinkGO.GetComponent<MeshFilter>();
                var boxCollider = toiletSinkGO.GetComponent<BoxCollider>();
                
                ModLogger.Debug($"Instantiated object components - MeshFilter: {meshFilter != null}, MeshRenderer: {meshRenderer != null}, BoxCollider: {boxCollider != null}");
                
                // Add missing components if they don't exist
                if (meshRenderer == null)
                {
                    meshRenderer = toiletSinkGO.AddComponent<MeshRenderer>();
                    ModLogger.Debug("Added MeshRenderer component");
                }
                
                if (meshFilter == null)
                {
                    meshFilter = toiletSinkGO.AddComponent<MeshFilter>();
                    ModLogger.Debug("Added MeshFilter component");
                }
                
                if (boxCollider == null)
                {
                    //boxCollider = toiletSinkGO.AddComponent<BoxCollider>();
                    //ModLogger.Debug("Added BoxCollider component");
                }

                // Set position
                toiletSinkGO.transform.position = spawnLocation;

                // Find parent container
                GameObject map = GameObject.Find("Map");
                if (map != null)
                {
                    toiletSinkGO.transform.SetParent(map.transform, false);
                }
                else
                {
                    ModLogger.Warn("Map container not found, spawning at world position");
                }

                // Apply material to all renderers in the prefab
                var allRenderers = toiletSinkGO.GetComponentsInChildren<Renderer>(true);
                ModLogger.Debug($"Found {allRenderers.Length} renderers in prefab");
                var drainMaterial = AssetManager.CreateMetalMaterial(new Color(0f, 0f, 0f, 1f), 1f);
#if MONO
                List<Material> materials = new List<Material>();
#else
                Il2CppSystem.Collections.Generic.List<Material> materials = new Il2CppSystem.Collections.Generic.List<Material>();
#endif

                materials.Add(material);
                materials.Add(drainMaterial);
                foreach (var renderer in allRenderers)
                {
                    if (renderer != null)
                    {
                        //if (renderer.name == "ToiletSink")
                        //{
                        //    renderer.material = material;
                        //    ModLogger.Info($"Applied {material.color} material to ToiletSink renderer");
                        //}
                        //else
                        //{
                        //    renderer.material = material1;
                        //    ModLogger.Info($"Applied {material1.color} material to ToiletSink renderer");
                        //}
                        renderer.SetMaterials(materials);

                        ModLogger.Debug($"Applied material to renderer: {renderer.name}");
                        
                        // Debug: Check if material was applied correctly
                        if (renderer.material != null)
                        {
                            ModLogger.Debug($"  Material applied: {renderer.sharedMaterial.name}, Shader: {renderer.sharedMaterial.shader?.name ?? "null"}");
                        }
                        else
                        {
                            ModLogger.Warn($"  Failed to apply material to renderer: {renderer.name}");
                        }
                    }
                }

                // Add the ToiletSink component
                var toiletSink = toiletSinkGO.AddComponent<ToiletSink>();

                // Initialize the ToiletSink component
                toiletSink.Initialize(spawnLocation, material, colliderSize);

                // Set layer
                SetLayerRecursively(toiletSinkGO.transform, LayerMask.NameToLayer(layerName));

                // Final visibility check
                ModLogger.Debug($"Final object state:");
                ModLogger.Debug($"  Active: {toiletSinkGO.activeInHierarchy}, ActiveSelf: {toiletSinkGO.activeSelf}");
                ModLogger.Debug($"  Position: {toiletSinkGO.transform.position}");
                ModLogger.Debug($"  Layer: {toiletSinkGO.layer} ({LayerMask.LayerToName(toiletSinkGO.layer)})");
                ModLogger.Debug($"  MeshFilter has mesh: {meshFilter.sharedMesh != null}, MeshRenderer has material: {meshRenderer.sharedMaterial != null}");
                ModLogger.Debug($"  MeshRenderer enabled: {meshRenderer.enabled}");
                ModLogger.Debug($"  MeshRenderer visible: {meshRenderer.isVisible}");
                
                // Force visibility and check for issues
                ForceObjectVisibility(toiletSinkGO);
                
                return toiletSink;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning toilet sink: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        private void ForceObjectVisibility(GameObject obj)
        {
            try
            {
                // Ensure the object is active
                obj.SetActive(true);
                
                // Check all renderers and ensure they're enabled
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                        ModLogger.Debug($"Enabled renderer: {renderer.name}");
                        
                        // Check if the renderer has a material
                        if (renderer.sharedMaterial == null)
                        {
                            ModLogger.Warn($"Renderer {renderer.name} has no material!");
                        }
                        else
                        {
                            ModLogger.Debug($"Renderer {renderer.name} material: {renderer.sharedMaterial.name}");
                        }
                    }
                }
                
                // Check if the object is in the camera's culling mask
                var camera = Camera.main;
                if (camera != null)
                {
                    var layerMask = camera.cullingMask;
                    var objectLayer = 1 << obj.layer;
                    if ((layerMask & objectLayer) == 0)
                    {
                        ModLogger.Warn($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is not in camera culling mask!");
                    }
                    else
                    {
                        ModLogger.Debug($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is visible to camera");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error forcing object visibility: {e.Message}");
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null) return;

            root.gameObject.layer = layer;

            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = root.GetChild(i);
                SetLayerRecursively(child, layer);
            }
        }
    }

#if MONO
    public sealed class ToiletSink : MonoBehaviour, ISpawnableAsset
#else
    public sealed class ToiletSink(IntPtr ptr) : MonoBehaviour(ptr), ISpawnableAsset
#endif
    {
        private bool _initialized = false;
        private Vector3 _currentPosition;
        private Material _assignedMaterial;
        private MeshRenderer _meshRenderer;
        private BoxCollider _boxCollider;

        public bool IsInitialized => _initialized;
        public Vector3 Position => _currentPosition;

        public void Initialize(Vector3 spawnLocation, Material material, Vector3? colliderSize = null)
        {
            if (_initialized) return;

            _currentPosition = spawnLocation;
            _assignedMaterial = material;
            
            // Get component references
            _meshRenderer = GetComponent<MeshRenderer>();
            _boxCollider = GetComponent<BoxCollider>();

            if (_meshRenderer == null)
            {
                ModLogger.Error("MeshRenderer component not found on ToiletSink");
                return;
            }

            if (_boxCollider == null)
            {
                ModLogger.Error("BoxCollider component not found on ToiletSink");
                return;
            }

            // Apply material
            _meshRenderer.sharedMaterial = _assignedMaterial;

            // Set up collider properties
            //_boxCollider.isTrigger = false;
            //_boxCollider.size = colliderSize ?? new Vector3(1f, 1f, 1f); // Use provided size or default

            _initialized = true;
            ModLogger.Debug($"ToiletSink initialized at {_currentPosition}");
        }

        public void SetPosition(Vector3 newPosition)
        {
            if (!_initialized) return;

            _currentPosition = newPosition;
            transform.position = _currentPosition;
            ModLogger.Debug($"ToiletSink moved to {_currentPosition}");
        }

        public void SetMaterial(Material newMaterial)
        {
            if (!_initialized || _meshRenderer == null) return;

            _assignedMaterial = newMaterial;
            _meshRenderer.sharedMaterial = _assignedMaterial;
            ModLogger.Debug($"ToiletSink material changed to {newMaterial.name}");
        }

        public void DestroyToiletSink()
        {
            if (!_initialized) return;

            // Replace this line:
            ToiletSinkManager.RemoveToiletSink(this);
            _initialized = false;
        }

        public void DestroyAsset()
        {
            DestroyToiletSink();
        }

        public string GetDebugInfo()
        {
            if (!_initialized)
                return "ToiletSink not initialized";
            
            return $"ToiletSink at {_currentPosition}, Material: {_assignedMaterial?.name ?? "None"}, " +
                   $"MeshRenderer: {_meshRenderer != null}, BoxCollider: {_boxCollider != null}";
        }

        private void OnDestroy()
        {
            if (_initialized)
            {
                // With this line:
                ToiletSinkManager.RemoveToiletSink(this);
            }
        }
    }

#if MONO
    public sealed class CommonRoomTableManager : MonoBehaviour
#else
    public sealed class CommonRoomTableManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private static CommonRoomTableManager _instance;
        private static readonly List<CommonRoomTable> _commonRoomTables = new();
        private static bool _isInitialized = false;

        public static CommonRoomTableManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("CommonRoomTableManager");
                    _instance = go.AddComponent<CommonRoomTableManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public static CommonRoomTable CreateCommonRoomTable(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            if (_instance == null)
            {
                Instance.Initialize();
            }

            // Check if we already have a common room table at this location
            foreach (var existingTable in _commonRoomTables)
            {
                if (Vector3.Distance(existingTable.transform.position, spawnLocation) < 0.1f)
                {
                    ModLogger.Debug($"Common room table already exists at location {spawnLocation}");
                    return existingTable;
                }
            }

            // Create new common room table
            var commonRoomTable = Instance.SpawnCommonRoomTable(spawnLocation, config);
            if (commonRoomTable != null)
            {
                _commonRoomTables.Add(commonRoomTable);
                ModLogger.Debug($"Created new common room table at {spawnLocation}. Total tables: {_commonRoomTables.Count}");
            }

            return commonRoomTable;
        }

        public static void RemoveCommonRoomTable(CommonRoomTable commonRoomTable)
        {
            if (_commonRoomTables.Remove(commonRoomTable))
            {
                if (commonRoomTable != null && commonRoomTable.gameObject != null)
                {
                    Destroy(commonRoomTable.gameObject);
                }
                ModLogger.Debug($"Removed common room table. Total tables: {_commonRoomTables.Count}");
            }
        }

        public static void ClearAllCommonRoomTables()
        {
            foreach (var table in _commonRoomTables.ToList())
            {
                RemoveCommonRoomTable(table);
            }
        }

        public static List<CommonRoomTable> GetAllCommonRoomTables()
        {
            return new List<CommonRoomTable>(_commonRoomTables);
        }

        public static int GetCommonRoomTableCount()
        {
            return _commonRoomTables.Count;
        }

        private void Initialize()
        {
            if (_isInitialized) return;
            
            ModLogger.Debug("CommonRoomTableManager initialized");
            _isInitialized = true;
        }

        private CommonRoomTable SpawnCommonRoomTable(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return null;
                }

                // Use config if provided, otherwise use defaults
                var prefabPath = config?.PrefabPath ?? "Assets/Jail/CommonRoomTable.prefab";
                var layerName = config?.LayerName ?? "Common_Room_Table";
                var colliderSize = config?.ColliderSize ?? new Vector3(3f, 1f, 2f);
                var materialColor = config?.MaterialColor ?? new Color(0.5f, 0.5f, 0.5f, 1.0f);

                // Load the prefab
                var prefab = AssetManager.bundle.LoadAsset<GameObject>(prefabPath);
                if (prefab == null)
                {
                    ModLogger.Error("Failed to load CommonRoomTable prefab from bundle");
                    
                    // Debug: List all available prefabs
                    var allPrefabs = AssetManager.bundle.LoadAllAssets<GameObject>();
                    ModLogger.Debug($"Available prefabs in bundle:");
                    foreach (var p in allPrefabs)
                    {
                        ModLogger.Debug($"  {p.name}");
                    }
                    return null;
                }

                ModLogger.Debug($"Successfully loaded prefab: {prefab.name}");
                
                // Debug: Check prefab components
                var prefabMeshFilter = prefab.GetComponent<MeshFilter>();
                var prefabMeshRenderer = prefab.GetComponent<MeshRenderer>();
                ModLogger.Debug($"Prefab has MeshFilter: {prefabMeshFilter != null}, MeshRenderer: {prefabMeshRenderer != null}");
                if (prefabMeshFilter != null)
                {
                    ModLogger.Debug($"Prefab mesh: {prefabMeshFilter.sharedMesh?.name ?? "null"}");
                }

                // Create material using the new CreateMaterial method with the configured color
                var material = AssetManager.CreateMetalMaterial(materialColor, 0.75f);
                if (material == null)
                {
                    ModLogger.Error("Failed to create material for CommonRoomTable");
                    return null;
                }

                ModLogger.Debug($"Successfully created material: {material.name}");

                // Instantiate the prefab instead of creating a new GameObject
                var commonRoomTableGO = Object.Instantiate(prefab);
                commonRoomTableGO.name = "CommonRoomTable";
                
                ModLogger.Debug($"Instantiated prefab: {commonRoomTableGO.name}, Active: {commonRoomTableGO.activeInHierarchy}, ActiveSelf: {commonRoomTableGO.activeSelf}");
                
                // Ensure required components exist
                var meshRenderer = commonRoomTableGO.GetComponent<MeshRenderer>();
                var meshFilter = commonRoomTableGO.GetComponent<MeshFilter>();
                var boxCollider = commonRoomTableGO.GetComponent<BoxCollider>();
                
                ModLogger.Debug($"Instantiated object components - MeshFilter: {meshFilter != null}, MeshRenderer: {meshRenderer != null}, BoxCollider: {boxCollider != null}");
                
                // Add missing components if they don't exist
                if (meshRenderer == null)
                {
                    meshRenderer = commonRoomTableGO.AddComponent<MeshRenderer>();
                    ModLogger.Debug("Added MeshRenderer component");
                }
                
                if (meshFilter == null)
                {
                    meshFilter = commonRoomTableGO.AddComponent<MeshFilter>();
                    ModLogger.Debug("Added MeshFilter component");
                }
                
                if (boxCollider == null)
                {
                    boxCollider = commonRoomTableGO.AddComponent<BoxCollider>();
                    ModLogger.Debug("Added BoxCollider component");
                }

                // Set position
                commonRoomTableGO.transform.position = spawnLocation;

                // Find parent container
                GameObject map = GameObject.Find("Map");
                if (map != null)
                {
                    commonRoomTableGO.transform.SetParent(map.transform, false);
                }
                else
                {
                    ModLogger.Warn("Map container not found, spawning at world position");
                }

                // Apply material to all renderers in the prefab
                var allRenderers = commonRoomTableGO.GetComponentsInChildren<Renderer>(true);
                ModLogger.Debug($"Found {allRenderers.Length} renderers in prefab");
                
                foreach (var renderer in allRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.material = material;
                        ModLogger.Debug($"Applied material to renderer: {renderer.name}");
                        
                        // Debug: Check if material was applied correctly
                        if (renderer.material != null)
                        {
                            ModLogger.Debug($"  Material applied: {renderer.material.name}, Shader: {renderer.material.shader?.name ?? "null"}");
                        }
                        else
                        {
                            ModLogger.Warn($"  Failed to apply material to renderer: {renderer.name}");
                        }
                    }
                }

                // Add the CommonRoomTable component
                var commonRoomTable = commonRoomTableGO.AddComponent<CommonRoomTable>();

                // Initialize the CommonRoomTable component
                commonRoomTable.Initialize(spawnLocation, material, colliderSize);

                // Set layer
                SetLayerRecursively(commonRoomTableGO.transform, LayerMask.NameToLayer(layerName));

                // Final visibility check
                ModLogger.Debug($"Final object state:");
                ModLogger.Debug($"  Active: {commonRoomTableGO.activeInHierarchy}, ActiveSelf: {commonRoomTableGO.activeSelf}");
                ModLogger.Debug($"  Position: {commonRoomTableGO.transform.position}");
                ModLogger.Debug($"  Layer: {commonRoomTableGO.layer} ({LayerMask.LayerToName(commonRoomTableGO.layer)})");
                ModLogger.Debug($"  MeshFilter has mesh: {meshFilter.sharedMesh != null}, MeshRenderer has material: {meshRenderer.material != null}");
                ModLogger.Debug($"  MeshRenderer enabled: {meshRenderer.enabled}");
                ModLogger.Debug($"  MeshRenderer visible: {meshRenderer.isVisible}");
                
                // Force visibility and check for issues
                ForceObjectVisibility(commonRoomTableGO);
                
                return commonRoomTable;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning common room table: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        private void ForceObjectVisibility(GameObject obj)
        {
            try
            {
                // Ensure the object is active
                obj.SetActive(true);
                
                // Check all renderers and ensure they're enabled
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                        ModLogger.Debug($"Enabled renderer: {renderer.name}");
                        
                        // Check if the renderer has a material
                        if (renderer.material == null)
                        {
                            ModLogger.Warn($"Renderer {renderer.name} has no material!");
                        }
                        else
                        {
                            ModLogger.Debug($"Renderer {renderer.name} material: {renderer.material.name}");
                        }
                    }
                }
                
                // Check if the object is in the camera's culling mask
                var camera = Camera.main;
                if (camera != null)
                {
                    var layerMask = camera.cullingMask;
                    var objectLayer = 1 << obj.layer;
                    if ((layerMask & objectLayer) == 0)
                    {
                        ModLogger.Warn($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is not in camera culling mask!");
                    }
                    else
                    {
                        ModLogger.Debug($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is visible to camera");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error forcing object visibility: {e.Message}");
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null) return;

            root.gameObject.layer = layer;

            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = root.GetChild(i);
                SetLayerRecursively(child, layer);
            }
        }
    }

#if MONO
    public sealed class CommonRoomTable : MonoBehaviour, ISpawnableAsset
#else
    public sealed class CommonRoomTable(IntPtr ptr) : MonoBehaviour(ptr), ISpawnableAsset
#endif
    {
        private bool _initialized = false;
        private Vector3 _currentPosition;
        private Material _assignedMaterial;
        private MeshRenderer _meshRenderer;
        private BoxCollider _boxCollider;

        public bool IsInitialized => _initialized;
        public Vector3 Position => _currentPosition;

        public void Initialize(Vector3 spawnLocation, Material material, Vector3? colliderSize = null)
        {
            if (_initialized) return;

            _currentPosition = spawnLocation;
            _assignedMaterial = material;
            
            // Get component references
            _meshRenderer = GetComponent<MeshRenderer>();
            _boxCollider = GetComponent<BoxCollider>();

            if (_meshRenderer == null)
            {
                ModLogger.Error("MeshRenderer component not found on CommonRoomTable");
                return;
            }

            if (_boxCollider == null)
            {
                ModLogger.Error("BoxCollider component not found on CommonRoomTable");
                return;
            }

            // Apply material
            _meshRenderer.material = _assignedMaterial;

            // Set up collider properties
            _boxCollider.isTrigger = false;
            _boxCollider.size = colliderSize ?? new Vector3(3f, 1f, 2f); // Use provided size or default

            _initialized = true;
            ModLogger.Debug($"CommonRoomTable initialized at {_currentPosition}");
        }

        public void SetPosition(Vector3 newPosition)
        {
            if (!_initialized) return;

            _currentPosition = newPosition;
            transform.position = _currentPosition;
            ModLogger.Debug($"CommonRoomTable moved to {_currentPosition}");
        }

        public void SetMaterial(Material newMaterial)
        {
            if (!_initialized || _meshRenderer == null) return;

            _assignedMaterial = newMaterial;
            _meshRenderer.material = _assignedMaterial;
            ModLogger.Debug($"CommonRoomTable material changed to {newMaterial.name}");
        }

        public void DestroyCommonRoomTable()
        {
            if (!_initialized) return;

            CommonRoomTableManager.RemoveCommonRoomTable(this);
            _initialized = false;
        }

        public void DestroyAsset()
        {
            DestroyCommonRoomTable();
        }

        public string GetDebugInfo()
        {
            if (!_initialized)
                return "CommonRoomTable not initialized";
            
            return $"CommonRoomTable at {_currentPosition}, Material: {_assignedMaterial?.name ?? "None"}, " +
                   $"MeshRenderer: {_meshRenderer != null}, BoxCollider: {_boxCollider != null}";
        }

        private void OnDestroy()
        {
            if (_initialized)
            {
                CommonRoomTableManager.RemoveCommonRoomTable(this);
            }
        }
    }

#if MONO
    public sealed class CellTableManager : MonoBehaviour
#else
    public sealed class CellTableManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private static CellTableManager _instance;
        private static readonly List<CellTable> _cellTables = new();
        private static bool _isInitialized = false;

        public static CellTableManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("CellTableManager");
                    _instance = go.AddComponent<CellTableManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public static CellTable CreateCellTable(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            if (_instance == null)
            {
                Instance.Initialize();
            }

            // Check if we already have a cell table at this location
            foreach (var existingTable in _cellTables)
            {
                if (Vector3.Distance(existingTable.transform.position, spawnLocation) < 0.1f)
                {
                    ModLogger.Debug($"Cell table already exists at location {spawnLocation}");
                    return existingTable;
                }
            }

            // Create new cell table
            var cellTable = Instance.SpawnCellTable(spawnLocation, config);
            if (cellTable != null)
            {
                _cellTables.Add(cellTable);
                ModLogger.Debug($"Created new cell table at {spawnLocation}. Total tables: {_cellTables.Count}");
            }

            return cellTable;
        }

        public static void RemoveCellTable(CellTable cellTable)
        {
            if (_cellTables.Remove(cellTable))
            {
                if (cellTable != null && cellTable.gameObject != null)
                {
                    Destroy(cellTable.gameObject);
                }
                ModLogger.Debug($"Removed cell table. Total tables: {_cellTables.Count}");
            }
        }

        public static void ClearAllCellTables()
        {
            foreach (var table in _cellTables.ToList())
            {
                RemoveCellTable(table);
            }
        }

        public static List<CellTable> GetAllCellTables()
        {
            return new List<CellTable>(_cellTables);
        }

        public static int GetCellTableCount()
        {
            return _cellTables.Count;
        }

        private void Initialize()
        {
            if (_isInitialized) return;
            
            ModLogger.Debug("CellTableManager initialized");
            _isInitialized = true;
        }

        private CellTable SpawnCellTable(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return null;
                }

                // Use config if provided, otherwise use defaults
                var prefabPath = config?.PrefabPath ?? "Assets/Jail/CellTable.prefab";
                var layerName = config?.LayerName ?? "Cell_Table";
                var colliderSize = config?.ColliderSize ?? new Vector3(2f, 1f, 1.5f);
                var materialColor = config?.MaterialColor ?? new Color(0.5f, 0.5f, 0.5f, 1.0f);

                // Load the prefab
                var prefab = AssetManager.bundle.LoadAsset<GameObject>(prefabPath);
                if (prefab == null)
                {
                    ModLogger.Error("Failed to load CellTable prefab from bundle");
                    
                    // Debug: List all available prefabs
                    var allPrefabs = AssetManager.bundle.LoadAllAssets<GameObject>();
                    ModLogger.Debug($"Available prefabs in bundle:");
                    foreach (var p in allPrefabs)
                    {
                        ModLogger.Debug($"  {p.name}");
                    }
                    return null;
                }

                ModLogger.Debug($"Successfully loaded prefab: {prefab.name}");
                
                // Debug: Check prefab components
                var prefabMeshFilter = prefab.GetComponent<MeshFilter>();
                var prefabMeshRenderer = prefab.GetComponent<MeshRenderer>();
                ModLogger.Debug($"Prefab has MeshFilter: {prefabMeshFilter != null}, MeshRenderer: {prefabMeshRenderer != null}");
                if (prefabMeshFilter != null)
                {
                    ModLogger.Debug($"Prefab mesh: {prefabMeshFilter.sharedMesh?.name ?? "null"}");
                }

                // Create material using the new CreateMaterial method with the configured color
                var material = AssetManager.CreateMetalMaterial(materialColor, 0.75f);
                if (material == null)
                {
                    ModLogger.Error("Failed to create material for CellTable");
                    return null;
                }

                ModLogger.Debug($"Successfully created material: {material.name}");

                // Instantiate the prefab instead of creating a new GameObject
                var cellTableGO = Object.Instantiate(prefab);
                cellTableGO.name = "CellTable";
                
                ModLogger.Debug($"Instantiated prefab: {cellTableGO.name}, Active: {cellTableGO.activeInHierarchy}, ActiveSelf: {cellTableGO.activeSelf}");
                
                // Ensure required components exist
                var meshRenderer = cellTableGO.GetComponent<MeshRenderer>();
                var meshFilter = cellTableGO.GetComponent<MeshFilter>();
                var boxCollider = cellTableGO.GetComponent<BoxCollider>();
                
                ModLogger.Debug($"Instantiated object components - MeshFilter: {meshFilter != null}, MeshRenderer: {meshRenderer != null}, BoxCollider: {boxCollider != null}");
                
                // Add missing components if they don't exist
                if (meshRenderer == null)
                {
                    meshRenderer = cellTableGO.AddComponent<MeshRenderer>();
                    ModLogger.Debug("Added MeshRenderer component");
                }
                
                if (meshFilter == null)
                {
                    meshFilter = cellTableGO.AddComponent<MeshFilter>();
                    ModLogger.Debug("Added MeshFilter component");
                }
                
                if (boxCollider == null)
                {
                    boxCollider = cellTableGO.AddComponent<BoxCollider>();
                    ModLogger.Debug("Added BoxCollider component");
                }

                // Set position
                cellTableGO.transform.position = spawnLocation;

                // Find parent container
                GameObject map = GameObject.Find("Map");
                if (map != null)
                {
                    cellTableGO.transform.SetParent(map.transform, false);
                }
                else
                {
                    ModLogger.Warn("Map container not found, spawning at world position");
                }

                // Apply material to all renderers in the prefab
                var allRenderers = cellTableGO.GetComponentsInChildren<Renderer>(true);
                ModLogger.Debug($"Found {allRenderers.Length} renderers in prefab");
                
                foreach (var renderer in allRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.material = material;
                        ModLogger.Debug($"Applied material to renderer: {renderer.name}");
                        
                        // Debug: Check if material was applied correctly
                        if (renderer.material != null)
                        {
                            ModLogger.Debug($"  Material applied: {renderer.material.name}, Shader: {renderer.material.shader?.name ?? "null"}");
                        }
                        else
                        {
                            ModLogger.Warn($"  Failed to apply material to renderer: {renderer.name}");
                        }
                    }
                }

                // Add the CellTable component
                var cellTable = cellTableGO.AddComponent<CellTable>();

                // Initialize the CellTable component
                cellTable.Initialize(spawnLocation, material, colliderSize);

                // Set layer
                SetLayerRecursively(cellTableGO.transform, LayerMask.NameToLayer(layerName));

                // Final visibility check
                ModLogger.Debug($"Final object state:");
                ModLogger.Debug($"  Active: {cellTableGO.activeInHierarchy}, ActiveSelf: {cellTableGO.activeSelf}");
                ModLogger.Debug($"  Position: {cellTableGO.transform.position}");
                ModLogger.Debug($"  Layer: {cellTableGO.layer} ({LayerMask.LayerToName(cellTableGO.layer)})");
                ModLogger.Debug($"  MeshFilter has mesh: {meshFilter.sharedMesh != null}, MeshRenderer has material: {meshRenderer.material != null}");
                ModLogger.Debug($"  MeshRenderer enabled: {meshRenderer.enabled}");
                ModLogger.Debug($"  MeshRenderer visible: {meshRenderer.isVisible}");
                
                // Force visibility and check for issues
                ForceObjectVisibility(cellTableGO);
                
                return cellTable;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning cell table: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        private void ForceObjectVisibility(GameObject obj)
        {
            try
            {
                // Ensure the object is active
                obj.SetActive(true);
                
                // Check all renderers and ensure they're enabled
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                        ModLogger.Debug($"Enabled renderer: {renderer.name}");
                        
                        // Check if the renderer has a material
                        if (renderer.material == null)
                        {
                            ModLogger.Warn($"Renderer {renderer.name} has no material!");
                        }
                        else
                        {
                            ModLogger.Debug($"Renderer {renderer.name} material: {renderer.material.name}");
                        }
                    }
                }
                
                // Check if the object is in the camera's culling mask
                var camera = Camera.main;
                if (camera != null)
                {
                    var layerMask = camera.cullingMask;
                    var objectLayer = 1 << obj.layer;
                    if ((layerMask & objectLayer) == 0)
                    {
                        ModLogger.Warn($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is not in camera culling mask!");
                    }
                    else
                    {
                        ModLogger.Debug($"Object layer {obj.layer} ({LayerMask.LayerToName(obj.layer)}) is visible to camera");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error forcing object visibility: {e.Message}");
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null) return;

            root.gameObject.layer = layer;

            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = root.GetChild(i);
                SetLayerRecursively(child, layer);
            }
        }
    }

#if MONO
    public sealed class CellTable : MonoBehaviour, ISpawnableAsset
#else
    public sealed class CellTable(IntPtr ptr) : MonoBehaviour(ptr), ISpawnableAsset
#endif
    {
        private bool _initialized = false;
        private Vector3 _currentPosition;
        private Material _assignedMaterial;
        private MeshRenderer _meshRenderer;
        private BoxCollider _boxCollider;

        public bool IsInitialized => _initialized;
        public Vector3 Position => _currentPosition;

        public void Initialize(Vector3 spawnLocation, Material material, Vector3? colliderSize = null)
        {
            if (_initialized) return;

            _currentPosition = spawnLocation;
            _assignedMaterial = material;
            
            // Get component references
            _meshRenderer = GetComponent<MeshRenderer>();
            _boxCollider = GetComponent<BoxCollider>();

            if (_meshRenderer == null)
            {
                ModLogger.Error("MeshRenderer component not found on CellTable");
                return;
            }

            if (_boxCollider == null)
            {
                ModLogger.Error("BoxCollider component not found on CellTable");
                return;
            }

            // Apply material
            _meshRenderer.material = _assignedMaterial;

            // Set up collider properties
            _boxCollider.isTrigger = false;
            _boxCollider.size = colliderSize ?? new Vector3(2f, 1f, 1.5f); // Use provided size or default

            _initialized = true;
            ModLogger.Debug($"CellTable initialized at {_currentPosition}");
        }

        public void SetPosition(Vector3 newPosition)
        {
            if (!_initialized) return;

            _currentPosition = newPosition;
            transform.position = _currentPosition;
            ModLogger.Debug($"CellTable moved to {_currentPosition}");
        }

        public void SetMaterial(Material newMaterial)
        {
            if (!_initialized || _meshRenderer == null) return;

            _assignedMaterial = newMaterial;
            _meshRenderer.material = newMaterial;
            ModLogger.Debug($"CellTable material changed to {newMaterial.name}");
        }

        public void DestroyCellTable()
        {
            if (!_initialized) return;

            CellTableManager.RemoveCellTable(this);
            _initialized = false;
        }

        public void DestroyAsset()
        {
            DestroyCellTable();
        }

        public string GetDebugInfo()
        {
            if (!_initialized)
                return "CellTable not initialized";
            
            return $"CellTable at {_currentPosition}, Material: {_assignedMaterial?.name ?? "None"}, " +
                   $"MeshRenderer: {_meshRenderer != null}, BoxCollider: {_boxCollider != null}";
        }

        private void OnDestroy()
        {
            if (_initialized)
            {
                CellTableManager.RemoveCellTable(this);
            }
        }
    }

#if MONO
    public sealed class JailManager : MonoBehaviour
#else
    public sealed class JailManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private static JailManager _instance;
        private static readonly List<JailAsset> _jails = new();
        private static bool _isInitialized = false;

        public static JailManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("JailManager");
                    _instance = go.AddComponent<JailManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public static JailAsset CreateJail(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            if (_instance == null)
            {
                Instance.Initialize();
            }

            // Check if we already have a jail (only allow one jail)
            if (_jails.Count > 0)
            {
                ModLogger.Debug($"Jail already exists, returning existing jail");
                return _jails[0];
            }

            // Create new jail
            var jail = Instance.SpawnJail(spawnLocation, config);
            if (jail != null)
            {
                _jails.Add(jail);
                ModLogger.Debug($"Created new jail at {spawnLocation}. Total jails: {_jails.Count}");
            }

            return jail;
        }

        public static void RemoveJailAsset(JailAsset jail)
        {
            if (_jails.Remove(jail))
            {
                if (jail != null && jail.gameObject != null)
                {
                    Destroy(jail.gameObject);
                }
                ModLogger.Debug($"Removed jail. Total jails: {_jails.Count}");
            }
        }

        public static void ClearAllJails()
        {
            foreach (var jail in _jails.ToList())
            {
                RemoveJailAsset(jail);
            }
        }

        public static List<JailAsset> GetAllJails()
        {
            return new List<JailAsset>(_jails);
        }

        public static int GetJailCount()
        {
            return _jails.Count;
        }

        private void Initialize()
        {
            if (_isInitialized) return;
            
            ModLogger.Debug("JailManager initialized");
            _isInitialized = true;
        }

        private JailAsset SpawnJail(Vector3 spawnLocation, AssetSpawnConfig config = null)
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return null;
                }

                // Use config if provided, otherwise use defaults
                var prefabPath = config?.PrefabPath ?? "Assets/Jail/Jail.prefab";
                var layerName = config?.LayerName ?? "Jail";
                var colliderSize = config?.ColliderSize ?? new Vector3(10f, 10f, 10f);
                var materialColor = config?.MaterialColor ?? new Color(0.6f, 0.6f, 0.6f, 1.0f);

                // Load the prefab
                var prefab = AssetManager.bundle.LoadAsset<GameObject>(prefabPath);
                if (prefab == null)
                {
                    ModLogger.Error("Failed to load Jail prefab from bundle");
                    
                    // Debug: List all available prefabs
                    var allPrefabs = AssetManager.bundle.LoadAllAssets<GameObject>();
                    ModLogger.Debug($"Available prefabs in bundle:");
                    foreach (var p in allPrefabs)
                    {
                        ModLogger.Debug($"  {p.name}");
                    }
                    return null;
                }

                ModLogger.Debug($"Successfully loaded prefab: {prefab.name}");

                // Instantiate the prefab
                var jailGO = Object.Instantiate(prefab);
                jailGO.name = "[Prefab] JailHouseBlues";
                
                ModLogger.Debug($"Instantiated prefab: {jailGO.name}, Active: {jailGO.activeInHierarchy}, ActiveSelf: {jailGO.activeSelf}");

                // Set position
                jailGO.transform.position = spawnLocation;

                // Don't parent to Map for jail - it's a large structure that should exist independently
                ModLogger.Info($"Jail spawned at position: {jailGO.transform.position}");

                // Apply materials to all renderers if needed
                var allRenderers = jailGO.GetComponentsInChildren<Renderer>(true);
                ModLogger.Debug($"Found {allRenderers.Length} renderers in jail prefab");
                
                if (allRenderers.Length > 0)
                {
                    var material = AssetManager.CreateMaterial(materialColor);
                    if (material != null)
                    {
                        foreach (var renderer in allRenderers)
                        {
                            if (renderer != null && renderer.sharedMaterial == null)
                            {
                                renderer.sharedMaterial = material;
                                ModLogger.Debug($"Applied material to renderer: {renderer.name}");
                            }
                        }
                    }
                }

                // Add the Jail component
                var jail = jailGO.AddComponent<JailAsset>();

                // Initialize the Jail component
                jail.Initialize(spawnLocation, null, colliderSize);

                // Set layer recursively
                SetLayerRecursively(jailGO.transform, LayerMask.NameToLayer(layerName));

                // Final log
                ModLogger.Info($"Jail successfully spawned with {allRenderers.Length} components");
                
                return jail;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning jail: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null) return;

            root.gameObject.layer = layer;

            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = root.GetChild(i);
                SetLayerRecursively(child, layer);
            }
        }
    }

#if MONO
    public sealed class JailAsset : MonoBehaviour, ISpawnableAsset
#else
    public sealed class JailAsset(IntPtr ptr) : MonoBehaviour(ptr), ISpawnableAsset
#endif
    {
        private bool _initialized = false;
        private Vector3 _currentPosition;
        private Material _assignedMaterial;

        public bool IsInitialized => _initialized;
        public Vector3 Position => _currentPosition;

        public void Initialize(Vector3 spawnLocation, Material material, Vector3? colliderSize = null)
        {
            if (_initialized) return;

            _currentPosition = spawnLocation;
            _assignedMaterial = material;

            _initialized = true;
            ModLogger.Debug($"Jail initialized at {_currentPosition}");
        }

        public void SetPosition(Vector3 newPosition)
        {
            if (!_initialized) return;

            _currentPosition = newPosition;
            transform.position = _currentPosition;
            ModLogger.Debug($"Jail moved to {_currentPosition}");
        }

        public void DestroyJail()
        {
            if (!_initialized) return;

            JailManager.RemoveJailAsset(this);
            _initialized = false;
        }

        public void DestroyAsset()
        {
            DestroyJail();
        }

        public string GetDebugInfo()
        {
            if (!_initialized)
                return "Jail not initialized";
            
            return $"Jail at {_currentPosition}, Material: {_assignedMaterial?.name ?? "None"}";
        }

        private void OnDestroy()
        {
            if (_initialized)
            {
                JailManager.RemoveJailAsset(this);
            }
        }
    }
}
