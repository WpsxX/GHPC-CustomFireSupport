using System;
using System.Reflection;

namespace CustomFireSupport
{
    internal static class CachedDelegate
    {
        // Keep the reflection fallback when a different runtime cannot bind a private method.
        internal static T Create<T>(MethodInfo method) where T : class
        {
            if (method == null) return null;
            try { return Delegate.CreateDelegate(typeof(T), method) as T; }
            catch (ArgumentException) { return null; }
            catch (MemberAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (NotSupportedException) { return null; }
        }
    }
}
