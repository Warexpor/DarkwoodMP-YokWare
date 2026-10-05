using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using DWMPHorde.Harmony;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host creature sounds → clients. The host runs every creature's AI, so each
    /// <see cref="CharacterSounds"/> one-shot it plays (play, playSingleInstance, playGrowl,
    /// playGetHitByAxe1, the death line) and each AudioController play parented to a creature
    /// (footsteps, shots, sniffs) goes out as <see cref="EntitySoundMessage"/> with its audio id,
    /// whatever triggered it: the host's AI, an animation event, or a client's hit the host is
    /// applying. Loops are state in the entity snapshot (<see cref="EntityLoopSync"/>).
    /// A client plays these on its copy of the creature; its own copy's CharacterSounds calls
    /// are blocked (its AI is frozen, the host's AI owns the creature's voice).
    /// <see cref="TraverseHack.InsideCharacterSounds"/> marks the CharacterSounds scope so the
    /// AudioController forward inside it is not sent a second time as an attached sound.
    /// </summary>
    internal static class EntitySoundSyncHelper
    {
        /// <summary>True while host <see cref="CharacterSounds.playFootHitGround"/> runs: its plays go unreliable.</summary>
        internal static bool InsideFootstep; // process-scoped: call-scoped, unwound by its Finalizer

        /// <summary>Vanilla CharacterSounds guards (underwaterCanPlay, isUnderground) for a one-shot.</summary>
        internal static bool VanillaWouldPlay(CharacterSounds s, bool canPlayWhenUnderground = false)
        {
            if (s == null || s.character == null)
                return false;
            Underwater uw = s.underwater;
            if (uw != null && uw.isUnderwater && !uw.playCharacterSoundsWhenUnderwater)
                return false;
            return canPlayWhenUnderground || !s.character.isUnderground;
        }

        internal static void Send(CharacterSounds s, EntitySoundKind kind, string soundId, float volume = 1f)
        {
            if (s == null || s.isPlayer || string.IsNullOrEmpty(soundId))
                return;
            Send(s.character as Character, kind, soundId, volume);
        }

        /// <returns>False when the creature has no host id (caller may fall back to a positional forward).</returns>
        internal static bool Send(Character c, EntitySoundKind kind, string soundId, float volume = 1f)
        {
            if (!NetGuard.ConnectedHost(out LanNetworkManager net))
                return false;
            if (c == null || string.IsNullOrEmpty(soundId))
                return false;
            if (!CharacterTracker.TryGetStableId(c, out short hostId) || hostId == 0)
                return false;
            // Within this sound's own carry of some player (a spectator listens at the one it follows),
            // and within client interest: a copy past it is not driven and drops the sound.
            float range = Mathf.Min(LocalAudioService.AudibleRange(soundId),
                ClientEntityInterpolationService.ClientInterestDistance);
            if (!LocalAudioService.IsNearAnyListener(c.transform.position, range))
                return true;

            var msg = new EntitySoundMessage
            {
                HostId = hostId,
                Kind = kind,
                SoundId = soundId,
                Volume = volume,
                // A hit the host applies from a client's attack: that client already showed it.
                AttackerId = kind == EntitySoundKind.GetHit && LanNetworkManager.IsApplyingRemoteState
                    ? net.CurrentReceivePlayerId
                    : -1
            };
            // A late footstep is worse than a lost one; voices, hits and deaths must arrive.
            DeliveryMethod delivery = InsideFootstep ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
            net.Broadcast(NetMessageType.EntitySound, w => msg.Serialize(w), delivery);
            return true;
        }

        /// <summary>Client: a host-synced copy's own CharacterSounds call (frozen AI, anim event) is not played.</summary>
        internal static bool ShouldSuppressClientLocal(CharacterSounds sounds)
        {
            if (sounds == null || sounds.isPlayer) return false;
            if (!NetGuard.Connected(out var net) || net.Role != NetworkRole.Client) return false;
            if (TraverseHack.InsideCharacterSounds) return false;
            Character c = sounds.character as Character;
            return c != null && ClientEntityInterpolationService.IsHostSynced(c);
        }

        internal static bool IsHost => NetGuard.ConnectedHost(out _);
    }

    /// <summary>
    /// Client: a host-synced copy's loop is the host's (<see cref="EntityLoopSync"/>). Its own
    /// playIdleLoop / destroySounds (WorldGrid enableComponents, OnEnable, Underwater, game
    /// events) would start the wrong loop or kill the right one. Host: the scope keeps the
    /// loop's AudioController play from being forwarded as a one-shot.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch]
    public static class ClientCreatureLoopGuardPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterSounds), "playIdleLoop", new[] { typeof(string), typeof(bool) });
            yield return AccessTools.Method(typeof(CharacterSounds), "playEscapingLoop");
            yield return AccessTools.Method(typeof(CharacterSounds), "destroySounds");
        }

        private static bool Prefix(CharacterSounds __instance, out bool __state)
        {
            __state = TraverseHack.InsideCharacterSounds;
            if (!EntityLoopSync.Applying && EntitySoundSyncHelper.ShouldSuppressClientLocal(__instance))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            TraverseHack.InsideCharacterSounds = __state;
        }
    }

    /// <summary>
    /// Every machine: the loop vanilla playIdleLoop chose (<see cref="EntityLoopSync"/>). The host
    /// sends that choice, not its own AudioObject, which its distance cull may have refused.
    /// playEscapingLoop goes through playIdleLoop.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "playIdleLoop", new[] { typeof(string), typeof(bool) })]
    public static class CreatureLoopIntentPatch
    {
        private static void Postfix(CharacterSounds __instance, string loopName, bool __runOriginal)
        {
            if (__runOriginal && __instance != null && !__instance.isPlayer)
                EntityLoopSync.NoteVanillaPlayIdleLoop(__instance, loopName);
        }
    }

    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "destroySounds")]
    public static class CreatureLoopIntentClearPatch
    {
        private static void Postfix(CharacterSounds __instance, bool __runOriginal)
        {
            if (__runOriginal && __instance != null && !__instance.isPlayer)
                EntityLoopSync.NoteVanillaDestroySounds(__instance);
        }
    }

    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "play", new[] { typeof(string), typeof(bool) })]
    public static class HostCharacterPlaySoundPatch
    {
        private static bool Prefix(CharacterSounds __instance, out bool __state)
        {
            __state = TraverseHack.InsideCharacterSounds;
            if (EntitySoundSyncHelper.ShouldSuppressClientLocal(__instance))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        private static void Postfix(CharacterSounds __instance, string sound, bool canPlayWhenUnderground, bool __runOriginal)
        {
            if (!__runOriginal || !EntitySoundSyncHelper.IsHost)
                return;
            if (!EntitySoundSyncHelper.VanillaWouldPlay(__instance, canPlayWhenUnderground))
                return;
            EntitySoundKind kind = sound == __instance.death ? EntitySoundKind.Death : EntitySoundKind.Play;
            EntitySoundSyncHelper.Send(__instance, kind, sound);
        }

        private static void Finalizer(bool __state)
        {
            TraverseHack.InsideCharacterSounds = __state;
        }
    }

    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "playSingleInstance", new[] { typeof(string) })]
    public static class HostSingleInstanceSoundPatch
    {
        private static bool Prefix(CharacterSounds __instance, out bool __state)
        {
            __state = TraverseHack.InsideCharacterSounds;
            if (EntitySoundSyncHelper.ShouldSuppressClientLocal(__instance))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        private static void Postfix(CharacterSounds __instance, string sound, bool __runOriginal)
        {
            if (!__runOriginal || !EntitySoundSyncHelper.IsHost)
                return;
            if (!EntitySoundSyncHelper.VanillaWouldPlay(__instance))
                return;
            EntitySoundKind kind = sound == __instance.death ? EntitySoundKind.Death : EntitySoundKind.Single;
            EntitySoundSyncHelper.Send(__instance, kind, sound);
        }

        private static void Finalizer(bool __state)
        {
            TraverseHack.InsideCharacterSounds = __state;
        }
    }

    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "playGrowl")]
    public static class HostGrowlSoundPatch
    {
        private static bool Prefix(CharacterSounds __instance, out bool __state)
        {
            __state = TraverseHack.InsideCharacterSounds;
            if (EntitySoundSyncHelper.ShouldSuppressClientLocal(__instance))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        private static void Postfix(CharacterSounds __instance, bool __runOriginal)
        {
            if (!__runOriginal || !EntitySoundSyncHelper.IsHost)
                return;
            if (!EntitySoundSyncHelper.VanillaWouldPlay(__instance))
                return;
            // playGrowl is playSingleInstance(growl) in all but name.
            EntitySoundSyncHelper.Send(__instance, EntitySoundKind.Single, __instance.growl);
        }

        private static void Finalizer(bool __state)
        {
            TraverseHack.InsideCharacterSounds = __state;
        }
    }

    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "playGetHitByAxe1")]
    public static class HostGetHitSoundPatch
    {
        private static bool Prefix(CharacterSounds __instance, out bool __state)
        {
            __state = TraverseHack.InsideCharacterSounds;
            if (EntitySoundSyncHelper.ShouldSuppressClientLocal(__instance))
                return false;
            TraverseHack.InsideCharacterSounds = true;
            return true;
        }

        private static void Postfix(CharacterSounds __instance, bool __runOriginal)
        {
            if (!__runOriginal || !EntitySoundSyncHelper.IsHost || __instance == null)
                return;
            EntitySoundSyncHelper.Send(__instance, EntitySoundKind.GetHit, __instance.getHitByAxe);
        }

        private static void Finalizer(bool __state)
        {
            TraverseHack.InsideCharacterSounds = __state;
        }
    }

    /// <summary>
    /// Footsteps: the client's copy plays none of its own (frozen AI, replayed clips); the
    /// host's go out attached to the creature, unreliable.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(CharacterSounds), "playFootHitGround", new[] { typeof(float) })]
    public static class ClientFootHitSuppressPatch
    {
        private static bool Prefix(CharacterSounds __instance, out bool __state)
        {
            __state = EntitySoundSyncHelper.InsideFootstep;
            if (EntitySoundSyncHelper.ShouldSuppressClientLocal(__instance))
                return false;
            EntitySoundSyncHelper.InsideFootstep = true;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            EntitySoundSyncHelper.InsideFootstep = __state;
        }
    }

    /// <summary>
    /// Client host-synced: die2 is presentation only and soundless; the host's Death sound is
    /// the one death line (vanilla die2 plays it, and BeartrapDeath's anim adds another).
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(Character), "die2")]
    public static class ClientDie2SoundlessPatch
    {
        private static void Prefix(Character __instance, ref bool soundless)
        {
            if (soundless) return;
            if (!NetGuard.Connected(out var net) || net.Role != NetworkRole.Client) return;
            if (__instance == null) return;
            if (Player.Instance != null && __instance.gameObject == Player.Instance.gameObject) return;
            if (ClientEntityInterpolationService.IsHostSynced(__instance))
                soundless = true;
        }
    }
}
