namespace DWMPHorde.Networking
{
    /// <summary>
    /// Domain handler ownership on <see cref="LanNetworkManager"/>.
    /// Properties live here; Dispatch and session code call into the handlers
    /// directly — no per-domain private Handle* façade wrappers.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal ExaminableNetHandlers ExaminableHandlers { get; private set; }
        internal ChapterNetHandlers ChapterHandlers { get; private set; }
        internal CutsceneNetHandlers CutsceneHandlers { get; private set; }
        internal DoorNetHandlers DoorHandlers { get; private set; }
        internal CursorActionNetHandlers CursorActionHandlers { get; private set; }
        internal MapNetHandlers MapHandlers { get; private set; }
        internal WorldBurnNetHandlers WorldBurnHandlers { get; private set; }
        internal ChainNetHandlers ChainHandlers { get; private set; }
        internal ShadowArmorNetHandlers ShadowArmorHandlers { get; private set; }
        internal EpilogueNetHandlers EpilogueHandlers { get; private set; }

        internal DialogOutcomeNetHandlers DialogOutcomeHandlers { get; private set; }
        internal DialogOutcomeApplyNetHandlers DialogOutcomeApplyHandlers { get; private set; }
        internal DialogOutcomeCloseNetHandlers DialogOutcomeCloseHandlers { get; private set; }
        internal DialogNpcLockNetHandlers DialogNpcLockHandlers { get; private set; }

        internal FlagNetHandlers FlagHandlers { get; private set; }
        internal GameEventNetHandlers GameEventHandlers { get; private set; }
        internal BarricadeNetHandlers BarricadeHandlers { get; private set; }
        internal TradeNetHandlers TradeHandlers { get; private set; }

        internal ContainerNetHandlers ContainerHandlers { get; private set; }
        internal ContainerLootNetHandlers ContainerLootHandlers { get; private set; }
        internal ContainerDeathDropNetHandlers ContainerDeathDropHandlers { get; private set; }
        internal ContainerPendingNetHandlers ContainerPendingHandlers { get; private set; }

        internal StationNetHandlers StationHandlers { get; private set; }
        internal LockNetHandlers LockHandlers { get; private set; }
        internal DreamNetHandlers DreamHandlers { get; private set; }
        internal NightNetHandlers NightHandlers { get; private set; }

        internal JournalNetHandlers JournalHandlers { get; private set; }
        internal LocationNetHandlers LocationHandlers { get; private set; }
        internal LocationEnterExitNetHandlers LocationEnterExitHandlers { get; private set; }
        internal LocationEntityTrapNetHandlers LocationEntityTrapHandlers { get; private set; }
        internal PlayerFXNetHandlers PlayerFXHandlers { get; private set; }

        // Batch 4 — Combat / FX / Players / World send+state
        internal CombatNetHandlers CombatHandlers { get; private set; }
        internal CombatDeathBagNetHandlers CombatDeathBagHandlers { get; private set; }
        internal CombatAttackNetHandlers CombatAttackHandlers { get; private set; }
        internal CombatDeathStateNetHandlers CombatDeathStateHandlers { get; private set; }

        internal WorldFxNetHandlers WorldFxHandlers { get; private set; }
        internal PlayerLightFxApplyNetHandlers PlayerLightFxApplyHandlers { get; private set; }
        internal PlayerLightFxAmbientNetHandlers PlayerLightFxAmbientHandlers { get; private set; }
        internal PlayerLightFxNetHandlers PlayerLightFxHandlers { get; private set; }
        internal CombatFxImpactNetHandlers CombatFxImpactHandlers { get; private set; }
        internal CombatFxGasBurnNetHandlers CombatFxGasBurnHandlers { get; private set; }
        internal CombatFxNetHandlers CombatFxHandlers { get; private set; }

        internal PlayerStateNetHandlers PlayerStateHandlers { get; private set; }
        internal PlayerHeldLightPackNetHandlers PlayerHeldLightPackHandlers { get; private set; }
        internal PlayerHeldLightApplyNetHandlers PlayerHeldLightApplyHandlers { get; private set; }
        internal PlayerHeldLightNetHandlers PlayerHeldLightHandlers { get; private set; }
        internal PlayerPresenceNetHandlers PlayerPresenceHandlers { get; private set; }
        internal PlayerInteractNetHandlers PlayerInteractHandlers { get; private set; }

        internal WorldObjectSendNetHandlers WorldObjectSendHandlers { get; private set; }
        internal WorldSendNetHandlers WorldSendHandlers { get; private set; }

        internal WorldPhysicsNetHandlers WorldPhysicsHandlers { get; private set; }
        internal WorldWeatherTimeNetHandlers WorldWeatherTimeHandlers { get; private set; }
        internal WorldLateJoinNetHandlers WorldLateJoinHandlers { get; private set; }
        internal WorldProxyLifecycleNetHandlers WorldProxyLifecycleHandlers { get; private set; }
        internal WorldProxyEffectNetHandlers WorldProxyEffectHandlers { get; private set; }
        internal WorldProxyNetHandlers WorldProxyHandlers { get; private set; }

        internal BulkSyncNetHandlers BulkSyncHandlers { get; private set; }
    }
}
