using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to player state / presence / interact NetHandlers.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal PlayerStateNetHandlers PlayerStateHandlers { get; private set; }
        internal PlayerHeldLightPackNetHandlers PlayerHeldLightPackHandlers { get; private set; }
        internal PlayerHeldLightApplyNetHandlers PlayerHeldLightApplyHandlers { get; private set; }
        internal PlayerHeldLightNetHandlers PlayerHeldLightHandlers { get; private set; }
        internal PlayerPresenceNetHandlers PlayerPresenceHandlers { get; private set; }
        internal PlayerInteractNetHandlers PlayerInteractHandlers { get; private set; }

        private void HandlePlayerState(PlayerStateMessage state)
        {
            PlayerStateHandlers.HandlePlayerState(state);
        }

        private void HandleEntityState(EntityStateMessage msg)
        {
            PlayerPresenceHandlers.HandleEntityState(msg);
        }

        private void HandleDragSync(DragSyncMessage msg)
        {
            PlayerInteractHandlers.HandleDragSync(msg);
        }

        private void TickClientCorpseSetup()
        {
            PlayerPresenceHandlers.TickClientCorpseSetup();
        }

        public bool HasAnyTrappedPlayer
        {
            get { return PlayerPresenceHandlers.HasAnyTrappedPlayer; }
        }

        public bool IsRemotePlayerHasLightProtection(int playerId)
        {
            return PlayerPresenceHandlers.IsRemotePlayerHasLightProtection(playerId);
        }

        public bool IsTrapOccupied(GameObject trapGo)
        {
            return PlayerPresenceHandlers.IsTrapOccupied(trapGo);
        }

        public bool IsRemotePlayerTrappedNear(Vector3 trapPos)
        {
            return PlayerPresenceHandlers.IsRemotePlayerTrappedNear(trapPos);
        }

        public static void NotifyBodyPushStarted(GameObject go)
        {
            var net = Instance;
            if (net == null) return;
            net.PlayerInteractHandlers.NotifyBodyPushStarted(go);
        }

        public static void NotifyBodyPushStopped(string objectName)
        {
            var net = Instance;
            if (net == null) return;
            net.PlayerInteractHandlers.NotifyBodyPushStopped(objectName);
        }

        internal void ClearSpawnedDragProxyItems()
        {
            PlayerInteractHandlers.ClearSpawnedDragProxyItems();
        }

        internal void DestroyRemoteItemLight(int playerId)
        {
            PlayerHeldLightHandlers.DestroyRemoteItemLight(playerId);
        }

        internal void DestroyRemoteFlareLight(int playerId)
        {
            PlayerHeldLightHandlers.DestroyRemoteFlareLight(playerId);
        }

        internal void PackContinuousLights(ref PlayerStateMessage msg, Player local)
        {
            PlayerHeldLightHandlers.PackContinuousLights(ref msg, local);
        }

        internal void ResetLocalLightSendCache()
        {
            PlayerHeldLightHandlers.ResetLocalLightSendCache();
        }

        internal static bool TryGetLocalHeldFlareLight(Player local, out Light2D light, out Flare flare)
        {
            return PlayerHeldLightNetHandlers.TryGetLocalHeldFlareLight(local, out light, out flare);
        }

        internal static bool TryGetLocalHeldMatchLight(Player local, out Light2D light)
        {
            return PlayerHeldLightNetHandlers.TryGetLocalHeldMatchLight(local, out light);
        }

        public static bool IsMatchLightItem(Player local)
        {
            return PlayerHeldLightNetHandlers.IsMatchLightItem(local);
        }

        internal void RemoveRemoteDragIds(string objectName)
        {
            PlayerInteractHandlers.RemoveRemoteDragIds(objectName);
        }

        internal void ReleaseRemoteDragKinematic(string objectName)
        {
            PlayerInteractHandlers.ReleaseRemoteDragKinematic(objectName);
        }

        public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>> EnumerateRemoteTrapOccupancy()
        {
            return PlayerPresenceHandlers.EnumerateRemoteTrapOccupancy();
        }
    }
}
