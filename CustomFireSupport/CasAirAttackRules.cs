namespace CustomFireSupport
{
    /// <summary>The air envelope uses slant range and a three-dimensional nose cone at every release.</summary>
    internal static class CasAirAttackRules
    {
        internal const float NoseConeDegrees = 5f;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static bool Accept(float range, float releaseDistance, float targetAngle, float aimAngle)
        {
            return Finite(range) && Finite(releaseDistance) && Finite(targetAngle) && Finite(aimAngle) &&
                range > 1f && releaseDistance > 1f && range <= releaseDistance &&
                targetAngle >= 0f && targetAngle <= NoseConeDegrees &&
                aimAngle >= 0f && aimAngle <= NoseConeDegrees;
        }
    }
}
