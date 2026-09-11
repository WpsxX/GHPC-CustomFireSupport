namespace CustomFireSupport
{
    /// <summary>
    /// One artillery slot's effective timing, worked out from the battery's vanilla values and the slot's
    /// two scale keys:
    ///
    ///   * <c>ImpactDelaySeconds</c> - how long after the call the FIRST round arrives (GHPC calls this
    ///     the on-call impact delay);
    ///   * <c>InterShotDelaySeconds</c> - how far apart the rounds of the volley are.
    ///
    /// Those two numbers must never be allowed to share one knob, and they used to. The CheatMode
    /// "time to target" semantics were ported literally, so <c>ImpactDelaySeconds &lt; 1</c> did not only
    /// cancel the first-round delay - it also multiplied the interval by the same factor, silently and
    /// with no key to turn it off. A slot configured "arrive fast, but space the rounds like vanilla"
    /// (ImpactDelaySeconds = 0.3, InterShotDelaySeconds = 1.0) therefore fired its rounds
    /// 0.7 x 0.3 = 0.21 s apart instead of the 0.7 s it asked for, which in game looks exactly like the
    /// interval key being ignored (the slot's log line showed <c>interShot=0.21s</c>).
    ///
    /// The interval answers to <c>InterShotDelaySeconds</c> alone. The single exception is
    /// an instant volley (<c>ImpactDelaySeconds &lt;= 0</c>): that fires every round on the frame of the
    /// call, so there is no spacing left to honour.
    ///
    /// Pure maths on System types only - no UnityEngine, no MelonLoader - so ConfigParsingTests can assert
    /// the numbers headlessly. That is deliberate: the whole point is that this arithmetic is checked.
    /// </summary>
    internal static class ArtilleryTiming
    {
        /// <summary>One slot's resolved timing, in seconds.</summary>
        internal struct Timing
        {
            /// <summary>Call to first round. 0 = the first round arrives immediately.</summary>
            internal float ImpactDelaySeconds;

            /// <summary>Round to round. 0 = the whole volley is fired on one frame.</summary>
            internal float InterShotSeconds;

            /// <summary>True when the whole volley is fired on the frame of the call.</summary>
            internal bool InstantVolley;

            /// <summary>
            /// Call to last round. Only used to keep the panel's cooldown bookkeeping valid for the whole
            /// strike (CooldownManager drops an entry whose cooldown has run out, and the button then
            /// freezes on "Incoming").
            /// </summary>
            internal float VolleySeconds(int shots)
            {
                int gaps = shots > 1 ? shots - 1 : 0;
                return ImpactDelaySeconds + gaps * InterShotSeconds;
            }
        }

        /// <summary>
        /// Resolves one slot's timing.
        ///
        /// <paramref name="impactScale"/> &gt;= 1 scales the first-round delay, anything below 1 cancels it
        /// ("arrive now") and &lt;= 0 makes the whole volley instant. <paramref name="interShotScale"/>
        /// scales the round spacing and is not touched by the other key.
        /// </summary>
        internal static Timing Resolve(float vanillaImpactSeconds, float vanillaInterShotSeconds,
            float impactScale, float interShotScale)
        {
            Timing timing;
            timing.InstantVolley = impactScale <= 0f;
            timing.ImpactDelaySeconds = impactScale >= 1f ? Scale(vanillaImpactSeconds, impactScale) : 0f;
            timing.InterShotSeconds = timing.InstantVolley ? 0f : Scale(vanillaInterShotSeconds, interShotScale);
            return timing;
        }

        /// <summary>
        /// A scale factor applied to a vanilla value: 1 = the vanilla value, 0.5 = half, &lt;= 0 = zero.
        /// The one implementation of the rule, shared by every scaled slot value so they cannot drift
        /// apart (see <see cref="CustomSlotBuilder.ScaleValue"/>).
        /// </summary>
        internal static float Scale(float vanilla, float scale)
        {
            if (scale <= 0f)
            {
                return 0f;
            }
            float value = vanilla * scale;
            // Also catches NaN (every comparison against NaN is false), which a hand-edited cfg can
            // produce and which would otherwise poison the battery's timers.
            return value > 0f ? value : 0f;
        }
    }
}
