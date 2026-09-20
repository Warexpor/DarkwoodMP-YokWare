namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        internal GameEventNetHandlers GameEventHandlers { get; private set; }

        private void HandleGameEventsFired(GameEventsFiredMessage msg)
        {
            GameEventHandlers.HandleGameEventsFired(msg);
        }

        private void HandleGameEventsBulk(GameEventsBulkMessage msg)
        {
            GameEventHandlers.HandleGameEventsBulk(msg);
        }

        internal void SendGameEventsBulkTo(int targetPlayerId)
        {
            GameEventHandlers.SendGameEventsBulkTo(targetPlayerId);
        }

        internal void ClearPendingDreamGameEvents()
        {
            GameEventHandlers.ClearPendingDreamGameEvents();
        }

        internal void TryFlushPendingGameEventsAfterDreamLoad()
        {
            GameEventHandlers.TryFlushPendingGameEventsAfterDreamLoad();
        }

        private void TryFlushPendingGameEvents()
        {
            GameEventHandlers.TryFlushPendingGameEvents();
        }
    }
}
