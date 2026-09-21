using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomFireSupport
{
    /// <summary>
    /// Rebuilds the materials that ship inside the "cas_assets" bundle with the GAME'S OWN shaders.
    ///
    /// Why this exists: the bundle is packed from an AssetRipper export, and an exporter cannot recover
    /// compiled shader bytecode - every exported shader is a placeholder that returns opaque white. The
    /// real shaders only exist inside the game build, so no amount of work on the exported project can
    /// reproduce GHPC's smoke, fire and flare effects faithfully. What CAN be reproduced is the material
    /// data: the exported materials keep their original textures, tints, atlas grids and shader keywords.
    ///
    /// So the mod keeps that data in a generated table (CasMaterialRecipe, recovered from the pre-repair
    /// bundle by the editor tool CasMaterialRecipeDump), and here - once the bundle is loaded - it looks up
    /// each material's original shader with Shader.Find, assigns it and re-applies every property and
    /// keyword. The result is the game's own effect, one to one. Only a shader missing from the running
    /// build falls back to the bundled simplified flipbook shader (still no white boxes, just an
    /// approximation), and the log says exactly which ones.
    /// </summary>
    internal static class CasBundleMaterialRepair
    {
        private const string FallbackShaderPrefix = "CustomFireSupport/";

        private sealed class Recipe
        {
            internal string Shader = string.Empty;
            internal string MainTexture = string.Empty;
            internal float Columns = 1f;
            internal float Rows = 1f;
            internal float Fps = 4f;
            internal string[] Keywords = new string[0];
            internal readonly Dictionary<string, string> Textures = new Dictionary<string, string>();
            internal readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            internal readonly Dictionary<string, Vector4> Vectors = new Dictionary<string, Vector4>();
        }

        private static Dictionary<string, Recipe> _recipes;
        private static Dictionary<string, Texture> _textures;
        private static bool _done;

        internal static void ClearSceneIndex()
        {
            _done = false;
            _textures = null;
        }

        internal static void RefreshForScene()
        {
            _done = false;
            _textures = null;
            RepairBundleMaterials();
        }

        /// <summary>
        /// Runs once per session, right after the bundle is loaded. Safe to call again: it does nothing.
        /// </summary>
        internal static void RepairBundleMaterials()
        {
            if (_done)
            {
                return;
            }
            _done = true;

            try
            {
                EnsureRecipes();
                if (_recipes.Count == 0)
                {
                    Log.Warn("CAS material repair: no material recipe found in this build - the bundled " +
                             "materials keep their simplified shaders.");
                    return;
                }

                int materials = 0;
                int restored = 0;
                int recovered = 0;
                int approximated = 0;
                int unknown = 0;
                int hidden = 0;
                List<string> missingShaders = new List<string>();
                HashSet<Material> seen = new HashSet<Material>();
                List<GameObject> prefabs = CasPrewarmer.BundlePrefabs;

                for (int p = 0; p < prefabs.Count; p++)
                {
                    GameObject prefab = prefabs[p];
                    if (prefab == null)
                    {
                        continue;
                    }

                    Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        Renderer renderer = renderers[r];
                        if (renderer == null)
                        {
                            continue;
                        }

                        if (IsTveElement(renderer))
                        {
                            // GHPC's effects use Boxophobic "The Visual Engine" push-interaction elements
                            // (wind turbulence) as helpers. In the game they are driven by TVE's own global
                            // state and only ever show as a subtle distortion; loaded from the bundle they
                            // draw their raw sheet instead - "Explosion Wind Turbulence (TVE)" is a
                            // horizontal billboard field whose particles reach 200 m, which is exactly the
                            // big flat translucent panels that float around the smoke. Pushing the smoke
                            // particles does not need the renderer, so it is switched off.
                            renderer.enabled = false;
                            hidden++;
                            continue;
                        }

                        Material[] shared = renderer.sharedMaterials;
                        for (int m = 0; m < shared.Length; m++)
                        {
                            Material material = shared[m];
                            if (material == null || !seen.Add(material))
                            {
                                continue;
                            }
                            materials++;

                            Recipe recipe;
                            if (!_recipes.TryGetValue(material.name, out recipe))
                            {
                                // No recipe: the material came with a newer asset (the composed missile
                                // prefabs). The bundle is packed from an AssetRipper export, where every
                                // placeholder shader keeps the ORIGINAL shader's name, so the game's real
                                // shader can still be found - and the material's own exported properties
                                // are already on it. Without this the material would render pure white.
                                if (RecoverByPlaceholderName(material, missingShaders))
                                {
                                    recovered++;
                                }
                                else
                                {
                                    unknown++;
                                }
                                continue;
                            }

                            if (RestoreWithGameShader(material, recipe, missingShaders))
                            {
                                restored++;
                            }
                            else
                            {
                                ApplyFallback(material, recipe);
                                approximated++;
                            }
                        }
                    }
                }

                Log.Info("CAS material repair: " + restored + " of " + materials + " bundled material(s) rebuilt " +
                         "with the game's own shaders" +
                         (recovered > 0
                             ? ", " + recovered + " more recovered from their placeholder shader's name (new assets)"
                             : string.Empty) +
                         (approximated > 0 ? ", " + approximated + " fell back to the bundled flipbook shader" : string.Empty) +
                         (unknown > 0 ? ", " + unknown + " had no recipe" : string.Empty) + ".");
                if (missingShaders.Count > 0)
                {
                    Log.Warn("CAS material repair: these shaders are not in this build, so those materials use the " +
                             "approximation: " + string.Join(", ", missingShaders.ToArray()));
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS material repair failed: " + ex);
            }
        }

        /// <summary>
        /// True for a renderer that draws a Boxophobic "The Visual Engine" helper element (the wind /
        /// vegetation push interaction fields). GHPC's own VegInteractLod.Start switches their renderer off -
        /// they only exist to push other particles around - and the loaded bundle copy is normally already
        /// disabled, but keeping the check here means a stray enabled one can never draw its 200 m sheet.
        /// </summary>
        private static bool IsTveElement(Renderer renderer)
        {
            Material material = renderer.sharedMaterial;
            if (material == null || material.shader == null)
            {
                return false;
            }
            return material.shader.name.StartsWith("BOXOPHOBIC/The Visual Engine", StringComparison.Ordinal) ||
                   material.name.IndexOf("TVE Element", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Last resort for a bundled material with no recipe: the AssetRipper export gives every
        /// placeholder shader the ORIGINAL shader's name, so the game's real shader can be looked up by
        /// that name. The material's own exported textures / numbers / keywords are already on it, so
        /// assigning the shader is all it takes - properties the new shader does not declare are simply
        /// invisible to it. This is what keeps a newly bundled asset (the composed AGM / missile
        /// prefabs) from rendering its placeholder white.
        /// </summary>
        private static bool RecoverByPlaceholderName(Material material, List<string> missingShaders)
        {
            Shader current = material.shader;
            if (current == null)
            {
                return false;
            }

            string name = current.name;
            if (string.IsNullOrEmpty(name) ||
                name.StartsWith(FallbackShaderPrefix, StringComparison.Ordinal) ||
                name.StartsWith("Hidden/", StringComparison.Ordinal))
            {
                return false;
            }

            Shader real = Shader.Find(name);
            if (real == null || ReferenceEquals(real, current))
            {
                if (real == null && !missingShaders.Contains(name))
                {
                    missingShaders.Add(name);
                }
                return false;
            }

            material.shader = real;
            return true;
        }

        /// <summary>
        /// Assigns the material's original shader (when this build has it) and re-applies the recorded
        /// textures, numbers, colours and keywords. False when the shader is missing.
        /// </summary>
        private static bool RestoreWithGameShader(Material material, Recipe recipe, List<string> missingShaders)
        {
            Shader shader = Shader.Find(recipe.Shader);
            if (shader == null)
            {
                if (!missingShaders.Contains(recipe.Shader))
                {
                    missingShaders.Add(recipe.Shader);
                }
                return false;
            }

            material.shader = shader;

            HashSet<string> properties = new HashSet<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                properties.Add(shader.GetPropertyName(i));
            }

            foreach (KeyValuePair<string, string> pair in recipe.Textures)
            {
                if (!properties.Contains(pair.Key))
                {
                    continue;
                }
                Texture texture = FindTexture(pair.Value);
                if (texture != null)
                {
                    material.SetTexture(pair.Key, texture);
                }
            }

            int propertyIndex;
            foreach (KeyValuePair<string, float> pair in recipe.Floats)
            {
                if (properties.Contains(pair.Key))
                {
                    material.SetFloat(pair.Key, pair.Value);
                    continue;
                }
                // a vector property whose components are all the same arrives as a float
                propertyIndex = IndexOf(shader, pair.Key);
                if (propertyIndex >= 0 && shader.GetPropertyType(propertyIndex) == ShaderPropertyType.Vector)
                {
                    material.SetVector(pair.Key, new Vector4(pair.Value, pair.Value, pair.Value, pair.Value));
                }
            }

            foreach (KeyValuePair<string, Vector4> pair in recipe.Vectors)
            {
                propertyIndex = IndexOf(shader, pair.Key);
                if (propertyIndex < 0)
                {
                    continue;
                }
                if (shader.GetPropertyType(propertyIndex) == ShaderPropertyType.Color)
                {
                    material.SetColor(pair.Key, new Color(pair.Value.x, pair.Value.y, pair.Value.z, pair.Value.w));
                }
                else if (shader.GetPropertyType(propertyIndex) == ShaderPropertyType.Vector)
                {
                    material.SetVector(pair.Key, pair.Value);
                }
            }

            // Keywords drive whole branches of these shaders (particle alpha, blackbody emission,
            // flipbook single row, ...), so they are restored exactly as recorded.
            material.shaderKeywords = recipe.Keywords;
            return true;
        }

        /// <summary>
        /// The bundle's own flipbook shader with as much of the original material data as it understands:
        /// the atlas grid, the main texture and the tint.
        /// </summary>
        private static void ApplyFallback(Material material, Recipe recipe)
        {
            Shader current = material.shader;
            if (current == null || !current.name.StartsWith(FallbackShaderPrefix, StringComparison.Ordinal))
            {
                return; // not one of ours - leave it alone
            }

            Texture main = FindTexture(recipe.MainTexture);
            if (main != null)
            {
                material.SetTexture("_MainTex", main);
            }
            if (material.HasProperty("_Columns"))
            {
                material.SetFloat("_Columns", recipe.Columns);
            }
            if (material.HasProperty("_Rows"))
            {
                material.SetFloat("_Rows", recipe.Rows);
            }
            if (material.HasProperty("_FPS"))
            {
                material.SetFloat("_FPS", recipe.Fps);
            }

            Vector4 tint;
            if (!recipe.Vectors.TryGetValue("_HDRTint", out tint) &&
                !recipe.Vectors.TryGetValue("_HDRColor", out tint) &&
                !recipe.Vectors.TryGetValue("_Color", out tint))
            {
                return;
            }
            material.SetColor("_TintColor", new Color(Mathf.Clamp01(tint.x), Mathf.Clamp01(tint.y),
                Mathf.Clamp01(tint.z), 1f));
        }

        private static int IndexOf(Shader shader, string property)
        {
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(shader.GetPropertyName(i), property, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        private static Texture FindTexture(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (_textures == null)
            {
                _textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
                Texture[] loaded = Resources.FindObjectsOfTypeAll<Texture>();
                for (int i = 0; i < loaded.Length; i++)
                {
                    Texture texture = loaded[i];
                    if (texture != null && !_textures.ContainsKey(texture.name))
                    {
                        _textures.Add(texture.name, texture);
                    }
                }
            }

            Texture found;
            return _textures.TryGetValue(name, out found) ? found : null;
        }

        private static void EnsureRecipes()
        {
            if (_recipes != null)
            {
                return;
            }

            _recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal);
            string[] entries = CasMaterialRecipe.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                Recipe recipe = Parse(entries[i]);
                if (recipe != null)
                {
                    _recipes[entries[i].Substring(0, entries[i].IndexOf('\t'))] = recipe;
                }
            }
        }

        private static Recipe Parse(string line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 2)
            {
                return null;
            }

            Recipe recipe = new Recipe();
            recipe.Shader = parts[1];
            if (parts.Length > 2)
            {
                ParseAssignments(parts[2], delegate(string key, string value) { recipe.Textures[key] = value; });
                recipe.MainTexture = MainTextureOf(recipe);
            }
            if (parts.Length > 3)
            {
                ParseAssignments(parts[3], delegate(string key, string value)
                {
                    float parsed;
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        recipe.Floats[key] = parsed;
                    }
                });
            }
            if (parts.Length > 4)
            {
                ParseAssignments(parts[4], delegate(string key, string value)
                {
                    string[] numbers = value.Split(',');
                    if (numbers.Length < 3)
                    {
                        return;
                    }
                    float x;
                    float y;
                    float z;
                    float w;
                    if (!float.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                        !float.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                        !float.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    {
                        return;
                    }
                    if (numbers.Length < 4 ||
                        !float.TryParse(numbers[3], NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                    {
                        w = 1f;
                    }
                    recipe.Vectors[key] = new Vector4(x, y, z, w);
                });
            }
            if (parts.Length > 5)
            {
                string[] atlas = parts[5].Split('x', '@');
                float columns;
                float rows;
                float fps;
                if (atlas.Length >= 3 &&
                    float.TryParse(atlas[0], NumberStyles.Float, CultureInfo.InvariantCulture, out columns) &&
                    float.TryParse(atlas[1], NumberStyles.Float, CultureInfo.InvariantCulture, out rows) &&
                    float.TryParse(atlas[2], NumberStyles.Float, CultureInfo.InvariantCulture, out fps))
                {
                    recipe.Columns = Mathf.Max(1f, columns);
                    recipe.Rows = Mathf.Max(1f, rows);
                    recipe.Fps = Mathf.Max(1f, fps);
                }
            }
            if (parts.Length > 6)
            {
                recipe.Keywords = parts[6].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }
            return recipe;
        }

        private static string MainTextureOf(Recipe recipe)
        {
            string[] preference = { "_Render", "_Texture", "_MainTex", "_BaseMap", "_BaseColorMap" };
            for (int i = 0; i < preference.Length; i++)
            {
                string name;
                if (recipe.Textures.TryGetValue(preference[i], out name))
                {
                    return name;
                }
            }
            return string.Empty;
        }

        private static void ParseAssignments(string block, Action<string, string> assign)
        {
            if (string.IsNullOrEmpty(block))
            {
                return;
            }

            string[] entries = block.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                int split = entries[i].IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }
                assign(entries[i].Substring(0, split), entries[i].Substring(split + 1));
            }
        }
    }
}
