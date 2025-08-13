using MelonLoader;
using System;

namespace Behind_Bars.Utils
{
    public static class Logger 
    {
        public static void Info(string message)
        {
            MelonLogger.Msg(message);
        }

        public static void Debug(string message)
        {
            MelonLogger.Msg($"[DEBUG] {message}");
        }

        public static void Error(string message)
        {
            MelonLogger.Msg($"[ERROR] {message}");
        }

        public static void Error(string messaage, Exception exception)
        {
            MelonLogger.Error($"{message}: {exception.Message}");
            MelonLogger.Error($"Stack trace: {exception.StackTrace}");
        }
    }
}