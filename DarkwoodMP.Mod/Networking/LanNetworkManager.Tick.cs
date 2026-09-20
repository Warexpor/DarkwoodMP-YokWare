using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Per-frame Update / LateUpdate send and flush tick (split from main for ownership).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        private void Update()
        {
            bool perf = IsConnected && _handshakeComplete
                && (_role == NetworkRole.Client || _role == NetworkRole.Host);
            ClientPerfProbe.SetActive(perf, _role);
            if (perf) ClientPerfProbe.FrameBegin();

            _net?.PollEvents();
            PollSteamBackend();
            Audio.VoiceChatService.Tick();
            if (perf) ClientPerfProbe.MarkPoll();

            if (perf) ClientPerfProbe.BeginUpdateSegment("walkie");
            Items.WalkieItem.Tick();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("flushPending");
            // Apply join bulk/deltas that arrived before Flags existed (menu → load)
            FlagHandlers.TryFlushPendingFlags();
            JournalHandlers.TryFlushPendingJournal();
            TradeHandlers.TryFlushPendingTradeInventories();
            LockHandlers.TryFlushPendingConstructibles();
            StationHandlers.TryFlushPendingSawStates();
            StationHandlers.TryFlushPendingFeederStates();
            StationHandlers.TryFlushPendingLureStates();
            ChainHandlers.TryFlushPendingChainStates();
            ShadowArmorHandlers.TryFlushPending();
            WorldBurnHandlers.TryFlushPending();
            Sync.StationSyncHelpers.FlushLureOutbox(force: false);
            BarricadeHandlers.TryFlushPendingBarricadeEvents();
            NightHandlers.TryFlushPendingScenario();
            LockHandlers.TryFlushPendingLocks();
            Sync.WorldPhysicsSyncService.TryFlushPendingLights();
            Sync.TrapNetworkId.FlushPending(
                (p, n) => Sync.WorldPhysicsSyncService.FindTrapByPos(p, n),
                (go, trig, silent) => Sync.WorldPhysicsSyncService.ApplyTrapState(go, trig, silentDisarm: silent));
            Sync.WorldPhysicsSyncService.TickThrownLightExpiry(this);
            PlayerPresenceHandlers.TickClientCorpseSetup();
            if (perf)
            {
                ClientPerfProbe.SetPendingCounts(
                    StationHandlers.PendingLureCount,
                    LockHandlers.PendingLockCount,
                    Sync.WorldPhysicsSyncService.PendingLightCount,
                    Sync.TrapNetworkId.PendingCount,
                    StationHandlers.PendingFeederCount,
                    StationHandlers.PendingSawCount,
                    LockHandlers.PendingConstructibleCount);
            }
            TickHeavyLateJoinBulk();
            TickHostWorldShareWhenReady();
            TickSaveSyncBroadcast();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("peerRoster");
            TickPeerRosterGossip();
            TickHostMigrationRetry();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("gameEvents");
            GameEventHandlers.TryFlushPendingGameEvents();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("meleeDebounce");
            CombatFxHandlers?.TickMeleeHitDebounceCleanup();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (!IsConnected || !_handshakeComplete)
            {
                if (perf) ClientPerfProbe.MarkUpdateRest();
                return;
            }

            // Flush flag updates deferred by the cooldown.
            if (_role == NetworkRole.Host || _role == NetworkRole.Client)
            {
                if (perf) ClientPerfProbe.BeginUpdateSegment("flagSync");
                Patches.FlagSyncBoolPatch.TickFlush();
                Patches.FlagSyncIntPatch.TickFlush();
                if (perf) ClientPerfProbe.EndUpdateSegment();
            }

            _sendTimer += Time.deltaTime;

            // Pause entity and physics traffic while packing and sending the world share.
            bool shareBusy = _worldSaveShare != null && _worldSaveShare.IsBusy;

            // Host: broadcast entity states to clients
            if (_role == NetworkRole.Host && !shareBusy)
            {
                if (perf) ClientPerfProbe.BeginUpdateSegment("entityBroadcast");
                EntityStateBroadcastService.Tick();
                if (perf) ClientPerfProbe.EndUpdateSegment();

                _proxyAggroTimer += Time.deltaTime;
                if (_proxyAggroTimer >= 0.5f)
                {
                    _proxyAggroTimer = 0f;
                    if (perf) ClientPerfProbe.BeginUpdateSegment("proxyAggro");
                    WorldProxyHandlers.ProxyAggroCheck();
                    if (perf) ClientPerfProbe.EndUpdateSegment();
                }

                if (perf) ClientPerfProbe.BeginUpdateSegment("timeShadow");
                _timeSyncTimer += Time.deltaTime;
                if (_timeSyncTimer >= TimeSyncInterval)
                {
                    _timeSyncTimer = 0f;
                    WorldWeatherTimeHandlers.SendTimeSyncTo(-1);
                }

                _shadowBroadcastTimer += Time.deltaTime;
                if (_shadowBroadcastTimer >= ShadowBroadcastInterval)
                {
                    _shadowBroadcastTimer = 0f;
                    WorldSendHandlers.BroadcastShadowStates();
                }
                if (perf) ClientPerfProbe.EndUpdateSegment();
            }

            // Both host and client send their local physics state:
            // - Host broadcasts to all clients (authoritative)
            // - Client sends to host so it can merge + forward
            // Skip while local player is in a dream -- dream objects don't exist
            // in the shared world and would cause phantom spawns on the other side.
            if (perf) ClientPerfProbe.BeginUpdateSegment("physTimer");
            _physicsSendTimer += Time.deltaTime;
            // Physics runs while awake; dream free bodies are also allowed.
            bool physTick = _physicsSendTimer >= PhysicsSendInterval && !shareBusy;
            if (physTick)
                _physicsSendTimer = 0f;
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (physTick)
            {
                bool clientNotReady = _role == NetworkRole.Client
                    && (Core.mainMenu || Core.loadingGame || !Core.coreStarted);
                if (!clientNotReady)
                {
                    if (perf) ClientPerfProbe.MarkUpdateRest();
                    bool built = Sync.WorldPhysicsSyncService.TryBuildWorldSnapshot(out var snap);
                    if (perf) ClientPerfProbe.MarkPhysBuild();
                    if (built)
                    {
                        if (_role == NetworkRole.Host)
                            BroadcastHot(NetMessageType.PhysicsState, w => snap.Serialize(w),
                                skipLoadingPeers: true);
                        else
                            BroadcastHot(NetMessageType.PhysicsState, w => snap.Serialize(w));
                    }
                }
                else if (perf)
                {
                    ClientPerfProbe.MarkUpdateRest();
                }
            }
            else if (perf)
            {
                ClientPerfProbe.MarkUpdateRest();
            }

            // Host still needs light PlayerState for proxies, but not mid-share
            if (shareBusy && _role == NetworkRole.Host)
                return;

            if (_sendTimer < SendInterval)
                return;

            Player local = Player.Instance;
            if (local == null)
                return;

            // Client join: do not emit PlayerState during title / LoadScene / before core
            // is ready. The host waits for the first in-world packet before
            // sending heavy bulk.
            if (_role == NetworkRole.Client
                && (Core.mainMenu || Core.loadingGame || !Core.coreStarted))
                return;

            // Don't send position updates while dead in a dream (freezes proxy at death position)
            if (Sync.FinalDreamsceneManager.IsLocalDead)
                return;

            _sendTimer = 0f;
            Vector3 pos = local.transform.position;

            // When spectating, report original (saved) position to network so the remote
            // doesn't see the host's proxy teleport into the client and cause body-pushing
            var netPosOverride = Spectator.SpectatorModeController.Instance?.NetworkPositionOverride;
            if (netPosOverride.HasValue)
                pos = netPosOverride.Value;

            Vector3 vel = (pos - _lastSentPosition) / SendInterval;
            _lastSentPosition = pos;

            // Host + clients: periodically sync wards / poison / bleed / skill flags to peers.
            // Include host effect flags so clients can present shadow and forest
            // spirit wards.
            _effectSyncTimer += Time.deltaTime;
            if (_effectSyncTimer >= 2f)
            {
                _effectSyncTimer = 0f;
                WorldProxyHandlers.SendPlayerEffects();
            }

            // Both sides: send own position to the other side at ~30 Hz
            string torsoClip = PlayerAnimationSnapshot.ReadTorsoClip(local);
            string legsClip = PlayerAnimationSnapshot.ReadLegsClip(local);
            // Night-dead + spectating: vanilla still plays get-up clips on the local body.
            // Force death clips so host never "revives" our proxy mid-spectate.
            if (DeathStateTracker.LocalNightDeath)
            {
                torsoClip = "Death1";
                legsClip = "Death1";
            }

            var msg = new PlayerStateMessage
            {
                PlayerId = _localPlayerId,
                Sequence = ++_nextPlayerStateSequence,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                VelX = vel.x,
                VelZ = vel.z,
                LocomotionState = (byte)PlayerAnimationSnapshot.ReadLocomotion(local),
                FlipX = false, // The game uses rotation for this pose.
                Running = local.running && !DeathStateTracker.LocalNightDeath,
                LegFacingY = PlayerAnimationSnapshot.ReadLegFacingY(local),
                ReverseLegs = PlayerAnimationSnapshot.ReadReverseLegs(local),
                TorsoFacingY = PlayerAnimationSnapshot.ReadTorsoFacingY(local),
                TorsoClip = torsoClip,
                LegsClip = legsClip,
                CurrentFrame = PlayerAnimationSnapshot.ReadCurrentFrame(local),
                InBearTrap = local.inBearTrap,
                HasLightProtection = local.isInLight,
                HasNightShadows = local.skills != null && local.skills.NightShadows,
                AfterNightActive = Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.isAfterNight,
                TrapNetId = local.inBearTrap
                    ? Sync.TrapNetworkId.ResolveOccupyingTrapId(pos, hostMint: _role == NetworkRole.Host)
                    : 0
            };

            PackContinuousLights(ref msg, local);

            // Continue sending PlayerState to loading peers so their proxies
            // can be created before the rest of the join traffic.
            // while host muted PlayerState under skipLoadingPeers during world share / phase-3 mute.
            BroadcastHot(NetMessageType.PlayerState, w => msg.Serialize(w),
                skipLoadingPeers: false);

            // Detect OutsideLocation (basement/bunker) transitions
            if (Singleton<OutsideLocations>.Instance != null)
            {
                bool inOutsideLoc = Singleton<OutsideLocations>.Instance.playerInOutsideLocation;
                string locName = Singleton<OutsideLocations>.Instance.currentLocationName ?? "";

                // While inside, send LocationEnter every 30 frames (~1 Hz) so the
                // receiver can retry after an async location spawn completes.
                // Dreams: send once on enter or rename instead of every tick.
                _locationSyncCounter++;
                bool dreamLocActive = Sync.DreamSyncManager.IsDreamActive;
                bool locChanged = !_previousInOutsideLocation || locName != _previousLocationName;
                bool heartbeatRetry = !dreamLocActive && _locationSyncCounter >= 30;
                if (inOutsideLoc && (locChanged || heartbeatRetry))
                {
                    _locationSyncCounter = 0;
                    if (!string.IsNullOrEmpty(locName))
                    {
                        // Use the live dream pad name during the session, not the
                        // vanilla completed-location name.
                        string txName = dreamLocActive
                            ? Sync.DreamSyncManager.CanonicalDreamLocationName(locName)
                            : locName;
                        BroadcastHot(NetMessageType.LocationEnter,
                            w => new LocationEnterMessage
                            {
                                LocationName = txName,
                                PlayerId = _localPlayerId
                            }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                        ModRuntime.LegacyInfo($"[LocationSync] sent LocationEnter: {txName} pid={_localPlayerId}");
                    }
                }

                // Exited the location (left the sub-location area)
                if (!inOutsideLoc && _previousInOutsideLocation)
                {
                    // Dream transport (dreamPrepared branch) never sets playerInOutsideLocation=true,
                    // so settle hooks set previous=true while live flag stays false → false Exit.
                    // While dreaming / EnteringDream, stay "inside" the dream pad.
                    bool stayInDreamPad = Sync.DreamSyncManager.IsDreamActive
                        || (Dreams.Instance != null && (Dreams.Instance.dreaming || Dreams.Instance.dreamPrepared))
                        || Core.EnteringDream;
                    if (stayInDreamPad)
                    {
                        inOutsideLoc = true;
                        if (string.IsNullOrEmpty(locName) && !string.IsNullOrEmpty(_previousLocationName))
                            locName = _previousLocationName;
                    }
                    else
                    {
                        Vector3 exitPos = local.transform.position;
                        BroadcastHot(NetMessageType.LocationExit,
                            w => new LocationExitMessage
                            {
                                PosX = exitPos.x,
                                PosY = exitPos.y,
                                PosZ = exitPos.z,
                                PlayerId = _localPlayerId
                            }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                        ModRuntime.LegacyInfo($"[LocationSync] sent LocationExit pid={_localPlayerId} pos={exitPos}");
                    }
                }

                _previousInOutsideLocation = inOutsideLoc;
                _previousLocationName = locName;
            }

            // Host: also track own position locally for AI checks
            if (_role == NetworkRole.Host)
                PlayerPositionManager.ReportHostPosition(pos);

            // Both sides: if dragging an object, sync its position at ~30 Hz
            if (local.dragging && local.itemBeingDragged != null)
            {
                Item dragged = local.itemBeingDragged;
                _lastDraggedItemName = dragged.gameObject.name;
                // Claim this object so other players can't grab it simultaneously
                _dragClaims[_lastDraggedItemName] = _localPlayerId;
                // Keep scrape authority so host PhysicsState / DragSync echo cannot arm MOS.
                DWMPHorde.Audio.ItemMovingSoundHelper.NoteLocalPushAuthority(_lastDraggedItemName);

                // Scrape intent follows player movement, using the same gate as
                // body push rather than object position delta.
                // hinge jitter kept scrape armed for observers after the host stopped walking.
                float hSpeed = 0f;
                if (local.Rigidbody != null)
                {
                    Vector3 v = local.Rigidbody.velocity;
                    hSpeed = new Vector3(v.x, 0f, v.z).magnitude;
                }
                bool playerMoving = hSpeed >= DragScrapeStopSpeed;
                if (playerMoving)
                {
                    _dragScrapeQuietSince = -1f;
                    _dragScrapeActive = true;
                }
                else
                {
                    if (_dragScrapeQuietSince < 0f)
                        _dragScrapeQuietSince = Time.unscaledTime;
                    if (Time.unscaledTime - _dragScrapeQuietSince >= DragScrapeStopGrace)
                        _dragScrapeActive = false;
                }

                var dragMsg = new DragSyncMessage
                {
                    PosX = dragged.transform.position.x,
                    PosY = dragged.transform.position.y,
                    PosZ = dragged.transform.position.z,
                    RotX = dragged.transform.eulerAngles.x,
                    RotY = dragged.transform.eulerAngles.y,
                    RotZ = dragged.transform.eulerAngles.z,
                    IsDragging = true,
                    ObjectName = _lastDraggedItemName,
                    ItemType = dragged.invItem != null ? dragged.invItem.type : "",
                    ClaimedByPlayerId = _localPlayerId,
                    ScrapeActive = _dragScrapeActive
                };
                // Quiet scrape stop must be reliable; unreliable quiet ticks can be lost
                // observers kept the last NoteMoving loop until full release.
                var dragDelivery = _dragScrapeActive
                    ? DeliveryMethod.Unreliable
                    : DeliveryMethod.ReliableOrdered;
                BroadcastHot(NetMessageType.DragSync, w => dragMsg.Serialize(w), dragDelivery);
                _wasDragging = true;
            }
            else if (_wasDragging)
            {
                // Backup if stopDragging never ran (edge cases). Prefer NotifyLocalDragEnded.
                NotifyLocalDragEnded(_lastDraggedItemName);
            }
        }
    }
}
