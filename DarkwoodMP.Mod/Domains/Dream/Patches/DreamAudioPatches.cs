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
        internal static bool ShouldForward(string audioID, Vector3 worldPosition, Transform parentObj)
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
            // Peers replay these themselves: creature sounds through EntitySound, synced object
            // and game-event sounds through their own replay. Forwarding them too doubled them.
            if (TraverseHack.InsideCharacterSounds || ReplayOwnedSound.Active)
                return false;
            // The host's own player sounds travel as PlayerAudio (allowed in dreams).
            if (parentObj != null && (PlayerAudioHelper.IsPlayerTransform(parentObj) || PlayerAudioHelper.IsPlayerChild(parentObj)))
                return false;
            // A synced creature's direct plays (footsteps, shots) go as EntitySound on its copy.
            if (parentObj != null && PlayerAudioHelper.IsEnemyTransform(parentObj)
                && CharacterTracker.TryGetStableId(parentObj.GetComponent<Character>(), out short creatureId)
                && creatureId != 0)
                return false;
            // A forwarded loop is a bare positional play nothing ever stops.
            if (LocalAudioService.IsLoopingItem(audioID))
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
            if (LocalAudioService.IsEquipGetHideSound(audioID))
                return false;
            if (audioID.IndexOf("aimReturn", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (LocalAudioService.IsCurrentItemActionSound(audioID))
                return false;
            // Entry transition stinger is already played locally on peers via StartRemoteDreamTransition.
            if (Core.EnteringDream)
                return false;

            // Match suppression: do not ship far dream SFX to spectators/peers.
            // A fully 2D one carries across the area each peer has awake: the receiver judges it.
            if (worldPosition != Vector3.zero && LocalAudioService.IsSpatial(audioID)
                && !LocalAudioService.IsNearAnyListener(worldPosition, LocalAudioService.AudibleRange(audioID)))
                return false;

            return true;
        }
    }

    /// <summary>
    /// Host-only dream world one-shot forward (not ambience/music/UI).
    /// Priority Last so distance/suppression prefixes run first.
    /// </summary>
    [HarmonyPatch(typeof(AudioController))]
    [HarmonyPatch("_PlayAsSound")]
    public static class DreamAudioPlayPrefix
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(string audioID, float volume, Vector3 worldPosition, Transform parentObj)
        {
            if (!DreamAudioForwarding.ShouldForward(audioID, worldPosition, parentObj)) return;
            if (!_keys.TryGetValue(audioID, out string key))
            {
                key = "dream:" + audioID;
                if (_keys.Count < 512)
                    _keys[audioID] = key;
            }
            if (!LocalAudioService.TryAllowForward(key)) return;

            var net = ModRuntime.Network;
            if (net == null) return;
            float vol = Mathf.Clamp01(volume);
            if (vol <= 0f) vol = 1f;
            _msg = new DreamAudioMessage
            {
                AudioID = audioID,
                PosX = worldPosition.x,
                PosY = worldPosition.y,
                PosZ = worldPosition.z,
                Volume = vol,
                Pitch = 1f
            };
            // Broadcast so host-originated dream SFX reach all clients (Send = first peer only).
            // It writes the message before it returns: one kept writer, no closure per sound.
            net.Broadcast(NetMessageType.DreamAudio, WriteMsg, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> _keys = new System.Collections.Generic.Dictionary<string, string>(); // process-scoped: rate-limit key per sound id
        private static DreamAudioMessage _msg; // process-scoped: the message Broadcast is writing
        private static readonly System.Action<DWMPHorde.Networking.NetWriter> WriteMsg = w => _msg.Serialize(w); // process-scoped: cached writer
    }
}
