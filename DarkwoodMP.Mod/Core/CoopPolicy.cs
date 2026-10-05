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
        /// When applying host TimeSync, only update clock UI and ambient. Do not
        /// call refreshTime() which fires startDay / startAfterNight / night setMe.
        /// </summary>
        public static bool ShouldUseRefreshTimeNoLogicOnClientSync => true;

        /// <summary>
        /// Vanilla stops the clock while the player is inside an outside location (village,
        /// bunker, basement). The shared clock runs while anyone is in the open world and
        /// stops only when nobody is. A peer counts only on its own report: one still loading
        /// or in the opening movie does not run the clock.
        /// </summary>
        public static bool SharedClockRuns(bool hostInOutsideLocation, bool anyRemoteInOpenWorld)
            => !hostInOutsideLocation || anyRemoteInOpenWorld;

        public const int MinutesPerDay = 1440;

        public static int WrapMinute(int time)
        {
            int m = time % MinutesPerDay;
            if (m < 0) m += MinutesPerDay;
            return m;
        }

        /// <summary>
        /// True when a short forward clock step crosses <paramref name="targetMinute"/>.
        /// Vanilla fires on exact equality; TimeSync can skip that minute.
        /// Steps longer than <paramref name="maxStep"/> are late-join or dream jumps.
        /// </summary>
        public static bool LiveStepCrossedMinute(int prevTime, int newTime, int targetMinute, int maxStep = 8)
        {
            prevTime = WrapMinute(prevTime);
            newTime = WrapMinute(newTime);
            targetMinute = WrapMinute(targetMinute);
            if (prevTime == newTime) return false;

            int step = newTime > prevTime
                ? newTime - prevTime
                : (MinutesPerDay - prevTime) + newTime;
            if (step <= 0 || step > maxStep) return false;

            if (newTime > prevTime)
                return targetMinute > prevTime && targetMinute <= newTime;
            return targetMinute > prevTime || targetMinute <= newTime;
        }
    }

    /// <summary>
    /// Party haveItem: same polarity as vanilla EventTriggerRequirement.
    /// activeModifier false means the party must NOT be holding the item.
    /// </summary>
    public static class PartyRequirementPolicy
    {
        public static bool HaveItem(bool partyHas, bool activeModifier)
            => partyHas ? activeModifier : !activeModifier;

        /// <summary>Vanilla health / darknessState: <c>value &gt;= area ? activeModifier : !activeModifier</c>.</summary>
        public static bool AtLeast(float value, float area, bool activeModifier)
            => value >= area ? activeModifier : !activeModifier;

        /// <summary>Vanilla enemiesAttacking: <c>count &lt; amount ? activeModifier : !activeModifier</c>.</summary>
        public static bool Below(int count, int amount, bool activeModifier)
            => count < amount ? activeModifier : !activeModifier;
    }

    /// <summary>
    /// Personal permadeath is a single-player profile end. A connected client
    /// dies on the shared night/day path instead of a local game-over the host
    /// never sees. Lives are the value before vanilla decrements them.
    /// </summary>
    public static class PermadeathPolicy
    {
        public const int Normal = 0;
        public const int Hard = 10;
        public const int Nightmare = 20;

        /// <summary>
        /// Connected host and clients share one death model: a death that vanilla
        /// would turn into permadeath is rewritten to the normal night/day death.
        /// </summary>
        public static bool UsesSharedDeath(bool connected, int difficulty, int livesBeforeDeath)
            => connected && IsPermadeathDeath(difficulty, livesBeforeDeath);

        /// <summary>Vanilla <c>Player.onDeath</c> permadeath branch (lives before the decrement).</summary>
        public static bool IsPermadeathDeath(int difficulty, int livesBeforeDeath)
        {
            if (difficulty == Nightmare) return true;
            if (difficulty == Hard && livesBeforeDeath <= 1) return true;
            return false;
        }

        /// <summary>
        /// A night party wipe ends the run for everyone only when every dead peer's
        /// death was permadeath-eligible. Anyone with a life left makes it a normal
        /// all-dead morning.
        /// </summary>
        public static bool PartyWipeEndsRun(bool localDeathEligible, int remoteDeaths, int remoteEligibleDeaths)
            => localDeathEligible && remoteEligibleDeaths >= remoteDeaths;
    }

    /// <summary>
    /// Connected clients must not write DynamicSave / Flags to disk except during
    /// host-coordinated SaveSync. Personal bag/skills go through ClientStateBackup;
    /// the host owns the shared world save.
    /// </summary>
    public static class ClientWorldSavePolicy
    {
        public static bool ShouldBlockConnectedClientWorldSave(
            bool connectedClient, bool hostCoordinatedSaveInProgress)
            => connectedClient && !hostCoordinatedSaveInProgress;
    }

    /// <summary>
    /// Dream end / cleanup classification. Failure paths must not MarkCompleted
    /// or fall through to the preset default reward.
    /// </summary>
    public static class DreamOutcomePolicy
    {
        public static bool IsRejectedOutcome(string outcomeName)
        {
            if (string.IsNullOrEmpty(outcomeName)) return false;
            return outcomeName.StartsWith("rejected", System.StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFailureCleanup(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            if (IsRejectedOutcome(reason)) return true;
            return reason == "storyEndTimeout"
                || reason == "disconnected"
                || reason == "hostLostMidDream"
                || reason == "prepareLocationFailed"
                || reason == "prepare_failed";
        }

        public static bool IsNonRewardOutcome(string outcomeName)
        {
            if (string.IsNullOrEmpty(outcomeName)) return true;
            if (IsFailureCleanup(outcomeName)) return true;
            return outcomeName == "playerDeath"
                || outcomeName == "allDead"
                || outcomeName.StartsWith("scene:", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Party-once latch only on real clears, not rejects / disconnect.</summary>
        public static bool ShouldMarkCompletedOnEnd(string outcomeName)
            => !IsFailureCleanup(outcomeName);
    }

    /// <summary>
    /// Dialog outcome buckets. Physical bag rewards stay speaker-personal.
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

        /// <summary>Physical bag give/remove is speaker-only; the host suppresses remote apply.</summary>
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

        /// <summary>Shared journal identity is applied by the host and fanned out through JournalItem.</summary>
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

        /// <summary>Morning traders keep per-player standing.</summary>
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
    /// GameEvent types that mutate vanilla <c>Player.Instance</c> (bag, recipes,
    /// teleport). World props / journal / flags stay shared. Actor id 0 = late-join
    /// bulk or unknown → skip personal on apply so peers are not party-granted.
    /// </summary>
    public static class GameEventPersonalPolicy
    {
        /// <summary>
        /// Live GameEventsFired: only the stamped actor re-runs personal Player.Instance
        /// effects. ActorPlayerId &lt;= 0 (bulk / unset) never grants personal on apply.
        /// </summary>
        public static bool ShouldRunPersonalEffectsOnApply(int actorPlayerId, int localPlayerId)
            => actorPlayerId > 0 && localPlayerId > 0 && actorPlayerId == localPlayerId;
    }
}

