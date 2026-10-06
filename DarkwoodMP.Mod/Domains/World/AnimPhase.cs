using System.Collections.Generic;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// World animations show the same frame on every machine at the same moment. Vanilla's
    /// <c>tk2dSpriteAnimator</c> adds <c>deltaTime</c> to its clip time while it is on screen, so
    /// a clip's place in its cycle depended on when that machine created the object and on how long
    /// that player had it in view: about 44,000 animators (trees, grass, water, fire, flies, lamps)
    /// each ran at their own phase on each machine.
    /// <para>
    /// In a session, a cycling clip (loop, random loop, ping-pong, the loop part of a loop
    /// section) on a world object takes its time from the shared animation clock
    /// (<see cref="AnimClock"/>): clip time = start offset + clock × fps, where the start offset is
    /// where the clip was started (frame 0, or a seeded random start frame). A one-shot clip a
    /// schedule started (<see cref="AnimSchedule"/>) runs from its scheduled start. An animator
    /// coming into view (or the clock changing source) snaps to its frame without firing frame
    /// events; one already running is pulled toward it by at most 30% speed, so jitter never shows.
    /// While this machine runs at another speed (a cutscene's slow motion) or is paused, the
    /// animator runs as vanilla and is pulled back afterwards.
    /// </para>
    /// Creatures and players are not touched: their animation is the host's, sent with them.
    /// Interface animations are not touched either (each player's own).
    /// </summary>
    internal static class AnimPhase
    {
        private const double PullPerSec = 2.0;
        private const double MaxSpeedChange = 0.3;

        private static readonly AccessTools.FieldRef<tk2dSpriteAnimator, float> ClipTime =
            AccessTools.FieldRefAccess<tk2dSpriteAnimator, float>("clipTime"); // process-scoped: reflection cache
        private static readonly AccessTools.FieldRef<tk2dSpriteAnimator, int> PreviousFrame =
            AccessTools.FieldRefAccess<tk2dSpriteAnimator, int>("previousFrame"); // process-scoped: reflection cache

        private sealed class Entry
        {
            public tk2dSpriteAnimator Animator;
            /// <summary>0: not decided, 1: follows the clock, -1: never (creature, player, interface).</summary>
            public int Eligible;
            public tk2dSpriteAnimationClip Clip;
            /// <summary>Clip time the clip was started at (its phase on the clock).</summary>
            public double Offset;
            /// <summary>A scheduled one-shot: the clock time it started (NaN: not scheduled).</summary>
            public double ScheduledStart = double.NaN;
            public int LastFrame = -1;
            public int Epoch = -1;
        }

        private static readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>(4096); // process-scoped: per animator, dead ones pruned
        private static int _pruneAt = 8192; // process-scoped: prune threshold for _entries
        private static readonly List<int> _deadScratch = new List<int>(256); // process-scoped: scratch buffer

        private static Entry Get(tk2dSpriteAnimator a)
        {
            int id = a.GetInstanceID();
            if (_entries.TryGetValue(id, out Entry e) && e.Animator == a)
                return e;
            if (_entries.Count >= _pruneAt)
                Prune();
            e = new Entry { Animator = a };
            _entries[id] = e;
            return e;
        }

        private static void Prune()
        {
            _deadScratch.Clear();
            foreach (KeyValuePair<int, Entry> kv in _entries)
            {
                if (kv.Value.Animator == null)
                    _deadScratch.Add(kv.Key);
            }
            for (int i = 0; i < _deadScratch.Count; i++)
                _entries.Remove(_deadScratch[i]);
            _deadScratch.Clear();
            _pruneAt = Mathf.Max(8192, _entries.Count * 2);
        }

        private static bool Cycling(tk2dSpriteAnimationClip.WrapMode mode)
            => mode == tk2dSpriteAnimationClip.WrapMode.Loop || mode == tk2dSpriteAnimationClip.WrapMode.RandomLoop
                || mode == tk2dSpriteAnimationClip.WrapMode.PingPong || mode == tk2dSpriteAnimationClip.WrapMode.LoopSection;

        /// <summary>
        /// <c>Play(clip, startTime, fps)</c> is about to run: true when it really starts the clip
        /// (vanilla returns early for the clip already playing from 0).
        /// </summary>
        internal static bool WillStart(tk2dSpriteAnimator a, tk2dSpriteAnimationClip clip, float startTime)
            => a != null && clip != null && !(startTime == 0f && a.IsPlaying(clip));

        /// <summary>A clip started: where it started is its phase on the clock.</summary>
        internal static void OnStarted(tk2dSpriteAnimator a, tk2dSpriteAnimationClip clip)
        {
            if (a == null || clip == null || (!Cycling(clip.wrapMode) && clip.wrapMode != tk2dSpriteAnimationClip.WrapMode.Once))
                return;
            Entry e = Get(a);
            if (e.Eligible < 0)
                return;
            e.Clip = clip;
            e.Offset = ClipTime(a);
            e.ScheduledStart = double.NaN;
            e.Epoch = -1;
        }

        /// <summary><see cref="AnimSchedule"/> started a one-shot at clock time <paramref name="start"/>.</summary>
        internal static void OnScheduledStart(tk2dSpriteAnimator a, double start)
        {
            if (a == null || a.CurrentClip == null)
                return;
            Entry e = Get(a);
            e.Clip = a.CurrentClip;
            e.Offset = 0;
            e.ScheduledStart = start;
            e.Epoch = -1;
        }

        private static bool DecideEligible(tk2dSpriteAnimator a)
        {
            if (a.GetComponentInParent<CharBase>(true) != null || a.GetComponentInParent<RemotePlayerProxy>(true) != null)
                return false;
            AnimationPlay ap = a.GetComponentInParent<AnimationPlay>(true);
            if (ap != null && ap.twitching)
                return false; // driven frame by frame by AnimSchedule
            Transform t = a.transform;
            UI ui = Singleton<UI>.Instance;
            if (ui != null && t.IsChildOf(ui.transform))
                return false;
            CamMain cam = Singleton<CamMain>.Instance;
            return cam == null || !t.IsChildOf(cam.transform);
        }

        /// <summary><c>UpdateAnimation(deltaTime)</c> prefix: bend this frame's step onto the clock.</summary>
        internal static void BeforeUpdate(tk2dSpriteAnimator a, ref float deltaTime)
        {
            // Vanilla's own gate first (off screen or not playing: nothing this frame), then the clock.
            if (a == null || (!a.isVisible && !a.IgnoreCameraVisibility) || deltaTime <= 0f || !AnimClock.Shared)
                return;
            if (!a.Playing || a.Paused || tk2dSpriteAnimator.g_Paused)
                return;
            tk2dSpriteAnimationClip clip = a.CurrentClip;
            if (clip == null || clip.frames == null || clip.frames.Length < 2)
                return;
            Entry e = Get(a);
            if (e.Eligible == 0)
                e.Eligible = DecideEligible(a) ? 1 : -1;
            if (e.Eligible < 0)
                return;
            if (Time.timeScale != 1f)
            {
                // This machine runs at its own speed (slow motion): vanilla, and it keeps running
                // (pulled back to the clock afterwards, not snapped).
                e.LastFrame = Time.frameCount;
                return;
            }
            if (e.Clip != clip)
            {
                // Started without Play (setClip): it starts where it stands.
                e.Clip = clip;
                e.Offset = ClipTime(a);
                e.ScheduledStart = double.NaN;
                e.Epoch = -1;
            }

            float fps = a.ClipFps;
            if (fps <= 0f)
                return;
            int n = clip.frames.Length;
            double now = AnimClock.FrameNow;
            double clipTime = ClipTime(a);
            double step = deltaTime * (double)fps;
            double target;
            double period;
            double sectionStart = 0;

            switch (clip.wrapMode)
            {
                case tk2dSpriteAnimationClip.WrapMode.Loop:
                case tk2dSpriteAnimationClip.WrapMode.RandomLoop:
                    period = n;
                    target = e.Offset + now * fps;
                    break;
                case tk2dSpriteAnimationClip.WrapMode.PingPong:
                    period = 2 * n - 2;
                    target = e.Offset + now * fps;
                    break;
                case tk2dSpriteAnimationClip.WrapMode.LoopSection:
                    // The lead-in plays as vanilla; the looping part follows the clock.
                    if (clipTime < clip.loopStart || n - clip.loopStart < 2)
                        return;
                    sectionStart = clip.loopStart;
                    period = n - clip.loopStart;
                    target = sectionStart + e.Offset + now * fps;
                    break;
                case tk2dSpriteAnimationClip.WrapMode.Once:
                    if (double.IsNaN(e.ScheduledStart))
                        return; // started by an event: it runs from that event, as vanilla
                    period = 0;
                    target = (now - e.ScheduledStart) * fps;
                    break;
                default:
                    return;
            }

            // Not stepped last frame (just started, back in view, unpaused): it is not running
            // along the clock yet, so it jumps there.
            int frameCount = Time.frameCount;
            bool snap = e.Epoch != AnimClock.Epoch || e.LastFrame < frameCount - 1;
            e.Epoch = AnimClock.Epoch;
            e.LastFrame = frameCount;

            if (snap)
            {
                double time = period > 0 ? sectionStart + AnimTiming.Mod(target - sectionStart, period) : target;
                if (period <= 0 && time >= n)
                {
                    // A scheduled one-shot already over: let vanilla end it now.
                    ClipTime(a) = (float)time;
                    deltaTime = 0f;
                    return;
                }
                if (time < 0)
                    time = 0;
                WarpQuietly(a, clip, (float)time);
                deltaTime = 0f;
                return;
            }

            double error = period > 0
                ? AnimTiming.WrapError(target - (clipTime + step), period)
                : target - (clipTime + step);
            double advance = step + error * System.Math.Min(1.0, deltaTime * PullPerSec);
            double lo = step * (1 - MaxSpeedChange);
            double hi = step * (1 + MaxSpeedChange);
            if (advance < lo) advance = lo;
            else if (advance > hi) advance = hi;
            deltaTime = (float)(advance / fps);
        }

        /// <summary>Jump to <paramref name="time"/> and show its frame, firing no frame events on the way.</summary>
        private static void WarpQuietly(tk2dSpriteAnimator a, tk2dSpriteAnimationClip clip, float time)
        {
            int n = clip.frames.Length;
            int whole = (int)time;
            int frame;
            switch (clip.wrapMode)
            {
                case tk2dSpriteAnimationClip.WrapMode.PingPong:
                    frame = n > 1 ? whole % (2 * n - 2) : 0;
                    if (frame >= n)
                        frame = 2 * n - 2 - frame;
                    break;
                case tk2dSpriteAnimationClip.WrapMode.LoopSection:
                    frame = whole < clip.loopStart ? whole
                        : clip.loopStart + (whole - clip.loopStart) % (n - clip.loopStart);
                    break;
                case tk2dSpriteAnimationClip.WrapMode.Once:
                    frame = Mathf.Min(whole, n - 1);
                    break;
                default:
                    frame = whole % n;
                    break;
            }
            ClipTime(a) = time;
            tk2dSpriteAnimationFrame f = clip.frames[frame];
            a.SetSprite(f.spriteCollection, f.spriteId);
            PreviousFrame(a) = frame;
        }
    }
}
