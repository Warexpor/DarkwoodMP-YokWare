using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Diagnostic only, off unless the process starts with <c>DWMP_FRAME_PROFILE=1</c>: times every
    /// Unity message method (Update, LateUpdate, FixedUpdate, OnGUI, render callbacks) of every
    /// game and plugin MonoBehaviour, and each camera's render, per frame. A frame whose CPU work
    /// (<see cref="PerfFramePhases"/>) runs well over the usual gets one <c>[FrameProf]</c> line
    /// naming the methods that took the most in it, against their per-frame average.
    /// The timing itself costs a little on every call, so it is never on in a normal session.
    /// </summary>
    internal static class FrameScriptProfiler
    {
        private static readonly string[] Messages =
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI",
            "OnWillRenderObject", "OnRenderObject", "OnPreRender", "OnPostRender", "OnPreCull",
        };

        private sealed class Acc
        {
            public string Name;
            public long FrameTicks;
            public int FrameCalls;
            public long TotalTicks;
        }

        private static readonly Dictionary<MethodBase, Acc> _byMethod = new Dictionary<MethodBase, Acc>(); // process-scoped: diagnostic
        private static readonly List<Acc> _all = new List<Acc>(); // process-scoped: diagnostic
        private static readonly Dictionary<Camera, long> _camStart = new Dictionary<Camera, long>(); // process-scoped: diagnostic
        private static readonly Dictionary<Camera, Acc> _camAcc = new Dictionary<Camera, Acc>(); // process-scoped: diagnostic
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static bool _on;
        private static int _frames;
        private static double _workAvg = -1;
        private static int _logged;
        private static float _nextSummary;

        internal static bool Enabled => _on;

        internal static void Install()
        {
            if (_on || Environment.GetEnvironmentVariable("DWMP_FRAME_PROFILE") != "1")
                return;
            _on = true;
            var harmony = new HarmonyLib.Harmony("com.yokware.frameprof");
            var prefix = new HarmonyMethod(typeof(FrameScriptProfiler), nameof(Prefix)) { priority = Priority.First };
            var postfix = new HarmonyMethod(typeof(FrameScriptProfiler), nameof(Postfix)) { priority = Priority.Last };
            int patched = 0, failed = 0;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an.StartsWith("Unity", StringComparison.Ordinal) || an.StartsWith("System", StringComparison.Ordinal)
                    || an == "mscorlib" || an == "netstandard" || an.StartsWith("Mono", StringComparison.Ordinal)
                    || an.StartsWith("BepInEx", StringComparison.Ordinal) || an.Contains("Harmony")
                    || an.StartsWith("Newtonsoft", StringComparison.Ordinal))
                    continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (Type t in types)
                {
                    if (t == null || t.IsGenericTypeDefinition || !typeof(MonoBehaviour).IsAssignableFrom(t) || t == typeof(PerfFramePhases) || t == typeof(PerfFramePhasesEnd))
                        continue;
                    foreach (string m in Messages)
                    {
                        MethodInfo mi;
                        try
                        {
                            mi = t.GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                null, Type.EmptyTypes, null);
                        }
                        catch { continue; }
                        if (mi == null || mi.IsAbstract || mi.GetMethodBody() == null)
                            continue;
                        try
                        {
                            var acc = new Acc { Name = an + ":" + t.Name + "." + m };
                            _byMethod[mi] = acc;
                            _all.Add(acc);
                            harmony.Patch(mi, prefix: prefix, postfix: postfix);
                            patched++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                }
            }
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            ModLog.Event(LogCat.Core, "[FrameProf] on: " + patched + " methods timed (" + failed + " failed)");
        }

        private static void Prefix(out long __state) => __state = Clock.ElapsedTicks;

        private static void Postfix(long __state, MethodBase __originalMethod)
        {
            if (__originalMethod != null && _byMethod.TryGetValue(__originalMethod, out Acc a))
            {
                a.FrameTicks += Clock.ElapsedTicks - __state;
                a.FrameCalls++;
            }
        }

        private static void OnPreCull(Camera cam)
        {
            if (cam != null) _camStart[cam] = Clock.ElapsedTicks;
        }

        private static void OnPostRender(Camera cam)
        {
            if (cam == null || !_camStart.TryGetValue(cam, out long s))
                return;
            if (!_camAcc.TryGetValue(cam, out Acc a))
            {
                a = new Acc { Name = "Camera:" + cam.name };
                _camAcc[cam] = a;
                _all.Add(a);
            }
            a.FrameTicks += Clock.ElapsedTicks - s;
            a.FrameCalls++;
        }

        /// <summary>At the start of a frame: judge the one that just ended, then start a new count.</summary>
        internal static void FrameBoundary()
        {
            if (!_on)
                return;
            double work = PerfFramePhases.LastWorkMs;
            _frames++;
            bool slow = _workAvg > 0 && work > Math.Max(10.0, _workAvg * 2.0);
            if (slow && _logged < 400)
            {
                _logged++;
                LogFrame(work);
            }
            _workAvg = _workAvg < 0 ? work : _workAvg * 0.98 + work * 0.02;
            for (int i = 0; i < _all.Count; i++)
            {
                Acc a = _all[i];
                a.TotalTicks += a.FrameTicks;
                a.FrameTicks = 0;
                a.FrameCalls = 0;
            }
            if (Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 60f;
                LogSummary();
            }
        }

        private static readonly List<Acc> _sort = new List<Acc>(); // process-scoped: scratch

        private static void LogFrame(double work)
        {
            _sort.Clear();
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].FrameTicks > 0) _sort.Add(_all[i]);
            _sort.Sort((x, y) => y.FrameTicks.CompareTo(x.FrameTicks));
            var sb = new StringBuilder(512);
            sb.Append("[FrameProf] slow frame work=").Append(work.ToString("F1"))
              .Append(" avg=").Append(_workAvg.ToString("F1"))
              .Append(" (phys=").Append(PerfFramePhases.LastPhysMs.ToString("F1"))
              .Append(" upd=").Append(PerfFramePhases.LastUpdMs.ToString("F1"))
              .Append(" co=").Append(PerfFramePhases.LastCoMs.ToString("F1"))
              .Append(" late=").Append(PerfFramePhases.LastLateMs.ToString("F1"))
              .Append(" rend=").Append(PerfFramePhases.LastRendMs.ToString("F1"))
              .Append(" fixed=").Append(PerfFramePhases.LastFixedSteps)
              .Append(") gc=").Append(GC.CollectionCount(0)).Append(" top:");
            for (int i = 0; i < _sort.Count && i < 8; i++)
            {
                Acc a = _sort[i];
                double avg = _frames > 0 ? a.TotalTicks * TicksToMs / _frames : 0;
                sb.Append(' ').Append(a.Name).Append('=').Append((a.FrameTicks * TicksToMs).ToString("F2"))
                  .Append("ms/").Append(a.FrameCalls).Append(" (avg ").Append(avg.ToString("F2")).Append(')');
            }
            ModLog.Event(LogCat.Core, sb.ToString());
        }

        private static void LogSummary()
        {
            if (_frames <= 0)
                return;
            _sort.Clear();
            _sort.AddRange(_all);
            _sort.Sort((x, y) => y.TotalTicks.CompareTo(x.TotalTicks));
            var sb = new StringBuilder(512);
            sb.Append("[FrameProf] per-frame average over ").Append(_frames).Append(" frames:");
            for (int i = 0; i < _sort.Count && i < 15; i++)
                sb.Append(' ').Append(_sort[i].Name).Append('=').Append((_sort[i].TotalTicks * TicksToMs / _frames).ToString("F3"));
            ModLog.Event(LogCat.Core, sb.ToString());
        }
    }
}
