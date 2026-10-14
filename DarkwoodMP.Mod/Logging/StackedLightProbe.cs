using System.Collections.Generic;
using System.Text;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Diagnostic for "lights look too bright". Every vanilla Light2D draws its own mesh into the
    /// light buffer additively, so a spot only gets brighter when two lights draw there: two
    /// Light2D on the same spot with the same shape (a light spawned twice), or two Light2D sharing
    /// one mesh (a live light copied by Instantiate, see Light2DUnshare). In a session in the world
    /// this scans the drawn lights every few seconds and logs each new stack once, on both peers,
    /// so the host and client logs can be compared with what single player would draw.
    /// </summary>
    internal static class StackedLightProbe
    {
        private const float ScanSec = 5f;
        private const float SummarySec = 60f;

        private static readonly HashSet<string> _reported = new HashSet<string>(); // reset-in: Reset
        private static float _nextScan; // reset-in: Reset
        private static float _nextSummary; // reset-in: Reset

        private static readonly Dictionary<Shape, List<Light2D>> _byShape = new Dictionary<Shape, List<Light2D>>();
        /// <summary>The lists of the last scan, handed out again (each scan groups every drawn light).</summary>
        private static readonly Stack<List<Light2D>> _freeLists = new Stack<List<Light2D>>();

        /// <summary>A light's spot and shape, whole units (no string made per light per scan).</summary>
        private struct Shape : System.IEquatable<Shape>
        {
            public int X, Z, R, A, Y;
            public bool Equals(Shape o) => X == o.X && Z == o.Z && R == o.R && A == o.A && Y == o.Y;
            public override bool Equals(object obj) => obj is Shape o && Equals(o);
            public override int GetHashCode() => (((X * 397 ^ Z) * 397 ^ R) * 397 ^ A) * 397 ^ Y;
            public override string ToString() => X + "," + Z + "|r" + R + "|a" + A + "|y" + Y;
        }
        private static readonly Dictionary<int, List<Light2D>> _byMesh = new Dictionary<int, List<Light2D>>();

        public static void Reset()
        {
            _reported.Clear();
            _nextScan = 0f;
            _nextSummary = 0f;
        }

        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected || !net.IsHandshakeComplete)
                return;
            if (!Core.coreStarted || Core.loadingGame || Player.Instance == null)
                return;
            float now = Time.unscaledTime;
            if (now < _nextScan)
                return;
            _nextScan = now + ScanSec;
            try
            {
                Scan(net, now);
            }
            catch (System.Exception ex)
            {
                ModLog.WarnRate(LogCat.World, "stacked-light-probe", "[LightStack] scan: " + ex.Message, 30f);
            }
        }

        private static void Scan(LanNetworkManager net, float now)
        {
            Recycle(_byShape);
            Recycle(_byMesh);
            // Registry-backed once the world is seeded: no scene scan (it cost 20-40 ms per pass).
            Light2D[] lights = Sync.WorldQueryHelper.GetCachedSceneComponents<Light2D>();
            int drawn = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                Light2D l = lights[i];
                if (!IsDrawn(l))
                    continue;
                drawn++;
                Vector3 p = l.transform.position;
                var shape = new Shape
                {
                    X = Mathf.RoundToInt(p.x),
                    Z = Mathf.RoundToInt(p.z),
                    R = Mathf.RoundToInt(l.LightRadius),
                    A = Mathf.RoundToInt(l.LightConeAngle),
                    Y = Mathf.RoundToInt(l.transform.eulerAngles.y)
                };
                Add(_byShape, shape, l);
                if (l._mesh != null)
                    Add(_byMesh, l._mesh.GetInstanceID(), l);
            }

            string role = net.Role.ToString();
            foreach (var kv in _byShape)
            {
                if (kv.Value.Count < 2 || !_reported.Add("s:" + kv.Key + ":" + kv.Value.Count))
                    continue;
                ModLog.Warn(LogCat.World, "[LightStack] " + role + " " + kv.Value.Count
                    + " lights drawn on one spot (" + kv.Key + "): " + Describe(kv.Value));
            }
            foreach (var kv in _byMesh)
            {
                if (kv.Value.Count < 2 || !_reported.Add("m:" + Describe(kv.Value)))
                    continue;
                ModLog.Warn(LogCat.World, "[LightStack] " + role + " " + kv.Value.Count
                    + " lights share one mesh: " + Describe(kv.Value));
            }

            if (now >= _nextSummary)
            {
                _nextSummary = now + SummarySec;
                ModLog.Event(LogCat.World, "[LightStack] " + role + " lights=" + lights.Length
                    + " drawn=" + drawn + " at " + Player.Instance.transform.position.ToString("F0"));
            }
        }

        private static bool IsDrawn(Light2D l)
        {
            if (l == null || !l.isActiveAndEnabled || !l.LightEnabled || l.LightRadius <= 1f)
                return false;
            MeshRenderer r = l.GetComponent<MeshRenderer>();
            return r != null && r.enabled;
        }

        private static void Add<TKey>(Dictionary<TKey, List<Light2D>> map, TKey key, Light2D l)
        {
            if (!map.TryGetValue(key, out List<Light2D> list))
            {
                list = _freeLists.Count > 0 ? _freeLists.Pop() : new List<Light2D>(2);
                map[key] = list;
            }
            list.Add(l);
        }

        private static void Recycle<TKey>(Dictionary<TKey, List<Light2D>> map)
        {
            foreach (var kv in map)
            {
                kv.Value.Clear();
                _freeLists.Push(kv.Value);
            }
            map.Clear();
        }

        private static string Describe(List<Light2D> lights)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lights.Count; i++)
            {
                if (i > 0)
                    sb.Append(" + ");
                Light2D l = lights[i];
                sb.Append(PathOf(l.transform))
                    .Append(" r=").Append(l.LightRadius.ToString("F0"))
                    .Append(" i=").Append(l.LightIntensity.ToString("F2"))
                    .Append(" c=").Append(ColorUtility.ToHtmlStringRGBA(l.LightColor))
                    .Append(" at ").Append(l.transform.position.ToString("F0"));
            }
            return sb.ToString();
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            Transform p = t.parent;
            for (int depth = 0; p != null && depth < 3; depth++, p = p.parent)
                path = p.name + "/" + path;
            return path;
        }
    }
}
