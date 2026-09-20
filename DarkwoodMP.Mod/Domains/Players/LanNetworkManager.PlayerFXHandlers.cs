namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="PlayerFXNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal PlayerFXNetHandlers PlayerFXHandlers { get; private set; }

        private void HandlePlayerAnimation(PlayerAnimationMessage msg)
        {
            PlayerFXHandlers.HandlePlayerAnimation(msg);
        }

        private void HandlePlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            PlayerFXHandlers.HandlePlayerAnimLibrary(msg);
        }

        private void DispatchRemotePlayerForward(NetMessageType innerType, byte[] innerPayload)
        {
            PlayerFXHandlers.DispatchRemotePlayerForward(innerType, innerPayload);
        }

        private void HandleBulletImpact(BulletImpactMessage msg)
        {
            PlayerFXHandlers.HandleBulletImpact(msg);
        }

        private void HandlePlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            PlayerFXHandlers.HandlePlayerFiredWeapon(msg);
        }

        private void HandleDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            PlayerFXHandlers.HandleDroppedItemSpawn(msg);
        }

        private void HandleDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            PlayerFXHandlers.HandleDroppedItemPickup(msg);
        }

        public static void ResetConsumedDropGuids()
        {
            var net = Instance;
            if (net == null) return;
            net.PlayerFXHandlers.ResetConsumedDropGuids();
        }
    }
}
