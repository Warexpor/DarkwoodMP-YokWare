namespace DWMPHorde
{
    /// <summary>
    /// The village empties at night. Villagers are away from the "night is coming" warning
    /// (two hours before night) until morning. The change happens at once while nobody is in
    /// the village, and with players inside only while none of them sees a villager.
    /// </summary>
    public static class VillageNightPolicy
    {
        public const int NightComingLeadMinutes = 130;

        public static bool IsNearNight(int time, int nightTime, int dayTime)
            => time >= nightTime - NightComingLeadMinutes || time < dayTime;

        public static bool ShouldFlip(bool currentlyAway, bool wantAway, bool occupied, bool anyoneSees)
            => currentlyAway != wantAway && (!occupied || !anyoneSees);
    }

    /// <summary>
    /// One active speaker per NPC slot. Different NPCs may be held in parallel
    /// (one slot per NPC: name, world and spot); same NPC is serialized.
    /// </summary>
    public static class NpcDialogueLockPolicy
    {
        public const float DefaultLeaseSeconds = 90f;

        /// <summary>
        /// NPC.name is not unique: the vanilla data has 10 NPCs named oven, 24 doctor, 8 musician,
        /// 7 wolfman, 4 shrine_village, 4 talkingTree. Two of one name in different places are
        /// thousands of units apart (each hideout's oven, each location's doctor); the closest
        /// distinct ones in one location are the train-wreck doctor variants (~150 apart, never
        /// out together), and twins at one spot (the shrine alive / dead) are one talker. Within
        /// this radius on the ground plane it is the same NPC; it also covers one that walked
        /// (the wolfman, a following doctor) between two peers' views.
        /// </summary>
        public const float SameNpcRadius = 300f;

        /// <summary>
        /// Same talker: same name (case-insensitive, as NPC lookups match) and, when both sides
        /// know where it stands, within <see cref="SameNpcRadius"/>. Without a position (an older
        /// peer) the name alone decides.
        /// </summary>
        public static bool IsSameNpc(string nameA, bool hasPosA, float ax, float az,
            string nameB, bool hasPosB, float bx, float bz)
        {
            if (string.IsNullOrEmpty(nameA) || string.IsNullOrEmpty(nameB))
                return false;
            if (!string.Equals(nameA, nameB, System.StringComparison.OrdinalIgnoreCase))
                return false;
            if (!hasPosA || !hasPosB)
                return true;
            float dx = ax - bx;
            float dz = az - bz;
            return dx * dx + dz * dz <= SameNpcRadius * SameNpcRadius;
        }

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
        /// After a remote disconnect during night death: advance morning when the host is
        /// night-dead and every remaining player is accounted for as dead. With no remotes
        /// left the host is a lone dead player, so it resolves like vanilla solo death
        /// whether or not the leaver was alive; otherwise it would spectate nobody forever.
        /// </summary>
        public static bool ShouldResolveMorningOnDisconnect(
            bool localNightDead,
            bool leaverWasNightDead,
            int remainingRemoteCount,
            int remainingRemoteDeadCount)
        {
            if (!localNightDead) return false;
            if (remainingRemoteCount <= 0)
                return true;
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
