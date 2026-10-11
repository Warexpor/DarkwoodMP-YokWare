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
    /// script cost at all. <c>fixed</c> counts the physics steps the frame ran.
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

        internal static void Ensure()
        {
            if (_instance != null)
                return;
            var go = new GameObject("YokWare_PerfFramePhases");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _instance = go.AddComponent<PerfFramePhases>();
        }

        private static double Now => Clock.Elapsed.TotalMilliseconds;

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
            }
        }

        private void FixedUpdate()
        {
            MarkFrameStart();
            _fixed++;
        }

        private void Update() => MarkFrameStart();

        private void Start() => StartCoroutine(EndOfFrameLoop());

        private IEnumerator EndOfFrameLoop()
        {
            while (true)
            {
                yield return EndOfFrame;
                _endPrev = Now;
                _startPrev = _frameStart;
                _fixedPrev = _fixed;
                _fixed = 0;
                _started = false;
            }
        }
    }
}
