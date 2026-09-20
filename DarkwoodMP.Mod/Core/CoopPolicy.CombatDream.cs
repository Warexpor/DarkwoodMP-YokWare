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
    }

    /// <summary>
    /// Dream objects must be resolved under the active dream Location. A global
    /// name fallback is unsafe because overworld and dream copies share names.
    /// </summary>
    public static class DreamResolutionPolicy
    {
        public static bool CanUseGlobalNameFallback(bool dreamActive)
            => !dreamActive;
    }

    /// <summary>Component-level AI suppression must not depend on Character lookup.</summary>
    public static class AiSuppressionPolicy
    {
        public static bool ShouldSuppressClientComponent(
            bool isClient, bool isRemotePlayer, bool isLocalPlayer)
            => isClient && !isRemotePlayer && !isLocalPlayer;
    }
}
