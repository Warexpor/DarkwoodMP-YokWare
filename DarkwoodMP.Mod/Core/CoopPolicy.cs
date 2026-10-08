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
    /// Vanilla's pause menu (Esc) stops the game. In co-op one player's menu must not stop the
    /// others' world: the menu pauses nothing until every player in the session has it open, and
    /// then the whole world pauses. A peer counts as in the menu only on its own report, so a peer
    /// still connecting or loading keeps the world running.
    /// </summary>
    public static class CoopPausePolicy
    {
        public static bool WorldPaused(bool hostInMenu, int peers, int peersInMenu)
            => hostInMenu && peersInMenu >= peers;
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

        /// <summary>
        /// A trip (the Wolf's lift to the Doctor's house and back) carries the speaker, as a door into
        /// a location does: the speaking client runs it, the host replaying the board does not.
        /// </summary>
        public static bool IsSpeakerTripOutcomeType(string outcomeType)
            => outcomeType == TypeTransportOutside || outcomeType == TypeReturnToWorld;

        /// <summary>
        /// <c>NPC.name</c> of every vanilla NPC whose Character has <c>isNightTrader</c> set
        /// (scene + prefab data): the night trader, the Three, and the underground soldier
        /// (outside_bunker_underground_part2_01). Used when the NPC is not loaded, e.g. after
        /// the trader is removed at day end.
        /// </summary>
        private static readonly string[] NightTraderNpcNames = { "nightTrader", "theThree", "soldier_underground" };

        /// <summary>
        /// <c>NPC.name</c> of the other traders whose standing is only what a player has to spend
        /// with them (trades and quest rewards): the Wolf (every "wolfman": the camps, the hideout
        /// mornings, the Doctor's house) and Piotrek. The Doctor trades too but is left out: his
        /// chapter 2 story keeps its state in his reputation (setDoctorState_A/B/C_act2).
        /// </summary>
        private static readonly string[] OwnStandingTraderNpcNames = { "wolfman", "piotrek" };

        /// <summary>Traders whose standing is each player's own (night and morning traders, the Wolf, Piotrek).</summary>
        public static bool IsPerPlayerReputationNpcName(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            for (int i = 0; i < NightTraderNpcNames.Length; i++)
            {
                if (string.Equals(npcName, NightTraderNpcNames[i], System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            for (int i = 0; i < OwnStandingTraderNpcNames.Length; i++)
            {
                if (string.Equals(npcName, OwnStandingTraderNpcNames[i], System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
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

    /// <summary>
    /// Vanilla Player functions a scripted step calls on the player body (runFunction with
    /// targetUniqueObjects "player") that act on the world rather than that body. Every other
    /// Player function such a step calls moves, animates, dresses or equips the body (diveIn,
    /// diveOut, fakeDeathAni, lieDown, pause/resumeAnimation, special_petDog, ...).
    /// </summary>
    public static class CoopStoryPolicy
    {
        public static bool IsWorldPlayerFunction(string fn)
        {
            switch (fn)
            {
                // Night shadow event: CharacterSpawner flags, shadow spawns and the generator
                // lights of the location (host-run wave, see the night shadow patches).
                case "tryToSpawnShadow":
                case "pauseShadows":
                case "unpauseShadows":
                case "removeShadows":
                case "shadowLightsTurnOff":
                // Events.refreshWorldEvents.
                case "special_refreshWorldEvents":
                // Story placements measured from the player: redirected to the body of the
                // player the scene belongs to (actor story function patches).
                case "special_hideDoctorsAct2":
                case "special_teleportMaciek":
                case "special_spawnDreamForestSpirit":
                case "wolf_stealSister":
                case "spawnWolfInCurrentHideout":
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// World flags (WorldFlagsDatabase) that describe one player, not the shared world. They stay
    /// local on every peer: no live FlagSync, no join bulk, no host replay of a peer's dialogue or
    /// GameEvent writing them on the host, no desync compare.
    /// </summary>
    public static class PerPlayerFlagPolicy
    {
        /// <summary>
        /// The player's own experience, from the vanilla setters and readers: the one-time help
        /// popups (map open, first active skill, secondary attack, first drained reloadable), the
        /// first talk to its own home oven, and its night (<c>player_survivedNight</c> set at its
        /// dawn, <c>player_diedDuringNight</c> on its night death; the trader greets by them and
        /// clears both on close). The other <c>player_*</c> flags are world story: the night gift
        /// chest, the radio night, the cellar dream, the hideout unlocks (porter), the epilogue,
        /// and <c>player_transportingFromCh1</c> (carries everyone into chapter 2).
        /// </summary>
        private static readonly string[] ExperienceFlags =
        {
            "player_shownMapPopup",
            "player_shownActiveSkillPopup",
            "player_firstActiveSkillObtained",
            "player_shownSecondaryAttackPopup",
            "player_firstDrainReloadableItem",
            "player_firstOvenInteraction",
            "player_survivedNight",
            "player_diedDuringNight",
            "player_diedAtLeastOneTime"
        };

        /// <summary>Set by vanilla <c>Flags.setCh2flags</c> for every player entering chapter 2.</summary>
        private static readonly string[] Chapter2TrueFlags =
        {
            "player_shownMapPopup",
            "player_shownActiveSkillPopup"
        };

        /// <summary>Per-player flags kept in the client's own character snapshot.</summary>
        public static string[] PersistedFlags => (string[])ExperienceFlags.Clone();

        /// <summary>Per-player flags vanilla turns on when the chapter-2 world is entered.</summary>
        public static string[] Chapter2Flags => (string[])Chapter2TrueFlags.Clone();

        /// <summary>
        /// Where this one player is or is arriving: hideout bookkeeping (<c>player_in*</c>), "at the
        /// doctor's house" (hides talk options there), "entering the road from the radio tower"
        /// (picks the entry spawn). Recomputed from position, so never persisted.
        /// </summary>
        public static bool IsSpatial(string flagName)
        {
            if (string.IsNullOrEmpty(flagName))
                return false;
            return flagName.StartsWith("player_in", System.StringComparison.OrdinalIgnoreCase)
                || flagName.StartsWith("player_at", System.StringComparison.OrdinalIgnoreCase)
                || flagName.StartsWith("player_entering", System.StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsExperience(string flagName)
        {
            if (string.IsNullOrEmpty(flagName))
                return false;
            for (int i = 0; i < ExperienceFlags.Length; i++)
            {
                if (string.Equals(flagName, ExperienceFlags[i], System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool IsPerPlayer(string flagName) => IsSpatial(flagName) || IsExperience(flagName);

        /// <summary>
        /// Vanilla sets <c>player_survivedNight</c> in <c>startBeforeDay</c>, which a player who died
        /// that night never reaches (<c>skipDay</c> jumps past it). The shared clock reaches it for
        /// everyone; only a player alive at dawn counts as having survived.
        /// </summary>
        public static bool SurvivedNight(int nightDay, int localNightDeathDay)
            => localNightDeathDay != nightDay;
    }
}

