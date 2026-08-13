namespace DWMPHorde
{
    /// <summary>
    /// Pure multiplayer policy helpers (no Unity). PathB tests exercise these
    /// without game assemblies; runtime patches call the same decisions.
    /// </summary>
    public static class CoopTimePolicy
    {
        /// <summary>
        /// Client must not advance Controller.CurrentTime / day-chain edges;
        /// host TimeSync is the sole clock authority.
        /// </summary>
        public static bool ShouldSuppressClientClock(bool isConnected, bool isClient)
            => isConnected && isClient;

        /// <summary>
        /// When applying host TimeSync, only update clock UI / ambient — do not
        /// call refreshTime() which fires startDay / startAfterNight / night setMe.
        /// </summary>
        public static bool ShouldUseRefreshTimeNoLogicOnClientSync => true;
    }

    /// <summary>
    /// Dialog outcome buckets. Physical bag stays speaker-personal (C2).
    /// Journal identity and session mutations are host-authoritative.
    /// </summary>
    public static class DialogApplyPolicy
    {
        public const string TypeGiveItem = "giveItem";
        public const string TypeRemoveItem = "removeItem";
        public const string TypeGiveJournalItem = "giveJournalItem";
        public const string TypeAddJournalEntry = "addJournalEntry";

        public const string TypeWorldFlag = "worldFlag";
        public const string TypeFireWorldEvent = "fireWorldEvent";
        public const string TypeStartDream = "startDream";
        public const string TypeEndDream = "endDream";
        public const string TypeTransportOutside = "transportToOutsideLoc";
        public const string TypeReturnToWorld = "returnToWorld";
        public const string TypeModifyReputation = "modifyReputation";
        public const string TypeMarkOnMap = "markOnMap";
        public const string TypeEnableDialogue = "enableDialogue";
        public const string TypeAddSpecialOption = "addSpecialDialogueOption";
        public const string TypeSetDontWantToTalk = "setDontWantToTalk";
        public const string TypeChangePortrait = "changePortrait";
        public const string TypeChangePortraitOverlay = "changePortraitWithOverlayAnim";
        public const string TypeCook = "cook";
        public const string TypeExitDialogue = "exitDialogue";
        public const string TypeExitDialogueLong = "exitDialogueLong";
        public const string TypeDisplayMainOptions = "displayMainOptions";
        public const string TypeDontSaveAfterExit = "dontSaveAfterExit";
        public const string TypeDontTweenBlack = "dontTweenBlackScreenWhenExiting";
        public const string TypeSwitchToDialogue = "switchToDialogue";

        /// <summary>Physical bag give/remove — speaker only; host suppresses on remote apply.</summary>
        public static bool IsPersonalRewardType(string outcomeType)
        {
            if (string.IsNullOrEmpty(outcomeType)) return false;
            switch (outcomeType)
            {
                case TypeGiveItem:
                case TypeRemoveItem:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Shared journal identity — host apply + JournalItem fan-out.</summary>
        public static bool IsWorldJournalOutcomeType(string outcomeType)
        {
            if (string.IsNullOrEmpty(outcomeType)) return false;
            switch (outcomeType)
            {
                case TypeGiveJournalItem:
                case TypeAddJournalEntry:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// World / session outcomes: client defers during displayNextBoard so host
        /// DialogOutcome apply is sole author.
        /// </summary>
        public static bool IsWorldAuthOutcomeType(string outcomeType)
        {
            if (string.IsNullOrEmpty(outcomeType)) return false;
            switch (outcomeType)
            {
                case TypeWorldFlag:
                case TypeFireWorldEvent:
                case TypeStartDream:
                case TypeEndDream:
                case TypeTransportOutside:
                case TypeReturnToWorld:
                case TypeModifyReputation:
                case TypeMarkOnMap:
                case TypeEnableDialogue:
                case TypeAddSpecialOption:
                case TypeSetDontWantToTalk:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Speaker UI / cook / close — do not open cook menu on host remote apply.</summary>
        public static bool IsSpeakerPresentationOutcomeType(string outcomeType)
        {
            if (string.IsNullOrEmpty(outcomeType)) return false;
            switch (outcomeType)
            {
                case TypeCook:
                case TypeExitDialogue:
                case TypeExitDialogueLong:
                case TypeDisplayMainOptions:
                case TypeDontSaveAfterExit:
                case TypeDontTweenBlack:
                case TypeSwitchToDialogue:
                case TypeChangePortrait:
                case TypeChangePortraitOverlay:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Model C: morning traders keep per-player standing.</summary>
        public static bool IsPerPlayerReputationNpcName(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            if (npcName == "NightTrader" || npcName == "TheThree")
                return true;
            if (npcName.StartsWith("NightTrader") || npcName.StartsWith("TheThree"))
                return true;
            return false;
        }

        /// <summary>Shared NPC reputation defers on client; night traders apply locally.</summary>
        public static bool ShouldDeferSharedReputation(bool isNightTrader)
            => !isNightTrader;

        public static bool ShouldSuppressPersonalInventoryMutation(bool hostApplyingRemoteOutcome)
            => hostApplyingRemoteOutcome;

        /// <summary>
        /// Client co-op board apply: skip world mutations; host will re-run via DialogOutcomeSync.
        /// </summary>
        public static bool ShouldDeferWorldOnClient(bool isConnected, bool isClient, bool applyingRemote)
            => isConnected && isClient && !applyingRemote;

        /// <summary>Host remote apply must not open cook / leveling UI.</summary>
        public static bool ShouldSuppressCookOnHostRemoteApply(bool hostApplyingRemoteOutcome)
            => hostApplyingRemoteOutcome;
    }

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
        /// Multi-NPC map simulation: after P1 holds wolfman and P2 holds doctor,
        /// P2 must still be denied wolfman (does not overwrite).
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
    }

    /// <summary>
    /// Co-op loot share: the disarm double must only fire for the exact item being
    /// disarmed. A global "arm in progress" bool wrongly doubles any pickup that
    /// arrives while a disarm is in flight — so the decision is type-scoped.
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

        public static bool IsShareFailureTerminal => true;

        public static string FormatShareFailure(string reason)
            => ShareFailurePrefix + " " + (reason ?? "unknown")
               + " — do not continue (different forests). Host: save once, F2 Resend, or rejoin.";

        public static bool IsShareFailureMessage(string progressText)
            => !string.IsNullOrEmpty(progressText)
               && progressText.StartsWith(ShareFailurePrefix, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Host crash → elect new host among survivors (lowest player id).
    /// Pure policy — no Unity. n+ peers: deterministic so each survivor elects the same id offline.
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
        /// Join offline-load / title: do not steal host grant — session is not mid-coop play.
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
}
