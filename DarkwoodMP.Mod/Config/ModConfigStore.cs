using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DWMPHorde.Config
{
    /// <summary>
    /// BepInEx-compatible INI config store used by both loaders.
    /// Reads/writes the same section/key shape as ConfigFile so existing
    /// com.yokware.branch.cfg values keep working under BepInEx.
    /// </summary>
    public sealed class ModConfigStore
    {
        private readonly string _path;
        private readonly string _headerName;
        private readonly string _headerVersion;
        private readonly string _headerGuid;
        private readonly Dictionary<string, Dictionary<string, string>> _values =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BoundEntry> _bound = new List<BoundEntry>(48);
        private bool _dirty;

        private sealed class BoundEntry
        {
            public string Section;
            public string Key;
            public string Description;
            public string TypeName;
            public string DefaultText;
            public Action<object> ApplyRaw;
            public Func<object> ReadValue;
        }

        public ModConfigStore(string path, string headerName, string headerVersion, string headerGuid)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _headerName = headerName ?? "";
            _headerVersion = headerVersion ?? "";
            _headerGuid = headerGuid ?? "";
            LoadFile();
        }

        public string ConfigFilePath => _path;

        public ModSetting<T> Bind<T>(string section, string key, T defaultValue, string description = "")
        {
            string raw = TryGetRaw(section, key);
            T value = defaultValue;
            if (raw != null && TryParse(raw, out T parsed))
                value = parsed;
            else
            {
                SetRaw(section, key, FormatValue(defaultValue));
                _dirty = true;
            }

            var setting = new ModSetting<T>(this, section, key, value);
            _bound.Add(new BoundEntry
            {
                Section = section,
                Key = key,
                Description = description ?? "",
                TypeName = TypeLabel(typeof(T)),
                DefaultText = FormatValue(defaultValue),
                ApplyRaw = obj =>
                {
                    if (obj is T t)
                        setting.Value = t;
                },
                ReadValue = () => setting.Value
            });
            return setting;
        }

        internal void NotifyValueChanged(string section, string key, object value)
        {
            SetRaw(section, key, FormatValue(value));
            _dirty = true;
            Save();
        }

        public void Save()
        {
            if (!_dirty && File.Exists(_path))
            {
                // Still rewrite so new binds get descriptions on first Bind pass.
            }

            string dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder(4096);
            sb.Append("## Settings file was created by plugin ").Append(_headerName);
            if (!string.IsNullOrEmpty(_headerVersion))
                sb.Append(" v").Append(_headerVersion);
            sb.AppendLine();
            sb.Append("## Plugin GUID: ").Append(_headerGuid).AppendLine();
            sb.AppendLine();

            // Stable section order: first-seen bind order, keys within section in bind order.
            var sections = new List<string>();
            var seenSec = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BoundEntry e in _bound)
            {
                if (seenSec.Add(e.Section))
                    sections.Add(e.Section);
            }

            foreach (string section in sections)
            {
                sb.Append('[').Append(section).Append(']').AppendLine();
                sb.AppendLine();
                foreach (BoundEntry e in _bound)
                {
                    if (!string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(e.Description))
                        sb.Append("## ").Append(e.Description).AppendLine();
                    sb.Append("# Setting type: ").Append(e.TypeName).AppendLine();
                    sb.Append("# Default value: ").Append(e.DefaultText).AppendLine();
                    object cur = e.ReadValue != null ? e.ReadValue() : null;
                    sb.Append(e.Key).Append(" = ").Append(FormatValue(cur)).AppendLine();
                    sb.AppendLine();
                }
            }

            File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
            _dirty = false;
        }

        private void LoadFile()
        {
            if (!File.Exists(_path))
                return;

            string section = "";
            foreach (string line in File.ReadAllLines(_path))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                {
                    section = t.Substring(1, t.Length - 2).Trim();
                    continue;
                }

                int eq = t.IndexOf('=');
                if (eq <= 0 || string.IsNullOrEmpty(section))
                    continue;
                string key = t.Substring(0, eq).Trim();
                string val = t.Substring(eq + 1).Trim();
                SetRaw(section, key, val);
            }
        }

        private string TryGetRaw(string section, string key)
        {
            if (!_values.TryGetValue(section, out Dictionary<string, string> map))
                return null;
            return map.TryGetValue(key, out string v) ? v : null;
        }

        private void SetRaw(string section, string key, string value)
        {
            if (!_values.TryGetValue(section, out Dictionary<string, string> map))
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _values[section] = map;
            }
            map[key] = value ?? "";
        }

        private static string TypeLabel(Type t)
        {
            if (t == typeof(bool)) return "Boolean";
            if (t == typeof(int)) return "Int32";
            if (t == typeof(float)) return "Single";
            if (t == typeof(string)) return "String";
            return t.Name;
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "";
            if (value is bool b) return b ? "true" : "false";
            if (value is float f) return f.ToString(CultureInfo.InvariantCulture);
            if (value is IFormattable fmt && !(value is string))
                return fmt.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString() ?? "";
        }

        private static bool TryParse<T>(string raw, out T value)
        {
            value = default;
            Type t = typeof(T);
            try
            {
                if (t == typeof(string))
                {
                    value = (T)(object)(raw ?? "");
                    return true;
                }
                if (t == typeof(bool))
                {
                    if (bool.TryParse(raw, out bool b))
                    {
                        value = (T)(object)b;
                        return true;
                    }
                    // BepInEx sometimes writes True/False; already covered. Also 0/1.
                    if (raw == "1") { value = (T)(object)true; return true; }
                    if (raw == "0") { value = (T)(object)false; return true; }
                    return false;
                }
                if (t == typeof(int))
                {
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    {
                        value = (T)(object)i;
                        return true;
                    }
                    return false;
                }
                if (t == typeof(float))
                {
                    if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    {
                        value = (T)(object)f;
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }
    }
}
