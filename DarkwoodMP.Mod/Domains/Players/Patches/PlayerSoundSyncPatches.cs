using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;
using DWMPHorde.Harmony;

namespace DWMPHorde.Patches
{
    internal static class PlayerAudioHelper
    {
        /// <param name="fromPlayer">
        /// When true, suppress player footstep / personal SFX (remote peers use
        /// HandleProxyFootstep). When false (enemy/world), footsteps are forwarded.
        /// </param>
        internal static void ForwardSound(string audioID, float volume, Vector3 position, bool requireRateLimit = true, bool fromPlayer = true, bool allowObjectLoop = false)
        {
            if (string.IsNullOrEmpty(audioID)) return;
            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (!net.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            // A step from the player's torso clip (window-jump landing, dodge) goes out: the
            // stand-in replays only its legs' steps, and its legs are hidden through those clips.
            bool torsoStep = fromPlayer && PlayerTorsoFrameTriggerScope.Active
                && LocalAudioService.IsPlayerStepSound(audioID);
            if (!torsoStep && LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: fromPlayer)) return;
            // The gunshot reaches peers with PlayerFiredWeapon.
            if (fromPlayer && LocalAudioService.IsCurrentFirearmShotSound(audioID)) return;
            // Never network menu / BGM tracks (5.3).
            if (!allowObjectLoop && AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            if (!allowObjectLoop && LocalAudioService.IsWorldAmbientLocalOnly(audioID)) return;

            // Dream: host world one-shots go DreamAudio (host-only). Enemy AI → EntitySound.
            // Player-origin still needs PlayerAudio so peers hear client guns/equip in dream.
            // Non-player one-shots during dream: leave to EntitySound / host DreamAudio.
            if (!allowObjectLoop && Dreams.Instance != null && Dreams.Instance.dreaming && !fromPlayer)
                return;

            // Torso steps come only from clip frames, a few per jump or dodge; the per-id limit
            // would drop a jump's second landing step.
            if (requireRateLimit && !torsoStep && !LocalAudioService.TryAllowForward(audioID))
                return;

            net.SendPlayerAudio(new PlayerAudioMessage
            {
                SoundId = audioID,
                Volume = Mathf.Clamp01(volume),
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z,
                StickToSender = fromPlayer
            });
        }

        /// <summary>
        /// Host: a play parented to a creature goes as an attached EntitySound, so the client plays
        /// it on its copy (moving with it, with the game's reverb and wall occlusion). A creature
        /// without a host id falls back to a positional forward.
        /// </summary>
        internal static void ForwardCreatureSound(Transform parentObj, string audioID, float volume)
        {
            if (string.IsNullOrEmpty(audioID) || volume <= 0.001f) return;
            // A prologue pad creature has no id on purpose: no positional fallback for it either.
            if (PersonalPrologue.IsOnProloguePad(parentObj)) return;
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: false)) return;
            if (AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            if (LocalAudioService.IsLoopingItem(audioID)) return;
            if (EntitySoundSyncHelper.Send(parentObj.GetComponent<Character>(), EntitySoundKind.Attached, audioID, volume))
                return;
            ForwardSound(audioID, volume, parentObj.position, fromPlayer: false);
        }

        /// <summary>
        /// Door.Update owns start/stop of hinge scrape on every peer that has the
        /// door swinging. Networking those loops left an
        /// orphan AudioController loop on peers because Stop is never forwarded.
        /// </summary>
        internal static bool IsDoorOwnedRotatingLoop(string audioID)
        {
            if (string.IsNullOrEmpty(audioID)) return false;
            return string.Equals(audioID, "door_rotating", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(audioID, "door_metal_rotating", System.StringComparison.OrdinalIgnoreCase);
        }

        internal static void ForwardWorldObjectSound(string audioID, float volume, Vector3 position)
        {
            if (TraverseHack.InsideCharacterSounds) return;
            // Door hinge scrape is simulated locally via Door.Update — never network it.
            if (IsDoorOwnedRotatingLoop(audioID)) return;
            // Keep foot/walk_clothes local-or-proxy-owned. Enemy feet use ForwardSound
            // (fromPlayer: false) on the enemy path above; proxy feet must not re-enter.
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: true)) return;
            // Silent start (vanilla fades it in by volume afterwards): nothing to hear on a peer.
            if (volume <= 0.001f) return;
            // A loop needs an owner that can stop it; a forwarded copy is a bare positional play
            // nothing ever stops. Object loops come from the peer's own replay of the object.
            if (LocalAudioService.IsLoopingItem(audioID)) return;
            ForwardSound(audioID, volume, position, fromPlayer: false, allowObjectLoop: true);
        }

        /// <summary>
        /// Trap snap / activate is applied on peers via TrapState → ApplyTrapState
        /// (AudioController.Play on the trap transform). Forwarding the host's local
        /// activateSound doubled the snap for listeners.
        /// </summary>
        internal static bool IsTrapOwnedActivateSound(Transform parentObj, string audioID)
        {
            if (parentObj == null || string.IsNullOrEmpty(audioID)) return false;
            Trigger trig = parentObj.GetComponent<Trigger>();
            if (trig == null) trig = parentObj.GetComponentInParent<Trigger>();
            if (trig == null) return false;
            if (!TrapNetworkId.IsWorldTrap(trig.gameObject)) return false;
            if (string.IsNullOrEmpty(trig.activateSound)) return false;
            return string.Equals(audioID, trig.activateSound, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Live session, not replaying a peer's sound, and not inside a vanilla method every peer
        /// re-runs itself (<see cref="ReplayOwnedSound"/>): only then is any forward possible.
        /// </summary>
        internal static bool CanForward()
        {
            var net = ModRuntime.Network;
            return net != null && net.IsConnected && !TraverseHack.ApplyingFromNetwork
                && !ReplayOwnedSound.Active;
        }

        internal static bool IsPlayerTransform(Transform t)
        {
            if (t == null) return false;
            Player p = Player.Instance;
            if (p == null) return false;
            // Vanilla often parents SFX to Player._transform (same as transform after Awake).
            return t == p.transform || t == p._transform;
        }

        internal static bool IsEnemyTransform(Transform t)
        {
            if (t == null) return false;
            Player p = Player.Instance;
            if (p != null && t == p.transform) return false;
            return t.GetComponent<Character>() != null;
        }

        internal static bool IsPlayerChild(Transform t)
        {
            if (t == null) return false;
            Player p = Player.Instance;
            return p != null && t != p.transform && t.IsChildOf(p.transform);
        }
    }

    /// <summary>Play(string audioID, Transform parentObj)</summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string), typeof(Transform))]
    public static class AudioPlayStrTrans
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID, Transform parentObj)
        {
            if (parentObj == null) return;
            // Single-player / replaying a peer: skip the per-play component lookups below.
            if (!PlayerAudioHelper.CanForward()) return;
            if (PlayerAudioHelper.IsTrapOwnedActivateSound(parentObj, audioID))
                return;

            if (PlayerAudioHelper.IsPlayerTransform(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, 1f, parentObj.position);
                return;
            }

            if (PlayerAudioHelper.IsPlayerChild(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, 1f, parentObj.position);
                return;
            }

            // Creature sounds: CharacterSounds one-shots are sent by their own patches; a direct
            // play on the creature (footsteps, shots, sniffs) goes attached to it.
            if (!TraverseHack.InsideCharacterSounds
                && ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host
                && PlayerAudioHelper.IsEnemyTransform(parentObj))
            {
                PlayerAudioHelper.ForwardCreatureSound(parentObj, audioID, 1f);
                return;
            }

            PlayerAudioHelper.ForwardWorldObjectSound(audioID, 1f, parentObj.position);
        }
    }

    /// <summary>Play(string audioID, Transform parentObj, float volume, float delay, float startTime)</summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string), typeof(Transform), typeof(float), typeof(float), typeof(float))]
    public static class AudioPlayStrTransFloatFloatFloat
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID, Transform parentObj, float volume)
        {
            if (parentObj == null) return;
            // Single-player / replaying a peer: skip the per-play component lookups below.
            if (!PlayerAudioHelper.CanForward()) return;
            if (PlayerAudioHelper.IsTrapOwnedActivateSound(parentObj, audioID))
                return;

            if (PlayerAudioHelper.IsPlayerTransform(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, volume, parentObj.position);
                return;
            }

            if (PlayerAudioHelper.IsPlayerChild(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, volume, parentObj.position);
                return;
            }

            if (!TraverseHack.InsideCharacterSounds
                && ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host
                && PlayerAudioHelper.IsEnemyTransform(parentObj))
            {
                PlayerAudioHelper.ForwardCreatureSound(parentObj, audioID, volume);
                return;
            }

            PlayerAudioHelper.ForwardWorldObjectSound(audioID, volume, parentObj.position);
        }
    }

    /// <summary>Play(string audioID, Vector3 worldPosition, Transform parentObj = null)</summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string), typeof(Vector3), typeof(Transform))]
    public static class AudioPlayStrVecTrans
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID, Vector3 worldPosition, Transform parentObj)
        {
            if (!PlayerAudioHelper.CanForward()) return;
            // Parentless world/ambient plays must not flood the network; each peer
            // already runs local ambience / other sync messages cover combat FX.
            if (parentObj == null)
                return;

            bool enemy = PlayerAudioHelper.IsEnemyTransform(parentObj);
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: !enemy))
                return;

            if (PlayerAudioHelper.IsTrapOwnedActivateSound(parentObj, audioID))
                return;

            if (PlayerAudioHelper.IsPlayerTransform(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, 1f, parentObj.position);
                return;
            }

            if (PlayerAudioHelper.IsPlayerChild(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, 1f, parentObj.position);
                return;
            }

            if (!TraverseHack.InsideCharacterSounds
                && ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host
                && enemy)
            {
                PlayerAudioHelper.ForwardCreatureSound(parentObj, audioID, 1f);
                return;
            }

            PlayerAudioHelper.ForwardWorldObjectSound(audioID, 1f, worldPosition);
        }
    }

    /// <summary>
    /// True while a frame event of the local player's torso clip runs. Vanilla routes the torso
    /// and the legs animator through <see cref="AnimationTriggerListener"/> into
    /// <c>Player.checkFrameTrigger</c>, and both play steps (FootHitGround / FootHitGroundRun).
    /// A stand-in replays its legs' steps itself (RemotePlayerProxy.OnLegAnimationEvent), so leg
    /// steps stay local; a torso step (the JumpWindow landing, Dodge, a stumble) has no legs to
    /// come from on the stand-in, whose legs are hidden and stopped through those clips.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(AnimationTriggerListener), nameof(AnimationTriggerListener.animationTriggerListener))]
    public static class PlayerTorsoFrameTriggerScope
    {
        internal static bool Active; // process-scoped: call-scoped, unwound by the Finalizer

        private static void Prefix(tk2dSpriteAnimator animator, out bool __state)
        {
            __state = Active;
            Player p = Player.Instance;
            Active = p != null && animator != null && animator == p.torsoAnimator;
        }

        private static void Finalizer(bool __state)
        {
            Active = __state;
        }
    }

    /// <summary>
    /// Play(string audioID). Only allowlisted player one-shots, such as molotov, are forwarded.
    /// Blanket forwarding previously spammed ambient/music helpers over the LAN.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string))]
    public static class AudioPlayStrOnly
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID)
        {
            // The local player's own hit sound goes out even while a peer's message is being
            // applied: a hit the host or a friendly-fire attacker dealt arrives as DamagePlayer,
            // and only the victim's getHit knows whether it landed, was blocked or dodged.
            bool ownHit = LocalPlayerHitScope.Active && LocalAudioService.IsPlayerHitFeedbackSound(audioID);
            var net = ModRuntime.Network;
            if (ownHit)
            {
                if (net == null || !net.IsConnected) return;
            }
            else if (!PlayerAudioHelper.CanForward()) return;
            if (LocalAudioService.IsPersonalOrUiSound(audioID)) return;
            if (AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            if (!LocalAudioService.IsAllowlistedNoParentSound(audioID)) return;
            // The gunshot reaches peers with PlayerFiredWeapon.
            if (LocalAudioService.IsCurrentFirearmShotSound(audioID)) return;
            if (!LocalAudioService.TryAllowForward(audioID)) return;

            // Always stamp player pos for the hear gate on RX; peers play it on this player's
            // stand-in (3D, with its reverb and wall muffle).
            float px = float.NaN, py = float.NaN, pz = float.NaN;
            if (Player.Instance != null)
            {
                Vector3 p = Player.Instance._transform != null
                    ? Player.Instance._transform.position
                    : Player.Instance.transform.position;
                px = p.x;
                py = p.y;
                pz = p.z;
            }

            net.SendPlayerAudio(new PlayerAudioMessage
            {
                SoundId = audioID,
                Volume = 1f,
                PosX = px,
                PosY = py,
                PosZ = pz,
                StickToSender = true
            }, ownOutcome: ownHit);
        }
    }

    /// <summary>
    /// True while the local player's own <c>getHit</c> / <c>getHitByShadow</c> runs. The hit
    /// sound it plays (<c>player_melee_hit</c>, <c>door_hit_metal</c> when blocked,
    /// <c>shadow_hit</c>) is the one every peer hears on this player's stand-in, wherever the
    /// hit came from: a creature on this machine, the host's DamagePlayer, a friendly-fire
    /// attacker. A player is only ever hit on its own machine, so no peer plays it twice.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch]
    public static class LocalPlayerHitScope
    {
        private static int _depth; // process-scoped: call-scoped, unwound by the Finalizer

        internal static bool Active => _depth > 0;

        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Player), "getHit", new[]
            {
                typeof(float), typeof(Transform), typeof(bool), typeof(bool), typeof(bool),
                typeof(bool), typeof(bool), typeof(bool), typeof(bool)
            });
            yield return AccessTools.Method(typeof(Player), "getHitByShadow", new[] { typeof(float) });
        }

        private static void Prefix(Player __instance, out bool __state)
        {
            __state = __instance != null && __instance == Player.Instance;
            if (__state)
                _depth++;
        }

        private static void Finalizer(bool __state)
        {
            if (__state && _depth > 0)
                _depth--;
        }
    }
}
