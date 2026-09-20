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
    ///   * after <see cref="SustainBurnSeconds"/> the motor itself shuts down: the sustainer flame, the
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

        /// <summary>Sustainer burn time in seconds: the flame goes out this long after launch.</summary>
        internal const float SustainBurnSeconds = 5f;

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

        private void Update()
        {
            try
            {
                if (!_collected)
                {
                    Collect();
                }

                _age += Time.deltaTime;

                if (!_boosterOut && _age >= BoosterBurnSeconds)
                {
                    _boosterOut = true;
                    SetFlamesOff("booster");
                }

                if (!_motorOut && _age >= SustainBurnSeconds)
                {
                    _motorOut = true;
                    int count = SetFlamesOff(null);
                    string trail = StopMotorParticles();
                    int lights = SetLightsOff();
                    int audio = StopAudio();
                    // These custom visuals are destroyed rather than pooled. All timed work is done.
                    enabled = false;
                    Log.Info("CAS missile motor: burnout after " + SustainBurnSeconds.ToString("0.#") +
                             " s of flight - " + count + " flame object(s) off, " + trail +
                             ", " + audio + " engine audio source(s) off, " + lights +
                             " light(s) off; nothing is left trailing behind the missile for the rest of the flight.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS missile motor burnout failed: " + ex);
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

            if (_flames.Count == 0)
            {
                Log.Warn("CAS missile motor: this missile prefab has no '" + FlamePrefix +
                         "' objects, so the motor cannot be switched off in flight.");
            }
        }

        /// <summary>Switches off the flames whose name contains the stage (null = all of them).</summary>
        private int SetFlamesOff(string stage)
        {
            int count = 0;
            for (int i = 0; i < _flames.Count; i++)
            {
                GameObject flame = _flames[i];
                if (flame == null || (stage != null &&
                                      flame.name.IndexOf(stage, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                flame.SetActive(false);
                count++;
            }
            return count;
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
        /// anything that somehow survives from being drawn. Returns a description for the log.
        /// </summary>
        private string StopMotorParticles()
        {
            List<string> stopped = new List<string>();
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
                stopped.Add("'" + system.name + "'");
            }

            if (stopped.Count == 0)
            {
                return "no smoke / heat effect(s) found on the missile";
            }
            return stopped.Count + " trail effect(s) stopped and cleared (" +
                   string.Join(", ", stopped.ToArray()) + ")";
        }

        /// <summary>
        /// Silences the motor: the donor's looping engine sound node is switched off together with the
        /// flame, so a burned-out missile is quiet.
        /// </summary>
        private int StopAudio()
        {
            int count = 0;
            for (int i = 0; i < _audioNodes.Count; i++)
            {
                GameObject node = _audioNodes[i];
                if (node != null && node.activeSelf)
                {
                    node.SetActive(false);
                    count++;
                }
            }
            return count;
        }

        private int SetLightsOff()
        {
            int count = 0;
            if (_lights == null)
            {
                return 0;
            }
            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] != null && _lights[i].enabled)
                {
                    _lights[i].enabled = false;
                    count++;
                }
            }
            return count;
        }
    }
}
