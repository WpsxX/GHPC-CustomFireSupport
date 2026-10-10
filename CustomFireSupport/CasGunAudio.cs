using System;
using System.Collections.Generic;
using System.IO;
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
    /// take is chosen from the distance between the listener and the gun, and its volume is scaled by
    /// inverse-distance (1/r) so the sound keeps receding WITHIN a range instead of holding flat and
    /// jumping at the boundary. Result: one continuous fade as the aircraft closes in and recedes.
    ///
    /// NOTHING LOOPS. Every take is a complete recording that plays once and stops on its own. The
    /// GSh-30 is the reason this matters: its fire and its spin-down are merged offline into a single
    /// take (85% sustained fire, 15% spin-down, sized to one burst), so the sustained sound and its
    /// ending arrive as one recording rather than as a loop that must be cut and handed over to a tail.
    /// That removes an entire class of fault - an emitter whose owner is lost can no longer repeat
    /// forever, it can only finish the take it was given.
    ///
    /// THE SPEED OF SOUND IS MODELLED. The burst is delayed by distance / 343 m/s, so a gun firing a
    /// kilometre away is heard about 2.9 s after it fires, and the volume follows the distance while
    /// the sound is in flight.
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

        /// <summary>
        /// One recording: the name it carries inside the bundle, and whether it is meant to repeat.
        ///
        /// Looping belongs to the RECORDING, not to the gun. Treating it as a property of the gun made
        /// every take of the GSh-30 loop, including "CFS GSh30 far" - which is a one-shot distant
        /// recording, so it repeated for as long as anything kept the emitter alive. That is the
        /// "GSh-30 keeps looping after firing" report: a single flag let a one-shot take behave like a
        /// loop, and the only reason a non-looping clip cannot do that is that it stops on its own.
        /// </summary>
        internal sealed class GunClip
        {
            internal string Name;
            internal bool Loops;
        }

        internal sealed class GunSound
        {
            internal GunClip Close;
            internal GunClip Mid;
            internal GunClip Far;
        }

        /// <summary>
        /// A take that plays once and stops on its own. Every recording the bundle ships is one of these:
        /// since the GSh-30's fire and its spin-down are merged into a single clip, no take needs to
        /// repeat any more. The flag is kept on the take rather than on the gun so that looping would
        /// remain expressible per recording if a future take ever wants it.
        /// </summary>
        private static GunClip OneShot(string name)
        {
            return new GunClip { Name = name, Loops = false };
        }

        // The A-10's GAU-8: three separate complete takes, none of them a loop.
        private static readonly GunSound Gau8 = new GunSound
        {
            Close = OneShot("CFS GAU8 close"),
            Mid = OneShot("CFS GAU8 mid"),
            Far = OneShot("CFS GAU8 far")
        };

        // The Soviet GSh-30. The close band plays ONE recording that already contains the whole event:
        // the sustained fire followed by its spin-down, merged offline at 85% / 15% of the burst (140
        // rounds at 3900 rpm = 2.15 s). Because that single take covers the firing AND its ending, this gun
        // needs no loop and no separate stop-tail - which is what makes "it keeps looping after firing"
        // structurally impossible rather than merely unlikely: no take of any gun repeats, so an emitter
        // that somehow outlives its owner can only finish its one recording and go quiet.
        private static readonly GunSound Gsh30 = new GunSound
        {
            Close = OneShot("CFS GSh30 combined"),
            Mid = OneShot("CFS GSh30 far"),
            Far = OneShot("CFS GSh30 far")
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
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                {
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
            }
            catch (Exception)
            {
            }
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
                GunClip take = PickClip(sound, DistanceToListener(origin));
                AudioClip clip = take != null ? Clip(take.Name) : null;
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
                audio.loop = take.Loops;      // the TAKE decides, not the gun
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
                return handle;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The take for this distance, or null when the band has no recording configured.</summary>
        private static GunClip PickClip(GunSound sound, float distance)
        {
            return distance <= CloseRange ? sound.Close
                 : distance <= MidRange ? sound.Mid
                 : sound.Far;
        }

        /// <summary>Resolves a take's name to the loaded AudioClip, or null when the bundle lacks it.</summary>
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
        /// Ends the burst: the aircraft has stopped firing.
        ///
        /// THE RECORDING IS THE WHOLE EVENT, so it is left to play to its own end rather than being cut
        /// here. The GSh-30's take already contains the sustained fire followed by its spin-down (merged
        /// offline at 85% / 15% of the burst), and the GAU-8's takes are complete bursts whose own decay
        /// is the ending. Cutting at this instant would truncate them: the sound is delayed by the speed
        /// of sound, so at 500 m it has only been audible for about 0.7 s when the last round leaves the
        /// barrel, and a hard stop there would clip most of the take away.
        ///
        /// Nothing loops any more, so letting a take run out is BOUNDED: the per-frame pump destroys the
        /// emitter at <c>EndsAt</c>, and even if this method were never called at all - an exception
        /// unwinding the burst coroutine, or the aircraft being destroyed mid-run - a non-repeating
        /// recording stops by itself after one pass. That is what makes the "keeps looping after firing"
        /// fault impossible rather than merely unlikely.
        /// </summary>
        internal static void End(Handle handle)
        {
            if (handle == null)
            {
                return;
            }

            handle.Firing = false;   // the shooting has stopped; the anchor stops following from here
            handle.Anchor = null;
            handle.PendingEnd = true;

            AudioSource audio = handle.Source;
            float length = audio != null && audio.clip != null ? audio.clip.length : 0.5f;
            handle.EndsAt = handle.StartedAt + Mathf.Max(0.01f, length);

            // Belt and braces: with a non-repeating take the emitter cannot outlive its recording.
            if (audio != null)
            {
                audio.loop = false;
            }

            TrackTail(handle);
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

        /// <summary>One playing burst. Owned by the per-frame pump so it can be updated and torn down.</summary>
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
            /// Set by <see cref="End"/> once the shooting has stopped. The take is left to play out and the
            /// emitter is destroyed at <see cref="EndsAt"/>, which is also the hard upper bound on how long
            /// any gun sound can live - so a handle that somehow loses its owner still cannot live forever.
            /// </summary>
            internal bool PendingEnd;
            internal float EndsAt;
        }

        /// <summary>Every gun sound still alive, pumped once per frame by the mod's update hook.</summary>
        private static readonly List<Handle> _tails = new List<Handle>();

        /// <summary>
        /// Advances every live gun sound and drops the finished ones. Called once per frame from the
        /// mod's update hook - NOT from the firing coroutine.
        ///
        /// The pump has to own the whole lifetime: a distant burst is heard SECONDS after it is fired
        /// (2.9 s at 1 km) while the burst itself only lasts 2.15 s, so the firing loop is long over by
        /// the time the sound should become audible. Driving the volume from there left every shot beyond
        /// ~700 m permanently silent.
        ///
        /// Every handle also has a hard end (<see cref="Handle.EndsAt"/>) set by <see cref="End"/>, so the
        /// emitter can never outlive its recording by much even if the burst coroutine never got to call
        /// End at all.
        /// </summary>
        internal static void PumpTails()
        {
            for (int i = _tails.Count - 1; i >= 0; i--)
            {
                Handle handle = _tails[i];

                if (!DriveVolume(handle))
                {
                    // Finished. Destroy it here rather than only forgetting it: dropping the handle while
                    // the emitter is still alive would leave a world-anchored AudioSource with no owner -
                    // the shape of the original "sound keeps playing after firing" report.
                    Release(handle);
                    _tails.RemoveAt(i);
                    continue;
                }

                // The take has played out (or its end is due): tear the emitter down.
                if (handle.PendingEnd && Time.time >= handle.EndsAt)
                {
                    handle.PendingEnd = false;
                    Release(handle);
                    _tails.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Sets a live sound's volume for this frame from what it is doing right now.
        ///
        /// Two phases, in order:
        ///   * still travelling - silent, and the anchor follows the aircraft while it is still firing;
        ///   * audible          - the 1/r volume for the current distance.
        ///
        /// The take itself is fixed at <see cref="Begin"/> and is never exchanged: nothing loops, so
        /// switching recording mid-burst could only restart a one-shot that is already playing.
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

                // The take is NOT exchanged here. Nothing loops any more, so every recording is a
                // one-shot that is already playing: swapping it as the aircraft crosses a range band
                // could only restart it from its beginning, which the player hears as a stutter. The
                // take is therefore chosen once, in Begin, and only the volume follows the distance.
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Keeps a playing burst's volume in step with the distance and follows the aircraft while it is
        /// still firing, so closing in or pulling away is heard as one continuous change.
        ///
        /// Kept as the firing-loop entry point so the coroutine works the same way it always did; the
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
            catch (Exception)
            {
            }
            return null;
        }
    }
}

