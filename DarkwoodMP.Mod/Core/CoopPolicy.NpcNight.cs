namespace DWMPHorde
{

    /// <summary>
    /// One active speaker per NPC slot. Different NPCs may be held in parallel
    /// (Dictionary of slots); same NPC is serialized.
    /// </summary>
    public static class NpcDialogueLockPolicy
    {
        public const float DefaultLeaseSeconds = 90f;

        /// <summary>
        /// Per-NPC slot: free if unheld (owner &lt; 0), expired, or same owner renewing.
        /// </summary>
        public static bool CanAcquireNpcSlot(
            int heldOwnerId,
            float heldExpireAt,
            int requestOwnerId,
            float now)
        {
            if (requestOwnerId < 0) return false;
            if (heldOwnerId < 0) return true;
            if (now >= heldExpireAt) return true;
            return heldOwnerId == requestOwnerId;
        }

        public static bool IsNpcSlotHeldBy(
            int heldOwnerId,
            float heldExpireAt,
            int ownerId,
            float now)
        {
            if (heldOwnerId < 0 || ownerId < 0) return false;
            if (now >= heldExpireAt) return false;
            return heldOwnerId == ownerId;
        }

        /// <summary>
        /// Legacy single-slot helper (tests / docs). Different NPCs do not block each other;
        /// same NPC uses <see cref="CanAcquireNpcSlot"/>.
        /// </summary>
        public static bool CanAcquire(
            string lockedNpc,
            int lockedOwnerId,
            float lockExpireAt,
            string requestNpc,
            int requestOwnerId,
            float now)
        {
            if (string.IsNullOrEmpty(requestNpc)) return false;
            // No hold, or different NPC (parallel talks OK).
            if (string.IsNullOrEmpty(lockedNpc)
                || !string.Equals(lockedNpc, requestNpc, System.StringComparison.Ordinal))
                return true;
            return CanAcquireNpcSlot(lockedOwnerId, lockExpireAt, requestOwnerId, now);
        }

        public static bool IsHeldBy(
            string lockedNpc,
            int lockedOwnerId,
            float lockExpireAt,
            string npcName,
            int ownerId,
            float now)
        {
            if (string.IsNullOrEmpty(lockedNpc) || string.IsNullOrEmpty(npcName)) return false;
            if (!string.Equals(lockedNpc, npcName, System.StringComparison.Ordinal)) return false;
            return IsNpcSlotHeldBy(lockedOwnerId, lockExpireAt, ownerId, now);
        }

        /// <summary>
        /// Multi-NPC map simulation: holding one NPC must not overwrite another
        /// NPC's lock.
        /// </summary>
        public static bool SimulateMultiNpcAcquire(
            System.Collections.Generic.Dictionary<string, int> owners,
            System.Collections.Generic.Dictionary<string, float> expires,
            string requestNpc,
            int requestOwnerId,
            float now)
        {
            if (owners == null || expires == null || string.IsNullOrEmpty(requestNpc))
                return false;

            int heldOwner = -1;
            float heldExpire = 0f;
            if (owners.TryGetValue(requestNpc, out int o)
                && expires.TryGetValue(requestNpc, out float e))
            {
                heldOwner = o;
                heldExpire = e;
            }

            if (!CanAcquireNpcSlot(heldOwner, heldExpire, requestOwnerId, now))
                return false;

            owners[requestNpc] = requestOwnerId;
            expires[requestNpc] = now + DefaultLeaseSeconds;
            return true;
        }
    }

    /// <summary>
    /// Partial night death: suppress SP world mutations that soft-desync survivors.
    /// </summary>
    public static class NightDeathPolicy
    {
        public static bool ShouldSuppressWorldDeathMutations(
            bool mpConnected,
            bool localNightDeath,
            bool allDeadAtNight)
            => mpConnected && localNightDeath && !allDeadAtNight;

        /// <summary>
        /// After a remote disconnect during night death: only advance morning when the
        /// host is night-dead and every relevant player is accounted for as dead.
        /// An alive leaver with no remotes left must not trigger skipDay.
        /// </summary>
        public static bool ShouldResolveMorningOnDisconnect(
            bool localNightDead,
            bool leaverWasNightDead,
            int remainingRemoteCount,
            int remainingRemoteDeadCount)
        {
            if (!localNightDead) return false;
            if (remainingRemoteCount <= 0)
                return leaverWasNightDead;
            return remainingRemoteDeadCount >= remainingRemoteCount;
        }

        /// <summary>
        /// Night AllRemoteDead must not under-count a handshaked peer whose
        /// proxy has not spawned yet (would skipDay while they are still alive).
        /// </summary>
        public static int SessionRemoteCount(int proxyCount, int handshakedRemoteCount)
        {
            if (proxyCount < 0) proxyCount = 0;
            if (handshakedRemoteCount < 0) handshakedRemoteCount = 0;
            return proxyCount > handshakedRemoteCount ? proxyCount : handshakedRemoteCount;
        }
    }
}
