using MelonLoader;

namespace CustomFireSupport
{
    /// <summary>
    /// Small logging facade. Everything the mod prints is prefixed with "[CustomFireSupport]" so it
    /// can be filtered out of the MelonLoader console / Latest.log. Warnings are printed through
    /// MelonLogger.Msg with an explicit "[WARN]" tag (instead of MelonLogger.Warning) so the mod
    /// behaves the same on every MelonLoader build.
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[CustomFireSupport] ";

        internal static void Info(string message)
        {
            MelonLogger.Msg(Prefix + message);
        }

        internal static void Warn(string message)
        {
            MelonLogger.Msg(Prefix + "[WARN] " + message);
        }

        internal static void Error(string message)
        {
            MelonLogger.Error(Prefix + message);
        }

        /// <summary>Only printed when the global "VerboseLogging" preference is on.</summary>
        internal static void Verbose(string message)
        {
            if (CustomFireSupportMod.VerboseLogging)
            {
                MelonLogger.Msg(Prefix + "[debug] " + message);
            }
        }
    }
}
