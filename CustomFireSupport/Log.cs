using System.Diagnostics;
using MelonLoader;

namespace CustomFireSupport
{
    /// <summary>Logging facade for abnormal conditions.</summary>
    internal static class Log
    {
        private const string Prefix = "[CustomFireSupport] ";

        [Conditional("CFS_DIAGNOSTICS")]
        internal static void Warn(string message)
        {
            MelonLogger.Msg(Prefix + "[WARN] " + message);
        }

        [Conditional("CFS_DIAGNOSTICS")]
        internal static void Error(string message)
        {
            MelonLogger.Error(Prefix + message);
        }

    }
}

