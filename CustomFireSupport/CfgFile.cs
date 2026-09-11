using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Locates and reads this mod's section of MelonPreferences.cfg.
    ///
    /// The file is the same one the user edits (Bin\UserData\MelonPreferences.cfg); MelonLoader still
    /// owns it and keeps the [CustomFireSupport] section alive across sessions, but the mod reads the
    /// values from the raw text (CfgSectionParser) rather than from MelonLoader's entry objects.
    /// </summary>
    internal static class CfgFile
    {
        private static string _path;

        internal static string Path
        {
            get
            {
                if (string.IsNullOrEmpty(_path))
                {
                    _path = ResolvePath();
                }
                return _path;
            }
        }

        /// <summary>Raw key/value pairs of the [CustomFireSupport] section (empty when unavailable).</summary>
        internal static Dictionary<string, string> ReadCustomSection()
        {
            try
            {
                string path = Path;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Log.Warn("config file not found: " + path);
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                return CfgSectionParser.ParseSection(File.ReadAllText(path), "CustomFireSupport");
            }
            catch (Exception ex)
            {
                Log.Error("could not read " + Path + ": " + ex.Message);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Appends the generated [CustomFireSupport] section when the file does not have one yet.
        /// Returns true when the file was written. MelonLoader's own sections and comments are left
        /// untouched (the text is appended, never rewritten).
        /// </summary>
        internal static bool EnsureSection(string sectionText)
        {
            try
            {
                string path = Path;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sectionText))
                {
                    return false;
                }

                string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                if (CfgSectionParser.ParseSection(existing, "CustomFireSupport").Count > 0)
                {
                    return false;
                }

                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string text = existing.TrimEnd('\n', '\r') + "\n\n" + sectionText.TrimEnd('\n') + "\n";
                File.WriteAllText(path, text);
                Log.Info("created the [CustomFireSupport] section in " + path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("could not write " + Path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The game executable lives in &lt;game&gt;\Bin, so the user data folder is its sibling.
        /// Application.dataPath is &lt;game&gt;\Bin\GHPC_Data.
        /// </summary>
        private static string ResolvePath()
        {
            try
            {
                DirectoryInfo bin = Directory.GetParent(Application.dataPath);
                if (bin != null)
                {
                    string candidate = System.IO.Path.Combine(bin.FullName, "UserData", "MelonPreferences.cfg");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    // UserData may not exist yet (first run): still use the canonical location.
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("could not derive the game folder from Application.dataPath: " + ex.Message);
            }
            return System.IO.Path.Combine("UserData", "MelonPreferences.cfg");
        }
    }
}
