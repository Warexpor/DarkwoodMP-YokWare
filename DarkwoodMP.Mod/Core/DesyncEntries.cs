using System;
using System.Collections.Generic;
using System.Text;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Desync check entries: one <c>key\tvalue</c> line per piece of state, sorted by key so the
    /// host and a client hash the same text for the same state. Pure (no Unity), unit tested.
    /// </summary>
    internal static class DesyncEntries
    {
        internal static string Format(List<KeyValuePair<string, string>> entries)
        {
            entries.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
            });
            var sb = new StringBuilder(entries.Count * 24);
            string last = null;
            int dup = 1;
            for (int i = 0; i < entries.Count; i++)
            {
                string key = Clean(entries[i].Key);
                // Two objects under one key (same name at the same spot): keep both, numbered.
                if (key == last)
                {
                    dup++;
                    key = key + "#" + dup;
                }
                else
                {
                    last = key;
                    dup = 1;
                }
                sb.Append(key).Append('\t').Append(Clean(entries[i].Value)).Append('\n');
            }
            return sb.ToString();
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            if (s.IndexOf('\t') < 0 && s.IndexOf('\n') < 0)
                return s;
            return s.Replace('\t', ' ').Replace('\n', ' ');
        }

        /// <summary>FNV-1a over the formatted text.</summary>
        internal static uint Hash(string formatted)
        {
            uint h = 2166136261u;
            if (formatted == null)
                return h;
            for (int i = 0; i < formatted.Length; i++)
            {
                h ^= formatted[i];
                h *= 16777619u;
            }
            return h;
        }

        internal static Dictionary<string, string> Parse(string formatted)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(formatted))
                return map;
            int start = 0;
            while (start < formatted.Length)
            {
                int nl = formatted.IndexOf('\n', start);
                if (nl < 0)
                    nl = formatted.Length;
                int tab = formatted.IndexOf('\t', start, nl - start);
                if (tab > start)
                    map[formatted.Substring(start, tab - start)] = formatted.Substring(tab + 1, nl - tab - 1);
                start = nl + 1;
            }
            return map;
        }

        internal static int Count(string formatted)
        {
            int n = 0;
            if (formatted == null)
                return 0;
            for (int i = 0; i < formatted.Length; i++)
                if (formatted[i] == '\n')
                    n++;
            return n;
        }

        /// <summary>
        /// Whether the host's and the client's value for one key agree. A missing side is null.
        /// </summary>
        internal delegate bool SameFn(string key, string host, string client);

        internal static bool Exact(string key, string host, string client) => string.Equals(host, client, StringComparison.Ordinal);

        internal struct Diff
        {
            public string Key;
            public string Host;
            public string Client;
        }

        /// <param name="hostTruncated">The host's list was cut: keys only the client has are not evidence.</param>
        internal static List<Diff> Compare(Dictionary<string, string> host, Dictionary<string, string> client,
            SameFn same, bool hostTruncated = false)
        {
            var diffs = new List<Diff>();
            same = same ?? Exact;
            foreach (KeyValuePair<string, string> kv in host)
            {
                client.TryGetValue(kv.Key, out string c);
                if (!same(kv.Key, kv.Value, c))
                    diffs.Add(new Diff { Key = kv.Key, Host = kv.Value, Client = c });
            }
            if (!hostTruncated)
            {
                foreach (KeyValuePair<string, string> kv in client)
                {
                    if (host.ContainsKey(kv.Key))
                        continue;
                    if (!same(kv.Key, null, kv.Value))
                        diffs.Add(new Diff { Key = kv.Key, Host = null, Client = kv.Value });
                }
            }
            PairNeighbours(diffs, same);
            diffs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return diffs;
        }

        /// <summary>
        /// An object right on a grid line is keyed one step apart on the two machines
        /// ("Wardrobe@12056,11,11370" here, "...,11371" there): a host-only and a client-only entry
        /// of the same name one step apart that agree are one object, not two missing ones.
        /// </summary>
        private static void PairNeighbours(List<Diff> diffs, SameFn same)
        {
            for (int i = 0; i < diffs.Count; i++)
            {
                if (diffs[i].Client != null || diffs[i].Host == null)
                    continue;
                for (int j = 0; j < diffs.Count; j++)
                {
                    if (diffs[j].Host != null || diffs[j].Client == null)
                        continue;
                    if (!Neighbours(diffs[i].Key, diffs[j].Key) || !same(diffs[i].Key, diffs[i].Host, diffs[j].Client))
                        continue;
                    int hi = Math.Max(i, j), lo = Math.Min(i, j);
                    diffs.RemoveAt(hi);
                    diffs.RemoveAt(lo);
                    i = -1;
                    break;
                }
            }
        }

        /// <summary>"Name@x,y,z" keys with the same name, each coordinate at most one grid step apart.</summary>
        internal static bool Neighbours(string a, string b)
        {
            int ia = a.LastIndexOf('@'), ib = b.LastIndexOf('@');
            if (ia <= 0 || ib <= 0 || ia != ib || string.CompareOrdinal(a, 0, b, 0, ia) != 0)
                return false;
            string[] pa = a.Substring(ia + 1).Split(','), pb = b.Substring(ib + 1).Split(',');
            if (pa.Length != pb.Length || pa.Length == 0)
                return false;
            for (int k = 0; k < pa.Length; k++)
            {
                if (!int.TryParse(pa[k], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(pb[k], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int y)
                    || Math.Abs(x - y) > 1)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Client: which differences are real. A difference is reported once it shows in two checks of
    /// its section in a row (a change still in flight shows in one); reported once, then again
    /// as resolved when it goes away.
    /// </summary>
    internal sealed class DesyncLedger
    {
        internal const int MaxLinesPerCheck = 25;

        private readonly Dictionary<byte, int> _checks = new Dictionary<byte, int>();
        private readonly Dictionary<string, int> _pending = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _reported = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _scratch = new List<string>();

        internal int ReportedCount => _reported.Count;

        internal void Clear()
        {
            _checks.Clear();
            _pending.Clear();
            _reported.Clear();
        }

        /// <summary>One check of a section; returns the lines to log (new desyncs, resolved ones).</summary>
        internal List<string> Evaluate(byte section, string sectionName, List<DesyncEntries.Diff> diffs)
        {
            var lines = new List<string>();
            _checks.TryGetValue(section, out int prev);
            int idx = prev + 1;
            _checks[section] = idx;
            string prefix = section + "/";
            var now = new HashSet<string>(StringComparer.Ordinal);
            int hidden = 0;

            if (diffs != null)
            {
                foreach (DesyncEntries.Diff d in diffs)
                {
                    string k = prefix + d.Key;
                    now.Add(k);
                    if (_reported.ContainsKey(k))
                        continue;
                    if (_pending.TryGetValue(k, out int since) && since == idx - 1)
                    {
                        _pending.Remove(k);
                        _reported[k] = sectionName + " " + d.Key;
                        string line = "DESYNC " + sectionName + " " + d.Key
                            + ": host=" + Show(d.Host) + " client=" + Show(d.Client);
                        if (lines.Count < MaxLinesPerCheck)
                            lines.Add(line);
                        else
                            hidden++;
                    }
                    else
                    {
                        _pending[k] = idx;
                    }
                }
            }

            _scratch.Clear();
            foreach (string k in _reported.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal) && !now.Contains(k))
                    _scratch.Add(k);
            foreach (string k in _scratch)
            {
                if (lines.Count < MaxLinesPerCheck)
                    lines.Add("resolved " + _reported[k]);
                else
                    hidden++;
                _reported.Remove(k);
            }

            _scratch.Clear();
            foreach (string k in _pending.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal) && !now.Contains(k))
                    _scratch.Add(k);
            foreach (string k in _scratch)
                _pending.Remove(k);

            if (hidden > 0)
                lines.Add("... and " + hidden + " more in " + sectionName);
            return lines;
        }

        private static string Show(string v) => v == null ? "<none>" : (v.Length == 0 ? "\"\"" : v);
    }
}
