using System.Diagnostics;
using MelonLoader;

namespace CustomFireSupport
{
    /// <summary>Logging facade. Routine messages are stripped from Release builds.</summary>
    internal static class Log
    {
        /// <summary>
        /// The symbol that turns the ROUTINE logging on (Info / Verbose). Warn and Error are always
        /// compiled in - see the class comment.
        /// </summary>
        internal const string Symbol = "CAS_LOG";

        private const string Prefix = "[CustomFireSupport] ";

        [Conditional(Symbol)]
        internal static void Info(string message)
        {
            MelonLogger.Msg(Prefix + message);
        }

        [Conditional(Symbol)]
        internal static void Verbose(string message)
        {
            if (CustomSupportRegistry.Global != null && CustomSupportRegistry.Global.VerboseLogging)
            {
                MelonLogger.Msg(Prefix + "[debug] " + message);
            }
        }

        /// <summary>Always compiled in: an abnormal condition must never be invisible.</summary>
        internal static void Warn(string message)
        {
            MelonLogger.Msg(Prefix + "[WARN] " + message);
        }

        /// <summary>Always compiled in: this is the line that names a failure's cause.</summary>
        internal static void Error(string message)
        {
            MelonLogger.Error(Prefix + message);
        }

    }
}

