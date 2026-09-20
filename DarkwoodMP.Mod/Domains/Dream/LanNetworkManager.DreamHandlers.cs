namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin dream-message façade: delegates to <see cref="DreamNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal DreamNetHandlers DreamHandlers { get; private set; }

        private void HandleDreamStarted(DreamStartedMessage msg)
        {
            DreamHandlers.HandleDreamStarted(msg);
        }

        internal void HandleDreamEnded(DreamEndedMessage msg)
        {
            DreamHandlers.HandleDreamEnded(msg);
        }

        private void HandleDreamStartRequest(DreamStartRequestMessage msg)
        {
            DreamHandlers.HandleDreamStartRequest(msg);
        }

        private void HandleDreamSessionBulk(DreamSessionBulkMessage msg)
        {
            DreamHandlers.HandleDreamSessionBulk(msg);
        }

        private void HandleDreamChainStart(DreamChainStartMessage msg)
        {
            DreamHandlers.HandleDreamChainStart(msg);
        }

        private void HandleDreamItemPickup(DreamItemPickupMessage msg)
        {
            DreamHandlers.HandleDreamItemPickup(msg);
        }

        private void HandleDreamAudio(DreamAudioMessage msg)
        {
            DreamHandlers.HandleDreamAudio(msg);
        }

        private void HandleDreamEntered(DreamEnteredMessage msg)
        {
            DreamHandlers.HandleDreamEntered(msg);
        }

        private void HandleDreamPropCollider(DreamPropColliderMessage msg)
        {
            DreamHandlers.HandleDreamPropCollider(msg);
        }

        /// <summary>Host: late-join dream completed + level flags.</summary>
        internal void SendDreamSessionBulkTo(int playerId)
        {
            DreamHandlers.SendDreamSessionBulkTo(playerId);
        }
    }
}
