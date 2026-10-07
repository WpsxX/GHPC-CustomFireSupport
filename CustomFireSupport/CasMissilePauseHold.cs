using System;
using GHPC.Weapons;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Keeps a mod-flown missile pointing where the mod is flying it while the game is PAUSED.
    ///
    /// WHY THIS EXISTS
    ///
    /// A paused round is not advanced at all: LiveRoundBatchHandler.Update returns outright on
    /// Time.timeScale == 0. The missile's attitude is therefore left wherever the last live frame put it,
    /// and any other component that writes the round's transform during those paused frames wins - which is
    /// what the player saw as "the missile suddenly turns in a random direction when I pause, then climbs".
    ///
    /// This component holds the last attitude CasMissileGuidance actually flew, for as long as the game is
    /// paused, so the round leaves the pause pointing the way it went in. Nothing else is touched: no
    /// position, no velocity, no flight law. The moment the game resumes, the round is the mod's again and
    /// this component does nothing until the next pause.
    ///
    /// It is deliberately NOT part of CasMissileGuidance: a MonoBehaviour's Update/LateUpdate keeps running
    /// while Time.timeScale is 0, which is the only way to observe the paused frames at all.
    /// </summary>
    internal sealed class CasMissilePauseHold : MonoBehaviour
    {
        /// <summary>The guidance whose attitude is held.</summary>
        internal CasMissileGuidance Guidance;

        private void LateUpdate()
        {
            if (Guidance == null || !IsFlying())
            {
                enabled = false;
                return;
            }

            try
            {
                if (Time.timeScale <= 0f)
                {
                    HoldGuidedAttitude();
                }
            }
            catch (Exception ex)
            {
                // Never let the hold itself interfere with a shot.
                Log.Error("CAS paused missile attitude hold failed: " + ex);
                enabled = false;
            }
        }

        /// <summary>
        /// True while this round is still one of ours and still under the guidance: a released or pooled
        /// round belongs to the game and must not have its transform written.
        /// </summary>
        private bool IsFlying()
        {
            LiveRound round = GetComponent<LiveRound>();
            if (round == null || round.Pooled || !CasPayloadFactory.IsOurMissile(round.Info))
            {
                return false;
            }
            CasImpactAim aim = GetComponent<CasImpactAim>();
            return aim != null && !aim.Released && aim.ShotId == round.ID;
        }

        /// <summary>Puts the last guided orientation back on the round for this paused frame.</summary>
        private void HoldGuidedAttitude()
        {
            if (Guidance.IntendedDirection.sqrMagnitude <= 1e-6f)
            {
                return;     // the guidance has not flown this round yet
            }
            transform.rotation = Guidance.IntendedRotation;
        }
    }
}
