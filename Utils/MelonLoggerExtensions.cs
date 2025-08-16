using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using MelonLoader;

namespace Behind_Bars.Helpers
{
    public static class MelonLoggerExtensions
    {
        private static string GetLoggerName(MelonLogger.Instance logger)
        {
            var field = typeof(MelonLogger.Instance).GetField(
                "Name",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            return field?.GetValue(logger) as string;
        }

        private static void InvokeNativeMsg(
            Color namesectionColor,
            Color textColor,
            string nameSection,
            string message
        )
        {
            var method = typeof(MelonLogger).GetMethod(
                "NativeMsg",
                BindingFlags.NonPublic | BindingFlags.Static
            );

            method?.Invoke(
                null,
                new object[]
                {
                    namesectionColor,
                    textColor,
                    nameSection,
                    message ?? "null",
                    false, // skipStackWalk
                }
            );
        }

        private static string GetCallerInfo()
        {
            var stackTrace = new StackTrace();
            for (int i = 2; i < stackTrace.FrameCount; i++)
            {
                var frame = stackTrace.GetFrame(i);
                var method = frame.GetMethod();
                if (method?.DeclaringType == null)
                    continue;

                return $"{method.DeclaringType.FullName}.{method.Name}";
            }

            return "unknown";
        }
    }
}
