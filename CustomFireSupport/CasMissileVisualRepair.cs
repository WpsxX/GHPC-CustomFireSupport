using System;
using System.Collections.Generic;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Puts the GAME'S OWN effect materials onto a bundled round's flight effects when the mission has
    /// them loaded.
    ///
    /// Why it is needed: the flight effects of the mod's own munitions (motor flame, exhaust, smoke trail,
    /// heat distortion, backblast) are the exported copies inside cas_assets, and the bundle is packed
    /// from an AssetRipper export whose materials the placeholder-shader repair had already rewritten to
    /// the mod's simplified flipbook shaders. The game's real particle shaders only exist in the running
    /// build, so the only way to get a one-to-one effect is to take the game's own material (which is
    /// loaded with whatever vehicle or weapon uses it) and use it in place of the bundled approximation. A
    /// material the game has not loaded stays approximate - a soft alpha-blended smoke puff instead of the
    /// real effect - which is a cosmetic difference, never a white box.
    ///
    /// Two callers, one mechanism:
    ///
    ///   * <see cref="ApplyMissileVisual"/> - the air-to-ground missile, whose effects were composed into
    ///     the bundled prefabs by the editor tool CasMissileComposer. It also wires the motor's burn clock.
    ///   * <see cref="ApplyRoundVisual"/> - the bundled ROCKET rounds (FFAR, S-5K, S-8K, plus the bombs),
    ///     whose in-flight prefab is an indirect dependency of the hardpoint
    ///     (hardpoint -> AmmoCodexScriptable -> AmmoType.ShotVisual). Those prefabs are covered by the
    ///     bundle shader repair like any other (see CasPrewarmer.TrackAmmoPrefab), and this pass is the
    ///     per-instance safety net for the case where a material could not be repaired at bundle level -
    ///     typically because the game's own copy of it was not loaded yet when the bundle was repaired.
    ///
    /// It runs when the round is actually spawned (LiveRound.Init), because that is the first moment the
    /// mission is guaranteed to be fully loaded. It shares the material index rebuilt after scene
    /// initialization and mission preparation, so a salvo never triggers a global scan per material.
    ///
    /// A rocket pod fires up to 32 rounds in one pass, so the per-instance walk is memoized per
    /// ammunition and per scene: the first round of a salvo decides, and if there was nothing to adopt or
    /// hide the rest return immediately.
    /// </summary>
    internal static class CasMissileVisualRepair
    {
        private const string OurShaderPrefix = "CustomFireSupport/";

        /// <summary>Names already reported in this scene.</summary>
        private static readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>Per-ammunition verdict for this scene: false = nothing left to do, true = inspect each round.</summary>
        private static readonly Dictionary<AmmoType, bool> _needsPerRoundWork =
            new Dictionary<AmmoType, bool>(AmmoReferenceComparer.Instance);

        internal static void ResetForScene()
        {
            _reported.Clear();
            _needsPerRoundWork.Clear();
        }

        /// <summary>Renderer names whose material could not be adopted and that are hidden instead.</summary>
        private static readonly string[] HideWhenApproximate = { "distortion", "heat" };

        /// <summary>The air-to-ground missile's flight effects, plus its motor's burn clock.</summary>
        internal static void ApplyMissileVisual(GameObject round)
        {
            Apply(round, null, true);
        }

        /// <summary>
        /// A round fired from one of the bundle's own hardpoints (rockets, bombs): adopt the game's
        /// materials for whatever the bundle-level repair left approximated.
        /// </summary>
        internal static void ApplyRoundVisual(GameObject round, AmmoType ammo)
        {
            Apply(round, ammo, false);
        }

        /// <summary>
        /// One round's visual: swap in the game's own materials where it has them, and hide the effects
        /// that would look wrong with a plain alpha-blended shader (heat distortion).
        /// </summary>
        private static void Apply(GameObject round, AmmoType ammo, bool wireMotorBurnout)
        {
            if (round == null)
            {
                return;
            }

            try
            {
                if (wireMotorBurnout && round.GetComponent<CasMissileMotorBurnout>() == null)
                {
                    // Starts the motor's burn clock at launch: the plume goes out a few seconds in.
                    round.AddComponent<CasMissileMotorBurnout>();
                }

                // A rocket pod fires its whole load in one pass; once this ammunition has been found
                // clean in this scene, the remaining rounds have nothing to inspect.
                if (!wireMotorBurnout && ammo != null)
                {
                    bool needsWork;
                    if (_needsPerRoundWork.TryGetValue(ammo, out needsWork) && !needsWork)
                    {
                        return;
                    }
                }

                Renderer[] renderers = round.GetComponentsInChildren<Renderer>(true);
                int adopted = 0;
                int approximated = 0;
                int hidden = 0;

                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (renderer == null)
                    {
                        continue;
                    }

                    Material material = renderer.sharedMaterial;
                    if (material == null || material.shader == null ||
                        !material.shader.name.StartsWith(OurShaderPrefix, StringComparison.Ordinal))
                    {
                        continue; // the game's material already (e.g. the Standard-shaded model)
                    }

                    Material game = FindGameMaterial(material.name);
                    if (game != null)
                    {
                        renderer.sharedMaterial = game;
                        adopted++;
                        continue;
                    }

                    if (LooksLikeDistortion(renderer.name) || LooksLikeDistortion(material.name))
                    {
                        // A distortion quad sampled by a plain alpha-blended shader draws a visible grey
                        // haze instead of a heat shimmer: with nothing better available, leave it out.
                        renderer.enabled = false;
                        hidden++;
                        continue;
                    }

                    approximated++;
                }

                if (!wireMotorBurnout)
                {
                    if (ammo != null)
                    {
                        _needsPerRoundWork[ammo] = adopted > 0 || hidden > 0 || approximated > 0;
                    }
                    if (adopted > 0 || hidden > 0)
                    {
                        Log.Verbose("CAS round visual: " + adopted + " effect material(s) taken from the game" +
                                    (hidden > 0 ? ", " + hidden + " distortion renderer(s) hidden" : string.Empty) +
                                    (approximated > 0 ? ", " + approximated + " left approximated" : string.Empty) + ".");
                    }
                    return;
                }

                int flames = 0;
                Transform[] nodes = round.GetComponentsInChildren<Transform>(true);
                for (int t = 0; t < nodes.Length; t++)
                {
                    if (nodes[t] != null && nodes[t].name.StartsWith("CFS Motor Flame", StringComparison.Ordinal))
                    {
                        flames++;
                    }
                }

                Log.Verbose("CAS missile visual: " + adopted + " effect material(s) taken from the game, " +
                            approximated + " left approximated" +
                            (hidden > 0 ? ", " + hidden + " distortion renderer(s) hidden" : string.Empty) +
                            ", " + flames + " motor flame object(s) wired for burnout.");
                if (flames == 0)
                {
                    Log.Warn("CAS missile visual: this round has no motor flame objects, so there is nothing " +
                             "to burn out (rebuild cas_assets with CasMissileComposer).");
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS missile visual repair failed: " + ex);
            }
        }

        private static bool LooksLikeDistortion(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            for (int i = 0; i < HideWhenApproximate.Length; i++)
            {
                if (name.IndexOf(HideWhenApproximate[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The game's own material of that name, or null. Our bundle's copies are skipped: the whole point
        /// is to get out of the approximated material, and the bundle copy is the only other candidate
        /// with this name.
        /// </summary>
        private static Material FindGameMaterial(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            Material found = CasBundleMaterialRepair.FindGameMaterial(name);
            if (_reported.Add(name))
            {
                Log.Info("CAS round visual: effect material '" + name + "' " +
                         (found != null
                             ? "taken from the game (shader '" + found.shader.name + "')"
                             : "has no loaded game copy - the bundled approximation is used for it"));
            }
            return found;
        }
    }
}
