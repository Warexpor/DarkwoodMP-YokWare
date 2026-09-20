namespace DWMPHorde
{

    /// <summary>
    /// Co-op loot share: the disarm double must only fire for the exact item being
    /// disarmed. A global "arm in progress" bool wrongly doubles any pickup that
        /// arrives while a disarm is in flight, so the decision is type-scoped.
    /// </summary>
    public static class LootPolicy
    {
        /// <summary>
        /// True only when an item of <paramref name="incomingType"/> is being added
        /// to the player and it matches the type currently armed for disarm-doubling.
        /// </summary>
        public static bool ShouldDoubleDisarm(string armedType, string incomingType)
            => !string.IsNullOrEmpty(armedType)
               && string.Equals(armedType, incomingType, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Chapter load tears the scene; co-op must rebind rather than silent solo.
    /// Credits may still end the session (documented residual).
    /// </summary>
    public static class ChapterSessionPolicy
    {
        public static bool ShouldAutoResumeNetworkAfterChapter => true;

        /// <summary>Credits end co-op; chapter mid-campaign does not.</summary>
        public static bool ShouldStopNetworkPermanently(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            return string.Equals(sceneName, "credits", System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Path B world identity = host world-share files, not per-chunk dual gen.
    /// Fail-loud when share cannot complete; do not claim layout is fixed.
    /// </summary>
    public static class WorldSharePolicy
    {
        public const string ShareFailurePrefix = "WORLD SHARE FAILED:";
        public const string WrongSavePrefix = "WRONG SAVE:";

        public static bool IsShareFailureTerminal => true;

        public static string FormatShareFailure(string reason)
            => ShareFailurePrefix + " " + (reason ?? "unknown")
               + " — do not continue (different forests). Host: save once, F2 Resend, or rejoin.";

        public static bool IsShareFailureMessage(string progressText)
            => !string.IsNullOrEmpty(progressText)
               && progressText.StartsWith(ShareFailurePrefix, System.StringComparison.Ordinal);

        public static string FormatWrongSave(string reason)
            => WrongSavePrefix + " " + (reason ?? "campaign mismatch");

        public static bool IsWrongSaveMessage(string progressText)
            => !string.IsNullOrEmpty(progressText)
               && progressText.IndexOf(WrongSavePrefix, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Host crash → elect new host among survivors (lowest player id).
        /// Pure policy with no Unity dependencies. The result is deterministic
        /// so every survivor elects the same ID.
    /// </summary>
    public static class HostMigrationPolicy
    {
        /// <summary>
        /// Lowest positive survivor id wins. Empty / all invalid → -1.
        /// </summary>
        public static int ElectNewHost(System.Collections.Generic.IEnumerable<int> survivorPlayerIds)
        {
            if (survivorPlayerIds == null) return -1;
            int best = int.MaxValue;
            foreach (int id in survivorPlayerIds)
            {
                if (id <= 0) continue;
                if (id < best) best = id;
            }
            return best == int.MaxValue ? -1 : best;
        }

        public static bool IsLocalElected(int localPlayerId, int electedId)
            => localPlayerId > 0 && electedId > 0 && localPlayerId == electedId;

        /// <summary>
        /// Join offline-load or title: do not steal the host grant because the
        /// session is not in active co-op play.
        /// </summary>
        public static bool ShouldAttemptMigration(
            bool featureEnabled,
            bool isClient,
            bool mainMenu,
            bool hasPlayableWorld,
            bool migrationAlreadyRunning)
        {
            if (!featureEnabled) return false;
            if (!isClient) return false;
            if (migrationAlreadyRunning) return false;
            if (mainMenu) return false;
            if (!hasPlayableWorld) return false;
            return true;
        }
    }

    /// <summary>
    /// Split-map presence: host simulation must keep remote bubbles even when
    /// vanilla location transport force-leaves the grid the host just left.
    /// </summary>
    public static class CoopWorldPresencePolicy
    {
        /// <summary>
        /// Keep a WorldGrid node that a remote occupies. Vanilla
        /// <c>Grid.leave()</c> uses force=true; that must not wipe client forest
        /// while the host is in a bunker / doctor house / village.
        /// </summary>
        public static bool ShouldKeepNodeForRemote(bool hostWithRemotes, bool remoteNear)
            => hostWithRemotes && remoteNear;

        /// <summary>
        /// Keep a Location GO a remote is still inside. Vanilla
        /// <c>leaveAllLocations</c> force-leaves every other pad.
        /// </summary>
        public static bool ShouldKeepLocationForRemote(bool hostWithRemotes, bool remoteInside)
            => hostWithRemotes && remoteInside;

        /// <summary>
        /// Local return-to-world must not yank proxies still inside a pad
        /// (host exits bunker while client B remains).
        /// </summary>
        public static bool ShouldSnapRemoteProxyOnLocalWorldReturn(bool remoteStillInOutsideLocation)
            => !remoteStillInOutsideLocation;

        public static bool LocationNamesMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(StripDoneSuffix(a), StripDoneSuffix(b),
                System.StringComparison.OrdinalIgnoreCase);
        }

        public static string StripDoneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= 5)
                return name ?? "";
            if (name.EndsWith("_done", System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 5);
            return name;
        }
    }
}
