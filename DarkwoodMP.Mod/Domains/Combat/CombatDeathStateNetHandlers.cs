using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Remote player death / night-morning / final-dreamscene death message handlers.
    /// </summary>
    internal sealed class CombatDeathStateNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CombatDeathStateNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandlePlayerDied(PlayerDiedMessage msg)
        {
            Vector3 deathPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool isNight = msg.IsNight;
            int playerId = _net.CurrentReceivePlayerId;
            if (playerId <= 0)
            {
                ModLog.WarnRate(LogCat.Death, "playerdied-badid", "[Death] PlayerDied with invalid playerId — ignored");
                return;
            }

            ModRuntime.LegacyInfo($"[Death] Remote player {playerId} died at {deathPos}, isNight={isNight}");

            RemotePlayerProxy diedProxy = _net.GetProxy(playerId);
            if (diedProxy != null)
            {
                CharBase cb = diedProxy.CachedCharBase;
                if (cb != null)
                {
                    cb.alive = false;
                    cb.Health = 0f;
                }
                var deathCols = diedProxy.CachedColliders;
                if (deathCols != null)
                {
                    for (int ci = 0; ci < deathCols.Length; ci++)
                    {
                        if (deathCols[ci] != null)
                            deathCols[ci].enabled = false;
                    }
                }

                // Apply the death pose immediately; do not wait for the next PlayerState tick.
                var anim = diedProxy.GetComponent<Players.SecondPlayerAnimController>();
                if (anim != null)
                    anim.PlayDeathClip("Death1");
            }

            if (_net.Role == NetworkRole.Host)
                Patches.MorningHideoutHold.TryEndIfHideoutEmpty();

            // The client decides IsNight on its own clock. After the host's morning edge a
            // lagging client would otherwise be recorded night-dead and spectate all day.
            // A one-life death is down until the next morning whatever the hour: not a stale clock.
            if (isNight && !msg.PermadeathEligible && _net.Role == NetworkRole.Host
                && DeathStateTracker.HostMorningAlreadyReleased())
                isNight = HostDowngradeStaleNightDeath(playerId, msg);

            if (isNight)
            {
                DeathStateTracker.OnRemoteNightDeath(playerId, deathPos, msg.PermadeathEligible);

                if (_net.Role == NetworkRole.Host)
                {
                    if (!DeathStateTracker.TryResolveNightMorning("remote PlayerDied"))
                    {
                        ModRuntime.LegacyInfo(
                            $"[Death] Remote night death — waiting " +
                            $"(localDead={DeathStateTracker.LocalNightDeath} " +
                            $"remotes={DeathStateTracker.RemoteNightDeathCount}/{DeathStateTracker.TotalRemoteCount})");
                    }
                }
                else
                {
                    // If we are spectating the player who just died, retarget.
                    var spec = Spectator.SpectatorModeController.Instance;
                    if (spec != null && spec.IsSpectating)
                        ModRuntime.LegacyInfo($"[Death] Client saw remote night death of P{playerId}");
                }
                return;
            }

            DeathStateTracker.OnRemoteDayDeath(playerId);

            if (_net.Role == NetworkRole.Host)
            {
                DeathStateTracker.HostClearHomeForRespawn(msg.HasHome
                    ? new Vector3(msg.HomeX, msg.HomeY, msg.HomeZ) : (Vector3?)null);
                RequestRemoteDeathSave();
            }
        }

        /// <summary>
        /// Host: a client reported a night death after this morning's release. Release that
        /// client now (it already entered night-death spectate locally) and relay the death to
        /// the other peers as a day death instead of the raw night flag.
        /// </summary>
        /// <returns>The corrected IsNight (always false).</returns>
        private bool HostDowngradeStaleNightDeath(int playerId, PlayerDiedMessage msg)
        {
            Controller ctrl = Singleton<Controller>.Instance;
            int day = ctrl != null ? ctrl.day : 0;
            ModLog.Event(LogCat.Death,
                $"p{playerId} night death arrived after the morning release (day {day}) — treated as day death");

            _net.SendToPlayer(playerId, NetMessageType.NightDeathRelease,
                w => new NightDeathReleaseMessage { Day = day }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);

            msg.IsNight = false;
            var inner = new NetWriter();
            msg.Serialize(inner);
            var fwd = new RemotePlayerForwardMessage
            {
                OriginalPlayerId = playerId,
                InnerType = (byte)NetMessageType.PlayerDied,
                InnerPayload = inner.CopyData()
            };
            _net.SuppressRelay();
            _net.SendToAllExcept(playerId, NetMessageType.RemotePlayerForward,
                w => fwd.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
            return false;
        }

        /// <summary>Minimum spacing of the host world saves a remote day death asks for.</summary>
        private const float RemoteDeathSaveCoalesceSec = 15f;
        private float _lastRemoteDeathSaveAt = -1000f;
        private bool _remoteDeathSavePending;

        /// <summary>
        /// A remote day death persists the world on the host, but a full SaveManager.Save per
        /// death hitches the host (and fans a SaveSync to every peer) when several players
        /// die close together. Deaths inside the window share one trailing save instead.
        /// </summary>
        private void RequestRemoteDeathSave()
        {
            if (_remoteDeathSavePending)
                return;

            float wait = RemoteDeathSaveCoalesceSec - (Time.unscaledTime - _lastRemoteDeathSaveAt);
            if (wait <= 0f)
            {
                SaveAfterRemoteDeath();
                return;
            }

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            _remoteDeathSavePending = true;
            ctrl.Invoke(delegate
            {
                _remoteDeathSavePending = false;
                SaveAfterRemoteDeath();
            }, wait, timeScaleDependent: false);
        }

        private void SaveAfterRemoteDeath()
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected)
                return;
            SaveManager save = Singleton<SaveManager>.Instance;
            if (save == null)
                return;
            _lastRemoteDeathSaveAt = Time.unscaledTime;
            save.Save(doJson: true);
        }

        internal void HandleNightDeathState(NightDeathStateMessage msg)
        {
            int hostId = _net.HostPlayerId > 0 ? _net.HostPlayerId : 1;
            if (msg.ResumeDead)
            {
                // Host: this player died this night before it left; it is down until the morning.
                if (_net.Role != NetworkRole.Client || _net.CurrentReceivePlayerId != hostId
                    || DeathStateTracker.LocalNightDeath)
                    return;
                ModRuntime.LegacyInfo("[Death] Rejoined after dying this night — spectating until morning");
                DeathStateTracker.OnLocalNightDeath(new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                Patches.NightDeathSkipDayPatch.EnterNightDeathSpectator();
                return;
            }
            if (!msg.AllDeadTrigger && !msg.PartyWipe)
                return;

            // Host is sole AllDeadTrigger emitter (DeathStateTracker.TryResolveNightMorning).
            if (_net.Role == NetworkRole.Host)
            {
                if (_net.CurrentReceivePlayerId > 0)
                    ModRuntime.LegacyInfo(
                        $"[Death] Rejected peer AllDeadTrigger from p{_net.CurrentReceivePlayerId}");
                return;
            }

            // Client: only accept morning resolve from host.
            if (_net.CurrentReceivePlayerId != hostId)
            {
                ModRuntime.LegacyInfo(
                    $"[Death] Rejected AllDeadTrigger from non-host p{_net.CurrentReceivePlayerId}");
                return;
            }

            if (msg.PartyWipe)
            {
                ModRuntime.LegacyInfo("[Death] Party wipe on permadeath difficulty — running permadeath outcome");
                Patches.PartyWipeOutcome.Begin("host PartyWipe");
                return;
            }

            ModRuntime.LegacyInfo("[Death] All dead at night — exiting spectator for morning");

            if (Spectator.SpectatorModeController.Instance != null)
                Spectator.SpectatorModeController.Instance.ExitAndRespawn();

            DeathStateTracker.Reset();
        }

        /// <summary>
        /// Host morning edge: leave night-death spectate, go home, drop night-death marks.
        /// Idempotent (a peer that is not night-dead only clears its remote bookkeeping),
        /// so a duplicate, a late join, or a reconnect around dawn is harmless.
        /// </summary>
        internal void HandleNightDeathRelease(NightDeathReleaseMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            int hostId = _net.HostPlayerId > 0 ? _net.HostPlayerId : 1;
            if (_net.CurrentReceivePlayerId != hostId)
            {
                ModRuntime.LegacyInfo(
                    $"[Death] Rejected NightDeathRelease from non-host p{_net.CurrentReceivePlayerId}");
                return;
            }

            ModRuntime.LegacyInfo($"[Death] Host morning release (day {msg.Day})");
            DeathStateTracker.ClientReleaseNightDeadAtMorning();
        }

        /// <summary>
        /// Host startAfterNight ran; apply this peer's own survival reward. Trader
        /// standing is per-player, so it is written straight into the local Flags state.
        /// </summary>
        internal void HandleMorningReward(MorningRewardMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            int hostId = _net.HostPlayerId > 0 ? _net.HostPlayerId : 1;
            if (_net.CurrentReceivePlayerId != hostId)
                return;

            Player player = Player.Instance;
            if (player == null || DeathStateTracker.LocalNightDeath)
                return;

            if (!string.IsNullOrEmpty(msg.TraderName) && msg.Reputation > 0)
            {
                Flags flags = Singleton<Flags>.Instance;
                if (flags != null)
                {
                    Flags.NPCState state = flags.getNPCState(msg.TraderName);
                    if (state == null)
                    {
                        state = new Flags.NPCState { name = msg.TraderName, wantsToTalk = true };
                        flags.npcStates.Add(state);
                    }
                    state.reputation += msg.Reputation;

                    string traderName = msg.TraderName;
                    int repGain = msg.Reputation;
                    Controller ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                    {
                        ctrl.Invoke(delegate
                        {
                            UI ui = Singleton<UI>.Instance;
                            if (ui != null && ui.journal != null)
                                ui.journal.showJournalInfoPopup("Reputation",
                                    Language.Get(traderName, "Objects") + ": " + repGain);
                        }, 3f, timeScaleDependent: true);
                    }
                }
            }

            if (msg.Saturation > 0f)
                player.gainSaturation(msg.Saturation);

            if (msg.ShowTraderHelp && Singleton<UI>.Instance != null)
            {
                var help = Singleton<UI>.Instance.displayHelpMessage(
                    Language.Get("Helpmsg_nightTraderReputation", "UI"));
                if (help != null)
                    help.longevity = 10f;
            }

            ModRuntime.LegacyInfo(
                $"[MorningRep] client reward: {msg.TraderName} +{msg.Reputation} sat {msg.Saturation}");
        }

        internal void HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            ModRuntime.LegacyInfo($"[FinalDreamscene] Received remote death notification for player {playerId}");

            RemotePlayerProxy diedProxy = _net.GetProxy(playerId);
            if (diedProxy != null)
            {
                CharBase cb = diedProxy.CachedCharBase;
                if (cb != null)
                {
                    cb.alive = false;
                    cb.Health = 0f;
                }
                var deathCols = diedProxy.CachedColliders;
                if (deathCols != null)
                {
                    for (int ci = 0; ci < deathCols.Length; ci++)
                    {
                        if (deathCols[ci] != null)
                            deathCols[ci].enabled = false;
                    }
                }
                _net.GetOrCreateState(playerId).IsDeadInDream = true;
            }

            Sync.FinalDreamsceneManager.OnRemoteDeathInDream(playerId);
        }
    }
}
