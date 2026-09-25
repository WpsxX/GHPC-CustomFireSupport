using System.Diagnostics;
using MelonLoader;

namespace CustomFireSupport
{
    /// <summary>
    /// Small logging facade. Everything the mod prints is prefixed with "[CustomFireSupport]" so it
    /// can be filtered out of the MelonLoader console / Latest.log. Warnings are printed through
    /// MelonLogger.Msg with an explicit "[WARN]" tag (instead of MelonLogger.Warning) so the mod
    /// behaves the same on every MelonLoader build.
    ///
    /// ============================ BUILD-TIME STRIPPING ============================
    ///
    /// Every method carries [Conditional("CAS_LOG")]. When that symbol is NOT defined the C#
    /// compiler REMOVES each call statement entirely - including the string literals that feed it -
    /// so a release build ships with no logging code at all (measured: ~8 KB of the assembly, the
    /// 179 call sites and their 3143 characters of message text).
    ///
    /// Why this instead of deleting the calls by hand:
    ///
    ///   * the source is untouched, so the messages keep documenting WHY each check exists, and the
    ///     call sites stay readable rather than turning into mystery empty blocks;
    ///   * turning diagnostics back on is one symbol, not a reverse-engineering exercise. When a
    ///     player reports "the aircraft did not appear", the build that can answer that question is
    ///     one csproj edit away instead of being gone;
    ///   * nothing is deleted by hand, so there is no chance of removing a Log.Warn that sat in
    ///     front of a `return` and silently changing control flow.
    ///
    /// To build WITH logging (for a bug report), define CAS_LOG:
    ///
    ///     dotnet msbuild CustomFireSupport.csproj /p:Configuration=Release /p:DefineConstants=CAS_LOG
    ///
    /// or add &lt;DefineConstants&gt;$(DefineConstants);CAS_LOG&lt;/DefineConstants&gt; to the csproj.
    ///
    /// IMPORTANT: [Conditional] removes the call and its arguments, but it does NOT remove a method
    /// that is still referenced elsewhere. The methods below therefore stay in the type table; what
    /// the compiler drops is every call site with its message strings, which is where the bytes are
    /// (3143 chars of text + 179 ldstr/call pairs, versus a few bytes of method metadata).
    /// </summary>
    internal static class Log
    {
        /// <summary>
        /// The symbol that turns logging on. Kept as a const so the intent is greppable from here.
        /// </summary>
        internal const string Symbol = "CAS_LOG";

        private const string Prefix = "[CustomFireSupport] ";

        [Conditional(Symbol)]
        internal static void Info(string message)
        {
            MelonLogger.Msg(Prefix + message);
        }

        [Conditional(Symbol)]
        internal static void Warn(string message)
        {
            MelonLogger.Msg(Prefix + "[WARN] " + message);
        }

        [Conditional(Symbol)]
        internal static void Error(string message)
        {
            MelonLogger.Error(Prefix + message);
        }

        /// <summary>Only printed when the global "VerboseLogging" preference is on.</summary>
        [Conditional(Symbol)]
        internal static void Verbose(string message)
        {
            if (CustomFireSupportMod.VerboseLogging)
            {
                MelonLogger.Msg(Prefix + "[debug] " + message);
            }
        }
    }
}
