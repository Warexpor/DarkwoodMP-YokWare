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
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: fromPlayer)) return;
            // Never network menu / BGM tracks (5.3).
            if (!allowObjectLoop && AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            if (!allowObjectLoop && LocalAudioService.IsWorldAmbientLocalOnly(audioID)) return;

            // Dream: host world one-shots go DreamAudio (host-only). Enemy AI → EntitySound.
            // Player-origin still needs PlayerAudio so peers hear client guns/equip in dream.
            // Non-player one-shots during dream: leave to EntitySound / host DreamAudio.
            if (!allowObjectLoop && Dreams.Instance != null && Dreams.Instance.dreaming && !fromPlayer)
                return;

            if (requireRateLimit && !LocalAudioService.TryAllowForward(audioID))
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
            if (HostBansheeAgitatedPatch.SuppressHostScreamForward
                && audioID != null
                && audioID.IndexOf("banshee", System.StringComparison.OrdinalIgnoreCase) >= 0)
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

            // Enemy sounds: host-only AI; skip CharacterSounds path (EntitySound handles
            // growl/idle/attack/etc.). Footsteps and other direct AudioController plays
            // still need this forward (fromPlayer: false keeps enemy foot SFX).
            if (!TraverseHack.InsideCharacterSounds
                && ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host
                && PlayerAudioHelper.IsEnemyTransform(parentObj))
            {
                PlayerAudioHelper.ForwardSound(audioID, 1f, parentObj.position, fromPlayer: false);
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
            if (HostBansheeAgitatedPatch.SuppressHostScreamForward
                && audioID != null
                && audioID.IndexOf("banshee", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return;

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
                PlayerAudioHelper.ForwardSound(audioID, volume, parentObj.position, fromPlayer: false);
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
                PlayerAudioHelper.ForwardSound(audioID, 1f, parentObj.position, fromPlayer: false);
                return;
            }

            PlayerAudioHelper.ForwardWorldObjectSound(audioID, 1f, worldPosition);
        }
    }

    /// <summary>
    /// Personal bag open SFX only. World containers already play open_drawer in
    /// Item.openInventory. Playing here too doubled loot sound on the client.
    /// (initiateOpenCloseInventory → Player.openInventory → this patch + Item.Play).
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(Player), "openInventory")]
    public static class PlayerOpenInventorySoundPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            // Vanilla plays nothing for the personal bag; the mod adds it only in a live session.
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (__instance == null) return;
            // Container / corpse open sets openedItemInventory before openInventory().
            if (__instance.openedItemInventory != null)
                return;
            AudioController.Play("open_drawer", __instance._transform);
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
            if (!PlayerAudioHelper.CanForward()) return;
            var net = ModRuntime.Network;
            if (LocalAudioService.IsPersonalOrUiSound(audioID)) return;
            if (AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            if (!LocalAudioService.IsAllowlistedNoParentSound(audioID)) return;
            if (!LocalAudioService.TryAllowForward(audioID)) return;

            // Always stamp player pos for distance cull on RX. Prefer2d one-shots still
            // play 2D (no proxy parent / reverb); hits spatialize on the victim proxy.
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
            });
        }
    }
}
