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

        public ToiletSink SpawnAsset(FurnitureType t)
        {
            switch (t)
            {
                case FurnitureType.BunkBed:
                    // Spawn bunk bed logic here
                    ModLogger.Debug("Bunk bed spawning not yet implemented");
                    return null;
                case FurnitureType.ToiletSink:
                    // Use the new management system
                    Vector3 spawnPoint = new Vector3(-58f, -1.0f, -57f);
                    return ToiletSinkManager.CreateToiletSink(spawnPoint);
                default:
                    ModLogger.Error("Unknown furniture type.");
                    return null;
            }
        }

        public void TestToiletSinkSystem()
        {
            ModLogger.Debug("Testing ToiletSink management system...");
            
            // Spawn a toilet sink
            var sink = SpawnAsset(FurnitureType.ToiletSink);
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
                    if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 2.0f); // 2 = Back face culling
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
    }

    public enum FurnitureType
    {
        BunkBed,
        ToiletSink
    }

#if MONO
    public sealed class BunkBedManager() : MonoBehaviour
#else
    public sealed class BunkBedManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private static readonly List<BunkBed> _bunkBeds = [];
    }

#if MONO
    public sealed class BunkBed() : MonoBehaviour
#else
    public sealed class BunkBed(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        public GameObject BunkBedObject;
        public Transform SpawnTarget;

        private bool _initialized;

        public void Initialize()
        {
            CreateBed();

            _initialized = true;
        }

        private void CreateBed()
        {
            try
            {
                BunkBedObject = new GameObject("Bunk_Bed");

            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating bed: {e.Message}");
            }
        }

        private void LoadBundle()
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return;
                }

                var prefab = AssetManager.bundle.LoadAsset<GameObject>("Assets/Meshes/Jail/JailBunkBeds.prefab");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error loading bundle: {e.Message}");
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

        public static ToiletSink CreateToiletSink(Vector3 spawnLocation)
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
            var toiletSink = Instance.SpawnToiletSink(spawnLocation);
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

        private ToiletSink SpawnToiletSink(Vector3 spawnLocation)
        {
            try
            {
                if (AssetManager.bundle == null)
                {
                    ModLogger.Error("Asset bundle is not loaded.");
                    return null;
                }

                // Load the prefab
                var prefab = AssetManager.bundle.LoadAsset<GameObject>("Assets/Jail/ToiletSink.prefab");
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

                // Create material using the new CreateMaterial method with a slightly gray color
                var material = AssetManager.CreateMaterial(new Color(0.7f, 0.7f, 0.7f, 1.0f));
                if (material == null)
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
                    boxCollider = toiletSinkGO.AddComponent<BoxCollider>();
                    ModLogger.Debug("Added BoxCollider component");
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
                
                foreach (var renderer in allRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.sharedMaterial = material;
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

                // Add the ToiletSink component
                var toiletSink = toiletSinkGO.AddComponent<ToiletSink>();

                // Initialize the ToiletSink component
                toiletSink.Initialize(spawnLocation, material);

                // Set layer
                SetLayerRecursively(toiletSinkGO.transform, LayerMask.NameToLayer("Toilet_Sink"));

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
    public sealed class ToiletSink : MonoBehaviour
#else
    public sealed class ToiletSink(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        private bool _initialized = false;
        private Vector3 _currentPosition;
        private Material _assignedMaterial;
        private MeshRenderer _meshRenderer;
        private BoxCollider _boxCollider;

        public bool IsInitialized => _initialized;
        public Vector3 Position => _currentPosition;

        public void Initialize(Vector3 spawnLocation, Material material)
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
            _boxCollider.isTrigger = false;
            _boxCollider.size = new Vector3(1f, 1f, 1f); // Adjust size as needed

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

            ToiletSinkManager.RemoveToiletSink(this);
            _initialized = false;
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
                ToiletSinkManager.RemoveToiletSink(this);
            }
        }
    }
}
