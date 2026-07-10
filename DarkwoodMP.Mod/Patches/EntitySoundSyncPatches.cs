using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Intercepts AI-driven CharacterSounds calls on the host and broadcasts
    /// them to the client. Covers sounds NOT already played by animation events
    /// or the existing hit/death sync.
    ///
    /// What the client plays locally without host sync:
    ///   - Footstep sounds (animation event → checkFrameTrigger)
    ///   - Attack1/Attack2 (animation event → checkFrameTrigger)
    ///   - GetHit (ClientMeleeSensorPatch → sounds.playGetHitByAxe1)
    ///   - Death (ClientEntityInterpolationService → c.die → die2)
    ///
    /// What this file syncs:
    ///   - Growl (AI behavior, not animation-tied)
    ///   - Curious (onHearSomething, AI behavior)
    ///   - Aggressive/Defensive state sounds
    ///   - Escaping (runAway/flee behavior)
    /// </summary>
    internal static class EntitySoundSyncHelper
    {
        internal static void Broadcast(CharacterSounds sounds, EntitySoundType type)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (sounds == null) return;
            Character c = sounds.character as Character;
            if (c == null) return;
            if (!CharacterTracker.TryGetStableId(c, out short hostId)) return;

            // Cull far SFX on send side (matches client listen range).
            if (!LocalAudioService.IsNearAnyListener(c.transform.position))
                return;

            var msg = new EntitySoundMessage { HostId = hostId, SoundType = type };
            LanNetworkManager.Instance?.Broadcast(NetMessageType.EntitySound, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal static void BroadcastIdleLoop(CharacterSounds sounds, string loopName)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (sounds == null) return;
            Character c = sounds.character as Character;
            if (c == null) return;
            if (!CharacterTracker.TryGetStableId(c, out short hostId)) return;

            // Idle stop (empty name) always send so distant loops don't stick.
            bool isStop = string.IsNullOrEmpty(loopName);
            if (!isStop && !LocalAudioService.IsNearAnyListener(c.transform.position))
                return;

            var msg = new EntitySoundMessage { HostId = hostId, SoundType = EntitySoundType.Idle, LoopName = loopName ?? "" };
            LanNetworkManager.Instance?.Broadcast(NetMessageType.EntitySound, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal static void BroadcastIdleStop(CharacterSounds sounds)
        {
            BroadcastIdleLoop(sounds, ""); // empty string signals stop
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playIdleLoop", new[] { typeof(string), typeof(bool) })]
    public static class HostIdleLoopPatch
    {
        [HarmonyPrefix]
        private static void Prefix() { TraverseHack.InsideCharacterSounds = true; }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance, object[] __args)
        {
            string loopName = (string)__args[0];
            TraverseHack.InsideCharacterSounds = false;
            EntitySoundSyncHelper.BroadcastIdleLoop(__instance, loopName);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "destroySounds")]
    public static class HostDestroySoundsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
            // When host destroys entity sounds (disable/despawn/state change),
            // tell client to stop the idle loop too so it doesn't loop forever.
            EntitySoundSyncHelper.BroadcastIdleStop(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playGrowl")]
    public static class HostGrowlSoundPatch
    {
        [HarmonyPrefix]
        private static void Prefix() { TraverseHack.InsideCharacterSounds = true; }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
            TraverseHack.InsideCharacterSounds = false;
            EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Growl);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playEscapingLoop")]
    public static class HostEscapingSoundPatch
    {
        [HarmonyPrefix]
        private static void Prefix() { TraverseHack.InsideCharacterSounds = true; }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
            TraverseHack.InsideCharacterSounds = false;
            EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Escaping);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playSingleInstance", new[] { typeof(string) })]
    public static class HostSingleInstanceSoundPatch
    {
        [HarmonyPrefix]
        private static void Prefix() { TraverseHack.InsideCharacterSounds = true; }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance, object[] __args)
        {
            string sound = (string)__args[0];
            TraverseHack.InsideCharacterSounds = false;
            if (string.IsNullOrEmpty(sound)) return;

            // Only broadcast AI-behavior sounds that animation events don't cover
            if (sound == __instance.curious)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Curious);
            else if (sound == __instance.aggressive)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Aggressive);
            else if (sound == __instance.defensive)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Defensive);
        }
    }

    /// <summary>
    /// Attack / death one-shots via CharacterSounds.play — client AI is frozen so
    /// these never fire locally on peers without host broadcast.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSounds), "play", new[] { typeof(string), typeof(bool) })]
    public static class HostCharacterPlaySoundPatch
    {
        [HarmonyPrefix]
        private static void Prefix() { TraverseHack.InsideCharacterSounds = true; }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance, object[] __args)
        {
            string sound = (string)__args[0];
            TraverseHack.InsideCharacterSounds = false;
            if (string.IsNullOrEmpty(sound) || __instance == null) return;
            // Player footsteps / personal audio use a different path.
            if (__instance.isPlayer) return;

            if (!string.IsNullOrEmpty(__instance.attack1) && sound == __instance.attack1)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Attack1);
            else if (!string.IsNullOrEmpty(__instance.attack2) && sound == __instance.attack2)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Attack2);
            else if (!string.IsNullOrEmpty(__instance.death) && sound == __instance.death)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Death);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playGetHitByAxe1")]
    public static class HostGetHitSoundPatch
    {
        [HarmonyPrefix]
        private static void Prefix() { TraverseHack.InsideCharacterSounds = true; }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
            TraverseHack.InsideCharacterSounds = false;
            if (__instance == null || __instance.isPlayer) return;
            EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.GetHit);
        }
    }
}
