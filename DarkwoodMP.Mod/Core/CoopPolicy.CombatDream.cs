namespace DWMPHorde
{

    /// <summary>
    /// Ordering rules for unreliable state snapshots. Sequence numbers are scoped
    /// to one sender and one session; unsigned serial arithmetic keeps wraparound
    /// deterministic without requiring synchronized clocks.
    /// </summary>
    public static class SnapshotSequencePolicy
    {
        public static bool IsNewer(uint candidate, uint last, bool hasLast)
        {
            if (!hasLast) return true;
            if (candidate == last) return false;
            return unchecked(candidate - last) < 0x80000000u;
        }
    }

    /// <summary>Pure validation shared by host combat handlers and tests.</summary>
    public static class CombatAuthorityPolicy
    {
        public static bool IsValidPlayerId(int playerId) => playerId > 0;

        public static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool IsFinitePosition(float x, float y, float z)
            => IsFinite(x) && IsFinite(y) && IsFinite(z);

        public static bool IsValidMeleeTargetType(byte targetType)
            => targetType <= 2;

        public static bool IsWithinRange(
            float fromX, float fromY, float fromZ,
            float toX, float toY, float toZ,
            float maxRange)
        {
            if (!IsFinitePosition(fromX, fromY, fromZ)
                || !IsFinitePosition(toX, toY, toZ)
                || !IsFinite(maxRange) || maxRange < 0f)
                return false;

            float dx = toX - fromX;
            float dy = toY - fromY;
            float dz = toZ - fromZ;
            return dx * dx + dy * dy + dz * dz <= maxRange * maxRange;
        }

        /// <summary>
        /// Per-peer token bucket for client-reported hits: refill both buckets for
        /// <paramref name="elapsed"/> seconds, then take one attack and
        /// <paramref name="damage"/> damage. Nothing is taken when either bucket is short.
        /// </summary>
        public static bool TryConsumeAttackBudget(
            ref float attackTokens, ref float damageTokens, float elapsed, int damage,
            float attackRate, float attackBurst, float damageRate, float damageBurst)
        {
            if (elapsed > 0f && IsFinite(elapsed))
            {
                attackTokens += elapsed * attackRate;
                if (attackTokens > attackBurst) attackTokens = attackBurst;
                damageTokens += elapsed * damageRate;
                if (damageTokens > damageBurst) damageTokens = damageBurst;
            }
            if (damage < 0) damage = 0;
            if (attackTokens < 1f || damageTokens < damage)
                return false;
            attackTokens -= 1f;
            damageTokens -= damage;
            return true;
        }
    }

    /// <summary>
    /// BulletImpact spawns a prefab by name on every peer: only the impact / blood FX the
    /// mod's own senders emit (BulletFXSyncPatch, HitscanImpactSyncPatch, proxy / FF blood).
    /// </summary>
    public static class ImpactFxPolicy
    {
        private const string BloodPrefix = "FX/Bloodsplats/";

        public static bool IsAllowedBulletImpact(string pool, string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
                return false;
            if (!string.IsNullOrEmpty(pool))
            {
                // Core.AddPooledPrefab("FX", ...) wall / projectile hit and projectile blood.
                return pool == "FX" && (prefab == "bullet_hit_1" || prefab == "Shotsplat1");
            }
            // Core.AddPrefab blood splats: one name directly under FX/Bloodsplats/.
            if (!prefab.StartsWith(BloodPrefix, System.StringComparison.OrdinalIgnoreCase))
                return false;
            string leaf = prefab.Substring(BloodPrefix.Length);
            return leaf.Length > 0 && leaf.IndexOf('/') < 0 && leaf.IndexOf('\\') < 0
                && leaf.IndexOf("..", System.StringComparison.Ordinal) < 0;
        }
    }

    /// <summary>Component-level AI suppression must not depend on Character lookup.</summary>
    public static class AiSuppressionPolicy
    {
        public static bool ShouldSuppressClientComponent(
            bool isClient, bool isRemotePlayer, bool isLocalPlayer)
            => isClient && !isRemotePlayer && !isLocalPlayer;
    }
}
