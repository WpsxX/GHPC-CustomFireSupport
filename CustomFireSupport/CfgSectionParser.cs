using System;
using System.Collections.Generic;
using System.Globalization;

namespace CustomFireSupport
{
    /// <summary>
    /// Pure parser for one MelonPreferences.cfg section (no Unity / MelonLoader dependency, so it is
    /// unit-testable outside the game).
    ///
    /// The mod reads its own values from the cfg text instead of MelonLoader's entry objects: the
    /// entry layer silently mangles some values (observed: 30 -> 0.0, 0.8 -> 0.3,
    /// "Bombs,Rockets,..." -> "GunRun") and CreateEntry() throws when an entry already exists.
    /// Parsing the text ourselves is deterministic, hot-reloadable and testable.
    /// </summary>
    public static class CfgSectionParser
    {
        /// <summary>
        /// Extracts key/value pairs from <c>[section]</c> up to the next section header. Comments
        /// (whole-line and trailing), quotes, CRLF and a UTF-8 BOM are all handled. Later duplicate
        /// keys win, matching TOML semantics.
        /// </summary>
        public static Dictionary<string, string> ParseSection(string text, string section)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(section))
            {
                return result;
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            bool inSection = false;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim('\r', ' ', '\t');
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    string name = close > 1 ? line.Substring(1, close - 1).Trim().Trim('"') : string.Empty;
                    inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim().Trim('"');
                if (key.Length == 0)
                {
                    continue;
                }

                string value = StripTrailingComment(line.Substring(equals + 1).Trim());
                result[key] = Unquote(value.Trim());
            }
            return result;
        }

        /// <summary>Removes a trailing <c># comment</c> that is not inside a quoted string.</summary>
        private static string StripTrailingComment(string value)
        {
            bool inQuotes = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == '#' && !inQuotes)
                {
                    return value.Substring(0, i).Trim();
                }
            }
            return value;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2);
            }
            return value;
        }

        // ------------------------------------------------------------------
        // Typed getters (total: never throw, always return a usable value)
        // ------------------------------------------------------------------

        public static bool GetBool(IDictionary<string, string> values, string key, bool fallback)
        {
            string raw;
            if (values == null || !values.TryGetValue(key, out raw) || string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1" ||
                string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0" ||
                string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return fallback;
        }

        public static int GetInt(IDictionary<string, string> values, string key, int fallback)
        {
            string raw;
            if (values == null || !values.TryGetValue(key, out raw) || string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            int parsed;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            double asDouble;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out asDouble))
            {
                return (int)Math.Round(asDouble);
            }
            return fallback;
        }

        public static float GetFloat(IDictionary<string, string> values, string key, float fallback)
        {
            string raw;
            if (values == null || !values.TryGetValue(key, out raw) || string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            float parsed;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        public static string GetString(IDictionary<string, string> values, string key, string fallback)
        {
            string raw;
            if (values == null || !values.TryGetValue(key, out raw))
            {
                return fallback;
            }
            return raw ?? fallback;
        }
    }
}
