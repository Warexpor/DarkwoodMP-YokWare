using System.Collections;
using HarmonyLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// <c>AnimationPlay</c>'s timed behaviour runs on the shared animation clock
    /// (<see cref="AnimClock"/>), as a pure function of the clock and the object's seed, so every
    /// machine (and a late joiner, and the host after a reload) twitches and replays the same body
    /// at the same moment.
    /// <list type="bullet">
    /// <item>Replays after a random delay (<c>playDelayMin/Max</c>: the mimic bodies under the
    /// church): replay <c>k</c> is due at <see cref="AnimTiming.ReplayTime"/>, every gap within
    /// vanilla's [min, max]. Vanilla waited <c>Random.Range(min, max)</c> seconds from whenever this
    /// machine started the coroutine.</item>
    /// <item>Twitching (<c>twitching</c>: the Musician's house zombies): the frame is
    /// <see cref="AnimTiming.TwitchFrame"/> of the clock, played forward to a seeded turning frame,
    /// back, then a rest of about 1-5 s, like vanilla's coroutine pair.</item>
    /// </list>
    /// Offline the clock is this machine's own, so single player keeps vanilla's look.
    /// </summary>
    internal static class AnimSchedule
    {
        private static readonly AccessTools.FieldRef<AnimationPlay, bool> WaitingToPlayAgain =
            AccessTools.FieldRefAccess<AnimationPlay, bool>("waitingToPlayAgain"); // process-scoped: reflection cache

        private static int SeedOf(AnimationPlay ap)
            => CosmeticRolls.SeedFor(ap.transform, CosmeticRolls.SaltSchedule,
                CosmeticKey.Salt(CosmeticRolls.PlaceKey(ap.transform, withRootPlacement: false), "Schedule"));

        /// <summary>Replaces vanilla <c>waitToPlayAgain</c>: <c>animator.Play()</c> at each due replay.</summary>
        internal static IEnumerator Replays(AnimationPlay ap)
        {
            if (ap == null)
                yield break;
            int seed = SeedOf(ap);
            double min = ap.playDelayMin;
            double max = ap.playDelayMax;
            if (max < min)
            {
                double swap = min;
                min = max;
                max = swap;
            }
            while (ap != null && WaitingToPlayAgain(ap))
            {
                int epoch = AnimClock.Epoch;
                double due = AnimTiming.NextReplay(seed, min, max, AnimClock.Now, out _);
                while (ap != null && AnimClock.Epoch == epoch && AnimClock.Now < due)
                    yield return null;
                if (ap == null)
                    yield break;
                if (AnimClock.Epoch != epoch)
                    continue; // the clock moved: work the next replay out again
                tk2dSpriteAnimator animator = ap.animator;
                if (animator == null)
                    yield break;
                animator.Play();
                if (animator.CurrentClip != null && animator.CurrentClip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once)
                    AnimPhase.OnScheduledStart(animator, due);
            }
        }

        /// <summary>
        /// Replaces vanilla <c>OnTwitch</c> (and the <c>waitToResumeAni</c> it started): holds the
        /// animator and shows the clock's twitch frame. Something else playing another clip on it
        /// ends the twitching and hands the animator back running.
        /// </summary>
        internal static IEnumerator Twitch(AnimationPlay ap)
        {
            tk2dSpriteAnimator animator = ap != null ? ap.animator : null;
            if (animator == null)
                yield break;
            tk2dSpriteAnimationClip clip = !string.IsNullOrEmpty(ap.currentAnim) ? animator.GetClipByName(ap.currentAnim) : null;
            if (clip == null)
                clip = animator.CurrentClip;
            if (clip == null || clip.frames == null || clip.frames.Length < 2)
                yield break;
            if (animator.CurrentClip != clip)
                animator.Play(clip);
            int seed = SeedOf(ap);
            int shown = -1;
            while (ap != null && animator != null && ap.twitching)
            {
                if (animator.CurrentClip != clip)
                {
                    animator.Resume();
                    yield break;
                }
                if (!animator.Paused)
                    animator.Pause();
                int frame = AnimTiming.TwitchFrame(seed, clip.frames.Length, clip.fps, AnimClock.Now);
                if (frame != shown)
                {
                    animator.SetFrame(frame);
                    shown = frame;
                }
                yield return null;
            }
        }
    }
}
