using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>A randomizer waking up during a save load rolls too, knowing it is a load (<see cref="CosmeticRolls.NoteAwake"/>).</summary>
    [HarmonyPatch(typeof(SpriteRandomizer), "Awake")]
    public static class SpriteRandomizerAwakePatch
    {
        private static void Prefix(SpriteRandomizer __instance) => CosmeticRolls.NoteAwake(__instance);
    }

    /// <summary>The roll itself, on the object's own seed (<see cref="CosmeticRolls.InitPrefix"/>).</summary>
    [HarmonyPatch(typeof(SpriteRandomizer), nameof(SpriteRandomizer.init))]
    public static class SpriteRandomizerInitPatch
    {
        private static bool Prefix(SpriteRandomizer __instance) => CosmeticRolls.InitPrefix(__instance);
    }

    /// <summary>A parallax set up during a save object's load waits for its save id (<see cref="CosmeticRolls.ParallaxInitPrefix"/>).</summary>
    [HarmonyPatch(typeof(Parallax), nameof(Parallax.init))]
    public static class ParallaxInitCosmeticPatch
    {
        private static bool Prefix(Parallax __instance, out bool __state)
            => CosmeticRolls.ParallaxInitPrefix(__instance, out __state);

        private static void Finalizer(bool __state) => CosmeticRolls.ParallaxInitFinalizer(__state);
    }

    /// <summary>A parallax layer's ease factor on the layer's own seed (<see cref="CosmeticRolls.EnterParallaxLayer"/>).</summary>
    [HarmonyPatch(typeof(Parallax.ParallaxObject), nameof(Parallax.ParallaxObject.init))]
    public static class ParallaxLayerCosmeticPatch
    {
        private static void Prefix(Parallax.ParallaxObject __instance, out CosmeticRolls.StreamScope __state)
            => __state = __instance != null ? CosmeticRolls.EnterParallaxLayer(__instance.transform) : default;

        private static void Finalizer(CosmeticRolls.StreamScope __state) => CosmeticRolls.ExitParallaxLayer(__state);
    }

    /// <summary><c>AnimationPlay.init</c> runs on the object's seed (<see cref="CosmeticRolls.EnterAnimInit"/>).</summary>
    [HarmonyPatch(typeof(AnimationPlay), "init")]
    public static class AnimationPlayInitPatch
    {
        private static void Prefix(AnimationPlay __instance, out CosmeticRolls.StreamScope __state)
            => __state = __instance != null ? CosmeticRolls.EnterAnimInit(__instance) : default;

        private static void Finalizer(CosmeticRolls.StreamScope __state) => CosmeticRolls.ExitScope(__state);
    }

    /// <summary><c>AnimationPlay</c>'s replays after a random delay run on the shared clock (<see cref="AnimSchedule.Replays"/>).</summary>
    [HarmonyPatch(typeof(AnimationPlay), "waitToPlayAgain")]
    public static class AnimationPlayReplayPatch
    {
        private static bool Prefix(AnimationPlay __instance, ref IEnumerator __result)
        {
            __result = AnimSchedule.Replays(__instance);
            return false;
        }
    }

    /// <summary><c>AnimationPlay</c>'s twitching runs on the shared clock (<see cref="AnimSchedule.Twitch"/>).</summary>
    [HarmonyPatch(typeof(AnimationPlay), "OnTwitch")]
    public static class AnimationPlayTwitchPatch
    {
        private static bool Prefix(AnimationPlay __instance, ref IEnumerator __result)
        {
            __result = AnimSchedule.Twitch(__instance);
            return false;
        }
    }

    /// <summary>
    /// World animations follow the shared animation clock (<see cref="AnimPhase.BeforeUpdate"/>).
    /// </summary>
    [HarmonyPatch(typeof(tk2dSpriteAnimator), nameof(tk2dSpriteAnimator.UpdateAnimation))]
    public static class Tk2dAnimatorClockPatch
    {
        private static void Prefix(tk2dSpriteAnimator __instance, ref float deltaTime)
            => AnimPhase.BeforeUpdate(__instance, ref deltaTime);
    }

    /// <summary>
    /// A clip starting: its phase on the clock is where it starts (<see cref="AnimPhase.OnStarted"/>),
    /// and a random-frame clip's first frame is seeded (<see cref="CosmeticRolls.EnterTk2dRandom"/>).
    /// </summary>
    [HarmonyPatch(typeof(tk2dSpriteAnimator), nameof(tk2dSpriteAnimator.Play),
        new[] { typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float) })]
    public static class Tk2dAnimatorPlayPatch
    {
        internal struct PlayState
        {
            public bool Starts;
            public CosmeticRolls.StreamScope Scope;
        }

        private static void Prefix(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip clip, float clipStartTime,
            out PlayState __state)
        {
            __state = default;
            __state.Starts = AnimPhase.WillStart(__instance, clip, clipStartTime);
            if (__state.Starts && (clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.RandomFrame
                    || clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.RandomLoop))
                __state.Scope = CosmeticRolls.EnterTk2dRandom(__instance, clip);
        }

        private static void Postfix(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip clip, PlayState __state)
        {
            if (__state.Starts)
                AnimPhase.OnStarted(__instance, clip);
        }

        private static void Finalizer(PlayState __state) => CosmeticRolls.ExitScope(__state.Scope);
    }

    /// <summary>Vine rotations on the spawner's own seed (<see cref="CosmeticRolls.EnterVineSpawn"/>).</summary>
    [HarmonyPatch(typeof(VineSpawner), "Start")]
    public static class VineSpawnerCosmeticPatch
    {
        private static void Prefix(VineSpawner __instance, out CosmeticRolls.StreamScope __state)
            => __state = CosmeticRolls.EnterVineSpawn(__instance);

        private static void Finalizer(CosmeticRolls.StreamScope __state) => CosmeticRolls.ExitVineSpawn(__state);
    }

    /// <summary>A load reads the roll seeds its save carries (<see cref="CosmeticRolls.BeginLoad"/>).</summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class CosmeticKeyStoreLoadPatch
    {
        private static void Prefix(SaveManager __instance) => CosmeticRolls.BeginLoad(__instance);
    }

    /// <summary>Rolls held while a save object loads run once it has its save id (<see cref="CosmeticRolls.ExitLoadObj"/>).</summary>
    [HarmonyPatch(typeof(SaveManager), "loadObj")]
    public static class CosmeticLoadObjPatch
    {
        private static void Prefix() => CosmeticRolls.EnterLoadObj();

        private static void Finalizer() => CosmeticRolls.ExitLoadObj();
    }

    /// <summary>A new world starts with no stored keys (<see cref="CosmeticRolls.BeginNewWorld"/>).</summary>
    [HarmonyPatch(typeof(Controller), nameof(Controller.generateChapter))]
    public static class CosmeticKeyStoreNewWorldPatch
    {
        private static void Postfix(bool __runOriginal)
        {
            if (__runOriginal)
                CosmeticRolls.BeginNewWorld();
        }
    }

    /// <summary>
    /// Every save writes the roll seeds next to it (<see cref="CosmeticRolls.WriteStore"/>). Vanilla
    /// <c>Save</c> returns early for night or a load in progress; it saved when it stamped
    /// <c>lastTimeSaved</c>.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save))]
    public static class CosmeticKeyStoreSavePatch
    {
        private static void Prefix(SaveManager __instance, out System.DateTime __state)
            => __state = __instance != null ? __instance.lastTimeSaved : default;

        private static void Postfix(SaveManager __instance, bool __runOriginal, System.DateTime __state)
        {
            if (__runOriginal && __instance != null && __instance.lastTimeSaved != __state)
                CosmeticRolls.WriteStore(__instance);
        }
    }

    /// <summary>The shared examine decks (<see cref="DescriptionDeck"/>).</summary>
    [HarmonyPatch(typeof(DescriptionPool), nameof(DescriptionPool.getDescriptionFromPool))]
    public static class DescriptionPoolDrawPatch
    {
        private static bool Prefix(string poolName, ref string __result, out int __state)
            => DescriptionDeck.DrawPrefix(poolName, ref __result, out __state);

        private static void Finalizer(Exception __exception, string poolName, string __result, int __state)
            => DescriptionDeck.DrawFinalizer(__exception, poolName, __result, __state);
    }
}
