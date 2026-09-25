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

        private static Dictionary<string, List<Recipe>> _recipes;
        private static Dictionary<string, Texture> _textures;
        private static bool _done;

        /// <summary>
        /// True once a full pass left no material on a bundled approximation, i.e. once a later scene's
        /// sweep of every material and shader in memory cannot change any decision (see
        /// <see cref="RepairBundleMaterials"/>).
        /// </summary>
        private static bool _everythingResolved;

        private static readonly Dictionary<string, Material> GameMaterials = new Dictionary<string, Material>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Shader> GameShaders = new Dictionary<string, Shader>(StringComparer.Ordinal);
        private static readonly HashSet<Material> Restored = new HashSet<Material>();
        private static readonly HashSet<Renderer> HiddenDistortion = new HashSet<Renderer>();
        private static readonly HashSet<string> RequiredMaterials = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> RequiredTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Shader, Dictionary<string, ShaderPropertyType>> ShaderProperties =
            new Dictionary<Shader, Dictionary<string, ShaderPropertyType>>();

        internal static void ClearSceneIndex()
        {
            // The per-scene texture cache is dropped, but the NATIVE DONOR INDEX is deliberately kept. It
            // holds at most one material per bundled material name - about eighty game ASSETS, never scene
            // objects - and it is what CasMissileVisualRepair adopts materials from when a round spawns,
            // so throwing it away would make the per-instance repair go blind until the next scan.
            // Rebuilding it costs two Resources.FindObjectsOfTypeAll sweeps of every loaded material and
            // shader; keeping it is what lets RepairBundleMaterials skip those sweeps once nothing is left
            // on a bundled approximation.
            _textures = null;
            _done = false;
        }

        internal static void RefreshForScene()
        {
            _done = false;
            _textures = null;
            CasMissileVisualRepair.ResetForScene();
            RepairBundleMaterials();
        }

        internal static Material FindGameMaterial(string name)
        {
            Material material;
            return GameMaterials.TryGetValue(name, out material) && material != null ? material : null;
        }

        private static Shader FindGameShader(string name)
        {
            Shader shader;
            if (GameShaders.TryGetValue(name, out shader) && shader != null) return shader;
            shader = Shader.Find(name);
            return shader != null && shader.isSupported && !CasPrewarmer.BundleShaders.Contains(shader)
                ? shader : null;
        }

        /// <summary>One scan at load, then retry unresolved materials after scene content is ready.</summary>
        internal static void RepairBundleMaterials()
        {
            if (_done || CasPrewarmer.BundleMaterials.Count == 0) return;
            try
            {
                EnsureRecipes();
                if (_everythingResolved)
                {
                    // Every bundled material already sits on one of the game's own shaders, so a fresh
                    // sweep of every material and shader in memory cannot change a single decision. This
                    // is what keeps two Resources.FindObjectsOfTypeAll passes off every later scene load:
                    // a session opens half a dozen scenes, and each one used to rescan the whole game to
                    // find nothing. The renderer pass below is idempotent and still has to run once.
                    int stillHidden = ApplyRendererPass();
                    _done = true;
                    Log.Verbose("CAS material repair: skipped (nothing is left on a bundled approximation); " +
                                stillHidden + " TVE helper renderer(s) disabled.");
                    return;
                }

                GameShaders.Clear();
                GameMaterials.Clear();
                RequiredMaterials.Clear();
                foreach (Material material in CasPrewarmer.BundleMaterials)
                    if (material != null) RequiredMaterials.Add(material.name);
                foreach (Shader shader in Resources.FindObjectsOfTypeAll<Shader>())
                {
                    if (shader != null && shader.isSupported && !CasPrewarmer.BundleShaders.Contains(shader))
                        GameShaders[shader.name] = shader;
                }
                foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (material == null || !RequiredMaterials.Contains(material.name) ||
                        CasPrewarmer.IsFromOurBundle(material) || material.shader == null ||
                        !material.shader.isSupported || CasPrewarmer.BundleShaders.Contains(material.shader) ||
                        material.shader.name.StartsWith(FallbackShaderPrefix, StringComparison.Ordinal)) continue;
                    if (!GameMaterials.ContainsKey(material.name)) GameMaterials.Add(material.name, material);
                }

                int restored = 0, recovered = 0, approximated = 0, unknown = 0;
                List<string> missingShaders = new List<string>();
                foreach (Material material in CasPrewarmer.BundleMaterials)
                {
                    if (material == null) continue;
                    if (Restored.Contains(material) && material.shader != null && material.shader.isSupported) continue;
                    Restored.Remove(material);
                    Recipe recipe = RecipeFor(material);
                    Material donor = FindGameMaterial(material.name);
                    if (donor != null && (recipe == null || (donor.shader.name == recipe.Shader &&
                        (string.IsNullOrEmpty(recipe.MainTexture) || HasTextureNamed(donor, recipe.MainTexture)))))
                    {
                        material.shader = donor.shader;
                        material.CopyPropertiesFromMaterial(donor);
                        material.renderQueue = donor.renderQueue;
                        Restored.Add(material);
                        restored++;
                    }
                    else if (recipe != null && RestoreWithGameShader(material, recipe, missingShaders))
                    {
                        Restored.Add(material);
                        restored++;
                    }
                    else if (recipe != null)
                    {
                        if (ApplyFallback(material, recipe)) approximated++;
                        else unknown++;
                    }
                    else if (RecoverByPlaceholderName(material, missingShaders))
                    {
                        Restored.Add(material);
                        recovered++;
                    }
                    else unknown++;
                }
                int hidden = ApplyRendererPass();
                _done = true;
                // "approximated" and "unresolved" are the materials that are NOT on a game shader; a later
                // scene may have the native material or shader loaded by then, so only while one of those
                // is left does the next scene need to scan again.
                _everythingResolved = approximated == 0 && unknown == 0;
                Log.Info("CAS material repair: " + CasPrewarmer.BundleMaterials.Count + " dependency material(s), " +
                    restored + " restored, " + recovered + " recovered by shader name, " + approximated +
                    " fallback, " + unknown + " unresolved; " + hidden + " TVE helper renderer(s) disabled.");
                if (missingShaders.Count > 0)
                    Log.Warn("CAS material repair: waiting for native shaders: " + string.Join(", ", missingShaders.ToArray()));
            }
            catch (Exception ex)
            {
                Log.Error("CAS material repair failed: " + ex);
            }
            finally
            {
                // Only the few native material donors are needed by later missile spawns.
                // Drop temporary texture/shader indexes even when a repair failed partway through.
                _textures = null;
                GameShaders.Clear();
                ShaderProperties.Clear();
            }
        }

        /// <summary>
        /// A heat-distortion map cannot be displayed by a plain colour/alpha shader. Keep smoke/fire
        /// visible; suppress only these helper renderers until native binding succeeds. Idempotent: a
        /// renderer that stops being approximated is switched back on. Returns how many are disabled.
        /// </summary>
        private static int ApplyRendererPass()
        {
            int hidden = 0;
            foreach (Renderer renderer in CasPrewarmer.BundleRenderers)
            {
                if (renderer == null) continue;
                if (IsTveElement(renderer))
                {
                    renderer.enabled = false;
                    hidden++;
                    continue;
                }
                Material material = renderer.sharedMaterial;
                bool approximate = material != null && material.shader != null &&
                    material.shader.name.StartsWith(FallbackShaderPrefix, StringComparison.Ordinal) &&
                    (renderer.name.IndexOf("distortion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     material.name.IndexOf("distortion", StringComparison.OrdinalIgnoreCase) >= 0);
                if (approximate && renderer.enabled)
                {
                    renderer.enabled = false;
                    HiddenDistortion.Add(renderer);
                }
                else if (!approximate && HiddenDistortion.Remove(renderer)) renderer.enabled = true;
            }
            return hidden;
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

            Shader real = FindGameShader(name);
            if (real == null || ReferenceEquals(real, current))
            {
                if (real == null && !missingShaders.Contains(name))
                {
                    missingShaders.Add(name);
                }
                return false;
            }

            material.shader = real;
            material.renderQueue = -1;
            return true;
        }

        /// <summary>
        /// Assigns the material's original shader (when this build has it) and re-applies the recorded
        /// textures, numbers, colours and keywords. False when the shader is missing.
        /// </summary>
        private static bool RestoreWithGameShader(Material material, Recipe recipe, List<string> missingShaders)
        {
            Shader shader = FindGameShader(recipe.Shader);
            if (shader == null)
            {
                if (!missingShaders.Contains(recipe.Shader))
                {
                    missingShaders.Add(recipe.Shader);
                }
                return false;
            }

            material.shader = shader;

            foreach (KeyValuePair<string, string> pair in recipe.Textures)
            {
                if (!material.HasProperty(pair.Key)) continue;
                // Keep the exact bundled texture reference when present; names need not be unique.
                Texture texture = material.GetTexture(pair.Key);
                if (texture == null || !string.Equals(texture.name, pair.Value, StringComparison.Ordinal))
                    texture = FindTexture(pair.Value);
                if (texture != null) material.SetTexture(pair.Key, texture);
            }

            Dictionary<string, ShaderPropertyType> properties = GetProperties(shader);
            ShaderPropertyType type;
            foreach (KeyValuePair<string, float> pair in recipe.Floats)
            {
                if (!properties.TryGetValue(pair.Key, out type)) continue;
                if (type == ShaderPropertyType.Vector)
                    material.SetVector(pair.Key, new Vector4(pair.Value, pair.Value, pair.Value, pair.Value));
                else if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
                    material.SetFloat(pair.Key, pair.Value);
            }

            foreach (KeyValuePair<string, Vector4> pair in recipe.Vectors)
            {
                if (!properties.TryGetValue(pair.Key, out type))
                {
                    continue;
                }
                if (type == ShaderPropertyType.Color)
                {
                    material.SetColor(pair.Key, new Color(pair.Value.x, pair.Value.y, pair.Value.z, pair.Value.w));
                }
                else if (type == ShaderPropertyType.Vector)
                {
                    material.SetVector(pair.Key, pair.Value);
                }
            }

            // Keywords drive whole branches of these shaders (particle alpha, blackbody emission,
            // flipbook single row, ...), so they are restored exactly as recorded.
            material.shaderKeywords = recipe.Keywords;
            material.renderQueue = -1;
            return true;
        }

        /// <summary>
        /// The bundle's own flipbook shader with as much of the original material data as it understands:
        /// the atlas grid, the main texture and the tint.
        /// </summary>
        private static bool ApplyFallback(Material material, Recipe recipe)
        {
            Shader current = material.shader;
            if (current == null || !current.name.StartsWith(FallbackShaderPrefix, StringComparison.Ordinal))
            {
                return false; // no compatible fallback is installed
            }

            Texture main = material.GetTexture("_MainTex");
            if (main == null || !string.Equals(main.name, recipe.MainTexture, StringComparison.Ordinal))
                main = FindTexture(recipe.MainTexture);
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

            if (material.HasProperty("_PackedDensity"))
                material.SetFloat("_PackedDensity", recipe.Shader.IndexOf("Channel Packed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    recipe.Shader.IndexOf("Colorizer", StringComparison.OrdinalIgnoreCase) >= 0 ? 1f : 0f);

            Vector4 tint;
            if (!recipe.Vectors.TryGetValue("_HDRTint", out tint) &&
                !recipe.Vectors.TryGetValue("_HDRColor", out tint) &&
                !recipe.Vectors.TryGetValue("_Color", out tint))
            {
                return true;
            }
            material.SetColor("_TintColor", new Color(Mathf.Clamp01(tint.x), Mathf.Clamp01(tint.y),
                Mathf.Clamp01(tint.z), 1f));
            return true;
        }

        private static Dictionary<string, ShaderPropertyType> GetProperties(Shader shader)
        {
            Dictionary<string, ShaderPropertyType> properties;
            if (ShaderProperties.TryGetValue(shader, out properties)) return properties;
            properties = new Dictionary<string, ShaderPropertyType>(StringComparer.Ordinal);
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                properties[shader.GetPropertyName(i)] = shader.GetPropertyType(i);
            }
            ShaderProperties.Add(shader, properties);
            return properties;
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
                    if (texture != null && RequiredTextures.Contains(texture.name) && !_textures.ContainsKey(texture.name))
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

            _recipes = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
            string[] entries = new string[CasMaterialRecipe.Entries.Length + CasNativeMaterialRecipe.Entries.Length];
            Array.Copy(CasMaterialRecipe.Entries, entries, CasMaterialRecipe.Entries.Length);
            Array.Copy(CasNativeMaterialRecipe.Entries, 0, entries, CasMaterialRecipe.Entries.Length,
                CasNativeMaterialRecipe.Entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                Recipe recipe = Parse(entries[i]);
                if (recipe != null)
                {
                    string name = entries[i].Substring(0, entries[i].IndexOf('\t'));
                    List<Recipe> variants;
                    if (!_recipes.TryGetValue(name, out variants))
                    {
                        variants = new List<Recipe>();
                        _recipes.Add(name, variants);
                    }
                    variants.Add(recipe);
                    foreach (string texture in recipe.Textures.Values) RequiredTextures.Add(texture);
                }
            }
        }

        private static Recipe RecipeFor(Material material)
        {
            List<Recipe> variants;
            if (!_recipes.TryGetValue(material.name, out variants)) return null;
            if (variants.Count == 1) return variants[0];
            // The source has several different materials called "Dust". Never let dump order
            // assign a different material's texture/atlas to a renderer with the same name.
            foreach (Recipe recipe in variants)
                if (!string.IsNullOrEmpty(recipe.MainTexture) && HasTextureNamed(material, recipe.MainTexture)) return recipe;
            return null;
        }

        private static bool HasTextureNamed(Material material, string name)
        {
            foreach (string property in material.GetTexturePropertyNames())
            {
                Texture texture = material.GetTexture(property);
                if (texture != null && string.Equals(texture.name, name, StringComparison.Ordinal)) return true;
            }
            return false;
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
