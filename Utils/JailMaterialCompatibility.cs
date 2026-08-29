using UnityEngine;
using Behind_Bars.Helpers;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Repairs shader references serialized by the lightweight authoring
    /// project so the jail uses the shaders already loaded by Schedule I.
    ///
    /// The game owns its URP shader variants. Shipping editor-local URP shader
    /// data in the jail bundle both bloats the payload and can resolve to the
    /// internal error shader at runtime, which renders the jail magenta.
    /// </summary>
    internal static class JailMaterialCompatibility
    {
        private const string FallbackShaderName = "Universal Render Pipeline/Lit";

        public static void RepairForScheduleOne(GameObject jailRoot)
        {
            if (jailRoot == null)
            {
                ModLogger.Warn("[Jail Materials] Cannot repair a null jail root");
                return;
            }

            Shader fallbackShader = Shader.Find(FallbackShaderName);
            if (fallbackShader == null)
            {
                ModLogger.Error($"[Jail Materials] Schedule I shader '{FallbackShaderName}' was not found; leaving bundled material bindings unchanged");
                return;
            }

            int rendererCount = 0;
            int materialCount = 0;
            int reboundCount = 0;
            int fallbackCount = 0;

            try
            {
                Renderer[] renderers = jailRoot.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    Renderer renderer = renderers[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }

                    rendererCount++;
                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null)
                    {
                        continue;
                    }

                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    {
                        Material material = materials[materialIndex];
                        if (material == null)
                        {
                            continue;
                        }

                        materialCount++;
                        string bundledShaderName = material.shader != null
                            ? material.shader.name
                            : string.Empty;
                        Shader scheduleOneShader = ResolveScheduleOneShader(bundledShaderName, fallbackShader);
                        if (scheduleOneShader == null || material.shader == scheduleOneShader)
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(bundledShaderName) ||
                            bundledShaderName.IndexOf("error", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            fallbackCount++;
                        }

                        material.shader = scheduleOneShader;
                        reboundCount++;
                    }
                }

                ModLogger.Info(
                    $"[Jail Materials] Bound {reboundCount}/{materialCount} material shader reference(s) " +
                    $"across {rendererCount} jail renderer(s) to Schedule I URP ({fallbackCount} error/fallback binding(s))");
            }
            catch (System.Exception exception)
            {
                ModLogger.Error($"[Jail Materials] Failed to repair jail material bindings: {exception}");
            }
        }

        private static Shader ResolveScheduleOneShader(string bundledShaderName, Shader fallbackShader)
        {
            // Prefer an exact game-owned shader by the original material's
            // shader name. This retains intentional Unlit/Particle variants.
            if (!string.IsNullOrWhiteSpace(bundledShaderName) &&
                bundledShaderName.IndexOf("error", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                Shader matchingShader = Shader.Find(bundledShaderName);
                if (matchingShader != null)
                {
                    return matchingShader;
                }
            }

            return fallbackShader;
        }
    }
}
