using MelonLoader;
using System;
using Behind_Bars;

namespace Behind_Bars.Helpers
{
    /// <summary>
    /// Small logging facade used by the mod.
    /// </summary>
    /// <remarks>
    /// The methods forward directly to MelonLoader, so logger failures are not
    /// swallowed here. The single-message error overload intentionally uses
    /// the normal message channel; only the exception overload uses
    /// <see cref="MelonLogger.Error(string)"/>.
    /// </remarks>
    public static class ModLogger 
    {
        /// <summary>
        /// Forwards an informational message without adding a prefix or
        /// consulting the debug-logging setting.
        /// </summary>
        /// <param name="message">The message to forward.</param>
        public static void Info(string message)
        {
            MelonLogger.Msg(message);
        }

        /// <summary>
        /// Forwards a debug message only when <see cref="Core.EnableDebugLogging"/> is enabled.
        /// </summary>
        /// <param name="message">The debug message to forward with a <c>[DEBUG]</c> prefix.</param>
        public static void Debug(string message)
        {
            // Only log debug messages if debug logging is enabled in config
            if (Core.EnableDebugLogging)
            {
                MelonLogger.Msg($"[DEBUG] {message}");
            }
        }

        /// <summary>
        /// Forwards an error-labelled message through MelonLoader's normal
        /// message channel rather than its native error channel.
        /// </summary>
        /// <param name="message">The message to forward with a <c>[ERROR]</c> prefix.</param>
        public static void Error(string message)
        {
            MelonLogger.Msg($"[ERROR] {message}");
        }

        /// <summary>
        /// Forwards an exception message and its stack trace as two native
        /// MelonLoader error entries.
        /// </summary>
        /// <param name="message">Context to prepend to the exception message.</param>
        /// <param name="exception">The exception to report; it is not null-checked before use.</param>
        /// <remarks>
        /// A null <paramref name="exception"/> therefore causes a
        /// <see cref="NullReferenceException"/> while formatting the report.
        /// </remarks>
        public static void Error(string message, Exception exception)
        {
            MelonLogger.Error($"{message}: {exception.Message}");
            MelonLogger.Error($"Stack trace: {exception.StackTrace}");
        }

        /// <summary>
        /// Forwards a warning through MelonLoader's warning channel.
        /// </summary>
        /// <param name="message">The warning message to forward.</param>
        public static void Warn(string message)
        {
            MelonLogger.Warning(message);
        }
    }
}
