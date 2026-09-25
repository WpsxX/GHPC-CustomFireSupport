using System;
using System.Collections.Generic;
using System.IO;
using GHPC.Weaponry.CAS;
using GHPC.Vehicle;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// The mod's own gun sound, played from a plain AudioSource instead of an FMOD event.
    ///
    /// WHY NOT FMOD. FMOD only plays events that were compiled into a .bank, and the mod has no access
    /// to GHPC's FMOD Studio project, so a custom recording cannot be added as an event at all. The
    /// AudioClips ship in a small AssetBundle ("cfs_audio") and are played through an AudioSource,
    /// which also makes the range behaviour below possible - a single FMOD event could not express it.
    ///
    /// THE THREE-RANGE DESIGN. Each gun has a close, a mid and a far recording of the same burst. The
    /// clip is chosen from the distance between the listener and the gun, and its volume is scaled by
    /// inverse-square (1/r^2) so the sound keeps fading WITHIN a range instead of holding flat and
    /// jumping at the boundary. Result: one continuous fade as the aircraft closes in and recedes.
    ///
    /// THE SOVIET GUN IS DIFFERENT. The GSh-30 recordings are a LOOP and its tail, not a one-shot:
    ///   * "burst loop"  - looped for as long as the trigger is held,
    ///   * "burst stop"  - played once when the burst ends, as the falling-off tail,
    ///   * "far"         - used for BOTH the mid and the far range (the supplied set has no separate
    ///                     mid clip for this gun).
    /// </summary>
    internal static class CasGunAudio
    {
        // The listener-relative distances the three ranges start at, in metres. A CAS strafe is
        // watched from kilometres away, which is why these are far larger than a normal weapon's.
        private const float CloseRange = 500f;
        private const float MidRange = 1500f;

        /// <summary>
        /// Speed of sound in air at sea level, in m/s. The burst is heard this much later per metre of
        /// distance: a gun firing 1 km away is heard about 2.9 s after it fires.
        /// </summary>
        private const float SpeedOfSound = 343f;

        /// <summary>Seconds the sound takes to travel a distance, at <see cref="SpeedOfSound"/>.</summary>
        internal static float SoundTravelSeconds(float distance)
        {
            return Mathf.Max(0f, distance) / SpeedOfSound;
        }

        /// <summary>
        /// Volume at the close-range cut-off, i.e. the reference level the curve is normalised against.
        /// </summary>
        private const float VolumeAtCloseRange = 1f;

        private const float MinVolume = 0.04f;

        /// <summary>
        /// Volume for a distance, using INVERSE-DISTANCE (1/r) falloff normalised against the
        /// close-range cut-off: v = close / r.
        ///
        /// 1/r rather than the physical 1/r^2 because 1/r^2 is far too steep here: at 1500 m it is 1/9 of
        /// the close value (-19 dB), which is effectively inaudible for a strafe the player is watching.
        /// 1/r halves per doubling instead of quartering, so the burst stays present as the aircraft
        /// recedes while still falling smoothly, with no plateau inside a range band.
        /// </summary>
        internal static float VolumeFor(float distance)
        {
            float r = Mathf.Max(distance, CloseRange);
            float volume = VolumeAtCloseRange * CloseRange / r;
            return Mathf.Clamp(volume, MinVolume, 1f);
        }

        private const string BundleName = "cfs_audio";

        // ------------------------------------------------------------------
        // Clip names inside the bundle, per gun.
        // ------------------------------------------------------------------

        internal sealed class GunSound
        {
            internal string Close;
            internal string Mid;
            internal string Far;
            internal bool Loops;        // true for the GSh-30, whose burst is a looping recording
            internal string StopTail;   // played once when a looping burst ends
        }

        // The A-10's GAU-8: three separate recordings, each a complete burst.
        private static readonly GunSound Gau8 = new GunSound
        {
            Close = "CFS GAU8 close",
            Mid = "CFS GAU8 mid",
            Far = "CFS GAU8 far",
            Loops = false
        };

        // The Soviet GSh-30: a loop plus its tail; mid and far share the "far" recording.
        private static readonly GunSound Gsh30 = new GunSound
        {
            Close = "CFS GSh30 burst loop",
            Mid = "CFS GSh30 far",
            Far = "CFS GSh30 far",
            Loops = true,
            StopTail = "CFS GSh30 burst stop"
        };

        private static AssetBundle _bundle;
        private static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private static bool _loadAttempted;

        /// <summary>
        /// Loads the sound bundle. Safe to call repeatedly; the bundle is loaded once and pinned.
        /// Failure is not fatal - the gun then simply falls back to the game's own FMOD one-shot.
        /// </summary>
        internal static void EnsureLoaded()
        {
            if (_loadAttempted)
            {
                return;
            }
            _loadAttempted = true;

            try
            {
                string path = FindBundlePath();
                if (path == null)
                {
                    Log.Warn("CAS gun audio: '" + BundleName + "' bundle not found next to the mod; " +
                             "the gun will fall back to the game's own FMOD shot.");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                {
                    Log.Warn("CAS gun audio: could not load '" + path + "' (wrong Unity version?).");
                    return;
                }

                UnityEngine.Object[] assets = _bundle.LoadAllAssets();
                for (int i = 0; i < assets.Length; i++)
                {
                    AudioClip clip = assets[i] as AudioClip;
                    if (clip != null && !string.IsNullOrEmpty(clip.name))
                    {
                        _clips[clip.name] = clip;
                    }
                }

                Log.Info("CAS gun audio: loaded " + _clips.Count + " clip(s) from '" +
                         Path.GetFileName(path) + "' (" + DescribeClips() + ").");
            }
            catch (Exception ex)
            {
                Log.Error("CAS gun audio: bundle load failed: " + ex);
            }
        }

        private static string DescribeClips()
        {
            List<string> names = new List<string>(_clips.Keys);
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names.ToArray());
        }

        /// <summary>True once at least one clip is usable, i.e. the custom sound should be used.</summary>
        internal static bool IsAvailable
        {
            get { return _clips.Count > 0; }
        }

        /// <summary>
        /// The airframe's name, read from the CASController above the firing hardpoint manager.
        /// The controller has no airframe-name field of its own (the game identifies the aircraft by
        /// its GameObject), and the prefab is cloned as "&lt;model&gt;(Clone)", so the clone suffix is
        /// stripped here.
        /// </summary>
        internal static string AirframeNameOf(CASHardpointManager manager)
        {
            try
            {
                if (manager == null)
                {
                    return null;
                }
                CASController controller = manager.GetComponentInParent<CASController>();
                if (controller == null || controller.gameObject == null)
                {
                    return null;
                }
                string name = controller.gameObject.name;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
                return clone > 0 ? name.Substring(0, clone).Trim() : name.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The sound definition for an airframe, chosen by its name (the A-10 flies the GAU-8, the
        /// Soviet types the GSh-30). Anything else falls back to the GAU-8 set rather than being silent.
        /// </summary>
        private static GunSound SoundFor(string airframeName)
        {
            if (!string.IsNullOrEmpty(airframeName))
            {
                string n = airframeName.ToLowerInvariant();
                if (n.Contains("mig") || n.Contains("su-") || n.Contains("su2") || n.Contains("gsh"))
                {
                    return Gsh30;
                }
            }
            return Gau8;
        }

        /// <summary>
        /// Starts the sustained fire sound for one burst and returns a handle to stop it with.
        /// Returns null when the custom sound is unavailable, in which case the caller keeps whatever
        /// the game itself plays.
        ///
        /// THE SOUND IS DELAYED BY THE SPEED OF SOUND. Gunfire from an aircraft a kilometre away is
        /// heard about three seconds after it is fired, so the burst is scheduled to begin at
        /// now + distance/343 rather than immediately. PlayScheduled is used (not a timer) so the delay
        /// is sample-accurate rather than frame-quantised.
        ///
        /// THE EMITTER IS ANCHORED IN THE WORLD, not parented to the aircraft. Two reasons:
        ///   * the delay means the aircraft may have moved (or been despawned at the end of its pass)
        ///     before the sound is due - a child emitter would be destroyed with it and the shot would
        ///     be heard as silence;
        ///   * a sound that is heard later should come from where it WAS MADE, so the position is the
        ///     aircraft's position at the moment of firing.
        /// The gun is moving fast enough that its travel during the delay is audible, so the anchor is
        /// updated by <see cref="Update"/> while the burst is still being fired.
        /// </summary>
        internal static Handle Begin(Transform source, string airframeName)
        {
            EnsureLoaded();
            if (!IsAvailable || source == null)
            {
                return null;
            }

            try
            {
                Vector3 origin = source.position;
                GunSound sound = SoundFor(airframeName);
                AudioClip clip = PickClip(sound, DistanceToListener(origin));
                if (clip == null)
                {
                    return null;
                }

                GameObject go = new GameObject("CFS Gun Audio");
                // Anchored in the world (no parent): see the comment above.
                go.transform.position = origin;

                AudioSource audio = go.AddComponent<AudioSource>();
                // Fully 3D so the engine pans by position; the volume curve is applied by this class
                // (see VolumeFor), because the shipped rolloff modes cannot express "1/r beyond 500 m
                // but never louder than the close-range reference".
                audio.spatialBlend = 1f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.minDistance = 1f;
                audio.maxDistance = 20000f;   // far enough not to clip the sound before we fade it
                audio.dopplerLevel = 0f;      // the flight time is modelled explicitly below, not by Doppler
                audio.loop = sound.Loops;
                audio.clip = clip;
                audio.volume = 0f;            // set once the sound is actually due to be heard

                float distance = DistanceToListener(origin);
                float delay = SoundTravelSeconds(distance);

                Handle handle = new Handle();
                handle.Source = audio;
                handle.GameObject = go;
                handle.Sound = sound;
                handle.ClipName = clip.name;
                handle.StartedAt = Time.time + delay;   // when the burst becomes audible
                handle.Anchor = source;                 // tracked while firing, so it does not lag
                handle.LastDistance = distance;
                handle.Firing = true;                   // the burst is still being fired right now

                // Schedule the clip so it starts at exactly the right moment, then let the per-frame
                // pump bring the volume up when it arrives.
                //
                // Play() must come FIRST. PlayScheduled on a source that has never started does not
                // reliably begin playback in every Unity version, and because the volume stays at 0 until
                // the sound is due, a source that silently failed to start is indistinguishable from a
                // delayed one - i.e. it is heard as no gun sound at all. Play() puts the source in the
                // playing state; PlayScheduled then moves the start to the requested DSP time.
                audio.Play();
                audio.SetScheduledStartTime(AudioSettings.dspTime + delay);

                // The pump must own this handle from now on: the burst ends long before a distant sound
                // becomes audible, so the FIRING loop cannot be what drives the volume up.
                TrackTail(handle);

                Log.Verbose("CAS gun audio: '" + clip.name + "' scheduled " + delay.ToString("0.00") +
                            "s from now (" + distance.ToString("0") + " m at " + SpeedOfSound +
                            " m/s).");
                return handle;
            }
            catch (Exception ex)
            {
                Log.Error("CAS gun audio: could not start the burst sound: " + ex);
                return null;
            }
        }

        private static AudioClip PickClip(GunSound sound, float distance)
        {
            string name = distance <= CloseRange ? sound.Close
                        : distance <= MidRange ? sound.Mid
                        : sound.Far;
            return Clip(name);
        }

        private static AudioClip Clip(string name)
        {
            AudioClip clip;
            if (!string.IsNullOrEmpty(name) && _clips.TryGetValue(name, out clip))
            {
                return clip;
            }
            return null;
        }

        private static float DistanceToListener(Vector3 position)
        {
            Camera camera = Camera.main;
            Vector3 listener = camera != null ? camera.transform.position : Vector3.zero;
            return Vector3.Distance(listener, position);
        }


        /// <summary>
        /// How long the stop tail is allowed to sound before it is faded out.
        ///
        /// The supplied tail recording is 4.07 s, which is LONGER than the burst that produces it
        /// (140 rounds at 3900 rpm = 2.15 s), so playing it in full would leave the tail dominating the
        /// effect. It is therefore cut short and faded, which is what makes the ending sound like the
        /// gun spinning down rather than a recording running on after the shooting stopped.
        /// </summary>
        private const float TailSeconds = 1.6f;

        /// <summary>Length of the fade applied at the cut, in seconds. Long enough to avoid a click.</summary>
        private const float TailFadeSeconds = 0.55f;

        /// <summary>
        /// Ends the burst.
        ///
        /// ONLY THE LOOPING GUN (GSh-30) IS TREATED SPECIALLY. Its sustained-fire recording is a loop,
        /// so stopping it abruptly would cut mid-cycle; instead the stop-tail recording is played, which
        /// continues the sound as the gun spins down, and that tail is faded out rather than played to
        /// its full 4.07 s (see TailSeconds).
        ///
        /// THE GAU-8 IS LEFT ALONE: its three recordings are complete bursts whose own decay already
        /// tails off, so they are simply stopped with the burst.
        ///
        /// THE SPEED-OF-SOUND DELAY IS RESPECTED: the burst is short (2.15 s) and the delay grows with
        /// distance (2.9 s at 1 km), so a distant burst ends BEFORE it has been heard. Stopping the
        /// source there would silence a shot that has not arrived yet, so in that case the clip is left
        /// to play out - the aircraft has already stopped firing; the sound is simply still in flight.
        ///
        /// The emitter is anchored in the world rather than parented to the aircraft, so the tail keeps
        /// the position the burst came from and cannot be destroyed with the aircraft mid-play.
        /// </summary>
        internal static void End(Handle handle)
        {
            if (handle == null)
            {
                return;
            }

            AudioSource audio = handle.Source;
            handle.Firing = false;   // the shooting has stopped; the anchor stops following from here

            // Not audible yet: the shots are still in flight. Let the scheduled clip play out rather
            // than cutting a burst that the player has not heard. The per-frame pump brings the volume
            // up when it arrives and tears the emitter down afterwards.
            if (audio != null && Time.time < handle.StartedAt)
            {
                handle.Anchor = null;          // it is no longer being fired; stop following the aircraft
                handle.EndsAt = handle.StartedAt + Mathf.Max(0.01f, audio.clip != null ? audio.clip.length : 0f);
                handle.PendingEnd = true;
                TrackTail(handle);
                return;
            }

            if (audio == null || !handle.Sound.Loops)
            {
                // The GAU-8 (and any non-looping set): stop it with the burst.
                try
                {
                    if (audio != null)
                    {
                        audio.Stop();
                    }
                }
                catch (Exception)
                {
                    // stopping is best-effort
                }
                Release(handle);
                return;
            }

            try
            {
                AudioClip tail = Clip(handle.Sound.StopTail);
                if (tail != null)
                {
                    // Anchor the tail where the burst ended, in world space.
                    if (handle.GameObject != null && handle.Anchor != null)
                    {
                        handle.GameObject.transform.position = handle.Anchor.position;
                    }
                    handle.Anchor = null;

                    // The tail recording is designed to continue the loop, so it starts at its own
                    // beginning - it picks up where the loop was cut.
                    audio.loop = false;
                    audio.clip = tail;
                    audio.time = 0f;
                    audio.Play();

                    handle.Fading = true;
                    handle.FadeFrom = Mathf.Max(audio.volume, MinVolume);
                    handle.FadeSeconds = TailFadeSeconds;
                    handle.DestroyAt = Time.time + TailSeconds;
                    TrackTail(handle);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS gun audio: could not play the burst stop tail: " + ex);
            }

            try
            {
                if (handle.Source != null)
                {
                    handle.Source.Stop();
                }
            }
            catch (Exception)
            {
                // stopping is best-effort
            }
            Release(handle);
        }

        /// <summary>
        /// Advances a tail's fade and cleans it up when it is done. Returns false once the handle is
        /// finished, so the caller can drop it.
        /// </summary>
        internal static bool UpdateTail(Handle handle)
        {
            if (handle == null || !handle.Fading)
            {
                return false;
            }

            AudioSource audio = handle.Source;
            if (audio == null || handle.GameObject == null)
            {
                handle.Fading = false;
                return false;
            }

            try
            {
                // The tail is detached, so its distance no longer changes: its volume is the fade alone.
                // The fade runs over the LAST FadeSeconds of the tail's life, so it holds its level
                // first and then falls away - a cut straight to silence would click.
                float timeLeft = handle.DestroyAt - Time.time;
                if (timeLeft <= 0f)
                {
                    handle.Fading = false;
                    Release(handle);
                    return false;
                }
                if (timeLeft < handle.FadeSeconds && handle.FadeSeconds > 0f)
                {
                    audio.volume = handle.FadeFrom * Mathf.Clamp01(timeLeft / handle.FadeSeconds);
                }
                else
                {
                    audio.volume = handle.FadeFrom;
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS gun audio: could not fade the burst tail: " + ex);
                handle.Fading = false;
                Release(handle);
                return false;
            }
            return true;
        }

        /// <summary>Tears down a handle whose emitter is no longer needed.</summary>
        private static void Release(Handle handle)
        {
            try
            {
                if (handle.GameObject != null)
                {
                    UnityEngine.Object.Destroy(handle.GameObject);
                }
            }
            catch (Exception)
            {
                // best-effort cleanup
            }
            handle.GameObject = null;
            handle.Source = null;
        }

        /// <summary>One playing burst. Owned by the burst coroutine so it can be updated and stopped.</summary>
        internal sealed class Handle
        {
            internal AudioSource Source;
            internal GameObject GameObject;
            internal GunSound Sound;
            internal string ClipName;

            /// <summary>Time.time at which the burst becomes audible (fire time + sound travel time).</summary>
            internal float StartedAt;

            /// <summary>The firing aircraft, followed until the sound arrives (then left behind).</summary>
            internal Transform Anchor;

            /// <summary>True while the gun is actually firing, so the anchor keeps up with the aircraft.</summary>
            internal bool Firing;

            internal float LastDistance;

            /// <summary>
            /// Set when the burst ended before its sound arrived: the clip plays out on its own and the
            /// emitter is torn down at <see cref="EndsAt"/> instead of being stopped immediately.
            /// </summary>
            internal bool PendingEnd;
            internal float EndsAt;

            // Stop-tail fade state (see End / UpdateTail).
            internal bool Fading;
            internal float FadeFrom;
            internal float FadeSeconds;
            internal float DestroyAt;
        }

        /// <summary>Every tail still fading, pumped once per frame by the mod's update hook.</summary>
        private static readonly List<Handle> _tails = new List<Handle>();

        /// <summary>
        /// Advances every live gun sound and drops the finished ones. Called once per frame from the
        /// mod's update hook - NOT from the firing coroutine.
        ///
        /// The pump has to own the whole lifetime: a distant burst is heard SECONDS after it is fired
        /// (2.9 s at 1 km) while the burst itself only lasts 2.15 s, so the firing loop is long over by
        /// the time the sound should become audible. Driving the volume from there left every shot beyond
        /// ~700 m permanently silent.
        /// </summary>
        internal static void PumpTails()
        {
            for (int i = _tails.Count - 1; i >= 0; i--)
            {
                Handle handle = _tails[i];

                if (!DriveVolume(handle))
                {
                    _tails.RemoveAt(i);
                    continue;
                }

                // A burst whose sound had not arrived when the shooting stopped: let the clip finish,
                // then tear the emitter down. Without this the sound still in flight would be cut off.
                if (handle.PendingEnd && !handle.Fading && Time.time >= handle.EndsAt)
                {
                    handle.PendingEnd = false;
                    Release(handle);
                    _tails.RemoveAt(i);
                    continue;
                }

                if (handle.Fading && !UpdateTail(handle))
                {
                    _tails.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Sets a live sound's volume for this frame from what it is doing right now.
        ///
        /// Three phases, in order:
        ///   * still travelling  - silent, and the anchor follows the aircraft while it is still firing;
        ///   * audible           - the 1/r volume for the current distance, plus a clip swap for the
        ///                         looping gun when the range band changes;
        ///   * fading out        - handled by UpdateTail.
        ///
        /// Returns false once the handle is finished and should be dropped.
        /// </summary>
        private static bool DriveVolume(Handle handle)
        {
            if (handle == null)
            {
                return false;
            }

            AudioSource audio = handle.Source;
            if (audio == null || handle.GameObject == null)
            {
                return false;
            }

            if (handle.Fading)
            {
                return true;   // UpdateTail owns the volume from here
            }

            try
            {
                if (Time.time < handle.StartedAt)
                {
                    // Still in flight. Follow the aircraft only while it is actually firing; after that
                    // the shot stays where it was fired from.
                    if (handle.Firing && handle.Anchor != null)
                    {
                        handle.GameObject.transform.position = handle.Anchor.position;
                    }
                    audio.volume = 0f;
                    return true;
                }

                if (!audio.isPlaying)
                {
                    // Not playing. That is either "the clip has not started yet" or "it has finished".
                    // The scheduled start can land a frame after StartedAt, so only treat it as finished
                    // once the expected end has passed - otherwise the emitter would be torn down in the
                    // very frame the sound was due.
                    float plannedEnd = handle.StartedAt +
                                       Mathf.Max(0.01f, audio.clip != null ? audio.clip.length : 0.5f);
                    if (Time.time < plannedEnd)
                    {
                        // Due but not started yet: keep it silent for this frame rather than dropping it.
                        audio.volume = 0f;
                        return true;
                    }
                    return false;
                }

                Transform t = handle.GameObject.transform;
                float distance = DistanceToListener(t.position);
                handle.LastDistance = distance;
                audio.volume = VolumeFor(distance);

                // A one-shot recording must not be swapped mid-play (it would restart); only the looping
                // GSh-30 changes its bed, and there the swap is the point.
                if (handle.Sound.Loops)
                {
                    AudioClip wanted = PickClip(handle.Sound, distance);
                    if (wanted != null && wanted.name != handle.ClipName)
                    {
                        audio.clip = wanted;
                        handle.ClipName = wanted.name;
                        audio.Play();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("CAS gun audio: could not update the burst sound: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Keeps a playing burst's volume in step with the distance, follows the aircraft while it is
        /// still firing, and swaps the clip when the range band changes - so closing in or pulling away
        /// is heard as one continuous change.
        ///
        /// Kept as the firing-loop entry point so the coroutine can report when firing has stopped; the
        /// per-frame pump does the actual work (see PumpTails).
        /// </summary>
        internal static void Update(Handle handle)
        {
            if (handle == null)
            {
                return;
            }
            DriveVolume(handle);
        }

        private static void TrackTail(Handle handle)
        {
            // The handle is queued by End; keep one entry per handle.
            for (int i = 0; i < _tails.Count; i++)
            {
                if (ReferenceEquals(_tails[i], handle))
                {
                    return;
                }
            }
            _tails.Add(handle);
        }

        /// <summary>
        /// Looks for the sound bundle next to the mod (Bin\Mods\cfs_audio), the same place cas_assets
        /// lives, so an install is still a single copy step.
        /// </summary>
        private static string FindBundlePath()
        {
            try
            {
                string modDir = Path.GetDirectoryName(typeof(CasGunAudio).Assembly.Location);
                if (!string.IsNullOrEmpty(modDir))
                {
                    string candidate = Path.Combine(modDir, BundleName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                // MelonLoader also stages mods under Mods\; check the game's own folder as a fallback.
                string gameDir = Directory.GetParent(Application.dataPath) != null
                    ? Directory.GetParent(Application.dataPath).FullName
                    : null;
                if (!string.IsNullOrEmpty(gameDir))
                {
                    string candidate = Path.Combine(Path.Combine(gameDir, "Mods"), BundleName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS gun audio: could not look for the bundle: " + ex);
            }
            return null;
        }
    }
}
