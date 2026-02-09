using System.Text;
using System.Reflection;
using Behind_Bars.Helpers;

namespace Behind_Bars.Utils;

public static class MelonLoaderVersionChecker
{
    private const string PROBLEMATIC_VERSION = "0.7.1.0";
    private const string RECOMMENDED_VERSION_1 = "0.7.0";
    private const string RECOMMENDED_VERSION_2 = "0.7.2-nightly";

    public static void CheckMelonLoaderVersion()
    {
        try
        {
            string melonVersion = GetMelonLoaderVersion();

            if (string.IsNullOrEmpty(melonVersion))
            {
                ModLogger.Warn("[VersionChecker] Could not determine MelonLoader version!");
                return;
            }

            ModLogger.Info("========================================");
            ModLogger.Info("[VersionChecker] MelonLoader Version Detected: " + melonVersion);
            ModLogger.Info("========================================");

            if (melonVersion == PROBLEMATIC_VERSION)
            {
                ShowBigWarning(melonVersion);
            }
            else if (IsVersionCloseToProblematic(melonVersion))
            {
                ModLogger.Warn("[VersionChecker] Warning: You are using a version very close to " +
                                    PROBLEMATIC_VERSION);
                ModLogger.Warn("[VersionChecker] It is recommended to use " + RECOMMENDED_VERSION_1 + " or " +
                                    RECOMMENDED_VERSION_2);
            }
            else
            {
                ModLogger.Info("[VersionChecker] Your MelonLoader version appears to be compatible!");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn("[VersionChecker] Version check failed: " + ex.Message);
        }
    }

    private static string GetMelonLoaderVersion()
    {
        try
        {
            Type melonType = Type.GetType("MelonLoader.MelonMod, MelonLoader");
            if (melonType != null)
            {
                Assembly melonLoaderAssembly = melonType.Assembly;
                Version assemblyVersion = melonLoaderAssembly.GetName().Version;

                if (assemblyVersion != null)
                {
                    return assemblyVersion.ToString();
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (name != null && (name.Equals("MelonLoader") || name.Equals("MelonLoader.Core")))
                {
                    Version v = assembly.GetName().Version;
                    if (v != null)
                    {
                        return v.ToString();
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVersionCloseToProblematic(string version)
    {
        try
        {
            if (version.StartsWith("0.7.1"))
            {

                if (version == "0.7.1" || version == "0.7.1.0")
                {
                    return false;
                }

                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowBigWarning(string detectedVersion)
    {
        StringBuilder warning = new StringBuilder();

        warning.AppendLine("");
        warning.AppendLine("╔════════════════════════════════════════════════════════════════════════╗");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║                    !!! URGENT WARNING !!!                              ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║           YOU ARE USING MELONLOADER VERSION " + detectedVersion.PadRight(8) +
                           "                  ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║  This version is KNOWN TO HAVE CRITICAL ISSUES and may cause:        ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║    - Game crashes and unexpected behavior                             ║");
        warning.AppendLine("║    - Mod incompatibility and loading failures                          ║");
        warning.AppendLine("║    - Performance issues and memory leaks                               ║");
        warning.AppendLine("║    - Random errors and instability                                     ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║  PLEASE UPDATE IMMEDIATELY to one of these recommended versions:       ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║    ► " + RECOMMENDED_VERSION_1.PadRight(20) +
                           " (Stable Release)                          ║");
        warning.AppendLine("║    ► " + RECOMMENDED_VERSION_2.PadRight(20) +
                           " (Latest Nightly)                          ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║  Download from: https://melonwiki.xyz/#/?id=automated-installation      ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("║      ║");
        warning.AppendLine("║                                                                        ║");
        warning.AppendLine("╚════════════════════════════════════════════════════════════════════════╝");
        warning.AppendLine("");

        for (int i = 0; i < 3; i++)
        {
            ModLogger.Error(warning.ToString());
        }

        ModLogger.Error("[VersionChecker] DETECTED PROBLEMATIC MELONLOADER VERSION: " + detectedVersion);
        ModLogger.Error("[VersionChecker] PLEASE UPDATE TO " + RECOMMENDED_VERSION_1 + " OR " +
                          RECOMMENDED_VERSION_2);
        ModLogger.Error("[VersionChecker] Download: https://github.com/LavaGang/MelonLoader/releases");
    }
}