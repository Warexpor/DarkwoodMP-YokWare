namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        internal LocationNetHandlers LocationHandlers { get; private set; }
        internal LocationEnterExitNetHandlers LocationEnterExitHandlers { get; private set; }
        internal LocationEntityTrapNetHandlers LocationEntityTrapHandlers { get; private set; }

        private void HandleLocationEnter(LocationEnterMessage msg)
        {
            LocationHandlers.HandleLocationEnter(msg);
        }

        private void HandleLocationExit(LocationExitMessage msg)
        {
            LocationHandlers.HandleLocationExit(msg);
        }

        private void HandleEntitySpawn(EntitySpawnMessage msg)
        {
            LocationHandlers.HandleEntitySpawn(msg);
        }

        private void HandleTrapTriggered(TrapTriggeredMessage msg)
        {
            LocationHandlers.HandleTrapTriggered(msg);
        }

        public void OnLocalOutsideLocationSettled(string locationName)
        {
            LocationHandlers.OnLocalOutsideLocationSettled(locationName);
        }

        public void OnLocalReturnedToWorld()
        {
            LocationHandlers.OnLocalReturnedToWorld();
        }

        public void OnLocalReturnedToWorldAfterDeath()
        {
            LocationHandlers.OnLocalReturnedToWorldAfterDeath();
        }

        public bool IsAnyRemoteInOutsideLocation(string locationName)
        {
            return LocationHandlers.IsAnyRemoteInOutsideLocation(locationName);
        }

        private void SyncExistingLocationsTo(int targetPlayerId)
        {
            LocationHandlers.SyncExistingLocationsTo(targetPlayerId);
        }

        internal static Location ResolveOutsideLocation(OutsideLocations ol, string locName)
        {
            return LocationNetHandlers.ResolveOutsideLocation(ol, locName);
        }

        internal void PlaceRemoteProxyInOutsideLocation(int playerId, Location loc, bool preferLastKnown)
        {
            LocationHandlers.PlaceRemoteProxyInOutsideLocation(playerId, loc, preferLastKnown);
        }
    }
}
