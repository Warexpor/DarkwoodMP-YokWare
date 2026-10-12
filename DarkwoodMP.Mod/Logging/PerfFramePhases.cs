using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Splits each frame for <see cref="CoopPerfProbe"/>: <c>work</c> runs from the frame's first
    /// script callback (a physics step or Update) to the end of rendering on the CPU, <c>wait</c>
    /// from there to the next frame's first callback (present, vsync, GPU, compositor). The mod's
    /// own tick is a small part of <c>work</c>; a long frame whose time sits in <c>wait</c> is not
    /// script cost at all. <c>fixed</c> counts the physics steps the frame ran. <c>work</c> is split
    /// further into <c>phys</c> (the physics steps), <c>upd</c> (every Update), <c>co</c> (coroutines and
    /// anything else between the last Update and the first LateUpdate), <c>late</c> (every
    /// LateUpdate) and <c>rend</c> (rendering up to the end of the frame).
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class PerfFramePhases : MonoBehaviour
    {
        private static PerfFramePhases _instance;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        private double _frameStart = -1;
        private bool _started;
        private int _fixed;
        private double _endPrev = -1;
        private double _startPrev = -1;
        private int _fixedPrev;

        /// <summary>The frame that just ended (the one <c>Time.unscaledDeltaTime</c> measures).</summary>
        internal static double LastWorkMs { get; private set; }
        internal static double LastWaitMs { get; private set; }
        internal static int LastFixedSteps { get; private set; }
        internal static double LastPhysMs { get; private set; }
        internal static double LastUpdMs { get; private set; }
        internal static double LastCoMs { get; private set; }
        internal static double LastLateMs { get; private set; }
        internal static double LastRendMs { get; private set; }

        // Marks of the frame in progress, and of the one that just ended.
        private double _updStart, _updEnd, _lateStart, _lateEnd;
        private double _physPrev, _updPrev, _coPrev, _latePrev, _rendPrev;

        internal static void Ensure()
        {
            if (_instance != null)
                return;
            var go = new GameObject("YokWare_PerfFramePhases");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _instance = go.AddComponent<PerfFramePhases>();
            go.AddComponent<PerfFramePhasesEnd>();
            // Every plugin is loaded by the time a session runs the probe.
            FrameScriptProfiler.Install();
        }

        internal static double Now => Clock.Elapsed.TotalMilliseconds;

        private void MarkFrameStart()
        {
            if (_started)
                return;
            _started = true;
            _frameStart = Now;
            if (_endPrev >= 0 && _startPrev >= 0)
            {
                LastWorkMs = _endPrev - _startPrev;
                LastWaitMs = _frameStart - _endPrev;
                LastFixedSteps = _fixedPrev;
                LastPhysMs = _physPrev;
                LastUpdMs = _updPrev;
                LastCoMs = _coPrev;
                LastLateMs = _latePrev;
                LastRendMs = _rendPrev;
                FrameScriptProfiler.FrameBoundary();
            }
        }

        private void FixedUpdate()
        {
            MarkFrameStart();
            _fixed++;
        }

        private void Update()
        {
            MarkFrameStart();
            _updStart = Now;
        }

        private void LateUpdate() => _lateStart = Now;

        internal static void MarkUpdateEnd()
        {
            if (_instance != null) _instance._updEnd = Now;
        }

        internal static void MarkLateEnd()
        {
            if (_instance != null) _instance._lateEnd = Now;
        }

        private void Start() => StartCoroutine(EndOfFrameLoop());

        private IEnumerator EndOfFrameLoop()
        {
            while (true)
            {
                yield return EndOfFrame;
                _endPrev = Now;
                _startPrev = _frameStart;
                _physPrev = _updStart - _frameStart;
                _updPrev = _updEnd - _updStart;
                _coPrev = _lateStart - _updEnd;
                _latePrev = _lateEnd - _lateStart;
                _rendPrev = _endPrev - _lateEnd;
                _fixedPrev = _fixed;
                _fixed = 0;
                _started = false;
            }
        }
    }

    /// <summary>Runs last in Update and LateUpdate: closes those phases for <see cref="PerfFramePhases"/>.</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class PerfFramePhasesEnd : MonoBehaviour
    {
        private void Update() => PerfFramePhases.MarkUpdateEnd();

        private void LateUpdate() => PerfFramePhases.MarkLateEnd();
    }
}
