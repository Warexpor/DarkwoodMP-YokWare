namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: combat impact FX + gas/burn siblings. Awake wires them.
    /// </summary>
    internal sealed class CombatFxNetHandlers
    {
        private readonly CombatFxImpactNetHandlers _impact;
        private readonly CombatFxGasBurnNetHandlers _gasBurn;

        internal CombatFxNetHandlers(
            CombatFxImpactNetHandlers impact,
            CombatFxGasBurnNetHandlers gasBurn)
        {
            _impact = impact ?? throw new System.ArgumentNullException(nameof(impact));
            _gasBurn = gasBurn ?? throw new System.ArgumentNullException(nameof(gasBurn));
        }

        internal void ClearMeleeHitDebounce() => _impact.ClearMeleeHitDebounce();

        internal void TickMeleeHitDebounceCleanup() => _impact.TickMeleeHitDebounceCleanup();

        internal void HandleThrowableSpawn(ThrowableSpawnMessage msg) =>
            _impact.HandleThrowableSpawn(msg);

        internal void HandleExplosionTrigger(ExplosionTriggerMessage msg) =>
            _impact.HandleExplosionTrigger(msg);

        internal void HandleMeleeWorldHit(MeleeWorldHitMessage msg) =>
            _impact.HandleMeleeWorldHit(msg);

        internal static bool TryHitDestructibleItemAt(
            UnityEngine.Vector3 pos, float radius, int damage, UnityEngine.Transform attackerT) =>
            CombatFxImpactNetHandlers.TryHitDestructibleItemAt(pos, radius, damage, attackerT);

        internal void HandleExplosionSpawnObject(ExplosionSpawnObjectMessage msg) =>
            _impact.HandleExplosionSpawnObject(msg);

        internal void HandleGasTrailSpawn(GasTrailSpawnMessage msg) =>
            _gasBurn.HandleGasTrailSpawn(msg);

        internal void HandleGasIgnite(GasIgniteMessage msg) =>
            _gasBurn.HandleGasIgnite(msg);

        internal void HandleEntityBurning(EntityBurningMessage msg) =>
            _gasBurn.HandleEntityBurning(msg);

        internal void HandlePlayerBurning(PlayerBurningMessage msg) =>
            _gasBurn.HandlePlayerBurning(msg);

        internal void SendGasStateTo(int targetPlayerId) =>
            _gasBurn.SendGasStateTo(targetPlayerId);

        internal void HandleLiquidStopBurning(LiquidStopBurningMessage msg) =>
            _gasBurn.HandleLiquidStopBurning(msg);
    }
}
