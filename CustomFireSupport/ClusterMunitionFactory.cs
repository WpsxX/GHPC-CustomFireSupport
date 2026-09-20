using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC;
using GHPC.Effects;
using GHPC.Weaponry;
using GHPC.Weaponry.Artillery;
using GHPC.Weapons;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Builds the cargo (cluster) ammunition the artillery's ANTI-ARMOUR shell is replaced with, and
    /// remembers the two rounds so the runtime patches recognise them.
    ///
    /// Asked for by the player:
    ///
    ///   Blue / US      M864,    155 mm, 72 HEDP submunitions
    ///   Red  / Soviet  3-O-23,  152 mm, 42 HEDP submunitions
    ///
    ///   * the round opens in a small explosion about 100 m above the called point;
    ///   * that little explosion uses the game's 20 mm HE explosion effect, pointed DOWN;
    ///   * every submunition also uses the game's 20 mm explosion effect;
    ///   * and both are heard with the game's 20 mm explosion sound;
    ///   * HEDP is the English name of the dual-purpose (anti-armour + fragmentation) submunition.
    ///
    /// EACH SUBMUNITION IS THE GAME'S HEAT ROUND IN MINIATURE
    ///
    /// The submunition is deliberately built to the game's own HEAT semantics rather than as a bare
    /// shaped charge, because a bare jet only does anything when it hits armour: drop one on grass and
    /// it leaves a puff. Built as HEAT it also cuts down infantry standing around the impact, which is
    /// the whole point of a dual-purpose round.
    ///
    /// Category ShapedCharge is the game's "HEAT" (LiveRound: _isHeat = Info.Category == ShapedCharge),
    /// and the four fields below are the ones that turn that category into the game's HEAT behaviour:
    ///
    ///   * 16 real fragments out of every detonation - see <see cref="ClusterFragments"/>. The game has a
    ///     spall-on-detonation field of its own (<c>AmmoType.DetonateSpallCount</c>, which LiveRound only
    ///     honours for HEAT), and the submunition used to just set it - but that path takes its rounds
    ///     from the INVISIBLE pool, so the fragments were lethal and completely unseen. The burst is
    ///     spawned from the game's visible spall pool instead, and the field is left at 0.
    ///   * AlwaysProduceBlast - a HEAT round fuses and blasts on whatever solid object it touches,
    ///     whether or not the jet defeated it (<c>_fuzeCompleted</c>). This is the game's other HEAT fuse
    ///     rule, and it is the one that applies here precisely because DetonateSpallCount is 0.
    ///   * MicroFragScaling - the game's non-ballistic frag cloud (BlastEffectManager), which damages
    ///     every unit in range of the detonation; it works on a ground hit as well.
    ///   * A real TntEquivalentKg - the charge the cloud's damage and range are derived from.
    ///
    /// WHERE THE "20 mm HE" EFFECT AND SOUND COME FROM
    ///
    /// GHPC's own 20 mm HE round is ammo_20mm_DM51A2 ("20mm HE-T DM51A2", the Marder's Rh202 round).
    /// Its AmmoType carries exactly two things this feature needs:
    ///
    ///   ImpactAudio            = 3 = ImpactAudioType.AutocannonExplosive   <- the 20 mm explosion sound
    ///   ImpactEffectDescriptor = HighExplosive / EffectSize Autocannon / Small / RicochetType Autocannon
    ///                            / MinFilterStrictness Medium              <- the 20 mm HE explosion effect
    ///
    /// So the mod does not guess at a prefab: it clones that round's AmmoType whenever a mission has it
    /// loaded (inheriting the descriptor, the decal descriptor AND the game's own effect-cache entry,
    /// which together ARE "the 20 mm HE explosion"), and otherwise writes the same descriptor by hand
    /// and lets ParticleEffectsManager resolve and cache it on the first hit. Either way the burst and
    /// every submunition impact go through the vanilla pipeline, so the effect, its orientation, the
    /// decal and the sound are the game's own.
    ///
    /// The numbers the player left to the mod ("伤害和破片的数据你看着办"): 70 mm RHAe of shaped-charge
    /// penetration (a real HEDP submunition is good for roughly 70 mm, which defeats light armour and
    /// a tank's roof or sides but never its glacis), 150 g TNTe of blast, 16 spall out of every
    /// detonation and a 1.5x frag cloud - so one submunition is a grenade-plus that clears infantry out
    /// of a 30 m circle even on open ground, and 72 of them on one 155 mm call is a real tank-killer.
    /// </summary>
    internal static class ClusterMunitionFactory
    {
        // ------------------------------------------------------------------
        // The two rounds
        // ------------------------------------------------------------------

        private static readonly ClusterShellSpec Nato = new ClusterShellSpec
        {
            ShellName = "M864 Cluster",
            SubmunitionName = "M864 HEDP",
            Submunitions = 72,
            CaliberMillimeters = 155f
        };

        private static readonly ClusterShellSpec Pact = new ClusterShellSpec
        {
            ShellName = "3-O-23 Cluster",
            SubmunitionName = "3-O-23 HEDP",
            Submunitions = 42,
            CaliberMillimeters = 152f
        };

        // ------------------------------------------------------------------
        // HEDP submunition data (the numbers the player delegated)
        // ------------------------------------------------------------------

        /// <summary>Shaped-charge penetration of one submunition, in mm RHAe.</summary>
        private const float HedpRhaPenetration = 70f;

        /// <summary>
        /// Blast filler of one submunition, in kg TNTe - **40 g, what a real 50 mm dual-purpose bomblet
        /// actually carries** (a 1.5 kg body is mostly steel, not explosive).
        ///
        /// NO OVERPRESSURE, BY DESIGN. The player's call: a charge this small does not overpressure
        /// anything. The game agrees once the number is honest:
        ///
        ///   * <c>BlastEffectManager.HandleExplosiveBlast</c> only applies overpressure when
        ///     <c>TNT - armourAlongPath * 0.2 &gt; 0</c>. At 40 g that needs the blast to reach the
        ///     component through **less than 0.2 mm RHAe** - i.e. nothing on a vehicle. (At the 150 g this
        ///     used to be it survived 0.75 mm, which still is not much, but the number was wrong anyway.)
        ///   * The overpressure radius it is gated by (Sadovskiy, <c>14.022 * kg^(1/3)</c>) drops from
        ///     ~7.4 m to ~4.8 m.
        ///   * <c>_compartmentHit?.InsertOverpressure(...)</c> runs for <c>_isHe</c> only, and this round
        ///     is HEAT (<c>_isHeat</c>), so it never had a compartment overpressure to begin with.
        ///
        /// The anti-personnel frag cloud does NOT shrink with it - both of its terms are floored:
        /// <c>maxFragRange = clamp(TNT*7, 20, 200)</c> and <c>maxFragDamage = clamp(TNT, 3, 60)</c>, so at
        /// 40 g they are still 20 m and 3, i.e. the same 30 m / 4.5 the player already tuned. Only the
        /// camera shake and the blast audio get quieter, which is what 40 g should sound like.
        /// </summary>
        private const float HedpTntKilograms = 0.04f;

        /// <summary>All-up mass of one submunition, in kg.</summary>
        private const float HedpMassKilograms = 1.5f;

        /// <summary>Body cross-section, m^2 (a ~50 mm submunition body).</summary>
        private const float HedpSectionalArea = 0.00196f;

        /// <summary>Drag coefficient - low enough that a 100 m fall is essentially ballistic.</summary>
        private const float HedpDragCoefficient = 0.15f;

        private const float HedpCaliberMillimeters = 50f;

        /// <summary>
        /// Spall thrown behind whatever the jet penetrates, and the strength of each fragment the
        /// detonation throws (see <see cref="ClusterFragments"/>). Deliberately modest: 1-8 mm RHAe is a
        /// round that ruins an unarmoured or lightly armoured hitzone without turning a submunition into an
        /// armour-piercer.
        ///
        /// Internal rather than private because the fragment burst uses the same numbers: one submunition
        /// should not have two different ideas of what its own fragments are worth.
        /// </summary>
        internal const float HedpMaxSpallRha = 8f;
        internal const float HedpMinSpallRha = 1f;
        private const float HedpSpallMultiplier = 0.5f;

        /// <summary>
        /// Fragments one submunition throws when it goes off: the scored / pre-formed steel body of a
        /// real 50 mm HEDP submunition breaking up. 20 rather than 16 because the fragments now leave in
        /// **every** direction (see <see cref="ClusterFragments"/>), and the ones that depart downwards
        /// are stopped by the ground within a metre or two - so roughly half of them never reach anyone.
        /// 20 keeps the number that actually flies out over the target where it was.
        ///
        /// It does NOT go into <c>AmmoType.DetonateSpallCount</c>: that field feeds the game's own
        /// createExplosion, which takes its rounds from the INVISIBLE pool - the fragments would be
        /// lethal and completely unseen. The burst is spawned by <see cref="ClusterFragments"/> from the
        /// game's visible spall pool instead, so the field below is left at 0 and this is the one count.
        /// </summary>
        internal const int HedpFragmentCount = 20;

        /// <summary>
        /// The submunition's **shaped-charge jet stays vanilla.** The body's copper-liner cone forms the
        /// jet, and whether it defeats the armour it hits is decided by the game's own HEAT path at
        /// impact - the mod sets the warhead's data (70 mm RHAe, see HedpRhaPenetration) and nothing
        /// else. In particular the fragment burst never rotates or steers the submunition itself, so
        /// "the jet following the fragments" cannot happen.
        /// </summary>

        /// <summary>
        /// HEAT rounds fuse and blast on whatever solid object they touch, penetrated or not - this is
        /// the value the game expects of the category, and it is the fuse rule a HEAT round falls back to
        /// (LiveRound: <c>_isHeat &amp;&amp; _hitSolidObject &amp;&amp; AlwaysProduceBlast &amp;&amp;
        /// DetonateSpallCount == 0</c> -&gt; <c>_fuzeCompleted</c>).
        ///
        /// Note the <c>DetonateSpallCount == 0</c> in that condition: this round deliberately leaves that
        /// field at 0 (its fragments are thrown by <see cref="ClusterFragments"/>, not by the game), so
        /// this is the rule that actually fires - which is exactly what a HEAT round should do.
        /// </summary>
        private const bool HedpAlwaysProduceBlast = true;

        /// <summary>
        /// The game's NON-BALLISTIC frag cloud - AmmoType.MicroFragScaling, "adjust the intensity and
        /// range of the frag cloud for blast damage (non-ballistic simulated frags)". It is the second
        /// half of the anti-personnel effect (the first being the detonation spall above) and it works
        /// on a ground hit, because DoExplosiveBlast runs for every ShapedCharge detonation.
        ///
        /// BlastEffectManager scales BOTH the cloud's radius and its damage by this value:
        ///     radius = clamp(TNT * 7, 20, 200) * this   -&gt; 20 m * 1.5 = 30 m
        ///     damage = clamp(TNT, 3, 60) * this         -&gt; 3 * 1.5 = 4.5, falling off to 0 at the edge
        /// with the damage roll's chance falling off as the square of the distance ratio. For scale, the
        /// mod's own 30 mm HEI rounds use 0.6; a dual-purpose submunition meant to clear infantry out of
        /// a 30 m circle uses 1.5.
        ///
        /// Deliberately kept well below the several-hundred-metre carpet the 72-submunition burst would
        /// get from a big value: one submunition should read as a grenade-plus, not as a 155 mm shell,
        /// and the burst's lethality comes from there being 72 of them.
        /// </summary>
        private const float HedpMicroFragScaling = 1.5f;

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        /// <summary>Every AmmoType this factory built, so the rest of the mod can recognise its own.</summary>
        private static readonly List<AmmoType> _ourAmmo = new List<AmmoType>();

        /// <summary>Per-faction launch description (at most two entries - reference compared).</summary>
        private static readonly List<ClusterLaunch> _launches = new List<ClusterLaunch>();

        /// <summary>The 20 mm HE round the mission has loaded, if any (searched once per session).</summary>
        private static AmmoCodexScriptable _twentyMillimetre;
        private static bool _twentyMillimetreSearched;
        private static string _twentyMillimetreSource = "(none loaded)";

        private static bool _warnedNoTwentyMillimetre;

        // ------------------------------------------------------------------
        // Public API used by the slot builder / the runtime patches
        // ------------------------------------------------------------------

        /// <summary>
        /// The cargo-round choice for one anti-armour artillery slot, or null when nothing could be
        /// assembled (the caller then keeps the vanilla anti-armour shell).
        ///
        /// <paramref name="donor"/> is the template the slot would otherwise have fired: its shell is
        /// the visual / flight donor of the cargo round, and it is a fallback effect donor when the
        /// mission has no 20 mm HE round loaded.
        /// </summary>
        internal static BatteryMunitionsChoice BuildChoice(Faction playerFaction, AmmoTemplate template,
            out string description)
        {
            description = null;
            if (template == null || template.Choice == null || template.Choice.Ammo == null ||
                template.Choice.Ammo.AmmoType == null)
            {
                return null;
            }

            ClusterShellSpec spec = playerFaction == Faction.Red ? Pact : Nato;
            AmmoCodexScriptable donor = template.Choice.Ammo;

            ClusterLaunch launch = Build(playerFaction, spec, donor);
            if (launch == null)
            {
                return null;
            }

            description = launch.Describe();
            return new BatteryMunitionsChoice
            {
                // The battery has to keep answering "yes" to this side's ANTI-ARMOUR queries (the map
                // button and FireMissionManager gate the call on it), so the type is left alone - only
                // the ammo behind it changes.
                Type = template.Choice.Type,
                Ammo = launch.Codex,
                DefaultProjectile = null
            };
        }

        /// <summary>True for an AmmoType this factory built (the cargo round or a HEDP submunition).</summary>
        internal static bool IsOurAmmo(AmmoType ammo)
        {
            if (ammo == null)
            {
                return false;
            }
            for (int i = 0; i < _ourAmmo.Count; i++)
            {
                if (ReferenceEquals(_ourAmmo[i], ammo))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The launch parameters of a cargo round, or null when this ammo is not one of ours. Called by
        /// ArtilleryBattery.DoSingleShot for every shot of every battery, so it is a reference scan of
        /// a two-entry list and nothing else.
        /// </summary>
        internal static ClusterLaunch LaunchFor(AmmoType ammo)
        {
            if (ammo == null)
            {
                return null;
            }
            for (int i = 0; i < _launches.Count; i++)
            {
                if (ReferenceEquals(_launches[i].CarrierAmmo, ammo))
                {
                    return _launches[i];
                }
            }
            return null;
        }

        // Note: nothing here is reset per mission on purpose. The built AmmoTypes and their codeces are
        // plain objects (not scene objects), so they outlive a scene change and a second anti-armour slot
        // - or the next mission, which very likely fires the same shell - reuses them instead of building
        // a second, identical set. The 20 mm donor is searched once per session for the same reason.

        // ------------------------------------------------------------------
        // Assembly
        // ------------------------------------------------------------------

        private static ClusterLaunch Build(Faction faction, ClusterShellSpec spec, AmmoCodexScriptable donor)
        {
            // Reuse an earlier slot's round for the same faction (two anti-armour slots share one spec).
            for (int i = 0; i < _launches.Count; i++)
            {
                if (_launches[i].Spec == spec)
                {
                    return _launches[i];
                }
            }

            AmmoCodexScriptable twenty = TwentyMillimetreHe();
            AmmoCodexScriptable carrierCodex = BuildCarrier(spec, donor);
            AmmoCodexScriptable submunitionCodex = BuildSubmunition(spec, donor, twenty);
            if (carrierCodex == null || submunitionCodex == null)
            {
                return null;
            }

            ClusterLaunch launch = new ClusterLaunch
            {
                CarrierAmmo = carrierCodex.AmmoType,
                Codex = carrierCodex,
                Spec = spec,
                Submunition = submunitionCodex,
                BurstEffect = submunitionCodex.AmmoType,
                CarrierSpeed = ClusterMunition.CarrierSpeedMetersPerSecond,
                BurstHeightMeters = ClusterMunition.BurstHeightMeters
            };
            _launches.Add(launch);

            Log.Info("cluster munition ready for " + faction + ": " + launch.Describe() +
                     "; 20 mm HE effect + sound taken from " + _twentyMillimetreSource + "; each submunition is a " +
                     "HEAT-type round (" + HedpTntKilograms.ToString("0.###") + " kg TNTe, " +
                     HedpFragmentCount + " visible fragments on detonation, frag cloud x" +
                     HedpMicroFragScaling.ToString("0.#") + " = about " +
                     (20f * HedpMicroFragScaling).ToString("0") + " m, so a ground hit still cuts down infantry); " +
                     "fired by the slot's anti-armour artillery battery, steered to the burst point and opened there " +
                     "(the vanilla anti-armour shell '" + donor.AmmoType.Name + "' is its visual and flight donor).");
            return launch;
        }

        /// <summary>
        /// The cargo round itself. It is the template's own anti-armour shell visually (real shell model,
        /// tracer geometry, drag) but carries NO warhead: the round only flies, and everything explosive
        /// happens at the burst point. That is also why its impact effect and decal are disabled - a cargo
        /// round that somehow reaches the ground should be a dud, not a 155 mm detonation.
        /// </summary>
        private static AmmoCodexScriptable BuildCarrier(ClusterShellSpec spec, AmmoCodexScriptable donor)
        {
            if (donor == null || donor.AmmoType == null)
            {
                return null;
            }

            AmmoType ammo = CloneAmmoType(donor.AmmoType);
            ammo.Name = spec.ShellName;
            ammo.Category = AmmoType.AmmoCategory.Explosive;
            ammo.ShortName = AmmoType.AmmoShortName.He;
            ammo.TntEquivalentKg = 0f;                  // the round opens, it does not detonate
            ammo.RhaPenetration = 0f;
            ammo.DoScabEffect = false;
            ammo.Mass = 45f;                            // a real M864-class cargo round, so the arc reads right
            ammo.Coeff = Mathf.Clamp(donor.AmmoType.Coeff, 0.02f, 2f);
            ammo.SectionalArea = donor.AmmoType.SectionalArea;
            ammo.Caliber = spec.CaliberMillimeters;
            ammo.MuzzleVelocity = ClusterMunition.CarrierSpeedMetersPerSecond;
            ammo.UseTracer = false;
            ammo.NoMuzzleEffects = true;
            ammo.MaximumRange = 30000f;
            ammo.Guidance = AmmoType.GuidanceType.Unguided;
            ammo.Flight = AmmoType.FlightPattern.Direct;
            ammo.TurnSpeed = 0f;
            ammo.ArmingDistance = 0f;
            ammo.UseErrorCorrection = false;            // the mod flies it; RK4 would only add cost
            ammo.NutationPenaltyDistance = 0f;
            ammo.CertainRicochetAngle = 10f;
            ammo.SpallMultiplier = 1f;
            ammo.MicroFragScaling = 1f;
            ammo.DetonateSpallCount = 0;
            ammo.NoPenSpall = true;
            ammo.ImpactFuseTime = 0f;
            ammo.RangedFuseTime = 0f;
            ammo.RhaToFuse = 0f;
            ammo.AlwaysProduceBlast = false;
            ammo.ShatterOnRicochet = false;
            ammo.Normalize = false;
            ammo.Tandem = false;
            ammo.IgnoreSlat = false;
            ammo.SphericalSpall = false;
            ammo.DoNotDestroy = false;
            ammo.DetonateEffect = null;
            ammo.TerrainImpactEffect = null;
            ammo.VisualModel = null;
            // Its own ammo does not look up an impact effect at all (see the class comment).
            ammo.ImpactEffectDescriptor = new ParticleEffectsManager.ImpactEffectDescriptor { HasImpactEffect = false };
            ammo.CachedIndex = -1;
            if (ammo.VisualType == LiveRoundMarshaller.LiveRoundVisualType.Custom)
            {
                // A Custom round is allocated fresh and destroyed again; the pooled Shell visual is the
                // right (and cheaper) fit for an artillery round.
                ammo.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Shell;
                ammo.ShotVisual = null;
            }

            AmmoCodexScriptable codex = ScriptableObject.CreateInstance<AmmoCodexScriptable>();
            codex.name = spec.ShellName;
            codex.AmmoType = ammo;
            _ourAmmo.Add(ammo);
            return codex;
        }

        /// <summary>
        /// One HEDP submunition, built to the game's HEAT semantics: Category is ShapedCharge (the game's
        /// "破甲" / HEAT category), so the jet is resolved against CE protection, it blasts like a HEAT
        /// round (<see cref="HedpAlwaysProduceBlast"/>), it throws real fragments out of its detonation
        /// (<see cref="ClusterFragments"/>) and it carries the frag cloud
        /// (<see cref="HedpMicroFragScaling"/>) - which together mean a submunition that lands on open
        /// ground still cuts down infantry around the impact, not just one that found armour.
        ///
        /// Its IMPACT EFFECT is deliberately the 20 mm HE one rather than a HEAT effect: the player asked
        /// for the 20 mm explosion on every submunition, and an autocannon-sized puff is also the honest
        /// size for a 1.5 kg submunition.
        /// </summary>
        private static AmmoCodexScriptable BuildSubmunition(ClusterShellSpec spec, AmmoCodexScriptable donor,
            AmmoCodexScriptable twenty)
        {
            // Effect / audio donor: the game's own 20 mm HE round when the mission has it loaded, else
            // the artillery shell (whose descriptor is then overwritten with the 20 mm one below).
            AmmoCodexScriptable source = twenty != null ? twenty : donor;
            if (source == null || source.AmmoType == null)
            {
                return null;
            }

            AmmoType ammo = CloneAmmoType(source.AmmoType);
            ammo.Name = spec.SubmunitionName;
            ammo.Category = AmmoType.AmmoCategory.ShapedCharge;
            ammo.ShortName = AmmoType.AmmoShortName.Heat;
            ammo.RhaPenetration = HedpRhaPenetration;
            ammo.TntEquivalentKg = HedpTntKilograms;
            ammo.DoScabEffect = false;
            ammo.Mass = HedpMassKilograms;
            ammo.Coeff = HedpDragCoefficient;
            ammo.SectionalArea = HedpSectionalArea;
            ammo.EdgeSetback = 0.01f;
            ammo.Caliber = HedpCaliberMillimeters;
            ammo.MuzzleVelocity = ClusterMunition.SubmunitionSpeedMetersPerSecond;
            ammo.UseTracer = false;                     // a released submunition has no tracer
            ammo.NoMuzzleEffects = true;
            ammo.MaximumRange = 2000f;
            ammo.Guidance = AmmoType.GuidanceType.Unguided;
            ammo.Flight = AmmoType.FlightPattern.Direct;
            ammo.TurnSpeed = 0f;
            ammo.ArmingDistance = 0f;
            ammo.UseErrorCorrection = false;            // a 100 m fall; the cheap integrator is exact enough
            ammo.NutationPenaltyDistance = 0f;          // it is dropped, not fired: no launch penalty
            ammo.CertainRicochetAngle = 10f;
            // ---- the HEAT half of a HEAT/HEP dual-purpose round ----
            ammo.SpallMultiplier = HedpSpallMultiplier;
            ammo.MinSpallRha = HedpMinSpallRha;
            ammo.MaxSpallRha = HedpMaxSpallRha;
            ammo.MicroFragScaling = HedpMicroFragScaling;      // stat frag cloud, works on ground hits
            // The real fragments are thrown by ClusterFragments from the game's VISIBLE spall pool; the
            // game's own spall-on-detonation field stays 0 so the burst is not thrown twice (and, more to
            // the point, so it does not come out invisible).
            ammo.DetonateSpallCount = 0;
            ammo.NoPenSpall = false;
            ammo.ImpactFuseTime = 0f;
            ammo.RangedFuseTime = 0f;
            ammo.RhaToFuse = 0f;                        // fuses on anything it touches, like the 20 mm round
            ammo.AlwaysProduceBlast = HedpAlwaysProduceBlast;
            ammo.ShatterOnRicochet = false;
            ammo.Normalize = false;
            ammo.Tandem = false;
            ammo.IgnoreSlat = false;
            ammo.SphericalSpall = false;
            ammo.ForcedSpallAngle = 0f;
            ammo.DoNotDestroy = false;
            ammo.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Invisible;
            ammo.ShotVisual = null;
            ammo.VisualModel = null;
            ammo.ImpactAudio = GHPC.Audio.ImpactAudioType.AutocannonExplosive;   // the 20 mm explosion sound
            ApplyClusterImpactEffect(ammo, twenty);

            AmmoCodexScriptable codex = ScriptableObject.CreateInstance<AmmoCodexScriptable>();
            codex.name = spec.SubmunitionName;
            codex.AmmoType = ammo;
            _ourAmmo.Add(ammo);
            return codex;
        }

        /// <summary>
        /// Makes this ammo's impact look (and be cached) like the game's 20 mm HE round: the exact
        /// impact-effect descriptor and impact audio of ammo_20mm_DM51A2, so the vanilla pipeline
        /// (ParticleEffectsManager.CreateImpactEffectOfType + ImpactSFXManager.PlaySimpleImpactAudio)
        /// picks the 20 mm HE explosion and the 20 mm explosion sound for every submunition - and for the
        /// airburst, which reuses this same descriptor.
        ///
        /// <c>CachedIndex</c> is deliberately cleared rather than copied: it is an index into a runtime
        /// dictionary on the mission's effect database, and letting the game build this ammo's own entry
        /// from the descriptor below is what makes the resolution correct in any mission, whatever the
        /// donor's serialized index happened to be. The donor's detonate prefab and decal descriptor are
        /// still inherited, so the fallback path and the ground marks stay the real 20 mm ones.
        /// </summary>
        private static void ApplyClusterImpactEffect(AmmoType ammo, AmmoCodexScriptable twenty)
        {
            ammo.ImpactEffectDescriptor = new ParticleEffectsManager.ImpactEffectDescriptor
            {
                HasImpactEffect = true,
                ImpactCategory = ParticleEffectsManager.Category.HighExplosive,
                EffectSize = ParticleEffectsManager.EffectSize.Autocannon,
                RicochetType = ParticleEffectsManager.RicochetType.Autocannon,
                Flags = ParticleEffectsManager.ImpactModifierFlags.Small,
                MinFilterStrictness = ParticleEffectsManager.FilterStrictness.Medium
            };

            if (twenty != null && twenty.AmmoType != null)
            {
                ammo.DetonateEffect = twenty.AmmoType.DetonateEffect;
                ammo.TerrainImpactEffect = twenty.AmmoType.TerrainImpactEffect;
                ammo.ImpactDecalDescriptor = twenty.AmmoType.ImpactDecalDescriptor;
            }
            else
            {
                ammo.DetonateEffect = null;
                ammo.TerrainImpactEffect = null;
                // No decal: 72 submunitions should not each stamp the artillery shell's scorch mark.
                ammo.ImpactDecalDescriptor = new ImpactDecalsManager.ImpactDecalDescriptor { HasImpactDecal = false };
            }

            ammo.CachedIndex = -1;
        }

        // ------------------------------------------------------------------
        // Donor search
        // ------------------------------------------------------------------

        /// <summary>
        /// Name fragments of GHPC's 20 mm rounds (the Marder's Rh202 ammunition).
        /// </summary>
        private static readonly string[] TwentyMillimetreHints =
        {
            "20mm", "20 mm", "20x139", "20 x 139", "rh202", "mk20", "mk 20", "dm51", "dm43", "dm63"
        };

        /// <summary>
        /// Finds the game's loaded 20 mm HE round, once per session. A 20 mm round is not artillery, so
        /// it never shows up in the shell harvest - this is a search of its own. Returns null when the
        /// mission has no 20 mm ammunition in memory, in which case the hand-written descriptor above is
        /// used instead (same effect family, resolved by the game).
        /// </summary>
        private static AmmoCodexScriptable TwentyMillimetreHe()
        {
            if (_twentyMillimetreSearched)
            {
                return _twentyMillimetre;
            }
            _twentyMillimetreSearched = true;

            AmmoCodexScriptable[] codexes = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
            AmmoCodexScriptable best = null;
            bool bestIsAutowired = false;
            for (int i = 0; i < codexes.Length; i++)
            {
                AmmoCodexScriptable codex = codexes[i];
                if (codex == null || codex.AmmoType == null || IsOurAmmo(codex.AmmoType))
                {
                    continue;
                }
                string name = codex.AmmoType.Name;
                if (!LooksLikeTwentyMillimetre(name))
                {
                    continue;
                }
                if (codex.AmmoType.Category != AmmoType.AmmoCategory.Explosive)
                {
                    continue; // DM43 API-T / DM63 APDS-T: right calibre, wrong effect
                }

                // Prefer the round whose descriptor already names the autocannon HE explosion: that is
                // the one whose effect this feature is copying.
                bool authoritative = codex.AmmoType.ImpactEffectDescriptor.HasImpactEffect &&
                                     codex.AmmoType.ImpactEffectDescriptor.EffectSize == ParticleEffectsManager.EffectSize.Autocannon &&
                                     codex.AmmoType.ImpactEffectDescriptor.ImpactCategory == ParticleEffectsManager.Category.HighExplosive;
                if (best == null || (authoritative && !bestIsAutowired))
                {
                    best = codex;
                    bestIsAutowired = authoritative;
                }
                if (bestIsAutowired)
                {
                    break; // cannot do better than "the game's own 20 mm HE round"
                }
            }

            _twentyMillimetre = best;
            if (best != null)
            {
                _twentyMillimetreSource = "'" + best.AmmoType.Name + "' (impact audio " +
                                          best.AmmoType.ImpactAudio + ", effect " +
                                          best.AmmoType.ImpactEffectDescriptor + ", cached index " +
                                          best.AmmoType.CachedIndex + ")";
            }
            else
            {
                _twentyMillimetreSource = "the built-in 20 mm HE descriptor (this mission loaded no 20 mm round)";
                if (!_warnedNoTwentyMillimetre)
                {
                    _warnedNoTwentyMillimetre = true;
                    Log.Info("cluster munition: this mission has no 20 mm round loaded, so the airburst and the " +
                             "HEDP impacts use the hand-written 20 mm HE impact-effect descriptor " +
                             "(HighExplosive / Autocannon / Small) and the game's autocannon explosion sound; " +
                             "ParticleEffectsManager resolves and caches it on the first hit.");
                }
            }
            return best;
        }

        private static bool LooksLikeTwentyMillimetre(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < TwentyMillimetreHints.Length; i++)
            {
                if (lower.Contains(TwentyMillimetreHints[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Shallow field-by-field copy of an AmmoType (all public instance fields). Shared with
        /// <see cref="ClusterFragments"/>, which clones the game's own spall round the same way.
        /// </summary>
        private static readonly FieldInfo[] AmmoFields = typeof(AmmoType).GetFields(BindingFlags.Public | BindingFlags.Instance);

        internal static AmmoType CloneAmmoType(AmmoType donor)
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
    }
}
