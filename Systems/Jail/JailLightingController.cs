using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Discovers named jail light groups and applies normal, emergency, or blackout states.
    /// The controller treats every discovered light as real-time during discovery; baked-light
    /// preference and LOD therefore describe the controller's culling policy, not a baked-light
    /// conversion pass.
    /// </summary>
#if MONO
    public sealed class JailLightingController : MonoBehaviour
#else
    public sealed class JailLightingController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Lighting System")]
#endif
        // Area state is rebuilt from JailRoot/Lights during Initialize. The list is mutable and
        // may be empty when the authored Lights parent or named child is missing.
        public List<AreaLighting> areaLights = new List<AreaLighting>();
        public LightingState currentLightingState = LightingState.Normal;

#if MONO
        [Header("Lighting LOD")]
#endif
        // LOD controls a distance poll around the player; maxRealTimeLights limits enabled
        // lights only when the player is nearby. It does not affect baked lightmaps.
        public bool enableLightingLOD = true;
        public float lightCullingDistance = 50f;
        public int maxRealTimeLights = 20;
        public bool preferBakedLighting = true;

#if MONO
        [Header("Emissive Material Control")]
#endif
        // Emissive control searches renderer.materials by name and mutates material instances;
        // absence of a matching material leaves lights usable but skips emissive updates.
        public Material emissiveMaterial;
        public List<Material> allEmissiveMaterials = new List<Material>();
        public string emissiveMaterialName = "M_LightEmissive";
        public bool enableEmissiveControl = true;

#if MONO
        [Header("Emissive Colors")]
#endif
        // Target colors selected for each lighting state.
        public Color emissiveNormalColor = Color.white;
        public Color emissiveEmergencyColor = Color.red;
        public Color emissiveBlackoutColor = Color.black;

#if MONO
        [Header("Emissive Intensities")]
#endif
        // Target emission multipliers. Blackout disables the _EMISSION keyword when supported.
        public float emissiveNormalIntensity = 1.0f;
        public float emissiveEmergencyIntensity = 0.8f;
        public float emissiveBlackoutIntensity = 0.0f;

        private const float LightingLodPollInterval = 0.25f;
        private Transform playerTransform;
        private float nextLightingLodPollTime;
        private bool hasAppliedLightingState;

        /// <summary>
        /// Runtime state and discovered light references for one named lighting area.
        /// </summary>
        [System.Serializable]
        public class AreaLighting
        {
            // Authored identity and light references. Discovery currently places every found
            // Light in realTimeLights; bakedLights remains an optional caller-provided list.
            public string areaName;
            public Transform lightsParent;
            public List<Light> lights = new List<Light>();
            public bool isOn = true;
            public float normalIntensity = 1f;
            public float emergencyIntensity = 0.3f;
            public Color normalColor = Color.white;
            public Color emergencyColor = Color.red;
            public List<Light> realTimeLights = new List<Light>();
            public List<Light> bakedLights = new List<Light>();
            public bool isPlayerNearby = true;

            /// <summary>
            /// Apply the configured intensity/color for a named lighting state.
            /// </summary>
            /// <param name="state">State whose per-area settings should be applied.</param>
            public void SetLightingState(LightingState state)
            {
                switch (state)
                {
                    case LightingState.Normal:
                        SetLights(true, normalIntensity, normalColor);
                        break;
                    case LightingState.Emergency:
                        SetLights(true, emergencyIntensity, emergencyColor);
                        break;
                    case LightingState.Blackout:
                        SetLights(false, 0f, normalColor);
                        break;
                }
            }

            /// <summary>
            /// Apply an explicit enabled state, intensity, and color to managed lights.
            /// </summary>
            /// <param name="enabled">Whether managed lights should be enabled.</param>
            /// <param name="intensity">Intensity assigned to eligible real-time/uncategorized lights.</param>
            /// <param name="color">Color assigned to eligible real-time/uncategorized lights.</param>
            public void SetLights(bool enabled, float intensity, Color color)
            {
                isOn = enabled;

                foreach (var light in realTimeLights)
                {
                    if (light != null && (isPlayerNearby || enabled))
                    {
                        light.enabled = enabled;
                        light.intensity = intensity;
                        light.color = color;
                    }
                }

                foreach (var light in lights)
                {
                    if (light != null && !realTimeLights.Contains(light) && !bakedLights.Contains(light))
                    {
                        light.enabled = enabled;
                        light.intensity = intensity;
                        light.color = color;
                    }
                }
            }

            /// <summary>
            /// Toggle all lights in the area's aggregate <see cref="lights"/> list.
            /// </summary>
            /// <remarks>This bypasses per-list LOD limits and does not update emissive materials.</remarks>
            public void ToggleLights()
            {
                isOn = !isOn;
                foreach (var light in lights)
                {
                    if (light != null)
                    {
                        light.enabled = isOn;
                    }
                }
            }
        }

        /// <summary>Global lighting modes understood by the jail controller.</summary>
        public enum LightingState
        {
            /// <summary>Lights on using normal intensity/color.</summary>
            Normal,
            /// <summary>Lights on using emergency intensity/color.</summary>
            Emergency,
            /// <summary>Lights disabled and emissive emission reduced/disabled.</summary>
            Blackout
        }

        void Update()
        {
            if (enableLightingLOD && Time.time >= nextLightingLodPollTime)
            {
                nextLightingLodPollTime = Time.time + LightingLodPollInterval;
                UpdateLightingLOD();
            }
        }

        /// <summary>
        /// Rebuild named light groups and apply the initial LOD/emissive setup.
        /// </summary>
        /// <param name="jailRoot">Root containing the <c>Lights</c> hierarchy.</param>
        /// <remarks>The current discovery uses the exact child names Booking, MainRec, Phones, Kitchen, and Laundry.</remarks>
        public void Initialize(Transform jailRoot)
        {
            hasAppliedLightingState = false;
            DiscoverAreaLighting(jailRoot);
            FindEmissiveMaterial();

            // Try to find player transform
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
            {
                playerTransform = player.transform;
            }

            nextLightingLodPollTime = Time.time + LightingLodPollInterval;
            if (enableLightingLOD)
            {
                UpdateLightingLOD(true);
            }
        }

        // Discovery intentionally uses exact authored names and registers every child Light as
        // real-time for simplicity. A missing group is logged and omitted; this is not a scene
        // lightmap/baked-light discovery pass.
        void DiscoverAreaLighting(Transform jailRoot)
        {
            areaLights.Clear();
            Transform lightsParent = jailRoot.Find("Lights");

            if (lightsParent == null)
            {
                ModLogger.Error("Lights parent folder not found! Expected: JailRoot/Lights/");
                return;
            }

            ModLogger.Debug($"Found Lights parent, discovering areas using exact structure...");

            // Use the EXACT structure from the hierarchy provided
            string[] areaNames = { "Booking", "MainRec", "Phones", "Kitchen", "Laundry" };

            foreach (string areaName in areaNames)
            {
                Transform areaTransform = lightsParent.Find(areaName);
                if (areaTransform != null)
                {
                    AreaLighting areaLighting = new AreaLighting();
                    areaLighting.areaName = areaName;
                    areaLighting.lightsParent = areaTransform;

                    // Find all light components in this area
                    Light[] lightsInArea = areaTransform.GetComponentsInChildren<Light>();
                    areaLighting.lights.AddRange(lightsInArea);

                    if (lightsInArea.Length > 0)
                    {
                        // Store original light settings from first light
                        areaLighting.normalIntensity = lightsInArea[0].intensity;
                        areaLighting.normalColor = lightsInArea[0].color;

                        // All lights are real-time for simplicity
                        foreach (var light in lightsInArea)
                        {
                            areaLighting.realTimeLights.Add(light);
                        }

                        areaLights.Add(areaLighting);
                        ModLogger.Debug($"✓ Registered {areaName}: {lightsInArea.Length} lights");
                    }
                    else
                    {
                        ModLogger.Warn($"No lights found in {areaName}");
                    }
                }
                else
                {
                    ModLogger.Warn($"Area not found: Lights/{areaName}");
                }
            }

            int totalLights = 0;
            foreach (var area in areaLights)
            {
                totalLights += area.lights.Count;
            }
            ModLogger.Debug($"✓ Lighting discovery complete: {areaLights.Count} areas, {totalLights} total lights");
        }

        // LOD is distance-based from each area's root position and runs only when a player
        // transform was found. No player means no culling update, even when LOD is enabled.
        void UpdateLightingLOD(bool forceApply = false)
        {
            if (playerTransform == null) return;

            Vector3 playerPosition = playerTransform.position;
            float cullingDistanceSquared = lightCullingDistance * lightCullingDistance;

            foreach (var areaLighting in areaLights)
            {
                if (areaLighting.lightsParent == null) continue;

                Vector3 offset = playerPosition - areaLighting.lightsParent.position;
                bool playerNearby = offset.sqrMagnitude <= cullingDistanceSquared;

                UpdateAreaLightingLOD(areaLighting, playerNearby, forceApply);
            }
        }

        // The nearby branch enables at most maxRealTimeLights; the far branch disables
        // real-time lights only when preferBakedLighting is true. Baked lightmaps themselves
        // are never toggled or generated here.
        void UpdateAreaLightingLOD(AreaLighting areaLighting, bool playerNearby, bool forceApply = false)
        {
            if (!forceApply && areaLighting.isPlayerNearby == playerNearby)
            {
                return;
            }

            areaLighting.isPlayerNearby = playerNearby;

            if (!playerNearby && preferBakedLighting)
            {
                foreach (var light in areaLighting.realTimeLights)
                {
                    if (light != null)
                    {
                        light.enabled = false;
                    }
                }
            }
            else if (playerNearby)
            {
                int enabledRealTimeLights = 0;
                foreach (var light in areaLighting.realTimeLights)
                {
                    if (light != null && enabledRealTimeLights < maxRealTimeLights)
                    {
                        light.enabled = areaLighting.isOn;
                        enabledRealTimeLights++;
                    }
                    else if (light != null)
                    {
                        light.enabled = false;
                    }
                }
            }
        }

        /// <summary>
        /// Apply a lighting state to every discovered area and update the cached emissive material.
        /// </summary>
        /// <param name="state">Normal, emergency, or blackout mode to apply.</param>
        /// <remarks>Repeated calls still log the state; light state work is skipped when the mode is unchanged.</remarks>
        public void SetJailLighting(LightingState state)
        {
            bool stateChanged = !hasAppliedLightingState || currentLightingState != state;
            currentLightingState = state;

            if (stateChanged)
            {
                foreach (var areaLighting in areaLights)
                {
                    areaLighting.SetLightingState(state);
                }

                // State transitions are immediate, while retaining the current LOD culling decision.
                if (enableLightingLOD)
                {
                    UpdateLightingLOD(true);
                    nextLightingLodPollTime = Time.time + LightingLodPollInterval;
                }

                hasAppliedLightingState = true;
            }

            SetEmissiveMaterial(state);

            string stateName = state switch
            {
                LightingState.Normal => "NORMAL",
                LightingState.Emergency => "EMERGENCY",
                LightingState.Blackout => "BLACKOUT",
                _ => "UNKNOWN"
            };

            ModLogger.Info($"💡 Jail lighting set to {stateName}");
        }

        /// <summary>
        /// Toggle one discovered area's aggregate light list by name.
        /// </summary>
        /// <param name="areaName">Case-insensitive discovered area name.</param>
        /// <remarks>Unknown names are logged and ignored; this does not change emissive material state.</remarks>
        public void ToggleAreaLighting(string areaName)
        {
            AreaLighting area = areaLights.FirstOrDefault(a => a.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase));
            if (area != null)
            {
                area.ToggleLights();
                if (enableLightingLOD)
                {
                    UpdateAreaLightingLOD(area, area.isPlayerNearby, true);
                }

                ModLogger.Info($"💡 Toggled {areaName} lights: {(area.isOn ? "ON" : "OFF")}");
            }
            else
            {
                ModLogger.Warn($"Area lighting not found: {areaName}");
            }
        }

        /// <summary>
        /// Set one discovered area's aggregate light list to an explicit state.
        /// </summary>
        /// <param name="areaName">Case-insensitive discovered area name.</param>
        /// <param name="enabled">Whether the area's lights should be enabled.</param>
        /// <remarks>The area's normal intensity/color are used even when the global mode is emergency.</remarks>
        public void SetAreaLighting(string areaName, bool enabled)
        {
            AreaLighting area = areaLights.FirstOrDefault(a => a.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase));
            if (area != null)
            {
                area.SetLights(enabled, area.normalIntensity, area.normalColor);
                if (enableLightingLOD)
                {
                    UpdateAreaLightingLOD(area, area.isPlayerNearby, true);
                }

                ModLogger.Info($"💡 Set {areaName} lights: {(enabled ? "ON" : "OFF")}");
            }
            else
            {
                ModLogger.Warn($"Area lighting not found: {areaName}");
            }
        }

        // The search examines renderer.materials instances whose names contain the configured
        // token. It caches the first match as emissiveMaterial and all matches in the list;
        // no match is a warning, not a fatal lighting failure.
        void FindEmissiveMaterial()
        {
            if (!enableEmissiveControl)
            {
                ModLogger.Debug("Emissive control disabled, skipping material search");
                return;
            }

            if (emissiveMaterial != null)
            {
                ModLogger.Debug($"Emissive material already cached: {emissiveMaterial.name}");
                return;
            }

            ModLogger.Debug($"Searching for emissive material containing name: '{emissiveMaterialName}'");

            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            ModLogger.Debug($"Found {renderers.Length} renderers in jail hierarchy");

            int totalMaterials = 0;
            List<string> allMaterialNames = new List<string>();

            foreach (var renderer in renderers)
            {
                if (renderer.materials != null)
                {
                    totalMaterials += renderer.materials.Length;
                    foreach (var material in renderer.materials)
                    {
                        if (material != null)
                        {
                            allMaterialNames.Add(material.name);

                            if (material.name.Contains(emissiveMaterialName))
                            {
                                if (!allEmissiveMaterials.Contains(material))
                                {
                                    allEmissiveMaterials.Add(material);
                                    ModLogger.Debug($"✓ Found emissive material: '{material.name}' on renderer: {renderer.name}");

                                    TestEmissiveMaterialProperties(material);

                                    if (emissiveMaterial == null)
                                    {
                                        emissiveMaterial = material;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (allEmissiveMaterials.Count > 0)
            {
                ModLogger.Debug($"✓ Found {allEmissiveMaterials.Count} emissive material instances total");
            }

            if (allEmissiveMaterials.Count == 0)
            {
                ModLogger.Warn($"⚠️ Emissive material containing '{emissiveMaterialName}' not found in jail hierarchy");
            }

            ModLogger.Debug($"Searched {totalMaterials} materials across {renderers.Length} renderers");

            if (allMaterialNames.Count > 0)
            {
                ModLogger.Debug("First 10 materials found:");
                for (int i = 0; i < System.Math.Min(10, allMaterialNames.Count); i++)
                {
                    ModLogger.Debug($"  [{i}]: {allMaterialNames[i]}");
                }
            }
        }

        void TestEmissiveMaterialProperties(Material material)
        {
            ModLogger.Debug($"Testing emission properties on material: {material.name}");

            bool hasEmissionColor = material.HasProperty("_EmissionColor");
            bool hasEmission = material.HasProperty("_Emission");
            bool hasEmissiveKeyword = material.IsKeywordEnabled("_EMISSION");

            ModLogger.Debug($"Material properties: _EmissionColor={hasEmissionColor}, _Emission={hasEmission}, _EMISSION keyword={hasEmissiveKeyword}");

            if (hasEmissionColor)
            {
                Color currentEmission = material.GetColor("_EmissionColor");
                ModLogger.Debug($"Current _EmissionColor: {currentEmission}");
            }

            if (hasEmission)
            {
                Color currentEmission = material.GetColor("_Emission");
                ModLogger.Info($"Current _Emission: {currentEmission}");
            }
        }

        // Emissive updates are best-effort: only the first supported shader property on each
        // cached material is written, and unsupported materials are counted as failures.
        void SetEmissiveMaterial(LightingState state)
        {
            if (!enableEmissiveControl)
            {
                ModLogger.Debug($"Emissive control disabled, skipping material update for {state}");
                return;
            }

            if (emissiveMaterial == null)
            {
                ModLogger.Warn($"No emissive material cached, cannot update for {state}");
                return;
            }

            ModLogger.Info($"Updating emissive material '{emissiveMaterial.name}' for lighting state: {state}");

            Color targetColor;
            float targetIntensity;

            switch (state)
            {
                case LightingState.Normal:
                    targetColor = emissiveNormalColor;
                    targetIntensity = emissiveNormalIntensity;
                    break;
                case LightingState.Emergency:
                    targetColor = emissiveEmergencyColor;
                    targetIntensity = emissiveEmergencyIntensity;
                    break;
                case LightingState.Blackout:
                    targetColor = emissiveBlackoutColor;
                    targetIntensity = emissiveBlackoutIntensity;
                    break;
                default:
                    targetColor = emissiveNormalColor;
                    targetIntensity = emissiveNormalIntensity;
                    break;
            }

            Color finalEmissionColor = targetColor * targetIntensity;

            int updatedCount = 0;
            int failedCount = 0;

            foreach (var material in allEmissiveMaterials)
            {
                if (material == null) continue;

                bool materialUpdated = false;

                if (material.HasProperty("_EmissionColor"))
                {
                    material.SetColor("_EmissionColor", finalEmissionColor);
                    materialUpdated = true;
                    ModLogger.Debug($"Set _EmissionColor on '{material.name}' to: {finalEmissionColor}");
                }
                else if (material.HasProperty("_Emission"))
                {
                    material.SetColor("_Emission", finalEmissionColor);
                    materialUpdated = true;
                    ModLogger.Debug($"Set _Emission on '{material.name}' to: {finalEmissionColor}");
                }
                else if (material.HasProperty("_EmissiveColor"))
                {
                    material.SetColor("_EmissiveColor", finalEmissionColor);
                    materialUpdated = true;
                    ModLogger.Debug($"Set _EmissiveColor on '{material.name}' to: {finalEmissionColor}");
                }

                if (materialUpdated)
                {
                    if (targetIntensity > 0)
                    {
                        material.EnableKeyword("_EMISSION");
                    }
                    else
                    {
                        material.DisableKeyword("_EMISSION");
                    }
                    updatedCount++;
                }
                else
                {
                    ModLogger.Warn($"Material '{material.name}' has no supported emission property!");
                    failedCount++;
                }
            }

            if (updatedCount > 0)
            {
                ModLogger.Info($"Successfully updated {updatedCount} emissive material instances to {state}: {finalEmissionColor} (intensity: {targetIntensity})");
            }

            if (failedCount > 0)
            {
                ModLogger.Error($"Failed to update {failedCount} emissive material instances - no compatible emission properties found");
            }
        }

        /// <summary>Diagnostic wrapper that applies <see cref="LightingState.Emergency"/>.</summary>
        public void EmergencyLightingTest()
        {
            SetJailLighting(LightingState.Emergency);
        }

        /// <summary>Diagnostic wrapper that applies <see cref="LightingState.Normal"/>.</summary>
        public void NormalLightingTest()
        {
            SetJailLighting(LightingState.Normal);
        }

        /// <summary>Diagnostic wrapper that applies <see cref="LightingState.Blackout"/>.</summary>
        public void BlackoutTest()
        {
            SetJailLighting(LightingState.Blackout);
        }
    }
}
