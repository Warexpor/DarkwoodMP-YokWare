using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class PlayerAudioHelper
    {
        /// <param name="fromPlayer">
        /// When true, suppress player footstep / personal SFX (remote peers use
        /// HandleProxyFootstep). When false (enemy/world), footsteps are forwarded.
        /// </param>
        internal static void ForwardSound(string audioID, float volume, Vector3 position, bool requireRateLimit = true, bool fromPlayer = true)
        {
            if (string.IsNullOrEmpty(audioID)) return;
            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (!net.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: fromPlayer)) return;
            // Never network menu / BGM tracks (5.3).
            if (AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            // Forest / SoundArea / RandomWorldSounds loops parent to Player for listener
            // follow only — not player SFX. Forwarding them makes peers hear ambients
            // from the remote proxy (host walks outside → client hears forest from host).
            if (LocalAudioService.IsWorldAmbientLocalOnly(audioID)) return;

            // Dream session: DreamAudioPatches owns _PlayAsSound/music forward.
            // Avoid double-send (PlayerAudio + DreamAudio) for the same one-shot (5.1).
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
                return;

            if (requireRateLimit && !LocalAudioService.TryAllowForward(audioID))
                return;

            net.SendPlayerAudio(new PlayerAudioMessage
            {
                SoundId = audioID,
                Volume = Mathf.Clamp01(volume),
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z
            });
        }

        internal static bool IsPlayerTransform(Transform t)
        {
            if (t == null) return false;
            Player p = Player.Instance;
            return p != null && t == p.transform;
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
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string), typeof(Transform))]
    public static class AudioPlayStrTrans
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID, Transform parentObj)
        {
            if (parentObj == null) return;

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
            }
        }
    }

    /// <summary>Play(string audioID, Transform parentObj, float volume, float delay, float startTime)</summary>
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string), typeof(Transform), typeof(float), typeof(float), typeof(float))]
    public static class AudioPlayStrTransFloatFloatFloat
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID, Transform parentObj, float volume)
        {
            if (parentObj == null) return;

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
            }
        }
    }

    /// <summary>Play(string audioID, Vector3 worldPosition, Transform parentObj = null)</summary>
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string), typeof(Vector3), typeof(Transform))]
    public static class AudioPlayStrVecTrans
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID, Vector3 worldPosition, Transform parentObj)
        {
            // Parentless world/ambient plays must NOT flood the network — each peer
            // already runs local ambience / other sync messages cover combat FX.
            if (parentObj == null)
                return;

            bool enemy = PlayerAudioHelper.IsEnemyTransform(parentObj);
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: !enemy))
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
            }
        }
    }

    /// <summary>
    /// Personal bag open SFX only. World containers already play open_drawer in
    /// Item.openInventory — playing here too doubled loot sound on client
    /// (initiateOpenCloseInventory → Player.openInventory → this patch + Item.Play).
    /// </summary>
    [HarmonyPatch(typeof(Player), "openInventory")]
    public static class PlayerOpenInventorySoundPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            if (TraverseHack.ApplyingFromNetwork) return;
            if (__instance == null) return;
            // Container / corpse open sets openedItemInventory before openInventory().
            if (__instance.openedItemInventory != null)
                return;
            AudioController.Play("open_drawer", __instance._transform);
        }
    }

    /// <summary>
    /// Play(string audioID) — only allowlisted player one-shots (e.g. molotov).
    /// Blanket forwarding previously spammed ambient/music helpers over the LAN.
    /// </summary>
    [HarmonyPatch(typeof(AudioController), "Play", typeof(string))]
    public static class AudioPlayStrOnly
    {
        [HarmonyPrefix]
        private static void Prefix(string audioID)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (!net.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (LocalAudioService.IsPersonalOrUiSound(audioID)) return;
            if (AudioSuppressionLogic.IsNeverCullSound(audioID)) return;
            if (!LocalAudioService.IsAllowlistedNoParentSound(audioID)) return;
            if (!LocalAudioService.TryAllowForward(audioID)) return;

            // Hit feedback (Player.getHit) is parentless/2D in vanilla. Peers must get a
            // world position so HandlePlayerAudio spatializes on the victim proxy —
            // NaN alone made some AudioItems play full-volume "on me" for bystanders.
            float px = float.NaN, py = float.NaN, pz = float.NaN;
            if (LocalAudioService.IsPlayerHitFeedbackSound(audioID) && Player.Instance != null)
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
                PosZ = pz
            });
        }
    }
}
