using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host AI <see cref="CharacterSounds"/> → clients (vanilla API surface from decompile):
    /// playGrowl, playSingleInstance (curious/aggressive/defensive/escapingStart),
    /// playIdleLoop, destroySounds, playEscapingLoop, play(attack1/2/death/escapingStart2),
    /// playGetHitByAxe1.
    ///
    /// Client entity AI is frozen, so peers never fire these locally — host must Broadcast.
    /// Prefix sets <see cref="TraverseHack.InsideCharacterSounds"/> so PlayerAudio
    /// AudioController hooks do not double-forward the same clip as a generic SFX.
    /// </summary>
    internal static class EntitySoundSyncHelper
    {
        /// <summary>True while host <see cref="CharacterSounds.playEscapingLoop"/> runs (suppress nested Idle).</summary>
        internal static bool InsideEscapingLoop;

        private static float EntitySoundRange =>
            ClientEntityInterpolationService.ClientInterestDistance;

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

            if (!LocalAudioService.IsNearAnyListener(c.transform.position, EntitySoundRange))
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
            // Nested playIdleLoop from playEscapingLoop — Escaping message owns the loop.
            if (InsideEscapingLoop)
                return;
            Character c = sounds.character as Character;
            if (c == null) return;
            if (!CharacterTracker.TryGetStableId(c, out short hostId)) return;

            bool isStop = string.IsNullOrEmpty(loopName);
            if (!isStop && !LocalAudioService.IsNearAnyListener(c.transform.position, EntitySoundRange))
                return;

            var msg = new EntitySoundMessage { HostId = hostId, SoundType = EntitySoundType.Idle, LoopName = loopName ?? "" };
            LanNetworkManager.Instance?.Broadcast(NetMessageType.EntitySound, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal static void BroadcastIdleStop(CharacterSounds sounds)
        {
            BroadcastIdleLoop(sounds, "");
        }

        /// <summary>Match vanilla idleLoop → idleLoopAggressive when chasing.</summary>
        internal static string ResolveIdleLoopName(CharacterSounds sounds, string loopName)
        {
            if (sounds == null || string.IsNullOrEmpty(loopName))
                return loopName ?? "";
            if (loopName == sounds.idleLoop && !string.IsNullOrEmpty(sounds.idleLoopAggressive))
            {
                Character ch = sounds.character as Character;
                if (ch != null && ch.behaviour == Character.Behaviour.chasingTarget)
                    return sounds.idleLoopAggressive;
            }
            return loopName;
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
            string resolved = EntitySoundSyncHelper.ResolveIdleLoopName(__instance, loopName);
            EntitySoundSyncHelper.BroadcastIdleLoop(__instance, resolved);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "destroySounds")]
    public static class HostDestroySoundsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
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
        private static void Prefix()
        {
            TraverseHack.InsideCharacterSounds = true;
            EntitySoundSyncHelper.InsideEscapingLoop = true;
        }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
            EntitySoundSyncHelper.InsideEscapingLoop = false;
            TraverseHack.InsideCharacterSounds = false;
            EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Escaping);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playSingleInstance", new[] { typeof(string) })]
    public static class HostSingleInstanceSoundPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CharacterSounds __instance, object[] __args)
        {
            // Client: host-synced entities — suppress local anim/AI one-shots; EntitySound owns them.
            if (ShouldSuppressClientLocal(__instance, (string)__args[0]))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance, object[] __args)
        {
            string sound = (string)__args[0];
            TraverseHack.InsideCharacterSounds = false;
            if (string.IsNullOrEmpty(sound)) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            if (sound == __instance.curious)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Curious);
            else if (sound == __instance.aggressive)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Aggressive);
            else if (sound == __instance.defensive)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Defensive);
            else if (!string.IsNullOrEmpty(__instance.escapingStart) && sound == __instance.escapingStart)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.EscapingStart);
        }

        internal static bool ShouldSuppressClientLocal(CharacterSounds sounds, string sound)
        {
            if (sounds == null || sounds.isPlayer) return false;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return false;
            if (ModRuntime.Network.Role != NetworkRole.Client) return false;
            if (TraverseHack.ApplyingFromNetwork || TraverseHack.InsideCharacterSounds) return false;
            Character c = sounds.character as Character;
            return c != null && ClientEntityInterpolationService.IsHostSynced(c);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "play", new[] { typeof(string), typeof(bool) })]
    public static class HostCharacterPlaySoundPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CharacterSounds __instance, object[] __args)
        {
            if (HostSingleInstanceSoundPatch.ShouldSuppressClientLocal(__instance, (string)__args[0]))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance, object[] __args)
        {
            string sound = (string)__args[0];
            TraverseHack.InsideCharacterSounds = false;
            if (string.IsNullOrEmpty(sound) || __instance == null) return;
            if (__instance.isPlayer) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            if (!string.IsNullOrEmpty(__instance.attack1) && sound == __instance.attack1)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Attack1);
            else if (!string.IsNullOrEmpty(__instance.attack2) && sound == __instance.attack2)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Attack2);
            else if (!string.IsNullOrEmpty(__instance.death) && sound == __instance.death)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.Death);
            else if (!string.IsNullOrEmpty(__instance.escapingStart2) && sound == __instance.escapingStart2)
                EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.EscapingStart2);
        }
    }

    [HarmonyPatch(typeof(CharacterSounds), "playGetHitByAxe1")]
    public static class HostGetHitSoundPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CharacterSounds __instance)
        {
            if (HostSingleInstanceSoundPatch.ShouldSuppressClientLocal(__instance, __instance != null ? __instance.getHitByAxe : null))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(CharacterSounds __instance)
        {
            TraverseHack.InsideCharacterSounds = false;
            if (__instance == null || __instance.isPlayer) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            EntitySoundSyncHelper.Broadcast(__instance, EntitySoundType.GetHit);
        }
    }

    /// <summary>
    /// Client host-synced: die2 must be soundless — EntitySound Death is sole authority
    /// (prevents die2 + EntitySound + BeartrapDeath anim DeathSound triple).
    /// </summary>
    [HarmonyPatch(typeof(Character), "die2")]
    public static class ClientDie2SoundlessPatch
    {
        private static void Prefix(Character __instance, ref bool soundless)
        {
            if (soundless) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role != NetworkRole.Client) return;
            if (__instance == null) return;
            if (Player.Instance != null && __instance.gameObject == Player.Instance.gameObject) return;
            if (ClientEntityInterpolationService.IsHostSynced(__instance))
                soundless = true;
        }
    }

    /// <summary>Client host-synced: foot SFX come from host PlayerAudio enemy path.</summary>
    [HarmonyPatch(typeof(CharacterSounds), "playFootHitGround", new[] { typeof(float) })]
    public static class ClientFootHitSuppressPatch
    {
        private static bool Prefix(CharacterSounds __instance)
        {
            return !HostSingleInstanceSoundPatch.ShouldSuppressClientLocal(__instance, "foot");
        }
    }
}
