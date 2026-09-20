using DWMPHorde.Players;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin combat façade: delegates to <see cref="CombatNetHandlers"/>.
    /// Keeps Dispatch and patch call sites stable after the 0.8 composition.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal CombatNetHandlers CombatHandlers { get; private set; }
        internal CombatDeathBagNetHandlers CombatDeathBagHandlers { get; private set; }
        internal CombatAttackNetHandlers CombatAttackHandlers { get; private set; }
        internal CombatDeathStateNetHandlers CombatDeathStateHandlers { get; private set; }

        /// <summary>Player id of the peer whose message is currently being handled.</summary>
        internal int CurrentReceivePlayerId => _currentReceivePlayerId;

        internal void ResetCombatSessionState()
        {
            CombatHandlers.Reset();
        }

        internal void RegisterDeathBag(string bagId, DeathDrop drop)
        {
            CombatHandlers.RegisterDeathBag(bagId, drop);
        }

        internal void RegisterDeathBagLooted(string bagId)
        {
            CombatHandlers.RegisterDeathBagLooted(bagId);
        }

        internal bool IsDeathBagLooted(string bagId)
        {
            return CombatHandlers.IsDeathBagLooted(bagId);
        }

        internal void UnregisterDeathBag(string bagId)
        {
            CombatHandlers.UnregisterDeathBag(bagId);
        }

        internal DeathDrop FindDeathBagById(string bagId)
        {
            return CombatHandlers.FindDeathBagById(bagId);
        }

        private void HandlePlayerAttack(PlayerAttackMessage msg)
        {
            CombatHandlers.HandlePlayerAttack(msg);
        }

        private void HandleDamagePlayer(DamagePlayerMessage msg)
        {
            CombatHandlers.HandleDamagePlayer(msg);
        }

        internal void HandlePlayerDied(PlayerDiedMessage msg)
        {
            CombatHandlers.HandlePlayerDied(msg);
        }

        private void HandleDeathBagSpawn(DeathBagSpawnMessage msg)
        {
            CombatHandlers.HandleDeathBagSpawn(msg);
        }

        private void HandleDeathBagLooted(DeathBagLootedMessage msg)
        {
            CombatHandlers.HandleDeathBagLooted(msg);
        }

        private void HandleNightDeathState(NightDeathStateMessage msg)
        {
            CombatHandlers.HandleNightDeathState(msg);
        }

        internal void HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage msg)
        {
            CombatHandlers.HandleFinalDreamsceneDeath(msg);
        }

        private void HandleFriendlyFire(FriendlyFireMessage msg)
        {
            CombatHandlers.HandleFriendlyFire(msg);
        }

        internal int SanitizePeerDamage(int reported, string context)
        {
            return CombatHandlers.SanitizePeerDamage(reported, context);
        }
    }
}
