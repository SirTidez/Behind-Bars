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
#if MONO
    public sealed class JailLightingController : MonoBehaviour
#else
    public sealed class JailLightingController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Lighting System")]
#endif
        public List<AreaLighting> areaLights = new List<AreaLighting>();
        public LightingState currentLightingState = LightingState.Normal;

#if MONO
        [Header("Lighting LOD")]
#endif
        public bool enableLightingLOD = true;
        public float lightCullingDistance = 50f;
        public int maxRealTimeLights = 20;
        public bool preferBakedLighting = true;

#if MONO
        [Header("Emissive Material Control")]
#endif
        public Material emissiveMaterial;
        public List<Material> allEmissiveMaterials = new List<Material>();
        public string emissiveMaterialName = "M_LightEmissive";
        public bool enableEmissiveControl = true;

#if MONO
        [Header("Emissive Colors")]
#endif
        public Color emissiveNormalColor = Color.white;
        public Color emissiveEmergencyColor = Color.red;
        public Color emissiveBlackoutColor = Color.black;

#if MONO
        [Header("Emissive Intensities")]
#endif
        public float emissiveNormalIntensity = 1.0f;
        public float emissiveEmergencyIntensity = 0.8f;
        public float emissiveBlackoutIntensity = 0.0f;

        private Transform playerTransform;

        [System.Serializable]
        public class AreaLighting
        {
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

        public enum LightingState
        {
            Normal,
            Emergency,
            Blackout
        }

        void Update()
        {
            if (enableLightingLOD)
            {
                UpdateLightingLOD();
            }
        }

        public void Initialize(Transform jailRoot)
        {
            DiscoverAreaLighting(jailRoot);
            FindEmissiveMaterial();

            // Try to find player transform
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
            {
                playerTransform = player.transform;
            }
        }

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

        void UpdateLightingLOD()
        {
            if (playerTransform == null) return;

            foreach (var areaLighting in areaLights)
            {
                if (areaLighting.lightsParent == null) continue;

                float distance = Vector3.Distance(playerTransform.position, areaLighting.lightsParent.position);
                bool playerNearby = distance <= lightCullingDistance;

                UpdateAreaLightingLOD(areaLighting, playerNearby);
            }
        }

        void UpdateAreaLightingLOD(AreaLighting areaLighting, bool playerNearby)
        {
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

        public void SetJailLighting(LightingState state)
        {
            currentLightingState = state;

            foreach (var areaLighting in areaLights)
            {
                areaLighting.SetLightingState(state);
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

        public void ToggleAreaLighting(string areaName)
        {
            AreaLighting area = areaLights.FirstOrDefault(a => a.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase));
            if (area != null)
            {
                area.ToggleLights();
                ModLogger.Info($"💡 Toggled {areaName} lights: {(area.isOn ? "ON" : "OFF")}");
            }
            else
            {
                ModLogger.Warn($"Area lighting not found: {areaName}");
            }
        }

        public void SetAreaLighting(string areaName, bool enabled)
        {
            AreaLighting area = areaLights.FirstOrDefault(a => a.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase));
            if (area != null)
            {
                area.SetLights(enabled, area.normalIntensity, area.normalColor);
                ModLogger.Info($"💡 Set {areaName} lights: {(enabled ? "ON" : "OFF")}");
            }
            else
            {
                ModLogger.Warn($"Area lighting not found: {areaName}");
            }
        }

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

        public void EmergencyLightingTest()
        {
            SetJailLighting(LightingState.Emergency);
        }

        public void NormalLightingTest()
        {
            SetJailLighting(LightingState.Normal);
        }

        public void BlackoutTest()
        {
            SetJailLighting(LightingState.Blackout);
        }
    }
}