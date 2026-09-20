using System;
using System.Collections.Generic;
using System.Reflection;
using FMOD.Studio;
using FMODUnity;
using GHPC;
//using GHPC.Audio;
using GHPC.Vehicle;
using GHPC.Weaponry;
using GHPC.Weaponry.CAS;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Runtime weapon hardpoint factory, used for GunRun and for the air-to-ground missile.
    ///
    /// A CAS aircraft can only fire attack types whose hardpoints are physically mounted. GHPC ships
    /// no gun hardpoint at all (the aircraft's own gun is not a CASHardpoint asset), so a gun run has
    /// to be CONSTRUCTED at runtime: a lightweight GameObject carrying a CASHardpoint whose private
    /// serialized fields are configured (via Harmony field access) to GunRun, firing a real
    /// AmmoCodexScriptable round the game already has in memory (a 30mm/23mm autocannon round).
    ///
    /// The air-to-ground missile is built here for the same reason, but its parts come from three
    /// different donors, exactly as the player asked for:
    ///   * the MODEL is the game's own missile (AGM-65 for Blue, the game's Soviet missile visual for
    ///     Red), composed with the TOW missile's flight effects by the editor tool CasMissileComposer
    ///     and shipped in the bundle;
    ///   * the DATA (ballistics, warhead, explosion effect, impact audio) is a BOMB's - the ammo of the
    ///     bomb hardpoint this mission loaded is cloned and only its visual is replaced;
    ///   * the FLIGHT is the mod's own impact resolver, so the missile flies straight into the slot's
    ///     impact circle instead of using the game's missile guidance.
    ///
    /// Bombs and rockets are NOT built here: they are mounted from the game's own hardpoint prefabs
    /// (the airframe's loadout or CasAttackLibrary), so the real vanilla pylons and ammo are used.
    /// </summary>
    internal static class CasPayloadFactory
    {
        private static readonly AccessTools.FieldRef<CASHardpoint, CASAttackType> TypeRef =
            AccessTools.FieldRefAccess<CASHardpoint, CASAttackType>("_type");
        private static readonly AccessTools.FieldRef<CASHardpoint, AmmoCodexScriptable> AmmoRef =
            AccessTools.FieldRefAccess<CASHardpoint, AmmoCodexScriptable>("_ammo");
        private static readonly AccessTools.FieldRef<CASHardpoint, float> DeviationRef =
            AccessTools.FieldRefAccess<CASHardpoint, float>("_launchDeviation");
        private static readonly AccessTools.FieldRef<CASHardpoint, bool> InheritVelocityRef =
            AccessTools.FieldRefAccess<CASHardpoint, bool>("_inheritVelocity");
        private static readonly AccessTools.FieldRef<CASHardpoint, bool> VisibleMunitionsRef =
            AccessTools.FieldRefAccess<CASHardpoint, bool>("_visibleMunitions");
        private static readonly AccessTools.FieldRef<CASHardpoint, int> MunitionCountRef =
            AccessTools.FieldRefAccess<CASHardpoint, int>("_munitionCount");
        private static readonly AccessTools.FieldRef<CASHardpoint, int> LaunchCountRef =
            AccessTools.FieldRefAccess<CASHardpoint, int>("_launchCount");
        private static readonly AccessTools.FieldRef<CASHardpoint, Transform> SpawnPointRef =
            AccessTools.FieldRefAccess<CASHardpoint, Transform>("_munitionSpawnPoint");
        private static readonly AccessTools.FieldRef<CASHardpoint, string> AudioEventRef =
            AccessTools.FieldRefAccess<CASHardpoint, string>("_audioEvent");

        /// <summary>
        /// FMOD one-shot played by CASHardpoint for every round. GHPC has no GAU-8 / GSh-30-2 event of
        /// its own; this is the game's only 30mm autocannon single-shot (the BMP-2 2A42), which is the
        /// closest match. Without it the runtime hardpoint fires in complete silence.
        /// </summary>
        private const string GunAudioEvent = "event:/Weapons/autocannon_2a42_single";

        /// <summary>
        /// Candidates for the SUSTAINED gun sound, best first. A distance strafe is heard as one long
        /// BRRRT, not as 140 separate pops, so the burst also drives a continuous emitter: the game's
        /// 2A42 sustained event (the same one a BMP-2 uses while the trigger is held). The single-shot
        /// one-shots of CASHardpoint stay on top of it so individual shots are still distinguishable.
        /// </summary>
        private static readonly string[] GunBurstEventCandidates =
        {
            "event:/Weapons/autocannon_2a42_600rpm",
            "event:/Weapons/autocannon_m242_500rpm",
            "event:/Weapons/autocannon_2a42_single"
        };

        /// <summary>A strafe is watched from kilometres away; a weapon event's stock attenuation dies long before that.</summary>
        private const float GunAudioMinDistance = 80f;

        private const float GunAudioMaxDistance = 6000f;

        /// <summary>
        /// A 30 mm detonation is only a few pixels wide at the distance a CAS aircraft fires from, so the
        /// substituted impact explosion is scaled up to stay visible.
        /// </summary>
        private const float FallbackExplosionScale = 2f;

        /// <summary>Built hardpoint per profile key; rebuilt if Unity unloaded it with a scene.</summary>
        private static readonly Dictionary<string, GameObject> _built = new Dictionary<string, GameObject>();

        /// <summary>Every round this factory cloned, so the patches can recognise the mod's own ammo.</summary>
        private static readonly WeakIdentitySet<AmmoType> _ourRounds = new WeakIdentitySet<AmmoType>();

        /// <summary>Ammo codex per profile key (avoids rescanning every slot build).</summary>
        private static readonly Dictionary<string, AmmoCodexScriptable> _ammo = new Dictionary<string, AmmoCodexScriptable>();

        /// <summary>Profiles that already warned about having no matching ammo (log each once).</summary>
        private static readonly HashSet<string> _warnedMissingAmmo = new HashSet<string>();

        /// <summary>
        /// Gun belt per built hardpoint TEMPLATE name.
        ///
        /// CASHardpointManager.SetUpHardpoints instantiates the runtime hardpoint, and Unity clones
        /// through its serializer: only [SerializeField] / public fields survive. The belt is also
        /// registered here so a clone whose belt did not survive can be re-seated from the template.
        /// </summary>
        private static readonly Dictionary<string, AmmoCodexScriptable[]> _belts =
            new Dictionary<string, AmmoCodexScriptable[]>();

        /// <summary>
        /// The 30 mm HIGH EXPLOSIVE detonation prefab of the donor round, used as the visible impact
        /// explosion for every round of the gun run (the AP round's own effect is a tiny spark).
        /// </summary>
        private static GameObject _explosionPrefab;

        private static string _gunBurstEvent;

        private static bool _gunBurstEventResolved;

        private static bool _gunBurstEventWarned;

        private static bool _sampleLogged;

        /// <summary>
        /// A runtime hardpoint prefab able to deliver the requested attack type, or null.
        ///
        /// GunRun and AirToGroundMissile are the two types this factory builds: GHPC ships no gun
        /// hardpoint at all, and no air-to-ground missile payload either (the A-10's Mavericks are
        /// pylon decoration and GHPC ships no separate Soviet missile asset), so both are constructed here.
        /// Bombs and rockets are mounted from the game's own hardpoint prefabs instead (see
        /// CustomSlotBuilder.TrySynthesizePayload), so they are refused outright.
        /// </summary>
        internal static GameObject EnsureWeapon(CASAttackType type, string airframeName, float accuracy)
        {
            if (type == CASAttackType.AirToGroundMissile)
            {
                return EnsureMissile(airframeName, accuracy);
            }

            if (type != CASAttackType.GunRun)
            {
                return null; // only the gun run and the AGM are synthesized (see the doc comment)
            }

            CasAirframeCatalog.GunProfile profile = UnifiedGun;
            string key = type + "/" + profile.GunId + "/disp" + accuracy.ToString("0.###");
            GameObject existing;
            if (_built.TryGetValue(key, out existing) && existing != null)
            {
                return existing;
            }

            AmmoCodexScriptable codex;
            if (!_ammo.TryGetValue(key, out codex))
            {
                codex = FindAmmo(type, profile);

                if (codex != null)
                {
                    // Only cache successes: a later mission may load the exact round.
                    _ammo[key] = codex;
                }
                else if (_warnedMissingAmmo.Add(key))
                {
                    Log.Warn("CAS payload factory: no loaded ammo matches '" + profile.GunId + "' (hints: " +
                             string.Join(", ", profile.AmmoHints) + ") for " + type + " on '" + airframeName +
                             "'; that attack type stays unavailable here.");
                    LogLoadedSample(profile);
                    return null;
                }
                else
                {
                    return null;
                }
            }

            try
            {
                GameObject go = Build(type, profile, codex, accuracy);
                AttachGunBelt(go, airframeName, codex);
                _built[key] = go;
                Log.Info("CAS payload factory: built runtime " + type + " hardpoint '" + profile.GunId +
                         "' for '" + airframeName + "' (donor round '" +
                         (codex.AmmoType != null ? codex.AmmoType.Name : codex.name) +
                         "', accuracy " + accuracy.ToString("0.##") + " = " +
                         AccuracyRadius(accuracy).ToString("0.##") + " m impact circle).");
                return go;
            }
            catch (Exception ex)
            {
                Log.Error("CAS payload factory: failed to build " + type + " hardpoint '" + profile.GunId + "': " + ex);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Air to ground missile: the game's missile model + a bomb's data
        // ------------------------------------------------------------------

        /// <summary>Missile ammo built by this factory, so the patches can recognise our own rounds.</summary>
        private static readonly WeakIdentitySet<AmmoType> _ourMissiles = new WeakIdentitySet<AmmoType>();

        /// <summary>Once-per-key warnings for a missile payload that cannot be assembled.</summary>
        private static readonly HashSet<string> _warnedMissingMissile = new HashSet<string>();

        /// <summary>Cached result of the missile prefab lookup (the bundle is loaded once per session).</summary>
        private static readonly Dictionary<string, GameObject> _missilePrefabs = new Dictionary<string, GameObject>();

        /// <summary>One bomb donor per side (Nato / Pact / Unknown), looked up once each.</summary>
        private static readonly AmmoCodexScriptable[] _bombDonors = new AmmoCodexScriptable[3];
        private static readonly bool[] _bombDonorSearched = new bool[3];

        /// <summary>
        /// Builds (once per airframe / accuracy) the runtime air-to-ground missile hardpoint: the game's
        /// missile model carrying the TOW flight effects, firing an ammo CLONE of the mission's bomb, so
        /// ballistics, warhead, explosion effect and impact audio are the bomb's. Only the visible model
        /// and the (unguided, mod-flown) flight are the missile's own.
        /// </summary>
        private static GameObject EnsureMissile(string airframeName, float accuracy)
        {
            AirframeSide side = CasAirframeCatalog.GuessSide(airframeName);
            CasAirframeCatalog.MissileProfile profile = CasAirframeCatalog.MissileFor(side);
            string key = "AirToGroundMissile/" + profile.PrefabHint + "/disp" + accuracy.ToString("0.###");

            GameObject existing;
            if (_built.TryGetValue(key, out existing) && existing != null)
            {
                return existing;
            }

            AmmoCodexScriptable bomb = FindBombDonor(side);
            if (bomb == null)
            {
                if (_warnedMissingMissile.Add(key + "/bomb"))
                {
                    Log.Warn("CAS payload factory: no loaded BOMB to take the missile's data from, so " +
                             "AirToGroundMissile stays unavailable" +
                             (string.IsNullOrEmpty(airframeName) ? string.Empty : " on '" + airframeName + "'") +
                             ". A mission with a bomb hardpoint (Mk-82 / FAB-250) or the bundled loadouts is needed.");
                }
                return null;
            }

            GameObject visual = FindMissilePrefab(profile.PrefabHint);
            if (visual == null)
            {
                if (_warnedMissingMissile.Add(key + "/prefab"))
                {
                    Log.Warn("CAS payload factory: the bundle does not carry the '" + profile.PrefabHint +
                             "' missile prefab (rebuild cas_assets with CasBundleBuilder), so " +
                             "AirToGroundMissile stays unavailable.");
                }
                return null;
            }

            try
            {
                GameObject go = BuildMissileHardpoint(profile, bomb, visual, accuracy);
                _built[key] = go;
                Log.Info("CAS payload factory: built runtime AirToGroundMissile hardpoint for '" +
                         airframeName + "' - model '" + visual.name + "' (" + profile.MissileId + ", pinned to " +
                         profile.Airframe + "), TOW flight effects, data cloned from bomb '" +
                         (bomb.AmmoType != null ? bomb.AmmoType.Name : bomb.name) + "' (TNT " +
                         bomb.AmmoType.TntEquivalentKg.ToString("0.#") + " kg, accuracy " +
                         accuracy.ToString("0.##") + " = " + AccuracyRadius(accuracy).ToString("0.##") +
                         " m impact circle).");
                return go;
            }
            catch (Exception ex)
            {
                Log.Error("CAS payload factory: failed to build the AirToGroundMissile hardpoint: " + ex);
                return null;
            }
        }

        /// <summary>
        /// The bomb whose data the missile borrows, for one side. Preferring the ammo of a loaded BOMB
        /// hardpoint means this is literally the bomb the aircraft would have dropped, with its own
        /// explosion effect, decal and impact audio attached - and picking the SIDE'S bomb keeps the two
        /// factions apart (a Red sortie clones the Soviet 250 kg FAB, a Blue one the US Mk-82), the same
        /// way the artillery and the smoke shells are faction specific.
        /// </summary>
        private static AmmoCodexScriptable FindBombDonor(AirframeSide side)
        {
            if (_bombDonorSearched[(int)side])
            {
                return _bombDonors[(int)side];
            }
            _bombDonorSearched[(int)side] = true;

            List<GameObject> hardpoints = CasAttackLibrary.AllFor(CASAttackType.Bombs);
            AmmoCodexScriptable first = null;
            AmmoCodexScriptable best = null;
            int bestScore = 0;

            for (int i = 0; i < hardpoints.Count; i++)
            {
                CASHardpoint hardpoint = hardpoints[i] != null
                    ? hardpoints[i].GetComponentInChildren<CASHardpoint>(true)
                    : null;
                if (hardpoint == null)
                {
                    continue;
                }
                AmmoCodexScriptable codex = AmmoRef(hardpoint);
                if (codex == null || codex.AmmoType == null)
                {
                    continue;
                }
                if (first == null)
                {
                    first = codex;
                }

                // A bomb, not a rocket or a gun pod that happened to be typed as one.
                if (codex.AmmoType.TntEquivalentKg < 20f)
                {
                    continue;
                }

                int score = 1;
                string name = (codex.name + " " + codex.AmmoType.Name).ToLowerInvariant();
                AirframeSide bombSide = CasAirframeCatalog.GuessBombSide(name);
                if (bombSide == side && side != AirframeSide.Unknown)
                {
                    score += 100;                       // the side's own bomb wins
                }
                else if (bombSide != AirframeSide.Unknown && side != AirframeSide.Unknown)
                {
                    score -= 50;                        // the other side's bomb is a fallback
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = codex;
                }
            }

            _bombDonors[(int)side] = best != null ? best : first;
            if (_bombDonors[(int)side] == null)
            {
                // No bomb hardpoint in this mission's content: fall back to the heaviest explosive round
                // loaded, which is what a bomb data set looks like (a large TNT equivalent).
                _bombDonors[(int)side] = HeaviestExplosiveRound();
            }
            return _bombDonors[(int)side];
        }

        /// <summary>Fallback donor: the explosive round with the largest TNT equivalent in memory.</summary>
        private static AmmoCodexScriptable HeaviestExplosiveRound()
        {
            AmmoCodexScriptable best = null;
            AmmoCodexScriptable[] codexes = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
            float bestTnt = 5f;
            for (int i = 0; i < codexes.Length; i++)
            {
                AmmoCodexScriptable codex = codexes[i];
                if (codex == null || codex.AmmoType == null || codex.AmmoType.ShotVisual == null)
                {
                    continue;
                }
                if (codex.AmmoType.Category != AmmoType.AmmoCategory.Explosive ||
                    codex.AmmoType.TntEquivalentKg <= bestTnt)
                {
                    continue;
                }
                string lower = (codex.name + " " + codex.AmmoType.Name).ToLowerInvariant();
                if (lower.Contains("grenade") || lower.Contains("smoke") || lower.Contains("flare"))
                {
                    continue;
                }
                bestTnt = codex.AmmoType.TntEquivalentKg;
                best = codex;
            }
            return best;
        }

        /// <summary>
        /// The composed missile prefab (CasMissileComposer put the game's missile model together with the
        /// TOW flight effects). Looks in our bundle first - that is where it ships - and then in anything
        /// else loaded, so a future asset with the same name is picked up too.
        /// </summary>
        private static GameObject FindMissilePrefab(string hint)
        {
            if (string.IsNullOrEmpty(hint))
            {
                return null;
            }

            GameObject cached;
            if (_missilePrefabs.TryGetValue(hint, out cached))
            {
                return cached != null ? cached : null;
            }

            GameObject found = null;
            List<GameObject> bundle = CasPrewarmer.BundlePrefabs;
            for (int i = 0; i < bundle.Count && found == null; i++)
            {
                GameObject prefab = bundle[i];
                if (prefab != null && prefab.name.ToLowerInvariant().Contains(hint))
                {
                    found = prefab;
                }
            }

            // No "search everything loaded" fallback on purpose: Resources.FindObjectsOfTypeAll<GameObject>()
            // also hands back wrappers whose native object is already gone (the previous mission's scene and
            // the assets addressables released with it), and reading a property on one of them is a native
            // access violation that kills the process - see FireSupportTemplates.FindArtilleryEffectPrefab.
            // The composed missile prefab ships in our own bundle, which is pinned for the whole session.

            _missilePrefabs[hint] = found;
            return found;
        }

        /// <summary>
        /// The speed the mod's flight resolver PINS a round of ours to, in m/s (0 = no pin; the round keeps
        /// its own current speed). An air-to-ground missile flies its WHOLE flight at this speed - the
        /// profile's Mach 1.2 - so its speed no longer depends on how fast the launching aircraft was
        /// going. Both sides' missile profiles use the same figure, so the value can be taken from either.
        /// </summary>
        internal static float CruiseSpeedFor(AmmoType ammo)
        {
            return IsOurMissile(ammo) ? CasAirframeCatalog.NatoMissile.CruiseSpeedMeters : 0f;
        }

        /// <summary>
        /// Plays an impact sound of the given type, forced to the FUZED variant. The game downgrades the
        /// sound of a round that did not fuze (a bomb's detonation becomes a kinetic clang, see
        /// ImpactSFXManager.PlaySimpleImpactAudio) - for a high-explosive missile that would mean "an
        /// explosion you cannot hear", so the mod plays the warhead's own explosion when that happens.
        /// </summary>
        internal static void PlayFuzedImpactAudio(AmmoType ammo, Vector3 position)
        {
            if (ammo == null)
            {
                return;
            }
            GHPC.Audio.ImpactSFXManager manager = GHPC.Audio.ImpactSFXManager.Instance;
            if (manager == null)
            {
                return;
            }
            manager.PlaySimpleImpactAudio(ammo.ImpactAudio, position, true);
        }

        /// <summary>
        /// True for the hardpoint of a missile payload that ALWAYS hits the locked target's centre,
        /// whatever SlotN_CasAccuracy is set to (see MissileProfile.GuaranteedHit). Only the mod's own
        /// air-to-ground missile behaves this way; the game's own missiles keep their own guidance.
        /// </summary>
        internal static bool IsGuaranteedHit(CASHardpoint hardpoint)
        {
            if (hardpoint == null || hardpoint.Type != CASAttackType.AirToGroundMissile ||
                !IsRuntimeHardpoint(hardpoint))
            {
                return false;
            }
            // Both sides' profiles are guaranteed-hit; ask the side's profile through the ammo's own name
            // so a future profile can opt out without touching this method.
            AirframeSide side = CasAirframeCatalog.GuessSide(hardpoint.Ammo != null ? hardpoint.Ammo.Name : null);
            return CasAirframeCatalog.MissileFor(side).GuaranteedHit;
        }

        /// <summary>
        /// The impact point offset for one round: a draw from the slot's impact circle, or EXACTLY the
        /// target's own centre for a guaranteed-hit missile (CasAccuracy is then irrelevant by design).
        /// </summary>
        internal static Vector3 ImpactOffsetFor(CASHardpoint hardpoint, float accuracy)
        {
            return IsGuaranteedHit(hardpoint) ? Vector3.zero : SampleImpactOffset(accuracy);
        }

        /// <summary>
        /// The hardpoint itself: the bomb's ammo cloned with the missile's model as its ShotVisual, a
        /// muzzle transform, and the launch parameters of the side's missile profile. Kept unguided so the
        /// mod's own impact resolver flies it - onto the target's centre, since the profile is a
        /// guaranteed-hit payload.
        /// </summary>

        private static GameObject BuildMissileHardpoint(CasAirframeCatalog.MissileProfile profile,
            AmmoCodexScriptable bomb, GameObject visual, float accuracyScale)
        {
            AmmoType ammo = CloneAmmoType(bomb.AmmoType);
            ammo.Name = profile.MissileId;
            ammo.ShotVisual = visual;                  // the model + TOW flight effects
            ammo.VisualModel = null;                   // no AAR/ammo-rack model to show
            ammo.Guidance = AmmoType.GuidanceType.Unguided;
            ammo.Flight = AmmoType.FlightPattern.Direct;
            // A missile's own ammo carries UseTracer = true (the game's BGM-71C I-TOW does): the visual
            // carries the TOW's engine light and tracer, and the game switches them on from this flag.
            ammo.UseTracer = true;
            ammo.ArmingDistance = 0f;
            // FLIGHT PROFILE. The bomb the data came from is a gravity bomb: MuzzleVelocity 0 and a blunt
            // body's drag. Fired as a missile that is far too slow, so the missile gets its own motor
            // boost (added to the aircraft's speed by CASHardpoint.SpawnMunition) and a slimmer drag
            // coefficient. Damage, warhead, explosion and sound stay the bomb's.
            ammo.MuzzleVelocity = profile.BoostVelocityMeters;
            ammo.Coeff = Mathf.Clamp(bomb.AmmoType.Coeff * profile.DragScale, 0.02f, 2f);
            // Custom = the round's visual is ours: the marshaller destroys it instead of pooling it with
            // the bombs (which would hand a later bomb a missile model).
            ammo.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Custom;
            // Everything else - Category / ShortName / TntEquivalentKg / RhaPenetration / Mass / Coeff /
            // SectionalArea / spall / fuzes / impact effect descriptor / decal / ImpactAudio - stays the
            // bomb's, which is what "the missile uses the bomb's data" means.

            AmmoCodexScriptable codex = ScriptableObject.CreateInstance<AmmoCodexScriptable>();
            codex.name = profile.MissileId;
            codex.AmmoType = ammo;
            _ourMissiles.Add(ammo);

            GameObject go = new GameObject("CFS " + CASAttackType.AirToGroundMissile + " " + profile.PrefabHint +
                                           " (runtime hardpoint) [acc=" + accuracyScale.ToString("0.###") + "]");
            CASHardpoint hardpoint = go.AddComponent<CASHardpoint>();

            TypeRef(hardpoint) = CASAttackType.AirToGroundMissile;
            AmmoRef(hardpoint) = codex;
            AudioEventRef(hardpoint) = BombAudioEvent();
            // Only a base deviation: the launch direction is overridden on the round's first frame by
            // CasImpactAimPatch, which flies it to the impact point (see UsesImpactResolver).
            DeviationRef(hardpoint) = profile.DeviationDegrees;
            InheritVelocityRef(hardpoint) = true;      // a missile leaves with the aircraft's speed
            VisibleMunitionsRef(hardpoint) = false;
            MunitionCountRef(hardpoint) = profile.Munitions;
            LaunchCountRef(hardpoint) = 1;

            GameObject muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(go.transform, false);
            muzzle.transform.localPosition = Vector3.zero;
            muzzle.transform.localRotation = Quaternion.identity;
            SpawnPointRef(hardpoint) = muzzle.transform;

            VerifyMissileHardpoint(hardpoint, profile, bomb, visual, codex);
            return go;
        }

        /// <summary>
        /// Reads the payload back and reports whether it really is "the game's missile model, flown by the
        /// mod, carrying a BOMB's data". This is written to the log at mission start, so one session log
        /// proves the assembly without anyone having to inspect the aircraft: every field the game needs is
        /// checked through the accessors the game itself uses.
        /// </summary>
        private static void VerifyMissileHardpoint(CASHardpoint hardpoint, CasAirframeCatalog.MissileProfile profile,
            AmmoCodexScriptable bomb, GameObject visual, AmmoCodexScriptable codex)
        {
            List<string> problems = new List<string>();

            if (hardpoint.Type != CASAttackType.AirToGroundMissile)
            {
                problems.Add("type is " + hardpoint.Type);
            }
            if (AmmoRef(hardpoint) != codex && hardpoint.Ammo == null)
            {
                problems.Add("no ammo");
            }

            AmmoType ammo = hardpoint.Ammo;
            if (ammo == null)
            {
                problems.Add("ammo type is null");
            }
            else
            {
                if (!ReferenceEquals(ammo.ShotVisual, visual))
                {
                    problems.Add("ShotVisual is not the missile model");
                }
                if (bomb != null && bomb.AmmoType != null)
                {
                    if (ReferenceEquals(ammo.ShotVisual, bomb.AmmoType.ShotVisual))
                    {
                        problems.Add("ShotVisual is still the bomb's");
                    }
                    if (ammo.TntEquivalentKg != bomb.AmmoType.TntEquivalentKg)
                    {
                        problems.Add("TNT " + ammo.TntEquivalentKg.ToString("0.#") + " kg is not the bomb's " +
                                     bomb.AmmoType.TntEquivalentKg.ToString("0.#") + " kg");
                    }
                    if (ammo.ImpactAudio != bomb.AmmoType.ImpactAudio)
                    {
                        problems.Add("impact audio is not the bomb's");
                    }
                    if (ammo.Category != bomb.AmmoType.Category || ammo.ShortName != bomb.AmmoType.ShortName)
                    {
                        problems.Add("warhead category/short name is not the bomb's");
                    }
                }
                if (ammo.Guidance != AmmoType.GuidanceType.Unguided)
                {
                    problems.Add("guidance is " + ammo.Guidance + ", the mod cannot fly it");
                }
            }

            if (hardpoint.TotalMunitionsCapacity != profile.Munitions)
            {
                problems.Add("munitions " + hardpoint.TotalMunitionsCapacity + " != " + profile.Munitions);
            }
            if (SpawnPointRef(hardpoint) == null)
            {
                problems.Add("no muzzle transform");
            }
            if (!hardpoint.InheritVelocity)
            {
                problems.Add("does not inherit the aircraft's speed (bomb data launches at 0 m/s)");
            }
            if (ammo != null && ammo.MuzzleVelocity < profile.BoostVelocityMeters - 1f)
            {
                problems.Add("no motor boost (MuzzleVelocity " + ammo.MuzzleVelocity.ToString("0") + " m/s)");
            }
            if (ammo != null && bomb != null && bomb.AmmoType != null &&
                ammo.Coeff >= bomb.AmmoType.Coeff - 0.0001f)
            {
                problems.Add("drag was not scaled down (Coeff " + ammo.Coeff.ToString("0.###") + ")");
            }

            if (problems.Count == 0)
            {
                Log.Info("CAS missile payload check: OK - " + profile.MissileId + " on " + profile.Airframe +
                         ", model '" + visual.name + "', data cloned from bomb '" +
                         (bomb != null && bomb.AmmoType != null ? bomb.AmmoType.Name : "?") + "' (TNT " +
                         (ammo != null ? ammo.TntEquivalentKg.ToString("0.#") : "?") + " kg, impact audio " +
                         (ammo != null ? ammo.ImpactAudio.ToString() : "?") + "), " +
                         hardpoint.TotalMunitionsCapacity + " missile(s), unguided + mod-flown, muzzle attached, " +
                         "motor boost " + (ammo != null ? ammo.MuzzleVelocity.ToString("0") : "?") +
                         " m/s over the aircraft's speed, drag x" + profile.DragScale.ToString("0.##") +
                         " (Coeff " + (ammo != null ? ammo.Coeff.ToString("0.###") : "?") + "), cruise floor " +
                         profile.CruiseSpeedMeters.ToString("0") + " m/s.");
            }
            else
            {
                Log.Warn("CAS missile payload check FAILED for " + profile.MissileId + ": " +
                         string.Join("; ", problems.ToArray()) +
                         " - report this line, the payload will not behave as intended.");
            }
        }

        /// <summary>The bomb hardpoint's own release audio, when the mission loaded one.</summary>
        private static string BombAudioEvent()
        {
            GameObject bombHardpoint = CasAttackLibrary.FirstFor(CASAttackType.Bombs);
            CASHardpoint hardpoint = bombHardpoint != null
                ? bombHardpoint.GetComponentInChildren<CASHardpoint>(true)
                : null;
            return hardpoint != null ? GetAudioEvent(hardpoint) : string.Empty;
        }

        /// <summary>
        /// Applies the missile payload's attack timing to its CASAttackMeta: one missile per trigger pull,
        /// a second apart, released further out than a bomb (a missile is a stand-off weapon). Called from
        /// CustomSlotBuilder when it synthesizes the AirToGroundMissile attack entry.
        /// </summary>
        internal static void ApplyMissileAttackProfile(CASAttackMeta meta, Faction airframeFaction)
        {
            if (meta == null)
            {
                return;
            }

            AirframeSide side = airframeFaction == Faction.Red ? AirframeSide.Pact : AirframeSide.Nato;
            CasAirframeCatalog.MissileProfile profile = CasAirframeCatalog.MissileFor(side);

            meta.TriggerPulls = Mathf.Max(1, profile.Munitions);
            meta.TriggerPullInterval = 1.1f;
            meta.FireAllAtOnce = false;
            meta.ApproachDistance = 3200f;
            meta.ReleaseDistance = profile.ReleaseDistanceMeters;
            meta.PreDelay = 0.5f;
            meta.PostDelay = 0.5f;
        }

        /// <summary>True for a missile round this factory built (reference match, not name match).</summary>
        internal static bool IsOurMissile(AmmoType ammo)
        {
            return _ourMissiles.Contains(ammo);
        }

        // ------------------------------------------------------------------
        // Real gun rounds, built by cloning a loaded 30mm round for its visuals / effects
        // ------------------------------------------------------------------

        /// <summary>
        /// Real GAU-8/A data. Penetration is the user's figure at 60 degrees obliquity (69 mm at 500 m,
        /// 38 mm at 1000 m) normalised to 0 degrees: 138 mm / 76 mm. GHPC's AmmoType carries a single
        /// RhaPenetration with no range falloff, so the 500 m value is used.
        /// </summary>
        private static AmmoCodexScriptable BuildPgu14(AmmoCodexScriptable donor)
        {
            return BuildRound(donor, "PGU-14/B API", AmmoType.AmmoCategory.Penetrator, AmmoType.AmmoShortName.Ap,
                138f, 0.01f, 1010f, 0.425f, 0.09f, 0.000707f, 30f, 0.75f, 0.2f);
        }

        /// <summary>PGU-13/B HEI: 48 g HE filler, minimal penetration, no tracer.</summary>
        private static AmmoCodexScriptable BuildPgu13(AmmoCodexScriptable donor)
        {
            return BuildRound(donor, "PGU-13/B HEI", AmmoType.AmmoCategory.Explosive, AmmoType.AmmoShortName.He,
                10f, 0.048f, 1010f, 0.36f, 0.10f, 0.000707f, 30f, 1f, 0.6f);
        }

        /// <summary>OFZ-30 HEI: the Su-22 gun-pod high-explosive incendiary round.</summary>
        private static AmmoCodexScriptable BuildOfz30(AmmoCodexScriptable donor)
        {
            return BuildRound(donor, "OFZ-30 HEI", AmmoType.AmmoCategory.Explosive, AmmoType.AmmoShortName.He,
                12f, 0.048f, 940f, 0.39f, 0.10f, 0.000707f, 30f, 1f, 0.6f);
        }

        /// <summary>BR-30 AP: the Su-22 gun-pod armour-piercing fragmentation round (better than the game's 30mm AP).</summary>
        private static AmmoCodexScriptable BuildBr30(AmmoCodexScriptable donor)
        {
            return BuildRound(donor, "BR-30 AP", AmmoType.AmmoCategory.Penetrator, AmmoType.AmmoShortName.Ap,
                80f, 0.008f, 940f, 0.4f, 0.09f, 0.000707f, 30f, 0.75f, 0.2f);
        }

        /// <summary>
        /// Builds one of the mod's own rounds by cloning a loaded donor (for ShotVisual / impact
        /// effects / audio) and overwriting every ballistic field. The clone is kept alive by the belt
        /// that references it.
        /// </summary>
        private static AmmoCodexScriptable BuildRound(AmmoCodexScriptable donor, string name,
            AmmoType.AmmoCategory category, AmmoType.AmmoShortName shortName,
            float rhaPenetration, float tntKg, float muzzleVelocity, float mass, float coeff,
            float sectionalArea, float caliber, float spallMultiplier, float microFrag)
        {
            if (donor == null || donor.AmmoType == null)
            {
                return null;
            }

            AmmoType ammo = CloneAmmoType(donor.AmmoType);
            ammo.Name = name;
            ammo.Category = category;
            ammo.ShortName = shortName;
            ammo.RhaPenetration = rhaPenetration;
            ammo.TntEquivalentKg = tntKg;
            ammo.MuzzleVelocity = muzzleVelocity;
            ammo.Mass = mass;
            ammo.Coeff = coeff;
            ammo.SectionalArea = sectionalArea;
            ammo.Caliber = caliber;
            ammo.SpallMultiplier = spallMultiplier;
            ammo.MicroFragScaling = microFrag;
            ammo.UseTracer = false;                       // the user asked for no tracer rounds
            ammo.DoScabEffect = category == AmmoType.AmmoCategory.Explosive;
            ammo.MaximumRange = 4000f;
            ammo.Guidance = AmmoType.GuidanceType.Unguided;
            ammo.Flight = AmmoType.FlightPattern.Direct;
            // CachedIndex is deliberately left at the donor's value: the impact effect / decal caches
            // are keyed by it, so the clone reuses the donor's effects (the hit effects are copied).
            // A donor that had no cache yet (-1) makes the clone register lazily from its descriptor.

            AmmoCodexScriptable codex = ScriptableObject.CreateInstance<AmmoCodexScriptable>();
            codex.name = name;
            codex.AmmoType = ammo;
            _ourRounds.Add(ammo);
            return codex;
        }

        /// <summary>True for an AmmoType this factory built (reference match, not name match).</summary>
        internal static bool IsOurRound(AmmoType ammo)
        {
            return _ourRounds.Contains(ammo);
        }

        // ------------------------------------------------------------------
        // Impact resolution: SlotN_CasAccuracy IS the impact circle
        // ------------------------------------------------------------------

        /// <summary>
        /// Radius in metres of the impact circle at SlotN_CasAccuracy = 1.
        ///
        /// The knob used to scale the launch deviation / a per-weapon predicted-impact ellipse, which
        /// left the actual landing point up to the game's ballistics - and that is exactly where the
        /// accuracy kept leaking away. It is now a plain radius around the locked target, and a round
        /// that the mod flies itself (CasImpactAim) lands in it by construction:
        ///
        ///   CasAccuracy = 0    -> radius 0: every round is flown into the target's own centre, i.e. a
        ///                         guaranteed hit on the hull, never a near miss.
        ///   CasAccuracy = 0.5  -> radius 5 m.
        ///   CasAccuracy = 1    -> radius 10 m (the "落点圆").
        ///   CasAccuracy &gt; 1    -> wider.
        /// </summary>
        internal static float AccuracyRadius(float accuracy)
        {
            return SlotConfigParsing.CasAccuracyRadius(accuracy);
        }

        /// <summary>
        /// One round's impact point, as an offset from the target centre: an independent uniform draw in
        /// the HORIZONTAL disc of radius AccuracyRadius(accuracy) - the "落点圆". A zero
        /// radius returns exactly zero, i.e. this round is aimed at the target's own centre.
        ///
        /// Every round is drawn from scratch (no memory of the previous one) and the draw is uniform per
        /// unit area, so a burst peppers the whole circle instead of clustering in the middle of it.
        /// </summary>
        internal static Vector3 SampleImpactOffset(float accuracy)
        {
            float radius = AccuracyRadius(accuracy);
            if (radius <= 0.001f)
            {
                return Vector3.zero;
            }

            float angle = UnityEngine.Random.value * Mathf.PI * 2f;
            float distance = Mathf.Sqrt(UnityEngine.Random.value) * radius;
            return new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
        }

        /// <summary>
        /// Offset (metres, horizontal plane) within which a round is still aimed at the target's CENTRE
        /// HEIGHT rather than at the ground under it. The impact circle is a circle on the ground, but a
        /// round aimed at a point 1.5 m up in the air would sail past it and land several metres further
        /// on (the "downrange stretch"), so only the points that are meant to HIT the vehicle - the ones
        /// inside its own footprint - use the centre height. Everything outside lands exactly where the
        /// draw put it, on the ground, inside the circle. See ImpactPointFor.
        /// </summary>
        private const float OnTargetHeightRadius = 2f;

        /// <summary>
        /// The world point this round is being flown to, re-read live: the locked target's centre plus
        /// the round's own offset, at the target's centre height while that point is still on the
        /// vehicle (so CasAccuracy = 0 is a centre-mass hit) and at ground level once it is outside the
        /// vehicle's footprint (so a near miss really lands inside the drawn circle).
        /// </summary>
        internal static bool TryGetImpactPoint(CasImpactAim aim, out Vector3 point)
        {
            point = Vector3.zero;
            if (aim == null || aim.Target == null)
            {
                return false;
            }

            Vector3 centre = aim.Target.position;
            point = centre + aim.Offset;

            if (aim.Offset.sqrMagnitude > OnTargetHeightRadius * OnTargetHeightRadius)
            {
                // Unit.Center is a child of the unit sitting 1.5 m above its origin, which is the
                // vehicle's own ground reference - use it instead of guessing a terrain height.
                Transform unit = aim.Target.parent;
                if (unit != null)
                {
                    point.y = unit.position.y;
                }
            }
            return true;
        }

        /// <summary>
        /// True for the hardpoints whose rounds this mod flies itself (CasImpactAim): the gun runs,
        /// rocket pods AND bombs of our own sorties, plus our own air-to-ground missile payload. The
        /// game's own missiles (vanilla hardpoints) keep the game's guidance.
        /// </summary>
        internal static bool UsesImpactResolver(CASHardpoint hardpoint)
        {
            if (hardpoint == null)
            {
                return false;
            }
            if (hardpoint.Type == CASAttackType.AirToGroundMissile)
            {
                // Our AGM payload is a runtime hardpoint fed by a bomb's data: fly it with the mod's own
                // resolver (straight into the impact circle) instead of the game's missile guidance.
                return IsRuntimeHardpoint(hardpoint) && IsOurSortie(hardpoint);
            }
            if (hardpoint.Type != CASAttackType.GunRun && hardpoint.Type != CASAttackType.Rockets &&
                hardpoint.Type != CASAttackType.Bombs)
            {
                return false; // anything else with guidance: the game's own guidance does the job
            }
            return IsRuntimeGun(hardpoint) || IsOurSortie(hardpoint);
        }

        /// <summary>
        /// True when the round is a FALLING one (a bomb) and therefore needs the gravity-aware terminal
        /// correction instead of the straight-line flight the gun bullets and rockets get.
        ///
        /// A bomb cannot be flown in a straight line at its own speed: its whole trajectory IS gravity
        /// (the release computer aims it, the fall brings it down), so zeroing the vertical velocity
        /// would leave it hanging in the air. CasImpactAimPatch predicts where the game's own ballistic
        /// model will drop it and nudges only the horizontal velocity, so the bomb keeps its arc and
        /// still lands on the point.
        /// </summary>
        internal static bool IsGravityAware(CASHardpoint hardpoint)
        {
            return hardpoint != null && hardpoint.Type == CASAttackType.Bombs;
        }

        // ------------------------------------------------------------------
        // Per-round impact point (stash consumed by LiveRound.Init)
        // ------------------------------------------------------------------

        private static bool _pendingImpact;
        private static Transform _pendingTarget;
        private static Vector3 _pendingOffset;
        private static AmmoType _pendingAmmo;
        private static bool _pendingGravityAware;

        /// <summary>
        /// Records the impact point of the round about to be spawned. CASHardpoint.SpawnMunition does
        /// not hand out the round it creates, but it does call LiveRound.Init on it before returning, so
        /// the Init postfix (CasImpactAttachPatch) picks this up and attaches CasImpactAim to that round.
        ///
        /// The ammo is stashed as well: if SpawnMunition ever returns without calling Init (or throws),
        /// a stale stash may otherwise be claimed by the next round of any other hardpoint, and the
        /// Init postfix only accepts a round firing exactly this ammo.
        /// </summary>
        internal static void SetPendingImpact(Transform target, Vector3 offset, AmmoType ammo, bool gravityAware)
        {
            _pendingImpact = target != null && ammo != null;
            _pendingTarget = target;
            _pendingOffset = offset;
            _pendingAmmo = ammo;
            _pendingGravityAware = gravityAware;
        }

        internal static void ClearPendingImpact()
        {
            _pendingImpact = false;
            _pendingTarget = null;
            _pendingOffset = Vector3.zero;
            _pendingAmmo = null;
            _pendingGravityAware = false;
        }

        /// <summary>Hands the stash to the round that just finished LiveRound.Init, if it is that round.</summary>
        internal static bool ConsumePendingImpact(AmmoType ammo, out Transform target, out Vector3 offset,
            out bool gravityAware)
        {
            bool mine = _pendingImpact && _pendingTarget != null && ammo != null &&
                        ReferenceEquals(ammo, _pendingAmmo);
            target = mine ? _pendingTarget : null;
            offset = mine ? _pendingOffset : Vector3.zero;
            gravityAware = mine && _pendingGravityAware;
            ClearPendingImpact();
            return mine;
        }

        /// <summary>
        /// The CasSlot whose airframe this hardpoint / hardpoint manager belongs to, or false when it
        /// cannot be resolved (vanilla / enemy aircraft, or a sortie spawned before the registry knew
        /// about it).
        /// </summary>
        internal static bool TryGetOwningSlot(Component context, out CasSlot slot)
        {
            slot = null;
            return context != null && TryGetOwningSlot(context.GetComponentInParent<CASController>(), out slot);
        }

        /// <summary>Same, for a controller the caller has already resolved (avoids a second hierarchy walk).</summary>
        internal static bool TryGetOwningSlot(CASController controller, out CasSlot slot)
        {
            slot = null;
            if (controller == null || controller.casManager == null)
            {
                return false;
            }

            CasAirframeUnit[] array = controller.unitFaction == Faction.Red
                ? controller.casManager.RedCasAirframes
                : controller.casManager.BlueCasAirframes;
            if (array == null || controller.frameNum < 0 || controller.frameNum >= array.Length)
            {
                return false;
            }
            return CustomSupportRegistry.TryGetCasSlot(array[controller.frameNum], out slot);
        }

        /// <summary>
        /// The slot's CasAccuracy, which is the single accuracy knob for our CAS weapons: the radius of
        /// the impact circle divided by five (AccuracyRadius), i.e. 0 = every round into the target's
        /// own centre (a guaranteed hit), 1 = a 5 m circle, 0.5 = 2.5 m, > 1 = wider. Falls back to the
        /// value baked into a runtime hardpoint's name when the owning slot cannot be resolved.
        /// </summary>
        internal static float SlotAccuracy(Component context)
        {
            CASHardpoint hardpoint = context as CASHardpoint;
            CasSlot slot;
            if (TryGetOwningSlot(context, out slot))
            {
                return AccuracyFromSlot(slot, hardpoint);
            }
            return hardpoint != null ? GetBakedAccuracyScale(hardpoint) : 1f;
        }

        /// <summary>Same, for a controller the caller has already resolved.</summary>
        internal static float SlotAccuracy(CASController controller, CASHardpoint hardpoint)
        {
            CasSlot slot;
            if (TryGetOwningSlot(controller, out slot))
            {
                return AccuracyFromSlot(slot, hardpoint);
            }
            return hardpoint != null ? GetBakedAccuracyScale(hardpoint) : 1f;
        }

        private static float AccuracyFromSlot(CasSlot slot, CASHardpoint hardpoint)
        {
            if (slot != null && slot.Config != null)
            {
                return slot.Config.CasAccuracy;
            }
            return hardpoint != null ? GetBakedAccuracyScale(hardpoint) : 1f;
        }

        /// <summary>True when the hardpoint belongs to an aircraft this mod summoned.
        ///
        /// The marker component is present for every sortie the mod builds (CasSetLoadoutManagerPatch
        /// adds it when the call was routed to one of our airframes); the spawn bookkeeping
        /// (casManager + frameNum -> CasAirframeUnit) is the fallback for anything spawned before that
        /// patch could run.
        /// </summary>
        internal static bool IsOurSortie(CASHardpoint hardpoint)
        {
            if (hardpoint == null)
            {
                return false;
            }
            if (hardpoint.GetComponentInParent<CustomCasMarker>() != null)
            {
                return true;
            }

            CASController controller = hardpoint.GetComponentInParent<CASController>();
            if (controller == null || controller.casManager == null)
            {
                return false;
            }

            CasAirframeUnit[] array = controller.unitFaction == Faction.Red
                ? controller.casManager.RedCasAirframes
                : controller.casManager.BlueCasAirframes;
            if (array == null || controller.frameNum < 0 || controller.frameNum >= array.Length)
            {
                return false;
            }
            return CustomSupportRegistry.IsOurAirframe(array[controller.frameNum]);
        }

        /// <summary>
        /// The game's own high-explosive autocannon round, used as the donor for the mod's HE rounds.
        ///
        /// This is what makes a strafe look and sound right: GHPC's 30mm HE round (3UOR6) carries the
        /// HIGH EXPLOSIVE impact effect descriptor (AutocannonImpactExplosive*), the
        /// "Explosion_AutoCanon" impact audio and the 30mm HE detonation prefabs. Cloning the AP round
        /// instead (as the old single-donor path did) gave the HE rounds a KINETIC spark effect and a
        /// kinetic "crack" sound - i.e. no explosion at all.
        /// </summary>
        private static AmmoCodexScriptable FindExplosiveDonor(AmmoCodexScriptable fallback)
        {
            string[] hints = { "3uor6", "3uof", "30mm he", "he-t", "hei", "he-frag", "hef", "he-i" };

            AmmoCodexScriptable best = null;
            int bestScore = 0;
            AmmoCodexScriptable[] codexes = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
            for (int i = 0; i < codexes.Length; i++)
            {
                AmmoCodexScriptable codex = codexes[i];
                if (codex == null || codex.AmmoType == null)
                {
                    continue;
                }
                AmmoType ammo = codex.AmmoType;
                if (ammo.ShotVisual == null || ammo.Category != AmmoType.AmmoCategory.Explosive ||
                    ammo.TntEquivalentKg <= 0f || ammo.Guidance != AmmoType.GuidanceType.Unguided ||
                    ammo.Caliber < 15f || ammo.Caliber > 45f)
                {
                    continue;
                }

                string lower = (ammo.Name ?? codex.name ?? string.Empty).ToLowerInvariant();
                int score = 1;
                for (int h = 0; h < hints.Length; h++)
                {
                    if (lower.Contains(hints[h]))
                    {
                        score += 1000;
                    }
                }
                if (Mathf.Abs(ammo.Caliber - 30f) < 0.6f)
                {
                    score += 200; // the same calibre as the gun we are modelling
                }
                if (ammo.DetonateEffect != null)
                {
                    score += 50;
                }
                if (ammo.ImpactEffectDescriptor.HasImpactEffect)
                {
                    score += 25;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = codex;
                }
            }

            if (best != null && best.AmmoType != null)
            {
                Log.Verbose("CAS payload factory: HE donor round = '" + best.AmmoType.Name +
                            "' (category " + best.AmmoType.Category + ", impact effect " +
                            best.AmmoType.ImpactEffectDescriptor.ImpactCategory + "/" +
                            best.AmmoType.ImpactEffectDescriptor.EffectSize + ", impact audio " +
                            best.AmmoType.ImpactAudio + ").");
                return best;
            }

            Log.Warn("CAS payload factory: no loaded high-explosive autocannon round found for the gun " +
                     "belt; the HE rounds will reuse the AP donor and therefore have no explosion effect.");
            return fallback;
        }

        /// <summary>
        /// Gives a runtime gun hardpoint its belt: the real GAU-8/A mix (4x PGU-14/B API + 1x
        /// PGU-13/B HEI) for NATO airframes, the Su-22 gun-pod mix (2x OFZ-30 HEI + 1x BR-30 AP) for
        /// Pact ones. AdvanceBelt() cycles it once per trigger pull.
        ///
        /// The AP rounds clone the game's 30mm AP round, the HE rounds the game's 30mm HE round, so
        /// each round carries exactly the impact effects, decals and audio of its real counterpart.
        /// </summary>
        private static void AttachGunBelt(GameObject hardpoint, string airframeName, AmmoCodexScriptable apDonor)
        {
            if (hardpoint == null)
            {
                return;
            }

            AmmoCodexScriptable heDonor = FindExplosiveDonor(apDonor);

            AmmoCodexScriptable[] belt;
            if (CasAirframeCatalog.GuessSide(airframeName) == AirframeSide.Pact)
            {
                AmmoCodexScriptable hei = BuildOfz30(heDonor);
                AmmoCodexScriptable ap = BuildBr30(apDonor);
                if (hei == null || ap == null)
                {
                    Log.Warn("CAS payload factory: could not build the Su-22 gun-pod rounds; using the donor round.");
                    return;
                }
                belt = new[] { hei, hei, ap };
            }
            else
            {
                AmmoCodexScriptable api = BuildPgu14(apDonor);
                AmmoCodexScriptable hei = BuildPgu13(heDonor);
                if (api == null || hei == null)
                {
                    Log.Warn("CAS payload factory: could not build the GAU-8/A rounds; using the donor round.");
                    return;
                }
                belt = new[] { api, api, api, api, hei };
            }

            CasGunBelt component = hardpoint.AddComponent<CasGunBelt>();
            component.Rounds = belt;
            component.Index = 0;

            // Registered under the template's name: the clone Unity makes in
            // CASHardpointManager.SetUpHardpoints is named "<template> (Clone)" and can ask for its
            // belt back if the serializer dropped it (see AdvanceBelt).
            _belts[hardpoint.name] = belt;

            if (heDonor != null && heDonor.AmmoType != null)
            {
                _explosionPrefab = heDonor.AmmoType.DetonateEffect ?? heDonor.AmmoType.TerrainImpactEffect;
            }

            AttachGunAudio(hardpoint);

            CASHardpoint point = hardpoint.GetComponent<CASHardpoint>();
            if (point != null)
            {
                AmmoRef(point) = belt[0];
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < belt.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(" + ");
                }
                builder.Append(belt[i].AmmoType != null ? belt[i].AmmoType.Name : belt[i].name);
            }
            Log.Info("CAS payload factory: gun belt for '" + airframeName + "' = " + builder +
                     " (no tracers, 140 rounds, 3900 rpm).");
        }

        /// <summary>
        /// Gives the runtime gun its sustained fire sound: a StudioEventEmitter (the same component the
        /// game uses for aircraft engine audio) driven by the burst patch, so a strafe is heard as a
        /// continuous BRRRT instead of 140 barely audible pops from a kilometre away.
        ///
        /// Its attenuation is widened on purpose: a stock weapon event is mixed to fade out within a few
        /// hundred metres, which is silent for an aircraft that fires from more than a kilometre out.
        /// </summary>
        private static void AttachGunAudio(GameObject hardpoint)
        {
            if (hardpoint == null)
            {
                return;
            }

            string eventPath = ResolveGunBurstEvent();
            if (string.IsNullOrEmpty(eventPath))
            {
                return;
            }

            GameObject child = new GameObject("GunFireAudio");
            child.transform.SetParent(hardpoint.transform, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localRotation = Quaternion.identity;

            StudioEventEmitter emitter = child.AddComponent<StudioEventEmitter>();
            emitter.Event = eventPath;
            emitter.PlayEvent = EmitterGameEvent.None;      // driven by the burst patch, never automatic
            emitter.StopEvent = EmitterGameEvent.ObjectDestroy;
            emitter.TriggerOnce = false;
            emitter.AllowFadeout = true;
            emitter.OverrideAttenuation = true;
            emitter.OverrideMinDistance = GunAudioMinDistance;
            emitter.OverrideMaxDistance = GunAudioMaxDistance;
        }

        /// <summary>
        /// First candidate of GunBurstEventCandidates that FMOD actually knows, or null. The event names
        /// were read out of the game's own Master.strings.bank, but a wrong path would throw inside
        /// FMOD on every shot, so every candidate is checked once and the winner is logged.
        /// </summary>
        private static string ResolveGunBurstEvent()
        {
            if (_gunBurstEventResolved)
            {
                return _gunBurstEvent;
            }

            for (int i = 0; i < GunBurstEventCandidates.Length; i++)
            {
                string candidate = GunBurstEventCandidates[i];
                try
                {
                    EventDescription description = RuntimeManager.GetEventDescription(candidate);
                    if (description.isValid())
                    {
                        _gunBurstEvent = candidate;
                        _gunBurstEventResolved = true;
                        Log.Info("CAS gun audio: sustained fire event = '" + candidate + "'.");
                        return _gunBurstEvent;
                    }
                }
                catch (Exception)
                {
                    // EventNotFoundException: this candidate does not exist in the loaded banks.
                }
            }

            // Not cached: the first slot can be built before every bank is loaded, so a later build
            // gets another chance. The warning is still logged once.
            if (!_gunBurstEventWarned)
            {
                _gunBurstEventWarned = true;
                Log.Warn("CAS gun audio: none of the gun-fire events exist in the loaded FMOD banks (" +
                         string.Join(", ", GunBurstEventCandidates) +
                         "); a strafe will only have the per-round one-shots.");
            }
            return null;
        }

        /// <summary>The sustained-fire emitter of a runtime gun hardpoint, if it has one.</summary>
        internal static StudioEventEmitter FindGunEmitter(CASAttackMeta meta)
        {
            List<CASHardpoint> included = GunMetaHardpoints(meta);
            if (included == null)
            {
                return null;
            }
            for (int i = 0; i < included.Count; i++)
            {
                CASHardpoint hardpoint = included[i];
                if (!IsRuntimeGun(hardpoint))
                {
                    continue;
                }
                StudioEventEmitter emitter = hardpoint.GetComponentInChildren<StudioEventEmitter>();
                if (emitter != null)
                {
                    return emitter;
                }

                // The event could not be resolved when this hardpoint was built (banks still loading);
                // it is available now, so give the gun its emitter late.
                AttachGunAudio(hardpoint.gameObject);
                emitter = hardpoint.GetComponentInChildren<StudioEventEmitter>();
                if (emitter != null)
                {
                    return emitter;
                }
            }
            return null;
        }

        /// <summary>Swaps the hardpoint's ammo to the next round of its belt (called per trigger pull).</summary>
        internal static void AdvanceBelt(CASHardpoint hardpoint)
        {
            if (hardpoint == null)
            {
                return;
            }
            CasGunBelt belt = hardpoint.GetComponent<CasGunBelt>();
            if (belt == null)
            {
                belt = hardpoint.gameObject.AddComponent<CasGunBelt>();
            }
            if (belt.Rounds == null || belt.Rounds.Length == 0)
            {
                // Unity clones the hardpoint through its serializer, which only carries
                // [SerializeField] / public fields; if the belt did not come across, re-seat it from
                // the template the clone was made from ("<template name> (Clone)").
                AmmoCodexScriptable[] restored = FindRegisteredBelt(hardpoint.name);
                if (restored == null)
                {
                    return;
                }
                belt.Rounds = restored;
                belt.Index = 0;
            }
            belt.Index = (belt.Index + 1) % belt.Rounds.Length;
            AmmoRef(hardpoint) = belt.Rounds[belt.Index];
        }

        /// <summary>The belt registered for the template a clone was instantiated from, or null.</summary>
        private static AmmoCodexScriptable[] FindRegisteredBelt(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
            {
                return null;
            }
            foreach (KeyValuePair<string, AmmoCodexScriptable[]> entry in _belts)
            {
                if (!string.IsNullOrEmpty(entry.Key) &&
                    objectName.StartsWith(entry.Key, StringComparison.Ordinal))
                {
                    return entry.Value;
                }
            }
            return null;
        }

        /// <summary>Reads / writes the hardpoint's per-round FMOD one-shot (used to throttle the burst).</summary>
        internal static string GetAudioEvent(CASHardpoint hardpoint)
        {
            return hardpoint != null ? AudioEventRef(hardpoint) : null;
        }

        internal static void SetAudioEvent(CASHardpoint hardpoint, string audioEvent)
        {
            if (hardpoint != null)
            {
                AudioEventRef(hardpoint) = audioEvent;
            }
        }

        /// <summary>True when an attack meta belongs to one of our runtime gun runs.</summary>
        internal static bool IsOurGunMeta(CASAttackMeta meta)
        {
            if (meta == null || meta.UniqueType != CASAttackType.GunRun)
            {
                return false;
            }
            List<CASHardpoint> included = GunMetaHardpoints(meta);
            if (included == null)
            {
                return false;
            }
            for (int i = 0; i < included.Count; i++)
            {
                if (IsRuntimeGun(included[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly AccessTools.FieldRef<CASAttackMeta, List<CASHardpoint>> GunMetaHardpoints =
            AccessTools.FieldRefAccess<CASAttackMeta, List<CASHardpoint>>("_hardpointsIncluded");

        /// <summary>
        /// Guaranteed impact EXPLOSION for the mod's own rounds.
        ///
        /// The vanilla path (ParticleEffectsManager.CreateImpactEffectOfType) is tried first; this is
        /// only called when it produced nothing, so there are never two effects on one hit. It spawns
        /// the donor round's 30 mm HIGH EXPLOSIVE detonation prefab (the AP round's own effect is a
        /// tiny spark that is invisible from the distance a strafe is watched at), scaled up so the
        /// burst reads as a line of explosions.
        ///
        /// No audio here on purpose: LiveRound.doImpactEffect always plays Info.ImpactAudio before it
        /// calls the VFX, so a round's own impact sound - kinetic crack for the AP rounds, the game's
        /// "Explosion_AutoCanon" for the HEI rounds - is already playing. Adding another one would just
        /// double every impact sound of the burst.
        /// </summary>
        internal static void SpawnFallbackImpact(LiveRound round, bool terrainHit)
        {
            if (round == null || round.Info == null)
            {
                return;
            }
            if (round.gameObject.GetComponent<CasFallbackImpact>() != null)
            {
                return;
            }
            round.gameObject.AddComponent<CasFallbackImpact>();

            AmmoType info = round.Info;
            Vector3 position = round.transform.position;

            GameObject prefab = _explosionPrefab;
            if (prefab == null)
            {
                prefab = terrainHit ? info.TerrainImpactEffect : info.DetonateEffect;
            }
            if (prefab != null)
            {
                GameObject fx = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                fx.name = "CFS impact " + info.Name;
                fx.transform.localScale = fx.transform.localScale * FallbackExplosionScale;
                UnityEngine.Object.Destroy(fx, 15f);
            }

            Log.Verbose("CAS impact fallback: '" + info.Name + "' produced no vanilla impact effect (" +
                        (terrainHit ? "terrain" : "object") + " hit); spawned '" +
                        (prefab != null ? prefab.name : "no prefab") +
                        "' at x" + FallbackExplosionScale.ToString("0.##") + ".");
        }

        /// <summary>Shallow field-by-field copy of an AmmoType (all public instance fields).</summary>
        private static readonly FieldInfo[] AmmoFields = typeof(AmmoType).GetFields(BindingFlags.Public | BindingFlags.Instance);

        private static AmmoType CloneAmmoType(AmmoType donor)
        {
            AmmoType clone = new AmmoType();
            FieldInfo[] fields = AmmoFields;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsInitOnly)
                {
                    continue;
                }
                field.SetValue(clone, field.GetValue(donor));
            }
            return clone;
        }

        /// <summary>
        /// The single gun-run spec applied to both sides: 140 rounds, 3900 rpm (the real GAU-8/A rate).
        /// The rounds themselves are the mod's own (see BuildGunBelt): PGU-14/B API + PGU-13/B HEI for
        /// NATO airframes, OFZ-30 HEI + BR-30 AP for Pact ones.
        ///
        /// DeviationDegrees is the gun's NATURAL launch deviation. It is only the fallback value of the
        /// hardpoint now: CasAccuracyPatch zeroes the launch deviation at fire time, because the rounds
        /// are flown onto their impact point by the mod's own resolver (CasImpactAim). 0.58 deg is the
        /// real GAU-8/A dispersion ("80% of rounds inside a 6.1 m radius at 1200 m", i.e.
        /// dev = (6.1/1200 rad) / 0.5046), kept so the hardpoint looks sane in the inspector.
        /// </summary>
        private static readonly CasAirframeCatalog.GunProfile UnifiedGun = new CasAirframeCatalog.GunProfile
        {
            GunId = "GAU-8/A / GSh-30 30mm (unified gun run)",
            // Donor-round hints only: the actual belt is built by BuildGunBelt.
            AmmoHints = new[] { "30mm", "3ubr6", "3uor6" },
            Munitions = 140,
            DeviationDegrees = 0.58f,
            InheritVelocity = true,
            RateOfFireRPM = 3900f
        };

        /// <summary>
        /// Applies the unified gun's stream fire rate to the GunRun attack entry: one round per
        /// trigger pull, pulled at 60/rpm seconds apart, so the whole magazine streams out as a burst.
        /// Called from CustomSlotBuilder when building the synthesized GunRun attack meta.
        /// </summary>
        internal static void ApplyGunRateOfFire(CASAttackMeta meta)
        {
            if (meta == null || UnifiedGun.RateOfFireRPM <= 0f)
            {
                return;
            }
            meta.TriggerPulls = UnifiedGun.Munitions;
            meta.TriggerPullInterval = 60f / UnifiedGun.RateOfFireRPM;
        }

        /// <summary>True when the object is one of our runtime-built hardpoints (any attack type).</summary>
        internal static bool IsRuntimeHardpoint(CASHardpoint hardpoint)
        {
            return hardpoint != null &&
                   !string.IsNullOrEmpty(hardpoint.name) &&
                   hardpoint.name.Contains("runtime hardpoint");
        }

        /// <summary>True when the hardpoint is one of our runtime-built gun hardpoints.</summary>
        internal static bool IsRuntimeGun(CASHardpoint hardpoint)
        {
            return IsRuntimeHardpoint(hardpoint) && hardpoint.Type == CASAttackType.GunRun;
        }

        /// <summary>
        /// The slot's CasAccuracy baked into the object name at build time ("[acc=...]"): the
        /// impact-circle radius divided by five. Only a fallback - CasPayloadFactory.SlotAccuracy reads
        /// the owning slot live and falls back to this when the slot cannot be resolved.
        /// </summary>
        internal static float GetBakedAccuracyScale(CASHardpoint hardpoint)
        {
            return ReadNameFloat(hardpoint, "[acc=", 1f);
        }

        /// <summary>Reads a "&lt;marker&gt;&lt;number&gt;]" value out of a runtime hardpoint's name.</summary>
        private static float ReadNameFloat(CASHardpoint hardpoint, string marker, float fallback)
        {
            if (hardpoint == null || string.IsNullOrEmpty(hardpoint.name))
            {
                return fallback;
            }
            int start = hardpoint.name.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return fallback;
            }
            int end = hardpoint.name.IndexOf(']', start);
            if (end <= start)
            {
                return fallback;
            }
            float parsed;
            if (float.TryParse(hardpoint.name.Substring(start + marker.Length, end - start - marker.Length), out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        /// <summary>
        /// True when the factory can build this attack type: GunRun always (GHPC ships no gun hardpoint),
        /// the air-to-ground missile when this mission loaded a bomb to take the data from and the bundle
        /// carries the composed missile prefab.
        /// </summary>
        internal static bool CanBuild(CASAttackType type)
        {
            if (type == CASAttackType.GunRun)
            {
                return true;
            }
            if (type == CASAttackType.AirToGroundMissile)
            {
                return (FindBombDonor(AirframeSide.Nato) != null || FindBombDonor(AirframeSide.Pact) != null) &&
                       FindMissilePrefab("cfs missile") != null;
            }
            return false;
        }

        private static GameObject Build(CASAttackType type, CasAirframeCatalog.GunProfile profile, AmmoCodexScriptable codex, float accuracyScale)
        {
            // The slot's accuracy radius is baked into the object name: the fire-time patch runs on the
            // instantiated clone, which has no other way to learn the slot's CasAccuracy setting. It is
            // only a fallback - CasPayloadFactory.SlotAccuracy reads the owning slot live.
            GameObject go = new GameObject("CFS " + type + " " + profile.GunId +
                                           " (runtime hardpoint) [acc=" + accuracyScale.ToString("0.###") + "]");
            CASHardpoint hardpoint = go.AddComponent<CASHardpoint>();

            TypeRef(hardpoint) = type;
            AmmoRef(hardpoint) = codex;
            AudioEventRef(hardpoint) = GunAudioEvent;
            // Base (natural) launch deviation only, and only as a fallback: the rounds of this hardpoint
            // are flown by the mod's own impact resolver (CasImpactAim), which overrides the launch
            // direction on the first frame of flight. The slot's CasAccuracy is read live at fire time
            // by CasImpactPointPatch, so what is baked here is never the authority.
            DeviationRef(hardpoint) = profile.DeviationDegrees;
            InheritVelocityRef(hardpoint) = profile.InheritVelocity;
            VisibleMunitionsRef(hardpoint) = false;
            MunitionCountRef(hardpoint) = profile.Munitions;
            LaunchCountRef(hardpoint) = 1;

            // Invisible-munition mode fires from _munitionSpawnPoint; give it a child at the origin.
            GameObject muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(go.transform, false);
            muzzle.transform.localPosition = Vector3.zero;
            muzzle.transform.localRotation = Quaternion.identity;
            SpawnPointRef(hardpoint) = muzzle.transform;

            return go;
        }

        /// <summary>
        /// Best loaded codex for the profile: hint matches win; a generic fallback accepts any fast
        /// unguided autocannon-style round so a strafe still works when the exact calibre is absent.
        /// </summary>
        private static AmmoCodexScriptable FindAmmo(CASAttackType type, CasAirframeCatalog.GunProfile profile)
        {
            bool missileType = type == CASAttackType.AirToGroundMissile || type == CASAttackType.AirToAirMissile;

            AmmoCodexScriptable[] codexes = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
            AmmoCodexScriptable best = null;
            int bestScore = 0;
            float bestMuzzle = 0f;

            for (int i = 0; i < codexes.Length; i++)
            {
                AmmoCodexScriptable codex = codexes[i];
                if (codex == null || codex.AmmoType == null)
                {
                    continue;
                }
                AmmoType ammo = codex.AmmoType;
                if (ammo.ShotVisual == null)
                {
                    // CASHardpoint.SpawnMunition instantiates ShotVisual; without it the round is
                    // invisible and the belt clone would inherit that.
                    continue;
                }
                if (missileType)
                {
                    // Missiles (AGM-65, AIM-9, R-60, ...) are guided; a CAS hardpoint fires them with
                    // the target it is holding. Only require a sane speed.
                    if (ammo.MuzzleVelocity < 50f)
                    {
                        continue;
                    }
                }
                else if (ammo.Guidance != AmmoType.GuidanceType.Unguided || ammo.MuzzleVelocity < 100f)
                {
                    continue;
                }

                string name = ammo.Name != null ? ammo.Name : codex.name;
                string lower = name.ToLowerInvariant();

                int score = 0;
                if (profile.AmmoHints != null)
                {
                    for (int h = 0; h < profile.AmmoHints.Length; h++)
                    {
                        if (lower.Contains(profile.AmmoHints[h].ToLowerInvariant()))
                        {
                            score += 1000;
                        }
                    }
                }

                // Generic fallback for autocannon-style rounds. Gun runs may borrow any fast gun round;
                // a bomb / rocket / missile hardpoint must NEVER fall back to a gun round - if the
                // right ammo is not loaded, degrade (Warn) instead of firing nonsense.
                if (score == 0 && type == CASAttackType.GunRun && lower.Contains("mm") &&
                    LooksLikeAutocannonRound(lower))
                {
                    score = 100;
                }

                if (score > 0 && (score > bestScore || (score == bestScore && ammo.MuzzleVelocity > bestMuzzle)))
                {
                    bestScore = score;
                    bestMuzzle = ammo.MuzzleVelocity;
                    best = codex;
                }
            }
            return best;
        }

        private static bool LooksLikeAutocannonRound(string lower)
        {
            if (lower.Contains("atgm") || lower.Contains("missile") || lower.Contains("rocket") ||
                lower.Contains("grenade") || lower.Contains("mortar") || lower.Contains("flare"))
            {
                return false;
            }
            return lower.Contains("apds") || lower.Contains("api") || lower.Contains("hei") ||
                   lower.Contains("ap-t") || lower.Contains("tracer") || lower.Contains("he-t") ||
                   lower.Contains("apbc");
        }

        /// <summary>Logs currently loaded ammo names relevant to the profile so hints can be tuned.</summary>
        private static void LogLoadedSample(CasAirframeCatalog.GunProfile profile)
        {
            if (_sampleLogged)
            {
                return;
            }
            _sampleLogged = true;

            List<string> names = new List<string>();
            AmmoCodexScriptable[] codexes = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
            for (int i = 0; i < codexes.Length; i++)
            {
                AmmoCodexScriptable codex = codexes[i];
                if (codex == null || codex.AmmoType == null)
                {
                    continue;
                }
                AmmoType ammo = codex.AmmoType;
                string name = ammo.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                string lower = name.ToLowerInvariant();

                bool relevant = profile.AmmoHints != null;
                if (relevant)
                {
                    relevant = false;
                    for (int h = 0; h < profile.AmmoHints.Length; h++)
                    {
                        if (lower.Contains(profile.AmmoHints[h].ToLowerInvariant()))
                        {
                            relevant = true;
                            break;
                        }
                    }
                }
                if (relevant || (lower.Contains("mm") && LooksLikeAutocannonRound(lower)))
                {
                    names.Add(name + " (" + ammo.MuzzleVelocity + " m/s)");
                }
            }
            Log.Info("CAS payload factory: relevant ammo currently loaded: " +
                     (names.Count == 0 ? "none" : string.Join(" | ", names.ToArray())));
        }
    }

    /// <summary>
    /// The belt of one runtime gun hardpoint: the ammo codexes are cycled round-robin so a burst mixes
    /// the real GAU-8/A load (4x PGU-14/B API + 1x PGU-13/B HEI) or the Su-22 gun-pod load
    /// (2x OFZ-30 HEI + 1x BR-30 AP). Advanced by CasPayloadFactory.AdvanceBelt on every trigger pull.
    /// </summary>
    internal sealed class CasGunBelt : MonoBehaviour
    {
        // [SerializeField] is REQUIRED, not cosmetic: CASHardpointManager.SetUpHardpoints instantiates
        // the runtime hardpoint, and Unity clones through its serializer - a plain internal field is
        // reset to null on the clone, which silently reduced every burst to the first round of the
        // belt (all AP, so a strafe had no explosion at all).
        [SerializeField]
        internal AmmoCodexScriptable[] Rounds;

        [SerializeField]
        internal int Index;
    }

    /// <summary>
    /// One-shot guard on a live round: set once the fallback impact effect / audio has been spawned,
    /// so a round that reports several impacts still produces exactly one explosion.
    /// </summary>
    internal sealed class CasFallbackImpact : MonoBehaviour
    {
    }

    /// <summary>
    /// The mod's own hit logic for one round of a gun run, a rocket salvo or a bomb stick.
    ///
    /// The round carries the locked target's centre plus its own point in the impact circle
    /// (CasPayloadFactory.SampleImpactOffset), and CasImpactAimPatch takes over its flight every frame -
    /// before LiveRound's own update - so it ends up on that point. The game's ballistics, launch
    /// deviation, aim computer and guidance therefore never get to decide where the round goes; the
    /// point does, and the point comes from SlotN_CasAccuracy alone:
    ///
    ///   CasAccuracy = 0   -> the point IS the target's centre, so the round is flown into the hull:
    ///                        a guaranteed hit, whatever the launch geometry was.
    ///   0 &lt; x &lt;= 1     -> a random point inside a 5*x m circle around the centre, so the round hits
    ///                        exactly like a real strafe: mostly on and around the target.
    ///
    /// Two flight modes (GravityAware):
    ///   * bullets and rockets - the flight state is rewritten to "straight at the point", which is what
    ///     a fast direct-fire round does anyway;
    ///   * bombs - the round keeps the game's own gravity / drag integration (its whole trajectory IS
    ///     gravity) and only its horizontal velocity is corrected toward the predicted impact point.
    ///
    /// The target centre is a Transform and is re-read every frame, so a moving target is tracked all
    /// the way in. The last couple of metres are handed back to the game (Released), which keeps the
    /// impact itself - obliquity, armour, penetration, spall, effects, audio - exactly as vanilla.
    /// </summary>
    internal sealed class CasImpactAim : MonoBehaviour
    {
        /// <summary>The locked target's centre (Unit.Center); re-read every frame.</summary>
        internal Transform Target;

        /// <summary>The round's fixed world-space offset from that centre: its own point in the circle.</summary>
        internal Vector3 Offset;

        /// <summary>
        /// The shot id (LiveRound.ID) this aim was attached for. LiveRoundMarshaller pools rounds and
        /// reuses them for any weapon of the same visual type, so a component left over from an earlier
        /// shot must never steer the new one: the steering patch compares this with the round's id.
        /// </summary>
        internal int ShotId;

        /// <summary>
        /// True for a FALLING round (a bomb): CasImpactAimPatch then applies the gravity-aware terminal
        /// correction (predict the drop with the game's own ballistics, nudge the horizontal velocity)
        /// instead of flying the round in a straight line to the point.
        /// </summary>
        internal bool GravityAware;

        /// <summary>One diagnostic line per round, the first time the terminal correction engages.</summary>
        internal bool CorrectionLogged;

        /// <summary>One diagnostic line per round: the speed an air-to-ground missile is pinned to.</summary>
        internal bool SpeedLogged;

        /// <summary>Set once the round is close enough / past the point: vanilla flight resumes.</summary>
        internal bool Released;
    }
}
