namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: location enter/exit + entity/trap siblings. Awake wires them.
    /// </summary>
    internal sealed class LocationNetHandlers
    {
        private readonly LocationEnterExitNetHandlers _enterExit;
        private readonly LocationEntityTrapNetHandlers _entityTrap;

        internal LocationNetHandlers(
            LocationEnterExitNetHandlers enterExit,
            LocationEntityTrapNetHandlers entityTrap)
        {
            _enterExit = enterExit ?? throw new System.ArgumentNullException(nameof(enterExit));
            _entityTrap = entityTrap ?? throw new System.ArgumentNullException(nameof(entityTrap));
        }

        internal void HandleLocationEnter(LocationEnterMessage msg) =>
            _enterExit.HandleLocationEnter(msg);

        internal void HandleLocationExit(LocationExitMessage msg) =>
            _enterExit.HandleLocationExit(msg);

        internal static Location ResolveOutsideLocation(OutsideLocations ol, string locName) =>
            LocationEnterExitNetHandlers.ResolveOutsideLocation(ol, locName);

        public void OnLocalOutsideLocationSettled(string locationName) =>
            _enterExit.OnLocalOutsideLocationSettled(locationName);

        public void OnLocalReturnedToWorld() =>
            _enterExit.OnLocalReturnedToWorld();

        public void OnLocalReturnedToWorldAfterDeath() =>
            _enterExit.OnLocalReturnedToWorldAfterDeath();

        internal void PlaceRemoteProxyInOutsideLocation(int playerId, Location loc, bool preferLastKnown) =>
            _enterExit.PlaceRemoteProxyInOutsideLocation(playerId, loc, preferLastKnown);

        public bool IsAnyRemoteInOutsideLocation(string locationName) =>
            _enterExit.IsAnyRemoteInOutsideLocation(locationName);

        internal void ClearMembershipForSoftReconnect() =>
            _enterExit.ClearMembershipForSoftReconnect();

        internal void ResetForNetworkStop() =>
            _enterExit.ResetForNetworkStop();

        internal void ForceAnnounceLocalOutsideLocationEnter(string reason) =>
            _enterExit.ForceAnnounceLocalOutsideLocationEnter(reason);

        internal void TryFlushPendingForceAnnounce() =>
            _enterExit.TryFlushPendingForceAnnounce();

        internal bool HasPendingForceAnnounce =>
            _enterExit.HasPendingForceAnnounce;

        internal void SyncExistingLocationsTo(int targetPlayerId) =>
            _enterExit.SyncExistingLocationsTo(targetPlayerId);

        internal void NotifyRemotePeerDisconnected(
            int playerId, string leftLoc, float posX = 0f, float posY = 0f, float posZ = 0f) =>
            _enterExit.NotifyRemotePeerDisconnected(playerId, leftLoc, posX, posY, posZ);

        internal void HandleEntitySpawn(EntitySpawnMessage msg) =>
            _entityTrap.HandleEntitySpawn(msg);

        internal void HandleTrapTriggered(TrapTriggeredMessage msg) =>
            _entityTrap.HandleTrapTriggered(msg);
    }
}
