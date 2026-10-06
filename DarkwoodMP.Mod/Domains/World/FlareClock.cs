using System;
using DG.Tweening;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// When a flare was lit. Vanilla <c>Flare</c> is a fixed clock from <c>Start</c> (lit on aim):
    /// the glow tweens 1→4 over 2 s, then 4→2 over 6 s, flickers throughout, and <c>waitToDie</c>
    /// fades it out over 2 s after <c>longevity</c>. A copy of a flare lit on another machine
    /// (a peer's held flare, a thrown flare, a late joiner's view of one burning on the ground)
    /// starts that same vanilla clock already <see cref="Age"/> seconds in, so every peer sees it
    /// in the same phase and burning out at the same moment, with no despawn messages.
    /// </summary>
    public sealed class FlareClock : MonoBehaviour
    {
        /// <summary>Time.time the flare was lit (on its owner's clock, carried as an age).</summary>
        public float LitAt;
        /// <summary>A copy of a flare lit elsewhere: <see cref="LitAt"/> was set before Start.</summary>
        public bool Preset;

        public float Age => Time.time - LitAt;

        /// <summary>Vanilla Flare.waitToDie fade after longevity.</summary>
        public const float FadeSec = 2f;

        /// <summary>Seconds since the flare under <paramref name="go"/> was lit, or -1 when unknown.</summary>
        public static float AgeOf(GameObject go)
        {
            if (go == null)
                return -1f;
            FlareClock c = go.GetComponentInChildren<FlareClock>(true);
            return c != null ? c.Age : -1f;
        }

        /// <summary>Unscaled time <see cref="MakeCopy"/> ran (-1 none): the copy's WaitAndDie.Start follows shortly.</summary>
        public float CopyMadeAt = -1f;

        /// <summary>A WaitAndDie (re)start this long after the copy was made belongs to a later reuse.</summary>
        public const float CopyStartWindowSec = 1f;

        private static readonly AccessTools.FieldRef<WaitAndDie, float> WaitAndDieStarted =
            AccessTools.FieldRefAccess<WaitAndDie, float>("timeStarted");

        /// <summary>Make <paramref name="go"/> a copy of a flare lit <paramref name="age"/> seconds ago (call before its Start).</summary>
        public static void MakeCopy(GameObject go, float age)
        {
            if (go == null || age < 0f)
                return;
            foreach (Flare fl in go.GetComponentsInChildren<Flare>(true))
            {
                FlareClock c = fl.GetComponent<FlareClock>() ?? fl.gameObject.AddComponent<FlareClock>();
                c.LitAt = Time.time - age;
                c.Preset = true;
            }
            // Flare.prefab's root also carries WaitAndDie (longevity 80, fade 2) that removes the
            // flare body. On the owner it started when the flare was lit, so the copy's starts that
            // far in too, or the copy's stick outlives the owner's by the age.
            foreach (WaitAndDie wd in go.GetComponentsInChildren<WaitAndDie>(true))
            {
                FlareClock c = wd.GetComponent<FlareClock>() ?? wd.gameObject.AddComponent<FlareClock>();
                c.LitAt = Time.time - age;
                c.Preset = true;
                c.CopyMadeAt = Time.unscaledTime;
                // A pooled spawn already ran OnSpawned → waitForDeath inside AddPrefab.
                if (wd.waitingForDeath)
                    AgeWaitAndDie(wd, c);
            }
        }

        /// <summary>Start <paramref name="wd"/>'s death clock at the copy's lit time.</summary>
        internal static void AgeWaitAndDie(WaitAndDie wd, FlareClock c)
        {
            float age = Mathf.Max(0f, c.Age);
            WaitAndDieStarted(wd) = (wd.timeScaleIndependent ? Time.unscaledTime : Time.time) - age;
        }

        /// <summary>Fully dark: past longevity and the fade.</summary>
        public static bool BurntOut(GameObject go)
        {
            Flare fl = go != null ? go.GetComponentInChildren<Flare>(true) : null;
            float age = AgeOf(go);
            return fl == null || age >= 0f && age >= fl.longevity + FadeSec;
        }
    }

    /// <summary>
    /// <c>WaitAndDie.waitForDeath</c> (its Start) on a flare copy just made: start the death clock
    /// at the owner's lit time (<see cref="FlareClock.MakeCopy"/>). Once, and only right after the
    /// copy is made, so a pooled body reused later starts fresh.
    /// </summary>
    [HarmonyPatch(typeof(WaitAndDie), nameof(WaitAndDie.waitForDeath))]
    public static class FlareClockWaitAndDiePatch
    {
        private static void Postfix(WaitAndDie __instance)
        {
            FlareClock c = __instance != null ? __instance.GetComponent<FlareClock>() : null;
            if (c == null || c.CopyMadeAt < 0f)
                return;
            if (Time.unscaledTime - c.CopyMadeAt <= FlareClock.CopyStartWindowSec)
                FlareClock.AgeWaitAndDie(__instance, c);
            c.CopyMadeAt = -1f;
        }
    }

    /// <summary>
    /// <c>Flare.Start</c>: a local flare records when it was lit; a preset copy runs vanilla's
    /// Start already <see cref="FlareClock.Age"/> seconds in (same tween phase, same death time).
    /// </summary>
    [HarmonyPatch(typeof(Flare), "Start")]
    public static class FlareClockStartPatch
    {
        private static readonly AccessTools.FieldRef<Flare, Color> OriginalColor =
            AccessTools.FieldRefAccess<Flare, Color>("lightFlareOriginalColor");
        private static readonly System.Reflection.MethodInfo FlickerMethod = AccessTools.Method(typeof(Flare), "flicker");
        private static readonly System.Reflection.MethodInfo WaitToDieMethod = AccessTools.Method(typeof(Flare), "waitToDie");

        private static bool Prefix(Flare __instance)
        {
            if (__instance == null)
                return true;
            FlareClock clock = __instance.GetComponent<FlareClock>();
            if (clock == null || !clock.Preset)
            {
                if (clock == null && NetGuard.Connected(out _))
                    clock = __instance.gameObject.AddComponent<FlareClock>();
                if (clock != null)
                    clock.LitAt = Time.time;
                return true;
            }

            float age = Mathf.Max(0f, clock.Age);
            Flare fl = __instance;
            if (fl.lightFlare != null)
                OriginalColor(fl) = fl.lightFlare.color;
            Action flicker = (Action)Delegate.CreateDelegate(typeof(Action), fl, FlickerMethod);
            Action waitToDie = (Action)Delegate.CreateDelegate(typeof(Action), fl, WaitToDieMethod);
            fl.startRoutine(flicker, 0.03f, looping: true);

            if (age >= fl.longevity)
            {
                // Already past its life: vanilla's burn-out now, and no light at all once the fade
                // would have finished too.
                waitToDie();
                if (age >= fl.longevity + FlareClock.FadeSec && fl.light2D != null)
                    UnityEngine.Object.Destroy(fl.light2D.gameObject);
                return false;
            }
            fl.startRoutine(waitToDie, fl.longevity - age);

            // Vanilla: tweenIntensity 1→4 over 2 s, then 4→2 over 6 s after a 2 s delay.
            if (age < 2f)
            {
                fl.tweenIntensity = Mathf.Lerp(1f, 4f, age / 2f);
                DOTween.To(() => fl.tweenIntensity, x => fl.tweenIntensity = x, 4f, 2f - age);
                DOTween.To(() => fl.tweenIntensity, x => fl.tweenIntensity = x, 2f, 6f).SetDelay(2f - age);
            }
            else if (age < 8f)
            {
                fl.tweenIntensity = Mathf.Lerp(4f, 2f, (age - 2f) / 6f);
                DOTween.To(() => fl.tweenIntensity, x => fl.tweenIntensity = x, 2f, 8f - age);
            }
            else
            {
                fl.tweenIntensity = 2f;
            }
            return false;
        }
    }
}
