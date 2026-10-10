using System;
using System.Collections.Generic;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Burns the air-to-ground missile's motor out: the motor plume (taken from the game's Malyutka, which
    /// models a real two-stage rocket motor) burns for a few seconds and then goes out, exactly as the
    /// player asked - "飞行 5 秒后发动机尾焰自动关闭".
    ///
    /// The plume objects are composed into the missile prefab by CasMissileComposer and named
    /// "CFS Motor Flame ...", so this component only has to switch them off:
    ///
    ///   * the booster flame goes out after <see cref="BoosterBurnSeconds"/> (a booster is a launch stage);
    ///   * after this round's own burn time the motor itself shuts down: the sustainer flame, the
    ///     smoke trail, the heat distortion, the engine light and the engine audio loop ALL go out
    ///     together, exactly as the player asked - the smoke trail disappears with the engine. The trail is
    ///     stopped with StopEmittingAndClear and then cleared, so the smoke already in the air goes with the
    ///     plume instead of trailing on for its particle lifetime, and its renderers are switched off too.
    ///
    /// Added to the round by CasMissileVisualRepair when the round is spawned, so its clock starts at
    /// launch. It only ever touches the missile's own visual - never the round's flight, damage or
    /// penetration.
    /// </summary>
    internal sealed class CasMissileMotorBurnout : MonoBehaviour
    {
        /// <summary>Booster stage burn time in seconds (launch flame).</summary>
        internal const float BoosterBurnSeconds = 1.6f;

        /// <summary>
        /// The motor's burn time in seconds when the round's own profile does not say (a rocket round, or a
        /// missile whose profile could not be resolved): the figure the player originally asked for.
        ///
        /// The two missiles do NOT share it - the AGM-65 is a short boost and then glides with no smoke at
        /// all, the Kh-25 burns for most of its flight - so CasImpactAttachPatch calls
        /// <see cref="SetBurnSeconds"/> with MissileProfile.MotorBurnSeconds as the round spawns.
        /// </summary>
        internal const float DefaultBurnSeconds = 5f;

        private float _burnSeconds = DefaultBurnSeconds;

        private const string FlamePrefix = "CFS Motor Flame";

        /// <summary>The donor TOW visual's engine sound node: silenced when the motor burns out.</summary>
        private const string EngineAudioName = "engine audio";

        private readonly List<GameObject> _flames = new List<GameObject>();
        private readonly List<GameObject> _audioNodes = new List<GameObject>();
        private Light[] _lights;
        private float _age;
        private bool _collected;
        private bool _boosterOut;
        private bool _motorOut;

        /// <summary>Sets this round's motor burn time, before the clock starts (i.e. right after spawning).</summary>
        internal void SetBurnSeconds(float seconds)
        {
            if (seconds > 0.1f)
            {
                _burnSeconds = seconds;
            }
        }

        private void Update()
        {
            try
            {
                if (!_collected)
                {
                    Collect();
                }

                _age += Time.deltaTime;

                if (!_boosterOut && _age >= Mathf.Min(BoosterBurnSeconds, _burnSeconds * 0.5f))
                {
                    _boosterOut = true;
                    SetFlamesOff("booster");
                }

                if (!_motorOut && _age >= _burnSeconds)
                {
                    _motorOut = true;
                    SetFlamesOff(null);
                    StopMotorParticles();
                    SetLightsOff();
                    StopAudio();
                    // These custom visuals are destroyed rather than pooled. All timed work is done.
                    enabled = false;
                }
            }
            catch (Exception)
            {
                enabled = false;
            }
        }

        private void Collect()
        {
            _collected = true;

            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform node = all[i];
                if (node == null)
                {
                    continue;
                }
                if (node.name.StartsWith(FlamePrefix, StringComparison.Ordinal))
                {
                    _flames.Add(node.gameObject);
                }
                else if (node.name.IndexOf(EngineAudioName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // The engine sound is silenced by switching its node off, so this component needs no
                    // reference to the audio module at all.
                    _audioNodes.Add(node.gameObject);
                }
            }

            _lights = GetComponentsInChildren<Light>(true);

        }

        /// <summary>Switches off the flames whose name contains the stage (null = all of them).</summary>
        private void SetFlamesOff(string stage)
        {
            for (int i = 0; i < _flames.Count; i++)
            {
                GameObject flame = _flames[i];
                if (flame == null || (stage != null &&
                                      flame.name.IndexOf(stage, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                flame.SetActive(false);
            }
        }

        /// <summary>
        /// Stops the exhaust trail together with the flame, as asked: the smoke billboards at the tail
        /// ("smoke"), the stretched trail sheets ("Smoke Trail Heavy", "Smoke Trail Billboards Side") and
        /// the heat distortion ARE the motor exhaust, so they end when the motor does.
        ///
        /// Every particle system in the missile is switched off, whether or not it is currently playing -
        /// one that is still dormant (its GameObject inactive, or not started yet) would otherwise stay
        /// armed and begin puffing after the motor is already out. <c>StopEmittingAndClear</c> plus an
        /// explicit <c>Clear</c> removes the particles already in the air, so the trail really goes with the
        /// plume instead of trailing on for its full particle lifetime, and switching the renderer off keeps
        /// anything that somehow survives from being drawn.
        /// </summary>
        private void StopMotorParticles()
        {
            ParticleSystem[] systems = GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                {
                    continue;
                }
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Clear(true);

                ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer != null)
                {
                    renderer.enabled = false;
                }
            }
        }

        /// <summary>
        /// Silences the motor: the donor's looping engine sound node is switched off together with the
        /// flame, so a burned-out missile is quiet.
        /// </summary>
        private void StopAudio()
        {
            for (int i = 0; i < _audioNodes.Count; i++)
            {
                GameObject node = _audioNodes[i];
                if (node != null && node.activeSelf)
                {
                    node.SetActive(false);
                }
            }
        }

        private void SetLightsOff()
        {
            if (_lights == null)
            {
                return;
            }
            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] != null && _lights[i].enabled)
                {
                    _lights[i].enabled = false;
                }
            }
        }
    }
}

