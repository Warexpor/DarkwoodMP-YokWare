namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: death-bag / attack / death-state siblings. Awake wires them.
    /// </summary>
    internal sealed class CombatNetHandlers
    {
        private readonly CombatDeathBagNetHandlers _deathBags;
        private readonly CombatAttackNetHandlers _attack;
        private readonly CombatDeathStateNetHandlers _deathState;

        internal CombatNetHandlers(
            CombatDeathBagNetHandlers deathBags,
            CombatAttackNetHandlers attack,
            CombatDeathStateNetHandlers deathState)
        {
            _deathBags = deathBags ?? throw new System.ArgumentNullException(nameof(deathBags));
            _attack = attack ?? throw new System.ArgumentNullException(nameof(attack));
            _deathState = deathState ?? throw new System.ArgumentNullException(nameof(deathState));
        }

        internal void Reset()
        {
            _deathBags.Reset();
            _attack.Reset();
        }

        internal void RegisterDeathBag(string bagId, DeathDrop drop) =>
            _deathBags.RegisterDeathBag(bagId, drop);

        internal void RegisterDeathBagLooted(string bagId) =>
            _deathBags.RegisterDeathBagLooted(bagId);

        internal bool IsDeathBagLooted(string bagId) =>
            _deathBags.IsDeathBagLooted(bagId);

        internal void SyncExistingDeathBags(int targetPlayerId) =>
            _deathBags.SyncExistingDeathBags(targetPlayerId);

        internal void UnregisterDeathBag(string bagId) =>
            _deathBags.UnregisterDeathBag(bagId);

        internal void TryHostFanDeathBagEmptied(Inventory inv) =>
            _deathBags.TryHostFanDeathBagEmptied(inv);

        internal DeathDrop FindDeathBagById(string bagId) =>
            _deathBags.FindDeathBagById(bagId);

        internal int SanitizePeerDamage(int reported, string context) =>
            _attack.SanitizePeerDamage(reported, context);

        internal void HandlePlayerAttack(PlayerAttackMessage msg) =>
            _attack.HandlePlayerAttack(msg);

        internal void HandleDamagePlayer(DamagePlayerMessage msg) =>
            _attack.HandleDamagePlayer(msg);

        internal void HandleFriendlyFire(FriendlyFireMessage msg) =>
            _attack.HandleFriendlyFire(msg);

        internal void HandleDeathBagSpawn(DeathBagSpawnMessage msg) =>
            _deathBags.HandleDeathBagSpawn(msg);

        internal void HandleDeathBagLooted(DeathBagLootedMessage msg) =>
            _deathBags.HandleDeathBagLooted(msg);

        internal void HandlePlayerDied(PlayerDiedMessage msg) =>
            _deathState.HandlePlayerDied(msg);

        internal void HandleNightDeathState(NightDeathStateMessage msg) =>
            _deathState.HandleNightDeathState(msg);

        internal void HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage msg) =>
            _deathState.HandleFinalDreamsceneDeath(msg);
    }
}
