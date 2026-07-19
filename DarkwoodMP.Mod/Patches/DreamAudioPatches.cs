using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class DreamAudioForwarding
    {
        /// <summary>
        /// Host-only world one-shots during dreams. Clients keep local scene audio +
        /// player SFX via PlayerAudio; bidirectional DreamAudio was flooding both ends
        /// (client→host 30+ pkt/2s) and stacking on local ambients.
        /// </summary>
        internal static bool ShouldForward(string audioID, Vector3 worldPosition)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return false;
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return false;
            if (LanNetworkManager.IsApplyingRemoteState)
                return false;
            if (Dreams.Instance == null || !Dreams.Instance.dreaming)
                return false;
            if (string.IsNullOrEmpty(audioID))
                return false;

            // Each peer already plays music/ambience/BGM from their local dream scene.
            if (Dreams.Instance.preset != null
                && !string.IsNullOrEmpty(Dreams.Instance.preset.music)
                && Dreams.Instance.preset.music == audioID)
                return false;
            if (AudioSuppressionLogic.IsNeverCullSound(audioID))
                return false;
            if (LocalAudioService.IsWorldAmbientLocalOnly(audioID))
                return false;
            if (LocalAudioService.IsPersonalOrUiSound(audioID, suppressFootsteps: true))
                return false;
            // Equip get/hide (Get_01 etc.) — PlayerAudio owns these; DreamAudio cannot resolve
            // many clip names and only produces "Could not resolve clip" noise.
            if (LocalAudioService.IsPrefer2dNetworkOneShot(audioID))
                return false;

            // Match suppression: do not ship far dream SFX to spectators/peers.
            if (worldPosition != Vector3.zero
                && !LocalAudioService.IsNearAnyListener(worldPosition, LocalAudioService.DefaultMaxAudioDistance))
                return false;

            return true;
        }
    }

    /// <summary>
    /// Host-only dream world one-shot forward (not ambience/music/UI).
    /// Priority Last so distance/suppression prefixes run first.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(AudioController))]
    [HarmonyPatch("_PlayAsSound")]
    public static class DreamAudioPlayPrefix
    {
        private static void Prefix(string audioID, float volume, Vector3 worldPosition)
        {
            if (!DreamAudioForwarding.ShouldForward(audioID, worldPosition)) return;
            if (!LocalAudioService.TryAllowForward("dream:" + audioID)) return;

            var net = ModRuntime.Network as LanNetworkManager;
            float vol = Mathf.Clamp01(volume);
            if (vol <= 0f) vol = 1f;
            // Broadcast so host-originated dream SFX reach all clients (Send = first peer only).
            net?.Broadcast(NetMessageType.DreamAudio,
                w => new DreamAudioMessage
                {
                    AudioID = audioID,
                    PosX = worldPosition.x,
                    PosY = worldPosition.y,
                    PosZ = worldPosition.z,
                    Volume = vol,
                    Pitch = 1f
                }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
    }
}
