using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class DreamAudioForwarding
    {
        internal static bool ShouldForward(Vector3 worldPosition)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return false;
            if (LanNetworkManager.IsApplyingRemoteState)
                return false;
            if (Dreams.Instance == null || !Dreams.Instance.dreaming)
                return false;

            // Match suppression: do not ship far dream SFX to spectators/peers.
            if (worldPosition != Vector3.zero
                && !LocalAudioService.IsNearListener(worldPosition, LocalAudioService.DefaultMaxAudioDistance))
                return false;

            return true;
        }
    }

    /// <summary>
    /// Runs after distance suppression defaults so far sounds are not also network-forwarded.
    /// Priority lower than default = runs later in Prefix chain (Harmony: higher priority first).
    /// We use First so we evaluate before play; distance check still blocks wasteful sends.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(AudioController))]
    [HarmonyPatch("_PlayAsSound")]
    public static class DreamAudioPlayPrefix
    {
        private static void Prefix(string audioID, float volume, Vector3 worldPosition)
        {
            if (!DreamAudioForwarding.ShouldForward(worldPosition)) return;
            if (string.IsNullOrEmpty(audioID)) return;

            // Fix 3: Dream preset music is played locally on each peer via startDreaming.
            // Forwarding would cause doubled audio on the receiver.
            if (Dreams.Instance?.preset != null
                && !string.IsNullOrEmpty(Dreams.Instance.preset.music)
                && Dreams.Instance.preset.music == audioID)
                return;

            if (!LocalAudioService.TryAllowForward("dream:" + audioID)) return;

            var net = ModRuntime.Network as LanNetworkManager;
            // Broadcast so host-originated dream SFX reach all clients (Send = first peer only).
            net?.Broadcast(NetMessageType.DreamAudio,
                w => new DreamAudioMessage
                {
                    AudioID = audioID,
                    PosX = worldPosition.x,
                    PosY = worldPosition.y,
                    PosZ = worldPosition.z,
                    Volume = volume,
                    Pitch = 1f
                }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
    }
}
