using MelonLoader;
using System;
using Behind_Bars;

namespace Behind_Bars.Helpers
{
    public static class ModLogger 
    {
        public static void Info(string message)
        {
            MelonLogger.Msg(message);
        }

        public static void Debug(string message)
        {
            // Only log debug messages if debug logging is enabled in config
            if (Core.EnableDebugLogging)
            {
                MelonLogger.Msg($"[DEBUG] {message}");
            }
        }

        public static void Error(string message)
        {
            MelonLogger.Msg($"[ERROR] {message}");
        }

        public static void Error(string message, Exception exception)
        {
            MelonLogger.Error($"{message}: {exception.Message}");
            MelonLogger.Error($"Stack trace: {exception.StackTrace}");
        }
        public static void Warn(string message)
        {
            MelonLogger.Warning(message);
        }
    }
}