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

        /// <summary>Speaker UI, cooking, and closing. Do not open the cook menu during host remote apply.</summary>
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
}
