using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleHandshake(HandshakeMessage handshake)
        {
            if (handshake.ProtocolVersion != PluginInfo.ProtocolVersion)
            {
                ModLog.Error(LogCat.Network,
                    "Protocol mismatch. Local="
                    + PluginInfo.ProtocolVersion
                    + " remote="
                    + handshake.ProtocolVersion
                    + " — update both mods to the same version.");
                if (_currentReceivePeer != null)
                    _currentReceivePeer.Disconnect();
                return;
            }

            if (_role == NetworkRole.Client)
            {
                // Client: store our assigned playerId from host's Handshake
                if (handshake.PlayerId > 0)
                    _localPlayerId = handshake.PlayerId;
                _handshakeComplete = true;
                _handshakedPeers.Clear();
                _handshakedPeers.Add(1); // host is player 1
                StatusText = "Connected — waiting for host world…";
                ModLog.Event(LogCat.Network, $"Handshake OK — assigned PlayerId={_localPlayerId}");
                if (Core.mainMenu)
                    ModLog.Event(LogCat.Session,
                        "On title menu: host will push save files if already in-world (auto load).");
            }
            else
            {
                int playerId = _currentReceivePlayerId;
                if (playerId > 0)
                    _handshakedPeers.Add(playerId);
                // Host gameplay traffic stays up for already-ready peers; first ready peer enables send loop.
                _handshakeComplete = _handshakedPeers.Count > 0;
                ModLog.Event(LogCat.Network, $"Handshake OK from Player {playerId} (ready peers: {_handshakedPeers.Count})");
                StatusText = $"Player {playerId} joined";

                // Sync current weather state to the newly connected client only
                SendWeatherSyncTo(playerId);

                // Late join from title menu: bulk state is not enough — client has no chapter loaded.
                // Delay a few frames so the client finishes handshake + WorldSession before megabyte save stream.
                if (playerId > 0)
                {
                    if (HostHasShareableWorld())
                    {
                        ModLog.Event(LogCat.Save,
                            "Client " + playerId + " handshaked while host in-world — scheduling auto world share");
                        StartCoroutine(DelayedWorldShareTo(playerId, 0.75f));
                    }
                    else
                    {
                        ModLog.Warn(LogCat.Save,
                            "Client " + playerId + " joined but host is NOT in-world (mainMenu="
                            + Core.mainMenu + " profile=" + (Core.currentProfile != null)
                            + " player=" + (Player.Instance != null)
                            + " loaded=" + Core.loadedGame
                            + ") — no auto world share yet. Will share when host enters chapter, or use F2 Resend.");
                    }
                }
            }
        }

        private System.Collections.IEnumerator DelayedWorldShareTo(int playerId, float delaySec)
        {
            float t = 0f;
            while (t < delaySec)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (_role != NetworkRole.Host) yield break;
            if (!_handshakedPeers.Contains(playerId)) yield break;
            if (!HostHasShareableWorld())
            {
                ModLog.Warn(LogCat.Save, "Delayed world share aborted — host left world before share for p" + playerId);
                // Still try bulk — client may load a matching save manually.
                SendLateJoinGameplayBulk(playerId);
                yield break;
            }
            ModLog.Event(LogCat.Save, "Auto world share → player " + playerId + " starting now");
            _worldSaveShare?.ScheduleHostShareToPlayer(playerId);
            // Mark bulk pending (settle clock starts on first valid PlayerState).
            _awaitingLateJoinBulk[playerId] = 0f;
            ModLog.Event(LogCat.Session,
                "Player " + playerId + " queued for late-join bulk after "
                + ClientBulkSettleSeconds.ToString("F0") + "s settled in-world");
        }

        /// <summary>
        /// After a broadcast world resend (host entered chapter), mark peers for bulk.
        /// </summary>
        public void ScheduleLateJoinBulkAfterWorldShare()
        {
            if (_role != NetworkRole.Host)
                return;
            foreach (int id in _handshakedPeers)
            {
                if (id > 1)
                    _awaitingLateJoinBulk[id] = 0f;
            }
            ModLog.Event(LogCat.Session,
                "Marked " + _awaitingLateJoinBulk.Count + " peer(s) for late-join bulk after settle");
        }

        /// <summary>
        /// Light late-join dump only (no FindObjectsOfType bag/drop scans, no proxy spawn).
        /// Heavy world object scans were freezing the host the frame a joiner finished loading.
        /// </summary>
        internal void SendLateJoinGameplayBulk(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0)
                return;

            _awaitingLateJoinBulk.Remove(playerId);
            ModLog.Event(LogCat.Session,
                "Sending light late-join bulk → player " + playerId
                + " (no deathbag/drop scans)");

            // Small reliable packets only — no scene-wide FindObjectsOfType.
            SendJournalBulkSyncTo(playerId);
            SendFlagBulkSyncTo(playerId);
            // Dialogue tree after flags so Flags.dialogues exists when applying.
            DWMPHorde.Sync.DialogTreeSync.SendBulkTo(this, playerId);
            SendReputationBulkSyncTo(playerId);
            SendHideoutStateSyncTo(playerId);
            SendWorkbenchLevelSyncTo(playerId);
            SendMapStateSyncTo(playerId);
            SendTimeSyncTo(playerId);
            SyncCurrentLightState();
            // Scenario bulk can re-fire night "unique events" on the client — skip on join.
            // SendScenarioBulkSyncTo(playerId);
            // Proxy is created from live PlayerState once CanSpawnRemoteProxies — not here.
        }

        /// <summary>
        /// Host: bulk only after joiner has been sending PlayerState for ClientBulkSettleSeconds.
        /// </summary>
        private void TryFlushLateJoinBulkForPeer(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0)
                return;
            if (!_awaitingLateJoinBulk.TryGetValue(playerId, out float firstSeen))
                return;

            float now = Time.realtimeSinceStartup;
            if (firstSeen <= 0f)
            {
                _awaitingLateJoinBulk[playerId] = now;
                ModLog.Event(LogCat.Session,
                    "Player " + playerId + " in-world — bulk in "
                    + ClientBulkSettleSeconds.ToString("F0") + "s (settle)");
                return;
            }

            if (now - firstSeen < ClientBulkSettleSeconds)
                return;

            SendLateJoinGameplayBulk(playerId);
        }

        /// <summary>True when host is past the title screen and has a profile world worth sending.</summary>
        private static bool HostHasShareableWorld()
        {
            try
            {
                if (Core.mainMenu)
                    return false;
                if (Player.Instance != null)
                    return true;
                if (Singleton<WorldGenerator>.Instance != null)
                    return true;
                if (Core.loadedGame || Core.loadingGame)
                    return true;
                if (Core.currentProfile != null && !Core.mainMenu)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Client may only apply journal/flags/world mutations once in a loaded chapter.
        /// Title menu has UI.journal stubs that NRE inside addJournalEntry / Inventory.Start.
        /// </summary>
        private static bool ClientCanApplyWorldBulk()
        {
            try
            {
                if (Core.mainMenu)
                    return false;
                if (Core.loadingGame)
                    return false;
                if (Player.Instance == null)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HandleWorldSession(WorldSessionMessage session)
        {
            _worldSync.ApplyHostSession(session, asClient: _role == NetworkRole.Client);
            if (_role == NetworkRole.Client)
                StatusText = "Synced — load " + session.SaveSlotName + " (ch" + session.ChapterId + ")";
        }

        private void HandlePlayerState(PlayerStateMessage state)
        {
            int playerId = _currentReceivePlayerId; // For host: which client sent state
            if (playerId <= 0) return;

            if (_role == NetworkRole.Host)
            {
                // First in-world state from a joiner → dump deferred bulk (not during their LoadScene)
                TryFlushLateJoinBulkForPeer(playerId);

                // Host: update position manager per-player
                PlayerPositionManager.UpdateRemotePlayer(playerId,
                    new Vector3(state.PosX, state.PosY, state.PosZ),
                    state.TorsoFacingY);

                if (playerId > 0)
                {
                    GetOrCreateState(playerId).InBearTrap = state.InBearTrap;
                    GetOrCreateState(playerId).BearTrapPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                    GetOrCreateState(playerId).HasLightProtection = state.HasLightProtection;
                    if (state.InBearTrap)
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Trap] host: player {playerId} trapped at {GetOrCreateState(playerId).BearTrapPos}");

                    EnsureRemoteProxy(playerId);
                    RemotePlayerProxy proxy = GetProxy(playerId);
                    if (proxy == null) return;

                    // Revive proxy only when the remote player has genuinely respawned
                    // (not while still sending death clips)
                    if (state.TorsoClip != "Death1" && state.TorsoClip != "Death2")
                    {
                        CharBase reviveCB = proxy.GetComponent<CharBase>();
                        if (reviveCB != null && !reviveCB.alive)
                        {
                            reviveCB.alive = true;
                            reviveCB.Health = reviveCB.maxHealth;
                            foreach (Collider col in proxy.GetComponentsInChildren<Collider>(true))
                                col.enabled = true;
                            var animComp = proxy.GetComponent<Players.SecondPlayerAnimController>();
                            if (animComp != null)
                                animComp.ResetDeathState();
                            var rb = proxy.GetComponent<Rigidbody>();
                            if (rb != null)
                                rb.position = new Vector3(state.PosX, state.PosY, state.PosZ);
                            ModRuntime.LegacyInfo($"[Death] Remote proxy revived for player {playerId}");
                        }
                    }

                    var netState = new PlayerStateNet
                    {
                        Position = new Vector3(state.PosX, state.PosY, state.PosZ),
                        Locomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState,
                        FlipX = state.FlipX,
                        LegFacingY = state.LegFacingY,
                        ReverseLegs = state.ReverseLegs,
                        TorsoFacingY = state.TorsoFacingY,
                        TorsoClip = state.TorsoClip,
                        LegsClip = state.LegsClip,
                        CurrentFrame = state.CurrentFrame
                    };

                    proxy.RemoteRunning = state.Running;
                    proxy.RemoteLocomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState;
                    proxy.ApplyNetworkState(netState);
                    HandleRemoteContinuousLights(state, playerId);

                    // Forward this client's state to all other connected clients (3+ support)
                    if (playerId > 0)
                        SendToAllExcept(playerId, NetMessageType.PlayerState, w => state.Serialize(w));

                    // DISABLED on join path: removeAfterNightEffect() is a full-screen native
                    // morning/event sequence. A joining client's AfterNightActive=false packet
                    // was firing it on the HOST mid-session ("unique event" + hitch).
                    // Live hideout leave sync can return later with an explicit reliable message.
                }
                return;
            }

            // Client receives host (or forwarded peer) state — ignore during LoadScene
            // or title (EnsureRemoteProxy was spamming 500+ "Player is inactive" / frame).
            if (!CanSpawnRemoteProxies())
                return;

            {
                int remotePlayerId = state.PlayerId > 0 ? state.PlayerId : 1;
                PlayerPositionManager.UpdateRemotePlayer(remotePlayerId,
                    new Vector3(state.PosX, state.PosY, state.PosZ),
                    state.TorsoFacingY);
                EnsureRemoteProxy(remotePlayerId);
                RemotePlayerProxy proxy = GetProxy(remotePlayerId);

                GetOrCreateState(remotePlayerId).InBearTrap = state.InBearTrap;
                GetOrCreateState(remotePlayerId).BearTrapPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                GetOrCreateState(remotePlayerId).HasLightProtection = state.HasLightProtection;
                if (state.InBearTrap)
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Trap] client: player {remotePlayerId} trapped at {GetOrCreateState(remotePlayerId).BearTrapPos}");

                if (proxy == null) return;

                // Only revive when player has genuinely respawned (not still sending death clips)
                CharBase reviveCB = proxy.GetComponent<CharBase>();
                if (reviveCB != null && !reviveCB.alive
                    && state.TorsoClip != "Death1" && state.TorsoClip != "Death2")
                {
                    reviveCB.alive = true;
                    reviveCB.Health = reviveCB.maxHealth;
                    foreach (Collider col in proxy.GetComponentsInChildren<Collider>(true))
                        col.enabled = true;
                    var animComp = proxy.GetComponent<Players.SecondPlayerAnimController>();
                    if (animComp != null)
                        animComp.ResetDeathState();
                    var rb = proxy.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.position = new Vector3(state.PosX, state.PosY, state.PosZ);
                    ModRuntime.LegacyInfo($"[Death] Remote proxy revived for player {remotePlayerId}");
                }

                proxy.RemoteRunning = state.Running;
                proxy.RemoteLocomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState;

                var remoteState = new PlayerStateNet
                {
                    Position = new Vector3(state.PosX, state.PosY, state.PosZ),
                    Locomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState,
                    FlipX = state.FlipX,
                    LegFacingY = state.LegFacingY,
                    ReverseLegs = state.ReverseLegs,
                    TorsoFacingY = state.TorsoFacingY,
                    TorsoClip = state.TorsoClip,
                    LegsClip = state.LegsClip,
                    CurrentFrame = state.CurrentFrame
                };

                proxy.ApplyNetworkState(remoteState);
                HandleRemoteContinuousLights(state, remotePlayerId);
            }
        }

        /// <summary>
        /// Continuous held lights from PlayerState (~30 Hz): flare B+ + flashlight stream.
        /// Flare is parented to the proxy with a hand local offset (not world body center).
        /// Sole owner for held flare — destroys any event-path ItemLight to prevent double light.
        /// </summary>
        private void HandleRemoteContinuousLights(PlayerStateMessage state, int playerId = -1)
        {
            if (playerId < 0) playerId = _currentReceivePlayerId;
            if (playerId < 0) playerId = _peers.Count > 0 ? _peers.Keys.First() : 1;

            HandleRemoteFlareLight(state, playerId);
            HandleRemoteFlashlightStream(state, playerId);
        }

        private void HandleRemoteFlareLight(PlayerStateMessage state, int playerId)
        {
            if (state.FlareActive)
            {
                // Mutex: continuous flare owns light — never keep event-path item light.
                DestroyRemoteItemLight(playerId);

                RemotePlayerProxy proxy = GetProxy(playerId);
                var remoteState = GetOrCreateState(playerId);
                Vector3 localOff = new Vector3(state.FlareLocalX, state.FlareLocalY, state.FlareLocalZ);
                bool rising = remoteState.FlareLight == null;

                if (state.FlareHasItemType && !string.IsNullOrEmpty(state.FlareItemType))
                    remoteState.FlareItemType = state.FlareItemType;

                if (rising)
                {
                    if (Config.ModConfig.IsVerboseLightSync)
                        ModRuntime.LegacyInfo($"[LightSync] remote flare ON p{playerId} type={remoteState.FlareItemType ?? "?"}");

                    Light2D template = null;
                    Transform lightDotT = Player.Instance?.transform.Find("PlayerLightDot");
                    if (lightDotT != null)
                        template = lightDotT.GetComponent<Light2D>();
                    GameObject flareLight;
                    if (template != null)
                    {
                        flareLight = UnityEngine.Object.Instantiate(template.gameObject);
                        flareLight.name = $"RemoteFlareLight_P{playerId}";
                    }
                    else
                    {
                        flareLight = new GameObject($"RemoteFlareLight_P{playerId}");
                        var created = flareLight.AddComponent<Light2D>();
                        if (created.LightMaterial == null)
                            created.LightMaterial = Resources.Load("RadialLight") as Material;
                    }

                    if (proxy != null)
                    {
                        flareLight.transform.SetParent(proxy.transform, false);
                        flareLight.transform.localPosition = localOff;
                        flareLight.transform.localRotation = Quaternion.identity;
                    }
                    else
                    {
                        flareLight.transform.SetParent(null);
                        flareLight.transform.position = new Vector3(state.PosX, state.PosY, state.PosZ) + localOff;
                    }

                    remoteState.FlareLight = flareLight;
                    Light2D light = flareLight.GetComponent<Light2D>();
                    if (light != null)
                    {
                        light.lightsPlayer = true;
                        light.updateGraph = true;
                        float r = state.FlareHasParams && state.FlareRadius > 0f ? state.FlareRadius : 650f;
                        float inten = state.FlareHasParams && state.FlareIntensity > 0f ? state.FlareIntensity : 1f;
                        light.LightRadius = r;
                        light.LightIntensity = inten;
                        if (state.FlareHasParams)
                            light.LightColor = new Color(state.FlareColorR, state.FlareColorG, state.FlareColorB);
                        else
                            light.LightColor = new Color(1f, 0.5f, 0.1f);
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null && !ctrl.logicLights.Contains(light))
                            ctrl.logicLights.Add(light);
                    }

                    SpawnRemoteFlareFx(playerId, remoteState, proxy, localOff);
                }
                else
                {
                    if (proxy != null && remoteState.FlareLight.transform.parent != proxy.transform)
                    {
                        remoteState.FlareLight.transform.SetParent(proxy.transform, false);
                        remoteState.FlareLight.transform.localRotation = Quaternion.identity;
                    }
                    remoteState.FlareLight.transform.localPosition = localOff;
                    if (remoteState.FlareFx != null)
                        remoteState.FlareFx.transform.localPosition = localOff;

                    Light2D light = remoteState.FlareLight.GetComponent<Light2D>();
                    if (light != null && state.FlareHasParams)
                    {
                        if (state.FlareRadius > 0f)
                            light.LightRadius = state.FlareRadius;
                        if (state.FlareIntensity > 0f)
                            light.LightIntensity = state.FlareIntensity;
                        light.LightColor = new Color(state.FlareColorR, state.FlareColorG, state.FlareColorB);
                    }
                }
            }
            else if (_remotePlayers.TryGetValue(playerId, out var existingState)
                     && (existingState.FlareLight != null || existingState.FlareFx != null))
            {
                if (Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo($"[LightSync] remote flare OFF p{playerId}");
                DestroyRemoteFlareLight(playerId);
            }
        }

        /// <summary>
        /// Held flare visual (particles + sprite). Light stays stream-owned; Flare die/longevity stripped.
        /// </summary>
        private void SpawnRemoteFlareFx(int playerId, RemotePlayerState remoteState, RemotePlayerProxy proxy, Vector3 localOff)
        {
            if (proxy == null) return;
            if (remoteState.FlareFx != null)
            {
                UnityEngine.Object.DestroyImmediate(remoteState.FlareFx);
                remoteState.FlareFx = null;
            }

            string itemType = remoteState.FlareItemType;
            InvItem itemDef = null;
            var db = Singleton<ItemsDatabase>.Instance;
            if (db != null)
            {
                if (!string.IsNullOrEmpty(itemType) && db.hasItem(itemType))
                    itemDef = db.getItem(itemType, instantiate: false);
                if (itemDef == null)
                {
                    // Fallback: first DB type containing "flare"
                    try
                    {
                        // ItemsDatabase API varies; try getItem on common names.
                        string[] candidates = { "flare", "Flare", "flare_red", "redFlare" };
                        for (int i = 0; i < candidates.Length && itemDef == null; i++)
                        {
                            if (db.hasItem(candidates[i]))
                                itemDef = db.getItem(candidates[i], instantiate: false);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        if (Config.ModConfig.IsVerboseLightSync)
                            ModRuntime.Log?.LogWarning("[LightSync] flare FX type resolve failed: " + ex.Message);
                    }
                }
            }

            GameObject prefab = itemDef != null ? itemDef.item as GameObject : null;
            if (prefab == null)
            {
                if (Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo($"[LightSync] flare FX missing prefab p{playerId} type={itemType}");
                return;
            }

            GameObject fx = UnityEngine.Object.Instantiate(prefab);
            fx.name = $"RemoteFlareFx_P{playerId}";
            fx.transform.SetParent(proxy.transform, false);
            fx.transform.localPosition = localOff;
            fx.transform.localRotation = Quaternion.identity;

            // Strip combat / physics / longevity — visual only; light is B+ stream.
            foreach (var rb in fx.GetComponentsInChildren<Rigidbody>(true))
                UnityEngine.Object.Destroy(rb);
            foreach (var col in fx.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            foreach (var ti in fx.GetComponentsInChildren<ThrownItem>(true))
                UnityEngine.Object.Destroy(ti);
            foreach (var ex in fx.GetComponentsInChildren<Explodes>(true))
                UnityEngine.Object.Destroy(ex);
            foreach (var flare in fx.GetComponentsInChildren<Flare>(true))
                UnityEngine.Object.Destroy(flare);
            // Disable any prefab Light2D so we don't double with RemoteFlareLight
            foreach (var lt in fx.GetComponentsInChildren<Light2D>(true))
            {
                lt.lightsPlayer = false;
                lt.updateGraph = false;
                lt.gameObject.SetActive(false);
            }
            foreach (var ad in fx.GetComponentsInChildren<AutoDestroyParticles>(true))
                UnityEngine.Object.Destroy(ad);

            EnsureEmitterVisible(fx);
            PlayAllParticleSystems(fx);
            SetupParticleSorting(fx);

            remoteState.FlareFx = fx;
            if (Config.ModConfig.IsVerboseLightSync)
                ModRuntime.LegacyInfo($"[LightSync] flare FX spawned p{playerId} type={itemType}");
        }

        private void HandleRemoteFlashlightStream(PlayerStateMessage state, int playerId)
        {
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;

            Transform flashT = proxy.transform.Find("Flashlight");
            if (flashT == null) return;

            if (state.FlashlightActive)
            {
                bool wasOff = !flashT.gameObject.activeSelf;
                flashT.gameObject.SetActive(true);
                if (wasOff && Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo($"[LightSync] remote flashlight ON p{playerId}");

                Light2D lt = flashT.GetComponent<Light2D>();
                if (lt != null)
                {
                    if (state.FlashHasParams)
                    {
                        if (state.FlashRadius > 0f)
                            lt.LightRadius = state.FlashRadius;
                        if (state.FlashIntensity > 0f)
                            lt.LightIntensity = state.FlashIntensity;
                        lt.LightColor = new Color(state.FlashColorR, state.FlashColorG, state.FlashColorB, 0f);
                    }
                    lt.lightsPlayer = true;
                    lt.updateGraph = true;
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null && !ctrl.logicLights.Contains(lt))
                        ctrl.logicLights.Add(lt);
                }
            }
            else
            {
                if (flashT.gameObject.activeSelf)
                {
                    if (Config.ModConfig.IsVerboseLightSync)
                        ModRuntime.LegacyInfo($"[LightSync] remote flashlight OFF p{playerId}");
                    Light2D lt = flashT.GetComponent<Light2D>();
                    if (lt != null)
                    {
                        lt.unlightGraphNodes();
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(lt);
                    }
                    flashT.gameObject.SetActive(false);
                }
            }
        }

        private void DestroyRemoteFlareLight(int playerId)
        {
            if (!_remotePlayers.TryGetValue(playerId, out var state))
                return;
            var flareLight = state.FlareLight;
            if (flareLight != null)
            {
                foreach (var fl in flareLight.GetComponentsInChildren<Light2D>(true))
                {
                    fl.unlightGraphNodes();
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(fl);
                }
                UnityEngine.Object.DestroyImmediate(flareLight);
                state.FlareLight = null;
            }
            if (state.FlareFx != null)
            {
                UnityEngine.Object.DestroyImmediate(state.FlareFx);
                state.FlareFx = null;
            }
            state.FlareItemType = null;
        }

        private void DestroyRemoteItemLight(int playerId)
        {
            if (!_remotePlayers.TryGetValue(playerId, out var state))
                return;
            var lightGo = state.ItemLight;
            if (lightGo == null) return;
            Light2D lt = lightGo.GetComponent<Light2D>();
            if (lt != null)
            {
                lt.unlightGraphNodes();
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ctrl.logicLights.Remove(lt);
            }
            UnityEngine.Object.DestroyImmediate(lightGo);
            state.ItemLight = null;
        }

        /// <summary>
        /// Returns true if the remote player is currently trapped in a bear trap.
        /// Removed distance check because the client's bear trap GameObject may be
        /// at a different position than the trapped player (e.g. dropped item vs
        /// world pool trap), making the distance check unreliable.
        /// </summary>
        public bool HasAnyTrappedPlayer => _remotePlayers.Values.Any(s => s.InBearTrap);

        /// <summary>True when the remote peer has shadow protection (torch, lantern, LightArea, etc.).</summary>
        public bool IsRemotePlayerHasLightProtection(int playerId) => _remotePlayers.TryGetValue(playerId, out var state) && state.HasLightProtection;

        public bool IsRemotePlayerTrappedNear(Vector3 trapPos)
        {
            if (!HasAnyTrappedPlayer)
                return false;

            ModRuntime.LegacyInfo("[Trap] remote trapped, blocking interaction");
            return true;
        }

        private void HandleEntityState(EntityStateMessage msg)
        {
            if (_role == NetworkRole.Client)
            {
                ClientEntityInterpolationService.ApplySnapshot(msg);
                // Ensure dead NPCs have corpses set up on the client
                EnsureDeadNpcCorpses();
            }
        }

        /// <summary>
        /// On the client, ensures that dead NPCs have the Item component and
        /// deathDrop inventory type so they can be searched/looted.
        /// This handles NPCs that die on the host (authoritative) but whose
        /// death never triggers Character.die() on the client (because AI
        /// updates are blocked and getHit() is forwarded to host).
        /// </summary>
        private void EnsureDeadNpcCorpses()
        {
            if (_role != NetworkRole.Client) return;

            Character[] all = CharacterTracker.GetAll();
            if (all == null || all.Length == 0) return;

            foreach (Character c in all)
            {
                if (c == null) continue;
                // Skip alive characters
                if (c.alive && c.Health > 0) continue;
                // Skip if already has Item component (corpse already set up)
                if (c.GetComponent<Item>() != null) continue;

                // Set up corpse — equivalent of CharacterDeathCorpsePatch
                Item item = c.gameObject.AddComponent<Item>();
                item.name = c.name.ToLower() + "_corpse";
                if (c.searched)
                    item.searched = true;

                if (c.inventory != null)
                    c.inventory.invType = Inventory.InvType.deathDrop;

                c.isActive = false;

                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Death] Set up corpse for '{c.name}' at {c.transform.position}");
            }
        }

        // Tracks host-spawned copies of items that don't exist on this side
        // (e.g. remote player dragged an item in an unloaded world-grid chunk).
        private readonly HashSet<int> _spawnedDragProxyItems = new HashSet<int>();

        private void HandleDragSync(DragSyncMessage msg)
        {
            Vector3 targetPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Vector3 targetRot = new Vector3(msg.RotX, msg.RotY, msg.RotZ);

            // Claim only while actively dragging (end packets release, not re-claim).
            if (msg.IsDragging && msg.ClaimedByPlayerId >= 0 && !string.IsNullOrEmpty(msg.ObjectName))
                _dragClaims[msg.ObjectName] = msg.ClaimedByPlayerId;

            // If a remote player claims an item we're dragging, force-stop our
            // drag to resolve the conflict and prevent double-drag desync.
            bool locallyDraggingThis = Player.Instance != null && Player.Instance.dragging &&
                Player.Instance.itemBeingDragged != null &&
                Player.Instance.itemBeingDragged.gameObject.name == msg.ObjectName;
            if (locallyDraggingThis && msg.IsDragging
                && msg.ClaimedByPlayerId >= 0 && msg.ClaimedByPlayerId != LocalPlayerId)
            {
                ModRuntime.LegacyInfo("[DragSync] remote player " + msg.ClaimedByPlayerId + " claimed " + msg.ObjectName + " — force-stopping local drag");
                Player.Instance.itemBeingDragged.stopDragging(force: true);
                _wasDragging = false;
                _lastDraggedItemName = null;
            }

            if (!msg.IsDragging)
            {
                // Intentional end: kill native ItemSounds residual + MOS with snappy fade.
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(msg.ObjectName);
                _lastDragSyncPos.Remove(msg.ObjectName);
                CleanupSpawnedDragProxy(msg.ObjectName);
                // Remove remote-drag tracking for items of this name so
                // PhysicsState can resume sending their position.
                RemoveRemoteDragIds(msg.ObjectName);
                // Release kinematic so local physics can affect the item again.
                ReleaseRemoteDragKinematic(msg.ObjectName);
                // Clear claim only if this peer owned it (or claim unknown).
                if (!string.IsNullOrEmpty(msg.ObjectName))
                {
                    if (!_dragClaims.TryGetValue(msg.ObjectName, out int owner)
                        || owner == msg.ClaimedByPlayerId
                        || msg.ClaimedByPlayerId <= 0
                        || owner == _currentReceivePlayerId)
                        _dragClaims.Remove(msg.ObjectName);
                }
                return;
            }

            Item item = FindDraggedItemLocally(msg.ObjectName, targetPos);
            if (item == null)
            {
                // Item doesn't exist on this side — spawn it on-demand so we
                // can reflect the remote player's manipulation.
                item = SpawnDraggedItem(msg);
                if (item == null)
                {
                    ModRuntime.LegacyInfo("[DragSync] cannot spawn \"" + msg.ObjectName + "\" type=" + msg.ItemType);
                    return;
                }
                _spawnedDragProxyItems.Add(item.GetInstanceID());
                ModRuntime.LegacyInfo("[DragSync] spawned " + item.name + " for remote drag");
            }
            else if (item.beingDragged || (Player.Instance != null && Player.Instance.dragging && Player.Instance.itemBeingDragged == item))
            {
                // Skip remote drag-updates for an object the local player is also
                // dragging — prevents tug-of-war jitter between both sides.
                Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(item.gameObject);
                // Release kinematic so local HingeJoint can drive position.
                if (ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                {
                    Rigidbody kinematicRb = item.GetComponent<Rigidbody>();
                    if (kinematicRb != null)
                        kinematicRb.isKinematic = false;
                }
                // Tag so TryBuildWorldSnapshot skips this item — prevents
                // PhysicsState (0.3 Hz) from fighting DragSync (30 Hz) even
                // when BOTH peers are dragging the same item (double grab).
                _remoteDragItemIds.Add(item.GetInstanceID());
                _remoteDragItemNames.Add(item.gameObject.name);
                return;
            }

            // Remove from interpolation — DragSyncMessage is more authoritative
            // and instant, while UpdateObjectInterpolation would smooth over
            // the jump and fight subsequent DragSync updates.
            Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(item.gameObject);
            // Tag so TryBuildWorldSnapshot skips this item — prevents
            // PhysicsState (0.3 Hz) from fighting DragSync (30 Hz).
            _remoteDragItemIds.Add(item.GetInstanceID());
            _remoteDragItemNames.Add(item.gameObject.name);

            Rigidbody targetRb = item.GetComponent<Rigidbody>();
            if (targetRb != null)
            {
                targetRb.position = targetPos;
                targetRb.rotation = Quaternion.Euler(targetRot);
                targetRb.velocity = Vector3.zero;
                targetRb.angularVelocity = Vector3.zero;
                // Lock to host position between DragSync frames — prevents proxy
                // collisions on the client from pushing the item away.
                if (ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                    targetRb.isKinematic = true;

                // Scrape: same immediacy as body-push (NotifyBodyPushStarted → NoteMoving).
                // Old path waited one DragSync tick for motion delta → audible delay, then
                // NoteStationary on slow frames faded the loop mid-drag.
                ItemSounds dragSounds = item.GetComponent<ItemSounds>();
                bool firstDragPacket = !_lastDragSyncPos.ContainsKey(msg.ObjectName);
                bool moved = !firstDragPacket
                    && Vector3.Distance(_lastDragSyncPos[msg.ObjectName], targetPos) > 0.005f;
                if (firstDragPacket || moved)
                {
                    DWMPHorde.Audio.MovingObjectSoundService.NoteMoving(
                        item.gameObject, msg.ObjectName, dragSounds);
                }
                // While IsDragging, do NOT NoteStationary — keep loop alive like push.
                _lastDragSyncPos[msg.ObjectName] = targetPos;
            }
            else
            {
                // Fallback: no Rigidbody — set transform directly
                item.transform.position = targetPos;
                item.transform.rotation = Quaternion.Euler(targetRot);
                ModRuntime.Log?.LogWarning("[DragSync] " + msg.ObjectName + " has no Rigidbody — used transform fallback");
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[DragSync] " + item.name + " -> " + targetPos);
        }

        /// <summary>Bridge called from WorldPhysicsSyncService when host applies a
        /// client's PhysicsState update for a body-pushed object (not E-drag).
        /// Plays scrape via MOS locally only. Start is NOT broadcast as PlayerAudio —
        /// PhysicsState fan-out already drives NoteMoving on observers (T2: single owner).
        /// Reliable stop still uses <see cref="NotifyBodyPushStopped"/>.</summary>
        public static void NotifyBodyPushStarted(GameObject go)
        {
            if (Instance == null || go == null) return;
            // After ForceStop, ignore late residual restarts (5.2).
            if (DWMPHorde.Audio.ItemMovingSoundHelper.IsScrapeSuppressed(go.name))
                return;

            Item item = go.GetComponent<Item>();
            if (item == null) return;

            // Remote ownership on this peer (host applying client push): MOS only.
            ItemSounds sounds = item.GetComponent<ItemSounds>();
            if (sounds != null)
                DWMPHorde.Audio.MovingObjectSoundService.NoteMoving(item.gameObject, item.gameObject.name, sounds);
            // ponytail: no BroadcastBodyPushSound — PhysicsState NoteMoving covers observers;
            // dual start was the observer double-scrape.
        }

        /// <summary>Bridge called from WorldPhysicsSyncService when a body-pushed
        /// object stops. Force-stops native+MOS scrape and tells peers to stop.</summary>
        public static void NotifyBodyPushStopped(string objectName)
        {
            if (Instance == null) return;
            if (string.IsNullOrEmpty(objectName)) return;

            // All roles: kill residual native ItemSounds + MOS immediately.
            DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(objectName);
            Sync.WorldPhysicsSyncService.TryStopBodyPushSound(objectName);

            if (Instance._role == NetworkRole.Host)
            {
                Instance.Broadcast(NetMessageType.PlayerAudio, w =>
                {
                    new PlayerAudioMessage
                    {
                        IsStopSignal = true,
                        ObjectName = objectName
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>Spawn an item on this peer so the remote drag can be reflected.</summary>
        private Item SpawnDraggedItem(DragSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ItemType))
            {
                ModRuntime.LegacyInfo("[DragSync] no ItemType to spawn \"" + msg.ObjectName + "\"");
                return null;
            }

            if (Singleton<ItemsDatabase>.Instance == null)
            {
                ModRuntime.Log?.LogWarning("[DragSync] ItemsDatabase not available");
                return null;
            }

            if (!Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.LegacyInfo("[DragSync] ItemsDatabase has no item type \"" + msg.ItemType + "\"");
                return null;
            }

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(msg.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
            {
                ModRuntime.LegacyInfo("[DragSync] no prefab for \"" + msg.ItemType + "\"");
                return null;
            }

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
            {
                ModRuntime.LegacyInfo("[DragSync] prefab is not a GameObject for \"" + msg.ItemType + "\"");
                return null;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            GameObject go = Core.AddPrefab(prefab, pos, rot, null);
            if (go == null)
            {
                // Fallback: direct instantiate if Core.AddPrefab fails
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            }

            if (go == null) return null;

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.position = pos;
                rb.rotation = rot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            return go.GetComponent<Item>();
        }

        /// <summary>Destroy a proxy-spawned copy when the remote player stops dragging.</summary>
        private void CleanupSpawnedDragProxy(string objectName)
        {
            if (_spawnedDragProxyItems.Count == 0) return;

            List<int> toRemove = new List<int>();
            foreach (int id in _spawnedDragProxyItems)
            {
                // Find by name as a safety check
                GameObject go = null;
                foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
                {
                    if (candidate.GetInstanceID() == id)
                    {
                        go = candidate.gameObject;
                        break;
                    }
                }

                if (go != null)
                {
                    // Match by name if provided
                    if (!string.IsNullOrEmpty(objectName) && !go.name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ModRuntime.LegacyInfo("[DragSync] destroying proxy-spawned " + go.name);
                    UnityEngine.Object.Destroy(go);
                }
                toRemove.Add(id);
            }

            foreach (int id in toRemove)
                _spawnedDragProxyItems.Remove(id);
        }

        /// <summary>Remove all remote-drag tracking for items matching the given name.
        /// Called when a DragSync with IsDragging=false arrives, so PhysicsState
        /// resumes tracking the item.</summary>
        private void RemoveRemoteDragIds(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;

            // Clean the name-based set (cross-peer check)
            _remoteDragItemNames.Remove(objectName);

            if (_remoteDragItemIds.Count == 0) return;

            // Clean InstanceID-based set (local PhysicsState skip)
            List<int> toRemove = new List<int>();
            foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
            {
                if (candidate == null) continue;
                if (!candidate.gameObject.name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                int id = candidate.GetInstanceID();
                if (_remoteDragItemIds.Contains(id))
                    toRemove.Add(id);
            }
            foreach (int id in toRemove)
                _remoteDragItemIds.Remove(id);
        }

        /// <summary>Release isKinematic on items matching the given name.
        /// Called when a remote drag ends so local physics can affect them again.</summary>
        private void ReleaseRemoteDragKinematic(string objectName)
        {
            if (string.IsNullOrEmpty(objectName) || ModRuntime.Network == null || ModRuntime.Network.Role == NetworkRole.Host)
                return;
            foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
            {
                if (candidate == null) continue;
                if (!candidate.gameObject.name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                Rigidbody rb = candidate.GetComponent<Rigidbody>();
                if (rb != null && rb.isKinematic)
                    rb.isKinematic = false;
                return; // only one item per name needs releasing
            }
        }

        /// <summary>Find a draggable Item on this peer by name and proximity.</summary>
        private Item FindDraggedItemLocally(string name, Vector3 nearPos)
        {
            // Quick check: if the local player has a reference to a dragged item
            // with this name, return it immediately.  Uses itemBeingDragged (not
            // Player.Instance.dragging) because:
            //   1. startDragging may set beingDragged=true before dragging is set
            //   2. force-stop sets dragging=false but leaves itemBeingDragged set,
            //      so subsequent DragSync messages find the item quickly without
            //      falling to OverlapSphere (which skips beingDragged) or the
            //      expensive FindObjectsOfType<Item>() global scan.
            if (Player.Instance != null && Player.Instance.itemBeingDragged != null &&
                !string.IsNullOrEmpty(name) &&
                Player.Instance.itemBeingDragged.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[DragSync] early-return hit for " + name + " (itemBeingDragged)");
                return Player.Instance.itemBeingDragged;
            }

            // Strategy 1: overlap sphere near the reported position (radius 6u
            // to cover the gap between the sent position and the local copy).
            Collider[] nearby = Physics.OverlapSphere(nearPos, 6f);
            Item best = null;
            float bestDist = 6f;
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null || nearby[i].isTrigger) continue;
                Rigidbody rb = nearby[i].attachedRigidbody;
                if (rb == null) continue;
                Item item = rb.GetComponent<Item>();
                if (item == null || !item.draggable) continue;
                if (item.beingDragged) continue; // skip locally-dragged items
                if (!string.IsNullOrEmpty(name) && !item.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                float d = Vector3.Distance(rb.position, nearPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = item;
                }
            }
            if (best != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[DragSync] found " + best.gameObject.name + " near pos (" + bestDist.ToString("F1") + " u)");
                return best;
            }

            // Strategy 2: global scan by name — catches objects on unloaded
            // world-grid chunks or far from the reported position.
            // We do NOT skip locally-dragged items here; HandleDragSync
            // handles that check after finding the item.
            if (!string.IsNullOrEmpty(name))
            {
                Item bestGlobal = null;
                float bestGlobalDist = float.MaxValue;
                foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
                {
                    if (candidate == null || !candidate.draggable) continue;
                    if (candidate.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        float d = Vector3.Distance(candidate.transform.position, nearPos);
                        if (d < bestGlobalDist)
                        {
                            bestGlobalDist = d;
                            bestGlobal = candidate;
                        }
                    }
                }
                if (bestGlobal != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[DragSync] found " + bestGlobal.gameObject.name + " via global scan (" + bestGlobalDist.ToString("F1") + " u)");
                    return bestGlobal;
                }
            }

            return null;
        }








        private void HandleConstructible(ConstructibleMessage msg)
        {
            RegisterConstructedSite(new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.OptionIndex);
            ApplyConstructible(msg, queueIfMissing: true);
        }

        private void ApplyConstructible(ConstructibleMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Constructible best = WorldQueryHelper.FindNearest<Constructible>(pos, 0.75f);
            if (best == null)
            {
                if (queueIfMissing)
                {
                    if (_pendingConstructibles.Count >= MaxPendingConstructibles)
                        _pendingConstructibles.RemoveAt(0);
                    _pendingConstructibles.Add(msg);
                    ModRuntime.LegacyInfo("[ConstructibleSync] queued (not loaded yet) at " + pos);
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[ConstructibleSync] no Constructible found near " + pos);
                }
                return;
            }

            // Already built locally — do not re-fire gameEvent / double-construct.
            if (best.constructed)
            {
                ModRuntime.LegacyInfo("[ConstructibleSync] already constructed " + best.name + " at " + pos);
                return;
            }

            ModRuntime.LegacyInfo("[ConstructibleSync] constructing " + best.name + " at " + pos);
            // Always pass manual=false on the receiving side — the
            // constructing player already consumed ingredients locally.
            // Using manual=true would crash (ConstructionMenu.Instance.
            // selectedIcon is null when the menu isn't open).
            int option = msg.OptionIndex >= 0 ? msg.OptionIndex : best.chosenOption;
            best.construct(false, option);
        }

        /// <summary>Host registry of constructed sites for late-join bulk.</summary>
        internal void RegisterConstructedSite(Vector3 key, int optionIndex)
        {
            string id = ConstructibleSiteKey(key);
            _constructedSites[id] = new ConstructibleMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                UseIngredients = false,
                OptionIndex = optionIndex
            };
        }

        private static string ConstructibleSiteKey(Vector3 key)
        {
            return $"{key.x:F1}_{key.y:F1}_{key.z:F1}";
        }

        /// <summary>Host: push known constructed sites to a joiner (or all if target &lt;= 0).</summary>
        internal void SendConstructedSitesTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;

            // Live registry from this session
            int sent = 0;
            foreach (var kvp in _constructedSites)
            {
                var msg = kvp.Value;
                SendBulkOrAll(NetMessageType.ConstructibleConstruction, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            // Also any Constructible still in scene with constructed==true (save-loaded)
            Constructible[] all = UnityEngine.Object.FindObjectsOfType<Constructible>();
            for (int i = 0; i < all.Length; i++)
            {
                Constructible c = all[i];
                if (c == null || !c.constructed) continue;
                Vector3 p = c.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                string id = ConstructibleSiteKey(key);
                if (_constructedSites.ContainsKey(id)) continue;
                var msg = new ConstructibleMessage
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    UseIngredients = false,
                    OptionIndex = c.chosenOption
                };
                _constructedSites[id] = msg;
                SendBulkOrAll(NetMessageType.ConstructibleConstruction, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} constructible sites to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} constructible sites to all clients");
        }

        private void TryFlushPendingConstructibles()
        {
            if (_pendingConstructibles.Count == 0) return;
            for (int i = _pendingConstructibles.Count - 1; i >= 0; i--)
            {
                var msg = _pendingConstructibles[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Constructible best = WorldQueryHelper.FindNearest<Constructible>(pos, 0.75f);
                if (best == null) continue;
                _pendingConstructibles.RemoveAt(i);
                ApplyConstructible(msg, queueIfMissing: false);
            }
        }

        // Late-join / not-yet-loaded: retry unlock/switch when world objects appear.
        private readonly System.Collections.Generic.List<InteractiveItemSwitchMessage> _pendingInteractive =
            new System.Collections.Generic.List<InteractiveItemSwitchMessage>();
        private readonly System.Collections.Generic.List<PadlockUnlockMessage> _pendingPadlocks =
            new System.Collections.Generic.List<PadlockUnlockMessage>();
        private readonly System.Collections.Generic.List<LockedUnlockMessage> _pendingLocked =
            new System.Collections.Generic.List<LockedUnlockMessage>();
        private const float LockFindRadius = 2.5f;
        private const int MaxPendingLocks = 64;

        private void HandleInteractiveItemSwitch(InteractiveItemSwitchMessage msg)
        {
            ApplyInteractiveItemSwitch(msg, queueIfMissing: true);
        }

        private void ApplyInteractiveItemSwitch(InteractiveItemSwitchMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            InteractiveItem best = WorldQueryHelper.FindNearest<InteractiveItem>(pos, LockFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                    QueuePendingLock(_pendingInteractive, msg, MaxPendingLocks);
                else
                    ModRuntime.Log?.LogWarning("[InteractiveItemSync] no InteractiveItem found near " + pos);
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                if (msg.IsOn && !best.isOn)
                {
                    if (best.onTrigger == null)
                        best.isOn = true;
                    else
                        best.switchOn();
                }
                else if (!msg.IsOn && best.isOn)
                {
                    if (best.offTrigger == null)
                        best.isOn = false;
                    else
                        best.switchOff();
                }
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private void HandlePadlockUnlock(PadlockUnlockMessage msg)
        {
            ApplyPadlockUnlock(msg, queueIfMissing: true);
        }

        private void ApplyPadlockUnlock(PadlockUnlockMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Padlock best = WorldQueryHelper.FindNearest<Padlock>(pos, LockFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                    QueuePendingLock(_pendingPadlocks, msg, MaxPendingLocks);
                else
                    ModRuntime.Log?.LogWarning("[PadlockSync] no Padlock found near " + pos);
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                // manually=false: set locked=false without UI / double triggers
                if (best.locked)
                    best.unlock(false);
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private void HandleLockedUnlock(LockedUnlockMessage msg)
        {
            ApplyLockedUnlock(msg, queueIfMissing: true);
        }

        private void ApplyLockedUnlock(LockedUnlockMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Locked best = WorldQueryHelper.FindNearest<Locked>(pos, LockFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                    QueuePendingLock(_pendingLocked, msg, MaxPendingLocks);
                else
                    ModRuntime.Log?.LogWarning("[LockedSync] no Locked found near " + pos);
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                if (best.locked)
                    best.unlock();
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private static void QueuePendingLock<T>(System.Collections.Generic.List<T> list, T msg, int max)
        {
            if (list.Count >= max)
                list.RemoveAt(0);
            list.Add(msg);
        }

        /// <summary>Flush padlock/locked/interactive pending when world objects appear.</summary>
        internal void TryFlushPendingLocks()
        {
            for (int i = _pendingPadlocks.Count - 1; i >= 0; i--)
            {
                var msg = _pendingPadlocks[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<Padlock>(pos, LockFindRadius) == null)
                    continue;
                _pendingPadlocks.RemoveAt(i);
                ApplyPadlockUnlock(msg, queueIfMissing: false);
            }
            for (int i = _pendingLocked.Count - 1; i >= 0; i--)
            {
                var msg = _pendingLocked[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<Locked>(pos, LockFindRadius) == null)
                    continue;
                _pendingLocked.RemoveAt(i);
                ApplyLockedUnlock(msg, queueIfMissing: false);
            }
            for (int i = _pendingInteractive.Count - 1; i >= 0; i--)
            {
                var msg = _pendingInteractive[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<InteractiveItem>(pos, LockFindRadius) == null)
                    continue;
                _pendingInteractive.RemoveAt(i);
                ApplyInteractiveItemSwitch(msg, queueIfMissing: false);
            }
        }

        /// <summary>
        /// Host join bulk: unlocked padlocks/doors and interactive isOn state for late joiners.
        /// </summary>
        private void SyncExistingLocksAndInteractives(int targetPlayerId)
        {
            if (_role != NetworkRole.Host || targetPlayerId <= 0) return;

            int padlocks = 0, locked = 0, interactive = 0;

            Padlock[] pads = Resources.FindObjectsOfTypeAll<Padlock>();
            for (int i = 0; i < pads.Length; i++)
            {
                Padlock p = pads[i];
                if (p == null || p.locked) continue;
                if (p.gameObject == null || !p.gameObject.scene.IsValid()) continue;
                Vector3 pos = p.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                SendToPlayer(targetPlayerId, NetMessageType.PadlockUnlock,
                    w => new PadlockUnlockMessage { PosX = key.x, PosY = key.y, PosZ = key.z }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                padlocks++;
            }

            Locked[] locks = Resources.FindObjectsOfTypeAll<Locked>();
            for (int i = 0; i < locks.Length; i++)
            {
                Locked l = locks[i];
                if (l == null || l.locked) continue;
                if (l.gameObject == null || !l.gameObject.scene.IsValid()) continue;
                Vector3 pos = l.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                SendToPlayer(targetPlayerId, NetMessageType.LockedUnlock,
                    w => new LockedUnlockMessage { PosX = key.x, PosY = key.y, PosZ = key.z }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                locked++;
            }

            InteractiveItem[] items = Resources.FindObjectsOfTypeAll<InteractiveItem>();
            for (int i = 0; i < items.Length; i++)
            {
                InteractiveItem ii = items[i];
                if (ii == null || !ii.isOn) continue;
                if (ii.gameObject == null || !ii.gameObject.scene.IsValid()) continue;
                Vector3 pos = ii.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                SendToPlayer(targetPlayerId, NetMessageType.InteractiveItemSwitch,
                    w => new InteractiveItemSwitchMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z, IsOn = true
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                interactive++;
            }

            if (padlocks + locked + interactive > 0)
                ModRuntime.LegacyInfo(
                    $"[BulkSync] Locks/interactives → p{targetPlayerId}: pad={padlocks} locked={locked} on={interactive}");
        }

        /// <summary>
        /// Host → new peer: all world lights/switchables currently isOn so late join
        /// matches hideout lamps without waiting for a toggle or generator event.
        /// </summary>
        private void SyncExistingWorldLightsTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host || targetPlayerId <= 0) return;

            Item[] items = Resources.FindObjectsOfTypeAll<Item>();
            int sent = 0;
            const int maxSend = 256;
            for (int i = 0; i < items.Length && sent < maxSend; i++)
            {
                Item item = items[i];
                if (item == null || item.gameObject == null || !item.gameObject.scene.IsValid())
                    continue;
                if (!item.isLight && !item.switchable)
                    continue;
                if (!item.isOn)
                    continue;
                if (item.GetComponent<Generator>() != null)
                    continue;

                Vector3 p = item.transform.position;
                string itemType = item.invItem != null ? item.invItem.type : "";
                string itemName = item.name ?? "";
                SendToPlayer(targetPlayerId, NetMessageType.LightState,
                    w => new LightStateMessage
                    {
                        PosX = p.x,
                        PosY = p.y,
                        PosZ = p.z,
                        IsOn = true,
                        ItemName = itemName,
                        ItemType = itemType
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0 || Config.ModConfig.IsVerboseLightSync)
                ModRuntime.LegacyInfo($"[BulkSync] World lights → p{targetPlayerId}: on={sent}");
        }

        private void HandleGameEventsFired(GameEventsFiredMessage msg)
        {
            if (_role != NetworkRole.Client)
                return;
            ApplyGameEventsFired(msg, queueIfMissing: true);
        }

        private void ApplyGameEventsFired(GameEventsFiredMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            GameEvents best = null;

            // Prefer name match within a generous radius (story events can sit close together).
            if (!string.IsNullOrEmpty(msg.EventName))
                best = WorldQueryHelper.FindNearestByName<GameEvents>(pos, msg.EventName, 3f);
            if (best == null)
                best = WorldQueryHelper.FindNearest<GameEvents>(pos, 2.5f);

            if (best == null)
            {
                if (queueIfMissing)
                {
                    QueuePendingGameEvent(msg);
                    return;
                }
                ModRuntime.Log?.LogWarning(
                    $"[GameEventsSync] no GameEvents near {pos} name='{msg.EventName}'");
                return;
            }

            // fire() under NetworkApplyGuard (receive path) — host re-broadcast suppressed.
            // Vanilla fired+!multipleFire guard prevents double-fire if client already ran it.
            best.fire();
        }

        private void QueuePendingGameEvent(GameEventsFiredMessage msg)
        {
            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var p = _pendingGameEvents[i];
                if (Mathf.Abs(p.PosX - msg.PosX) < 0.2f
                    && Mathf.Abs(p.PosY - msg.PosY) < 0.2f
                    && Mathf.Abs(p.PosZ - msg.PosZ) < 0.2f
                    && string.Equals(p.EventName, msg.EventName, System.StringComparison.Ordinal))
                    _pendingGameEvents.RemoveAt(i);
            }
            if (_pendingGameEvents.Count >= MaxPendingGameEvents)
                _pendingGameEvents.RemoveAt(0);
            _pendingGameEvents.Add(msg);
            ModRuntime.LegacyInfo(
                $"[GameEventsSync] queued (not loaded) at ({msg.PosX:F1},{msg.PosZ:F1}) name={msg.EventName}");
        }

        private void TryFlushPendingGameEvents()
        {
            if (_role != NetworkRole.Client || _pendingGameEvents.Count == 0)
                return;
            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingGameEvents[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                GameEvents found = null;
                if (!string.IsNullOrEmpty(msg.EventName))
                    found = WorldQueryHelper.FindNearestByName<GameEvents>(pos, msg.EventName, 3f);
                if (found == null)
                    found = WorldQueryHelper.FindNearest<GameEvents>(pos, 2.5f);
                if (found == null) continue;
                _pendingGameEvents.RemoveAt(i);
                ApplyGameEventsFired(msg, queueIfMissing: false);
            }
        }

        private void HandleHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ExperienceMachine best = WorldQueryHelper.FindNearest<ExperienceMachine>(pos, 0.5f);
            if (best == null)
            {
                ModRuntime.Log?.LogWarning("[HideoutUpgrade] no ExperienceMachine found near " + pos);
                return;
            }

            if (msg.IsOn && !best.isOn)
                best.enable();
            else if (!msg.IsOn && best.isOn)
                best.disable();
        }

        private void HandleContainerItem(ContainerItemMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Inventory inv = FindInventoryByPos(pos);
            if (inv == null)
            {
                ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: no inventory at {pos} for {msg.Action} slot={msg.SlotIndex} type={msg.ItemType}");
                return;
            }

            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: {msg.Action} inv={inv.name} type={inv.invType} pos={pos} slot={msg.SlotIndex} type={msg.ItemType} amt={msg.Amount}");

            if (msg.Action == ContainerAction.TakeItem || msg.Action == ContainerAction.RemoveItem)
            {
                if (msg.SlotIndex < inv.slots.Count)
                {
                    InvSlot slot = inv.slots[msg.SlotIndex];
                    if (!InvItemClass.isNull(slot.invItem))
                    {
                        if (msg.Amount >= slot.invItem.amount)
                        {
                            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: removing {slot.invItem.type} x{slot.invItem.amount} from slot {msg.SlotIndex}");
                            slot.removeItem();
                        }
                        else
                        {
                            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: removing {msg.Amount} from {slot.invItem.type} (had {slot.invItem.amount})");
                            slot.invItem.removeAmount(msg.Amount);
                        }
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: slot {msg.SlotIndex} already empty (type={msg.ItemType})");
                    }
                }
                else
                {
                    ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: slot index {msg.SlotIndex} >= slots count {inv.slots.Count}");
                }
            }
            else if (msg.Action == ContainerAction.PlaceItem)
            {
                if (msg.IsPlayerPlaced)
                    Patches.ItemDoublePickupPatch.MarkContainerSlotPlayerPlaced(pos, msg.SlotIndex);

                if (msg.SlotIndex < inv.slots.Count)
                {
                    InvSlot slot = inv.slots[msg.SlotIndex];
                    if (InvItemClass.isNull(slot.invItem))
                    {
                        slot.createItem(msg.ItemType, msg.Amount, msg.Durability > 0f ? msg.Durability : 1f);
                        if (msg.Ammo > 0 && !InvItemClass.isNull(slot.invItem))
                            slot.invItem.ammo = msg.Ammo;
                    }
                    else if (slot.invItem.type == msg.ItemType)
                    {
                        slot.invItem.amount += msg.Amount;
                        slot.invItem.refresh();
                    }
                }
            }
            else if (msg.Action == ContainerAction.Searched)
            {
                Item item = inv.GetComponent<Item>();
                if (item != null)
                {
                    item.searched = true;
                    Character c = inv.GetComponent<Character>();
                    if (c != null)
                        c.searched = true;
                }
            }
        }

        /// <summary>
        /// Client->Host: client requests the current state of a container inventory.
        /// Host captures all non-empty slots and sends ContainerStateSync.
        /// </summary>
        private void HandleContainerStateRequest(ContainerStateRequestMessage msg)
        {
            if (_role != NetworkRole.Host) return;

            ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: hash={msg.TargetEntityHash} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1})");

            // Try exact entity hash lookup first
            Inventory inv = null;
            if (msg.TargetEntityHash > 0)
            {
                Character c = CharacterTracker.FindByStableId((short)msg.TargetEntityHash);
                if (c != null)
                {
                    InvItemClass held = HarmonyLib.Traverse.Create(c).Field("currentItem").GetValue<InvItemClass>();
                    inv = c.GetComponent<Inventory>();
                    if (inv == null)
                        ModRuntime.LegacyInfo($"[Container] entity hash lookup found '{c.name}' but no Inventory component");
                    else
                        ModRuntime.LegacyInfo($"[Container] entity hash lookup OK: '{c.name}' invType={inv.invType} slots={inv.slots.Count}");
                }
                else
                {
                    ModRuntime.LegacyInfo($"[Container] entity hash {msg.TargetEntityHash} not found, falling back to position");
                }
            }

            if (inv == null)
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                inv = FindInventoryByPos(pos);
                if (inv == null)
                {
                    // Final fallback: scan dead Characters near the position — a dead
                    // body may have a disabled collider or the stable ID may not match.
                    Character[] allChars = UnityEngine.Object.FindObjectsOfType<Character>();
                    Character closestDead = null;
                    float closestDeadDist = 10f;
                    foreach (Character c in allChars)
                    {
                        if (c == null || c.alive) continue;
                        if (c.GetComponent<Inventory>() == null) continue;
                        float d = Vector3.Distance(c.transform.position, pos);
                        if (d < closestDeadDist)
                        {
                            closestDeadDist = d;
                            closestDead = c;
                        }
                    }
                    if (closestDead != null)
                    {
                        inv = closestDead.GetComponent<Inventory>();
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: found dead '{closestDead.name}' at {closestDeadDist:F1}m from request pos");
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning($"[Container] HandleContainerStateRequest: no inventory at ({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) hash={msg.TargetEntityHash}");
                        return;
                    }
                }
                else
                {
                    ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: found by pos: '{inv.name}' invType={inv.invType} slots={inv.slots.Count}");
                }
            }

            // Count non-empty slots
            int count = 0;
            for (int i = 0; i < inv.slots.Count; i++)
            {
                if (!InvItemClass.isNull(inv.slots[i].invItem))
                    count++;
            }

            ModRuntime.LegacyInfo($"[Container] HandleContainerStateRequest: responding with {count} items");

            short entityHash = 0;
            Character ownerChar = inv.GetComponent<Character>();
            if (ownerChar != null)
                entityHash = CharacterTracker.GetStableId(ownerChar);

            var sync = new ContainerStateSyncMessage
            {
                PosX = msg.PosX,
                PosY = msg.PosY,
                PosZ = msg.PosZ,
                EntityHash = entityHash,
                SlotCount = count,
                Slots = new SlotStateEntry[count]
            };
            int idx = 0;
            for (int i = 0; i < inv.slots.Count; i++)
            {
                var slot = inv.slots[i];
                if (!InvItemClass.isNull(slot.invItem))
                {
                    sync.Slots[idx++] = new SlotStateEntry
                    {
                        SlotIndex = (byte)i,
                        ItemType = slot.invItem.type,
                        Amount = slot.invItem.amount,
                        Durability = slot.invItem.durability,
                        Ammo = slot.invItem.ammo
                    };
                }
            }
            // Full snapshot only to the requester — Broadcast would wipe other
            // clients' mid-loot views (3+ and dual-open races).
            int requester = _currentReceivePlayerId;
            if (requester > 0)
            {
                SendToPlayer(requester, NetMessageType.ContainerStateSync,
                    w => sync.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            else
            {
                Broadcast(NetMessageType.ContainerStateSync, w => sync.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }

            // Shared world: mark container searched for everyone (hover text).
            Item openItem = inv.GetComponent<Item>();
            if (openItem != null && !openItem.searched)
            {
                openItem.searched = true;
                Character oc = inv.GetComponent<Character>();
                if (oc != null) oc.searched = true;
                Broadcast(NetMessageType.ContainerItem, w => new ContainerItemMessage
                {
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    Action = ContainerAction.Searched,
                    SlotIndex = 0,
                    ItemType = "",
                    Amount = 0,
                    Durability = 0,
                    Ammo = 0
                }.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>
        /// Host->Client: full container state snapshot.
        /// Clears all slots on the client container and recreates them from
        /// the host's authoritative slot data.
        /// </summary>
        private void HandleContainerStateSync(ContainerStateSyncMessage msg)
        {
            if (_role != NetworkRole.Client) return;

            ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: hash={msg.EntityHash} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) slotCount={msg.SlotCount}");

            // Try exact entity hash lookup first
            Inventory inv = null;
            if (msg.EntityHash > 0)
            {
                Character c = CharacterTracker.FindByStableId((short)msg.EntityHash);
                if (c != null)
                {
                    inv = c.GetComponent<Inventory>();
                    if (inv == null)
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity hash {msg.EntityHash} found '{c.name}' but no Inventory");
                    else
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity hash OK: '{c.name}' invType={inv.invType}");
                }
                else
                {
                    ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity hash {msg.EntityHash} not found, falling back to position");
                }
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (inv == null)
            {
                inv = FindInventoryByPos(pos);
                if (inv == null)
                {
                    ModRuntime.Log?.LogWarning($"[Container] HandleContainerStateSync: no inventory at ({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) hash={msg.EntityHash}");
                    return;
                }
                ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: found by pos: '{inv.name}' invType={inv.invType}");
            }

            // Log current client state before overwriting
            int beforeCount = 0;
            for (int i = 0; i < inv.slots.Count; i++)
                if (!InvItemClass.isNull(inv.slots[i].invItem)) beforeCount++;
            ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: client had {beforeCount} items, host says {msg.SlotCount} items — overwriting");

            // Check for pending local removes (dupe prevention): if the player
            // already took items from this container before the sync arrived,
            // don't re-add them.
            string containerKey = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}";
            _pendingContainerRemoves.TryGetValue(containerKey, out var pendingSlots);

            // Clear all slots on the client container
            foreach (var slot in inv.slots)
            {
                if (!InvItemClass.isNull(slot.invItem))
                    slot.removeItem();
            }

            // Recreate slots from host data, skipping slots with pending removes
            for (int i = 0; i < msg.SlotCount; i++)
            {
                var entry = msg.Slots[i];
                if (entry.SlotIndex >= inv.slots.Count || string.IsNullOrEmpty(entry.ItemType))
                    continue;

                // Skip slots that the player has pending local removal for
                if (pendingSlots != null && pendingSlots.Contains(entry.SlotIndex))
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: skipping slot {entry.SlotIndex} ({entry.ItemType}) — pending local removal");
                    continue;
                }

                inv.slots[entry.SlotIndex].createItem(entry.ItemType, entry.Amount,
                    entry.Durability > 0f ? entry.Durability : 1f);
                if (entry.Ammo > 0)
                {
                    var item = inv.slots[entry.SlotIndex].invItem;
                    if (!InvItemClass.isNull(item))
                        item.ammo = entry.Ammo;
                }
            }

            // Clean up pending tracking for this container. The items we skipped
            // are already in the player's inventory; the RemoveItem is in transit
            // and the host will process it shortly. On the next open (next sync),
            // the host's state will correctly reflect the removed items.
            if (pendingSlots != null)
                _pendingContainerRemoves.Remove(containerKey);

            // Play container sound on remote so they hear the open
            AudioController.Play("open_drawer", new Vector3(msg.PosX, msg.PosY, msg.PosZ));
        }

        /// <summary>
        /// Apply shared NPC reputation (model C). Host and clients both apply;
        /// night-trader names are ignored (per-player). Writes Flags.npcStates
        /// directly so it works if the NPC GameObject is not loaded yet.
        /// </summary>
        private void HandleReputationSync(ReputationSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;
            if (Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(msg.NpcName))
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            var state = flags.getNPCState(msg.NpcName);
            if (state != null)
            {
                state.reputation = msg.Reputation;
            }
            else
            {
                state = new Flags.NPCState
                {
                    name = msg.NpcName,
                    reputation = msg.Reputation,
                    wantsToTalk = true
                };
                flags.npcStates.Add(state);
            }

            ModRuntime.LegacyInfo($"[RepSync] applied shared rep '{msg.NpcName}': {msg.Reputation}");
        }

        private void HandleDoorOpen(DoorOpenMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Door door = Sync.DoorTracker.FindByPosition(pos);
            if (door == null)
            {
                // Fallback: search by name
                Door[] all = UnityEngine.Object.FindObjectsOfType<Door>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].name == msg.DoorName)
                    {
                        door = all[i];
                        break;
                    }
                }
            }
            if (door == null)
            {
                ModRuntime.Log?.LogWarning($"[DoorSync] Door '{msg.DoorName}' not found at {pos}");
                return;
            }

            if (door.opened) return;

            door.open(pos, null);

            ModRuntime.LegacyInfo($"[DoorSync] opened door '{msg.DoorName}' at {pos}");
        }

        /// <summary>
        /// Remote peers currently inside an OutsideLocation (name). Used to:
        /// - only snap proxy to playerSpawn on first enter (not on ~1 Hz retries)
        /// - bulk-sync location membership to late joiners
        /// </summary>
        private readonly Dictionary<int, string> _remoteOutsideLocation = new Dictionary<int, string>();

        private void HandleLocationEnter(LocationEnterMessage msg)
        {
            if (string.IsNullOrEmpty(msg.LocationName))
                return;

            int playerId = msg.PlayerId > 0 ? msg.PlayerId : _currentReceivePlayerId;
            if (playerId <= 0)
                return;

            ModRuntime.LegacyInfo($"[LocationSync] player {playerId} entered location: {msg.LocationName}");

            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null) return;

            bool firstEnterThisLoc = !_remoteOutsideLocation.TryGetValue(playerId, out string prev)
                || !string.Equals(prev, msg.LocationName, StringComparison.OrdinalIgnoreCase);
            _remoteOutsideLocation[playerId] = msg.LocationName;

            // Ensure location geometry is active for the remote proxy only.
            // Do NOT transport the local player (Location.enter just activates children).
            if (ol.spawnedLocations.ContainsKey(msg.LocationName))
            {
                var loc = ol.spawnedLocations[msg.LocationName];
                loc.enter();

                // Only snap to spawn on first enter / location change.
                // ~1 Hz LocationEnter retries used to reset proxy to spawn every second,
                // fighting PlayerState and looking broken in basements/bunkers/villages.
                if (firstEnterThisLoc && loc.playerSpawn != null)
                {
                    TeleportRemoteProxyTo(loc.playerSpawn.transform.position, playerId: playerId);
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] first enter — teleported p{playerId} proxy to {loc.name} playerSpawn");
                }
            }
            else
            {
                // Async spawn; ~1 Hz LocationEnter retries until key exists.
                // Keep firstEnter pending by clearing so next successful enter still snaps once.
                if (firstEnterThisLoc)
                    _remoteOutsideLocation.Remove(playerId);
                ModRuntime.LegacyInfo($"[LocationSync] location not spawned, creating: {msg.LocationName}");
                ol.createLocation(msg.LocationName);
            }
        }

        private void HandleLocationExit(LocationExitMessage msg)
        {
            int playerId = msg.PlayerId > 0 ? msg.PlayerId : _currentReceivePlayerId;
            if (playerId <= 0)
                return;

            _remoteOutsideLocation.Remove(playerId);

            // Never leaveAllLocations() — that deactivates locations the LOCAL player
            // may still be inside (2p/3+ desync / blackout).
            Vector3 worldPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            TeleportRemoteProxyTo(worldPos, playerId: playerId);
            ModRuntime.LegacyInfo($"[LocationSync] player {playerId} exited → proxy at {worldPos}");
        }

        /// <summary>
        /// Late join: push which remotes (and host) are inside OutsideLocations so geometry
        /// is active and proxies can be placed. PlayerState then refines position.
        /// </summary>
        private void SyncExistingLocationsTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host || targetPlayerId <= 0) return;

            int sent = 0;

            // Host's own outside location
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol != null && ol.playerInOutsideLocation
                && !string.IsNullOrEmpty(ol.currentLocationName))
            {
                string hostLoc = ol.currentLocationName;
                SendToPlayer(targetPlayerId, NetMessageType.LocationEnter,
                    w => new LocationEnterMessage
                    {
                        LocationName = hostLoc,
                        PlayerId = 1
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            foreach (var kvp in _remoteOutsideLocation)
            {
                if (kvp.Key == targetPlayerId) continue;
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                int pid = kvp.Key;
                string name = kvp.Value;
                SendToPlayer(targetPlayerId, NetMessageType.LocationEnter,
                    w => new LocationEnterMessage
                    {
                        LocationName = name,
                        PlayerId = pid
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0)
                ModRuntime.LegacyInfo($"[BulkSync] LocationEnter x{sent} → p{targetPlayerId}");
        }

        private void HandleEntitySpawn(EntitySpawnMessage msg)
        {
            if (string.IsNullOrEmpty(msg.PrefabPath))
                return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            try
            {
                GameObject go = Core.AddPrefab(msg.PrefabPath, pos, rot, null);
                if (go != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Physics] spawned: {msg.PrefabPath} at {pos}");
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[PhysicsSpawnSync] failed to spawn {msg.PrefabPath}: {ex}");
            }

            // Client-originated spawn: host already applied; relay to other clients (3+).
            if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
            {
                SendToAllExcept(_currentReceivePlayerId, NetMessageType.EntitySpawn,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }

        // P1.4: debounce duplicate trap triggers from multi-collider / double-send
        private readonly Dictionary<string, float> _trapTriggerDebounce = new Dictionary<string, float>();
        private const float TrapTriggerDebounceSec = 0.4f;

        private void HandleTrapTriggered(TrapTriggeredMessage msg)
        {
            if (_role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            string debounceKey = $"{msg.PosX:F1}_{msg.PosY:F1}_{msg.PosZ:F1}";
            float now = Time.time;
            if (_trapTriggerDebounce.TryGetValue(debounceKey, out float last) && now - last < TrapTriggerDebounceSec)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[TrapTrigger] Host: debounced duplicate at {pos}");
                return;
            }
            _trapTriggerDebounce[debounceKey] = now;
            if (_trapTriggerDebounce.Count > 64)
            {
                var stale = new List<string>();
                foreach (var kvp in _trapTriggerDebounce)
                    if (now - kvp.Value > 5f) stale.Add(kvp.Key);
                foreach (var k in stale) _trapTriggerDebounce.Remove(k);
            }

            GameObject go = WorldPhysicsSyncService.FindTrapByPos(pos);
            if (go == null)
            {
                ModRuntime.LegacyInfo($"[TrapTrigger] Host: no trap found at {pos} (may be outside loaded range)");
                return;
            }

            // Apply triggered state — the next SyncTraps() broadcast will
            // pick up the change and relay it to both sides.
            WorldPhysicsSyncService.ApplyTrapState(go, triggered: true);
            // Broadcast trap activation sound to remote peers
            var triggerSnd = go.GetComponent<Trigger>();
            if (triggerSnd != null && !string.IsNullOrEmpty(triggerSnd.activateSound))
                AudioController.Play(triggerSnd.activateSound, pos);
            ModRuntime.LegacyInfo($"[TrapTrigger] Host: applied triggered=true to {go.name} at {pos}");
        }

        private void HandleBarricadeEvent(BarricadeEventMessage msg)
        {
            ApplyBarricadeEvent(msg, queueIfMissing: true);
        }

        private void ApplyBarricadeEvent(BarricadeEventMessage msg, bool queueIfMissing)
        {
            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] HANDLE type={msg.IsWindow} act={msg.Action} hp={msg.Health} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) mainHp={msg.MainHealth}");
            _processingBarricadeEvent = true;
            try
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

                if (msg.IsWindow == 2)
                {
                    HandleItemDamageEvent(pos, msg);
                    return;
                }

                if (msg.IsWindow == 0)
                {
                    Door door = FindDoorByPos(pos);
                    if (door == null)
                    {
                        if (queueIfMissing)
                            QueuePendingBarricade(msg);
                        else if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Barr] door not found at {pos}");
                        return;
                    }
                    if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] found door={door.name} barricaded={door.barricaded} hp={door.barricadeHealth}");

                    if (msg.Action == BarricadeAction.Built)
                    {
                        door.playerBarricade = msg.PlayerBarricade;
                        // Health == 0 means the door was restored from destroyed
                        // (player built a new door in an empty doorway, no barricade).
                        if (msg.Health <= 0 && door.destroyed)
                        {
                            door.unDestroy();
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door restored from destroyed");
                        }
                        else
                        {
                            // Normal barricade build. setToBarricaded -> setBarricadeState.
                            // If door was destroyed, first call only restores (unDestroy),
                            // second call applies the barricade.
                            door.setToBarricaded();
                            if (!door.barricaded)
                                door.setToBarricaded();
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door barricade applied (destroyed={door.destroyed} barricaded={door.barricaded})");
                        }
                    }
                    else
                    {
                        // Bulk/state snapshots use DamageAmount < 0 (no combat hit FX).
                        // Client redirect registers a short suppress so striker does not double FX.
                        bool playCombatFx = msg.DamageAmount >= 0
                            && !DWMPHorde.Patches.ClientWorldMeleeRedirectHelper.ShouldSuppressApplyFx(0, pos);

                        // Apply barricade state changes
                        if (msg.Action == BarricadeAction.Destroyed)
                        {
                            if (door.barricaded)
                                door.destroyBarricade(silent: true);
                            if (playCombatFx)
                                AudioController.Play("woodenObject_destroy", door.body?.position ?? pos);
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door destroyed");
                        }
                        else if (msg.Action == BarricadeAction.Damaged)
                        {
                            if (door.barricaded)
                            {
                                door.barricadeHealth = msg.Health;
                                if (door.barricadeHealth <= 0)
                                    door.destroyBarricade(silent: true);
                            }
                            if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door damaged hp={msg.Health}");
                        }

                        // Apply main health changes + optional FX
                        if (msg.MainHealth >= 0)
                        {
                            if (msg.MainHealth <= 0 && !door.destroyed)
                            {
                                door.destroyDoor();
                                if (playCombatFx)
                                    AudioController.Play("woodenObject_destroy", door.body?.position ?? pos);
                                if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] door main destroyed");
                            }
                            else
                            {
                                Traverse.Create(door).Field("health").SetValue(msg.MainHealth);
                                if (playCombatFx && door.body != null)
                                {
                                    Core.AddPrefab("particles/door_hit_melee", door.body.position,
                                        Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                                    AudioController.Play("woodenObject_hit", door.body);
                                }
                            }
                        }

                        // Apply door-swing physics when an open, unbarricaded door is melee-hit.
                        // Mirrors vanilla Door.getHit(): bodyRB.AddForce(vector.normalized * -50000f)
                        // using the attacker position captured in DoorGetHitPatch.
                        if (msg.HasAttackerPos && door.opened && !door.destroyed && !door.barricaded)
                        {
                            Vector3 doorPos = door.body != null ? door.body.position : door.transform.position;
                            Vector3 forceDir = (new Vector3(msg.AttackerPosX, msg.AttackerPosY, msg.AttackerPosZ) - doorPos).normalized * -50000f;
                            Rigidbody doorRB = Traverse.Create(door).Field("bodyRB").GetValue<Rigidbody>();
                            if (doorRB != null)
                                doorRB.AddForce(forceDir);
                        }
                    }
                }
                else
                {
                    // Window (type 1)
                    Window window = FindWindowByPos(pos);
                    if (window == null)
                    {
                        if (queueIfMissing)
                            QueuePendingBarricade(msg);
                        else if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Barr] window not found at {pos}");
                        return;
                    }
                    if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] found window={window.name} barricaded={window.barricaded} hp={window.barricadeHealth}");

                    // B3: vanilla setBarricadeState / destroyBarricade for graph tags + sprites
                    if (msg.Action == BarricadeAction.Built)
                    {
                        window.barricadeState = 3;
                        // destHealth 0 => full max in vanilla; join bulk sends actual HP (>0 when boarded).
                        // byPlayer=false avoids gainSaturation / construction side effects on remote apply.
                        int destHp = msg.Health > 0 ? msg.Health : 0;
                        window.setBarricadeState(destHp, byPlayer: false);
                        window.playerBarricade = msg.PlayerBarricade;
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] window barricade via setBarricadeState hp={destHp}");
                    }
                    else if (msg.Action == BarricadeAction.Destroyed)
                    {
                        if (window.barricaded)
                            window.destroyBarricade(silent: true);
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] window destroyBarricade");
                    }
                    else if (msg.Action == BarricadeAction.Damaged)
                    {
                        if (msg.Health <= 0)
                        {
                            if (window.barricaded)
                                window.destroyBarricade(silent: true);
                        }
                        else if (window.barricaded)
                        {
                            window.barricadeHealth = msg.Health;
                        }
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo($"[Barr] window damaged/destroyed hp={msg.Health}");
                    }
                }
            }
            finally { _processingBarricadeEvent = false; }
        }

        private void QueuePendingBarricade(BarricadeEventMessage msg)
        {
            // Replace same rounded position + type
            for (int i = _pendingBarricadeEvents.Count - 1; i >= 0; i--)
            {
                var p = _pendingBarricadeEvents[i];
                if (p.IsWindow == msg.IsWindow
                    && Mathf.Abs(p.PosX - msg.PosX) < 0.15f
                    && Mathf.Abs(p.PosY - msg.PosY) < 0.15f
                    && Mathf.Abs(p.PosZ - msg.PosZ) < 0.15f)
                    _pendingBarricadeEvents.RemoveAt(i);
            }
            if (_pendingBarricadeEvents.Count >= MaxPendingBarricadeEvents)
                _pendingBarricadeEvents.RemoveAt(0);
            _pendingBarricadeEvents.Add(msg);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Barr] queued event type={msg.IsWindow} act={msg.Action}");
        }

        private void TryFlushPendingBarricadeEvents()
        {
            if (_pendingBarricadeEvents.Count == 0) return;
            for (int i = _pendingBarricadeEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingBarricadeEvents[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                bool found = msg.IsWindow == 0
                    ? FindDoorByPos(pos) != null
                    : msg.IsWindow == 1 && FindWindowByPos(pos) != null;
                if (!found) continue;
                _pendingBarricadeEvents.RemoveAt(i);
                ApplyBarricadeEvent(msg, queueIfMissing: false);
            }
        }

        /// <summary>
        /// Host: push barricade / door / furniture state so late joiners match
        /// host fortifications (including partial main door HP and item health).
        /// DamageAmount = -1 marks bulk/state (no combat hit FX on apply).
        /// </summary>
        internal void SendBarricadeStateTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;

            int sent = 0;
            const int maxSend = 512;

            Door[] doors = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < doors.Length && sent < maxSend; i++)
            {
                Door door = doors[i];
                if (door == null) continue;

                Vector3 p = door.transform.position;
                Vector3 key = new Vector3(
                    (float)System.Math.Round(p.x, 1),
                    (float)System.Math.Round(p.y, 1),
                    (float)System.Math.Round(p.z, 1));

                if (door.destroyed)
                {
                    var msg = new BarricadeEventMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z,
                        IsWindow = 0,
                        Action = BarricadeAction.Destroyed,
                        Health = 0,
                        PlayerBarricade = false,
                        MainHealth = 0,
                        DamageAmount = -1
                    };
                    SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                    sent++;
                }
                else if (door.barricaded)
                {
                    var msg = new BarricadeEventMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z,
                        IsWindow = 0,
                        Action = BarricadeAction.Built,
                        Health = door.barricadeHealth,
                        PlayerBarricade = door.playerBarricade,
                        MainHealth = door.health,
                        DamageAmount = -1
                    };
                    SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                    sent++;
                }
                else if (door.baseHealth > 0 && door.health < door.baseHealth)
                {
                    // B1: partial main HP on unboarded door (not destroyed)
                    var msg = new BarricadeEventMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z,
                        IsWindow = 0,
                        Action = BarricadeAction.Damaged,
                        Health = 0,
                        PlayerBarricade = false,
                        MainHealth = door.health,
                        DamageAmount = -1
                    };
                    SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                    sent++;
                }
            }

            Window[] windows = UnityEngine.Object.FindObjectsOfType<Window>();
            for (int i = 0; i < windows.Length && sent < maxSend; i++)
            {
                Window window = windows[i];
                if (window == null || !window.barricaded) continue;

                Vector3 p = window.transform.position;
                Vector3 key = new Vector3(
                    (float)System.Math.Round(p.x, 1),
                    (float)System.Math.Round(p.y, 1),
                    (float)System.Math.Round(p.z, 1));

                var msg = new BarricadeEventMessage
                {
                    PosX = key.x, PosY = key.y, PosZ = key.z,
                    IsWindow = 1,
                    Action = BarricadeAction.Built,
                    Health = window.barricadeHealth,
                    PlayerBarricade = window.playerBarricade,
                    MainHealth = -1,
                    DamageAmount = -1
                };
                SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            // B2: destructible furniture — absolute HP snapshot (or destroyed)
            int itemSent = 0;
            const int maxItems = 256;
            Item[] items = UnityEngine.Object.FindObjectsOfType<Item>();
            for (int i = 0; i < items.Length && itemSent < maxItems && sent < maxSend; i++)
            {
                Item item = items[i];
                if (item == null || item.gameObject == null || !item.gameObject.scene.IsValid())
                    continue;
                if (!item.destructible)
                    continue;

                bool needSync = item.destroyed
                    || (item.maxHealth > 0 && item.health < item.maxHealth);
                if (!needSync)
                    continue;

                Vector3 p = item.transform.position;
                Vector3 key = new Vector3(
                    (float)System.Math.Round(p.x, 1),
                    (float)System.Math.Round(p.y, 1),
                    (float)System.Math.Round(p.z, 1));

                var msg = new BarricadeEventMessage
                {
                    PosX = key.x, PosY = key.y, PosZ = key.z,
                    IsWindow = 2,
                    Action = item.destroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                    Health = item.destroyed ? 0 : item.health,
                    PlayerBarricade = false,
                    MainHealth = -1,
                    DamageAmount = -1
                };
                SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
                itemSent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} barricade/door/item states to player {targetPlayerId} (items={itemSent})"
                : $"[BulkSync] Sent {sent} barricade/door/item states to all clients (items={itemSent})");
        }

        private void HandleItemDamageEvent(Vector3 pos, BarricadeEventMessage msg)
        {
            Item item = null;
            Collider[] nearby = Physics.OverlapSphere(pos, 2f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                item = nearby[i].GetComponentInParent<Item>();
                if (item != null) break;
            }

            if (item == null)
            {
                // Fallback: scan all items by position (handles cases where the
                // item's collider is disabled or the item moved after death).
                Item best = null;
                float bestDist = 5f;
                Item[] all = UnityEngine.Object.FindObjectsOfType<Item>();
                for (int i = 0; i < all.Length; i++)
                {
                    Item candidate = all[i];
                    if (candidate == null || !candidate.destructible) continue;
                    float d = Vector3.Distance(candidate.transform.position, pos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = candidate;
                    }
                }
                item = best;
                if (item != null && ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[ItemDmgEvent] fallback found '{item.name}' at dist={bestDist:F2}");
            }

            if (item == null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[ItemDmgEvent] no item found at {pos}");
                return;
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[ItemDmgEvent] found {item.name} health={item.health} destructible={item.destructible} destroyed={item.destroyed}");
            if (!item.destructible) return;

            bool playFx = msg.DamageAmount >= 0
                && !DWMPHorde.Patches.ClientWorldMeleeRedirectHelper.ShouldSuppressApplyFx(2, pos);

            if (msg.Action == BarricadeAction.Destroyed || (msg.Action == BarricadeAction.Damaged && msg.Health <= 0))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ItemDmgEvent] destroying " + item.name);
                if (!item.destroyed)
                    item.die();
            }
            else
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ItemDmgEvent] setting " + item.name + " health to " + msg.Health);
                Traverse.Create(item).Field("health").SetValue(msg.Health);
                if (playFx && item.hitParticlePrefabObject != null)
                {
                    Core.AddPrefab(item.hitParticlePrefabObject, item.transform.position,
                        Quaternion.Euler(90f, 0f, 0f), null);
                }
                if (!string.IsNullOrEmpty(item.hitSound))
                    AudioController.Play(item.hitSound, item.transform.position);
            }
        }

        private void HandleShadowEvent(ShadowEventMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (Player.Instance == null) return;

            // Set the CharacterSpawner flags so the game knows shadows are active.
            // Do NOT call Player.tryToSpawnShadow() — that spawns shadows at wrong local
            // positions.  The host sends individual ShadowSpawnMessages with exact positions.
            var cs = Singleton<CharacterSpawner>.Instance;
            if (cs != null)
            {
                cs.shadowsRemove = false;
                cs.shadowsPaused = false;
                cs.spawnedShadows = true;
                cs.spawnedShadowsAmount = 8;
            }

            ModRuntime.LegacyInfo("[ShadowSync] client received NightShadows event, awaiting host ShadowSpawn + ShadowStateUpdate messages");
        }

        private void HandleShadowSpawn(ShadowSpawnMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (Player.Instance == null) return;

            var spawner = Singleton<CharacterSpawner>.Instance;
            if (spawner == null) return;

            if (Core.isDay() || spawner.shadowsRemove)
                return;

            string prefabPath = msg.ShadowType == 1
                ? "characters/fakechars/shadow_immortal"
                : "characters/fakechars/shadow";

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(90f, msg.RotY, 0f);
            GameObject go = Core.AddPrefab(prefabPath, pos, rot, null);
            if (go == null) return;

            // Attach a ShadowSyncInfo so HandleShadowStateUpdate can find it by ID
            var info = go.GetComponent<ShadowSyncInfo>();
            if (info == null)
                info = go.AddComponent<ShadowSyncInfo>();
            info.ShadowId = msg.ShadowId;
            info.ShadowType = msg.ShadowType;

            // Apply initial state
            var sc = go.GetComponent<ShadowCreature>();
            if (sc != null)
            {
                sc.distanceToPlayer = msg.DistanceToPlayer;
                sc.dead = (msg.Flags & 2) != 0;
            }

            // Start the Float animation (blocked Start/appear won't trigger it)
            var anim = go.GetComponent<tk2dSpriteAnimator>();
            if (anim != null && anim.GetClipByName("Float") != null)
                anim.Play("Float");

            if (_clientShadowLookups == null)
                _clientShadowLookups = new Dictionary<short, ShadowCreature>();
            if (sc != null)
                _clientShadowLookups[msg.ShadowId] = sc;
        }

        private Dictionary<short, ShadowCreature> _clientShadowLookups;

        private void HandleShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (_clientShadowLookups == null) return;

            if (_clientShadowLookups.TryGetValue(msg.ShadowId, out var sc) && sc != null)
            {
                // Update position
                sc.transform.position = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                sc.transform.rotation = Quaternion.Euler(90f, msg.RotY, 0f);
                sc.distanceToPlayer = msg.DistanceToPlayer;

                bool dead = (msg.Flags & 2) != 0;
                if (dead && !sc.dead)
                {
                    sc.dead = true;
                    var anim = sc.GetComponent<tk2dSpriteAnimator>();
                    if (anim != null && anim.GetClipByName("Death1") != null)
                        anim.Play("Death1");
                }

                if (dead)
                {
                    _clientShadowLookups.Remove(msg.ShadowId);
                }
            }
            else
            {
                // Shadow not yet created — treat as spawn if we missed ShadowSpawnMessage
                var spawnMsg = new ShadowSpawnMessage
                {
                    ShadowId = msg.ShadowId,
                    ShadowType = 0,
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    RotY = msg.RotY,
                    DistanceToPlayer = msg.DistanceToPlayer,
                    Flags = msg.Flags
                };
                HandleShadowSpawn(spawnMsg);
            }
        }

        private void HandleScenarioSync(ScenarioSyncMessage msg)
        {
            ApplyScenarioSync(msg);
        }

        /// <summary>
        /// Client: set host's current night scenario by name (bypasses setCurrentScenario
        /// which is blocked on clients). Used by live ScenarioSync and join ScenarioStateSync.
        /// </summary>
        private void ApplyScenarioSync(ScenarioSyncMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (string.IsNullOrEmpty(msg.ScenarioName)) return;

            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
            {
                _pendingScenarioSync = msg;
                _hasPendingScenarioSync = true;
                ModRuntime.LegacyInfo("[ScenarioSync] NightScenarios not ready — queued");
                return;
            }

            NightScenario scenario = ns.getScenario(msg.ScenarioName);
            if (scenario == null)
            {
                ModRuntime.Log?.LogWarning($"[ScenarioSync] unknown scenario '{msg.ScenarioName}'");
                return;
            }

            ns.currentScenario = scenario;
            ModRuntime.LegacyInfo($"[ScenarioSync] client set currentScenario to '{msg.ScenarioName}'");
        }

        private void TryFlushPendingScenario()
        {
            if (!_hasPendingScenarioSync) return;
            if (_role != NetworkRole.Client) return;
            if (Singleton<NightScenarios>.Instance == null) return;
            _hasPendingScenarioSync = false;
            var pending = _pendingScenarioSync;
            _pendingScenarioSync = default;
            ApplyScenarioSync(pending);
        }

        private void HandleScenarioEventFired(ScenarioEventFiredMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (msg.EventIndex < 0) return;

            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
            {
                _pendingScenarioEvent = msg;
                _hasPendingScenarioEvent = true;
                return;
            }

            ApplyScenarioEventFired(msg);
        }

        private void ApplyScenarioEventFired(ScenarioEventFiredMessage msg)
        {
            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null) return;

            NightScenario scenario = null;
            for (int i = 0; i < ns.scenarios.Count; i++)
            {
                if (ns.scenarios[i] != null && ns.scenarios[i].nightId == msg.NightId)
                {
                    scenario = ns.scenarios[i];
                    break;
                }
            }

            if (scenario == null)
            {
                ModRuntime.Log?.LogWarning($"[ScenarioEventFired] unknown nightId {msg.NightId}");
                return;
            }

            if (msg.EventIndex >= scenario.customEventAndInts.Count)
            {
                ModRuntime.Log?.LogWarning($"[ScenarioEventFired] index {msg.EventIndex} out of range (count={scenario.customEventAndInts.Count})");
                return;
            }

            if (scenario.customEventAndInts[msg.EventIndex].customEvent == null)
            {
                ModRuntime.Log?.LogWarning($"[ScenarioEventFired] null CustomEvent at index {msg.EventIndex}");
                return;
            }

            // Ensure client currentScenario matches the event's night (join race).
            if (ns.currentScenario != scenario)
                ns.currentScenario = scenario;

            ModRuntime.LegacyInfo($"[ScenarioEventFired] host fired event index {msg.EventIndex} in nightId {msg.NightId}");

            Patches.ScenarioPendingEventState.PendingEventIndex = msg.EventIndex;
            Patches.ScenarioPendingEventState.PendingScenario = scenario;
        }

        private void HandleSawState(SawStateMessage msg)
        {
            ApplySawState(msg, queueIfMissing: true);
        }

        private void ApplySawState(SawStateMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Saw saw = FindSawByPos(pos);
            if (saw == null)
            {
                if (queueIfMissing)
                {
                    // Replace pending for same approx position
                    for (int i = _pendingSawStates.Count - 1; i >= 0; i--)
                    {
                        var p = _pendingSawStates[i];
                        if (Mathf.Abs(p.PosX - msg.PosX) < 0.5f &&
                            Mathf.Abs(p.PosY - msg.PosY) < 0.5f &&
                            Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pendingSawStates.RemoveAt(i);
                    }
                    if (_pendingSawStates.Count >= MaxPendingSawStates)
                        _pendingSawStates.RemoveAt(0);
                    _pendingSawStates.Add(msg);
                    ModRuntime.LegacyInfo("[SawSync] queued (saw not loaded) at " + pos);
                }
                else
                {
                    ModRuntime.LegacyInfo("[SawSync] saw not found at " + pos);
                }
                return;
            }

            float prevFuel = saw.fuel;
            int prevLogs = 0, prevWood = 0;
            Inventory inv = Sync.SawSyncHelpers.GetInventory(saw);
            if (inv != null)
            {
                var logItem = inv.getItem("woodLog");
                if (!InvItemClass.isNull(logItem)) prevLogs = logItem.amount;
                var woodItem = inv.getItem("wood");
                if (!InvItemClass.isNull(woodItem)) prevWood = woodItem.amount;
            }

            saw.fuel = Mathf.Clamp(msg.Fuel, 0f, saw.maxFuel);

            if (inv != null)
            {
                SyncItemAmount(inv, "woodLog", msg.WoodLogAmount);
                SyncItemAmount(inv, "wood", msg.WoodAmount);
            }

            SafeSawRefresh(saw);

            // Only play convert SFX when wood stock actually changed (not pure fuel top-up).
            bool woodChanged = prevLogs != msg.WoodLogAmount || prevWood != msg.WoodAmount;
            if (woodChanged)
                AudioController.Play("saw_wood_01", saw.transform.position);

            ModRuntime.LegacyInfo(
                $"[SawSync] applied at {pos} fuel {prevFuel:F0}→{msg.Fuel:F0} logs={msg.WoodLogAmount} wood={msg.WoodAmount}");
        }

        private static void SafeSawRefresh(Saw saw)
        {
            if (saw == null) return;
            try
            {
                // Vanilla refresh can NRE if convertFuelBtn not yet wired (UI not open).
                if (saw.convertFuelBtn == null)
                {
                    Inventory inv = Sync.SawSyncHelpers.GetInventory(saw);
                    if (inv?.thisLabel?.rightText != null)
                        inv.thisLabel.rightText.text = Language.Get("Fuel", "UI") + ": " + saw.fuel;
                    return;
                }
                saw.refresh();
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[SawSync] refresh failed: " + ex.Message);
            }
        }

        private static Saw FindSawByPos(Vector3 pos)
        {
            return WorldQueryHelper.FindNearest<Saw>(pos, 2f);
        }

        /// <summary>Host: push absolute state for every loaded saw to a joiner.</summary>
        internal void SendSawStatesTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;

            Saw[] all = UnityEngine.Object.FindObjectsOfType<Saw>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Saw saw = all[i];
                if (saw == null) continue;
                var msg = Sync.SawSyncHelpers.BuildMessage(saw);
                SendBulkOrAll(NetMessageType.SawState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} saw state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} saw state(s) to all clients");
        }

        private void TryFlushPendingSawStates()
        {
            if (_pendingSawStates.Count == 0) return;
            for (int i = _pendingSawStates.Count - 1; i >= 0; i--)
            {
                var msg = _pendingSawStates[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (FindSawByPos(pos) == null) continue;
                _pendingSawStates.RemoveAt(i);
                ApplySawState(msg, queueIfMissing: false);
            }
        }

        private static void SyncItemAmount(Inventory inv, string itemType, int desiredAmount)
        {
            InvItemClass item = inv.getItem(itemType);
            int currentAmount = InvItemClass.isNull(item) ? 0 : item.amount;
            if (currentAmount == desiredAmount) return;
            if (currentAmount > 0)
                item.removeAmount(currentAmount);
            if (desiredAmount > 0)
                inv.addItemType(itemType, desiredAmount);
        }

        private void HandleWorkbenchLevel(WorkbenchLevelMessage msg)
        {
            ApplyWorkbenchLevel(msg.Level);
        }

        private void ApplyWorkbenchLevel(int level)
        {
            if (Singleton<Controller>.Instance == null) return;

            int prevLevel = Singleton<Controller>.Instance.workbenchLevel;
            if (prevLevel == level)
            {
                // Still refresh open UI in case recipes are stale.
            }
            else
            {
                Singleton<Controller>.Instance.workbenchLevel = level;
                ModRuntime.LegacyInfo("[Workbench] Level synced from " + prevLevel + " to " + level);
            }

            // If the workbench inventory is currently open, refresh the display
            // so the player sees the updated level and recipes immediately.
            try
            {
                if (Player.Instance != null && Player.Instance.openedItemInventory != null)
                {
                    Workbench wb = Player.Instance.openedItemInventory.GetComponent<Workbench>();
                    if (wb == null)
                        wb = Player.Instance.openedItemInventory.transform.parent?.GetComponent<Workbench>();

                    if (wb != null)
                    {
                        wb.currentLevel = level;
                        wb.refreshWorkbenchUpgrade();
                        if (wb.workbenchInventory != null)
                            wb.workbenchInventory.refreshRecipes();
                    }
                }
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void HandleJournalItem(JournalItemMessage msg)
        {
            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null) return;

            switch (msg.Kind)
            {
                case JournalItemKind.Note:
                    if (!journal.notesDict.ContainsKey(msg.Type))
                    {
                        Journal.Note note = new Journal.Note();
                        note.type = msg.Type;
                        note.timePickedUp = Singleton<Controller>.Instance != null
                            ? Singleton<Controller>.Instance.CurrentTime : 0;
                        journal.notesDict.Add(msg.Type, note);
                        journal.showJournalInfoPopup("Note", msg.Type);
                    }
                    break;
                case JournalItemKind.Key:
                    if (!journal.keysDict.ContainsKey(msg.Type))
                    {
                        Journal.Key key = new Journal.Key();
                        key.type = msg.Type;
                        journal.keysDict.Add(msg.Type, key);
                        journal.showJournalInfoPopup("Key", msg.Type);
                    }
                    break;
                case JournalItemKind.QuestItem:
                    if (!journal.itemsDict.ContainsKey(msg.Type))
                    {
                        Journal.Item item = new Journal.Item();
                        item.type = msg.Type;
                        journal.itemsDict.Add(msg.Type, item);
                        journal.showJournalInfoPopup("InvItem", msg.Type);
                    }
                    break;
                case JournalItemKind.JournalEntry:
                    journal.addJournalEntry(msg.Type, noPopup: false);
                    break;
                default:
                    ModRuntime.Log?.LogWarning($"[Journal] Unhandled JournalItemKind: {msg.Kind}");
                    break;
            }

            // World-object cleanup: destroy the physical journal object on this peer
            // so the world reflects that the item was already picked up.
            DestroyWorldJournalObject(msg.Kind, msg.Type);
        }

        /// <summary>
        /// Finds and destroys the physical world object (JournalNoteReference,
        /// KeyReference, or QuestItemReference) matching the given journal item,
        /// so the host's world reflects that the remote player already took it.
        /// </summary>
        private static void DestroyWorldJournalObject(JournalItemKind kind, string type)
        {
            if (string.IsNullOrEmpty(type)) return;

            switch (kind)
            {
                case JournalItemKind.Note:
                    {
                        var allNotes = Resources.FindObjectsOfTypeAll<JournalNoteReference>();
                        for (int i = 0; i < allNotes.Length; i++)
                        {
                            if (allNotes[i] == null) continue;
                            var note = Singleton<JournalDatabase>.Instance?.getNote(allNotes[i].noteName);
                            if (note != null && note.type == type)
                            {
                                if (allNotes[i].GetComponent<Item>() != null)
                                    UnityEngine.Object.Destroy(allNotes[i].gameObject);
                            }
                        }
                        break;
                    }
                case JournalItemKind.Key:
                    {
                        var allKeys = Resources.FindObjectsOfTypeAll<KeyReference>();
                        for (int i = 0; i < allKeys.Length; i++)
                        {
                            if (allKeys[i] != null && allKeys[i].type == type)
                            {
                                if (allKeys[i].GetComponent<Item>() != null)
                                    UnityEngine.Object.Destroy(allKeys[i].gameObject);
                            }
                        }
                        break;
                    }
                case JournalItemKind.QuestItem:
                    {
                        var allQuest = Resources.FindObjectsOfTypeAll<QuestItemReference>();
                        for (int i = 0; i < allQuest.Length; i++)
                        {
                            if (allQuest[i] != null && allQuest[i].type == type)
                                UnityEngine.Object.Destroy(allQuest[i].gameObject);
                        }
                        break;
                    }
                case JournalItemKind.JournalEntry:
                    // Story journal entries have no world pickup object to despawn.
                    break;
                default:
                    // Avoid per-frame spam: log once per kind value.
                    ModRuntime.Log?.LogWarning($"[Journal] Unhandled destroy kind: {kind}");
                    break;
            }
        }


        private static Inventory FindInventoryByPos(Vector3 pos)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, 1f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Inventory inv = nearby[i].GetComponentInParent<Inventory>();
                if (inv != null && (inv.invType == Inventory.InvType.itemInv || inv.invType == Inventory.InvType.deathDrop))
                    return inv;
            }

            // Fallback: scan all item/death-drop inventories by position — handles containers
            // that are loaded but outside the 1m OverlapSphere radius (e.g. on the
            // host's scene when the client player looted a distant container).
            Inventory best = null;
            float bestDist = 10f;
            Inventory[] all = UnityEngine.Object.FindObjectsOfType<Inventory>();
            for (int i = 0; i < all.Length; i++)
            {
                Inventory inv = all[i];
                if (inv == null || (inv.invType != Inventory.InvType.itemInv && inv.invType != Inventory.InvType.deathDrop)) continue;
                float d = Vector3.Distance(inv.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = inv;
                }
            }
            if (best != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] FindInventoryByPos fallback found '{best.name}' at dist={bestDist:F2} from {pos}");
                return best;
            }

            // Final fallback: search DeathDrop objects by position (they may not have
            // a physics collider and the inventory type may be set after initialization)
            DeathDrop[] bags = UnityEngine.Object.FindObjectsOfType<DeathDrop>(true);
            DeathDrop closestBag = null;
            float closestBagDist = 15f;
            foreach (DeathDrop bag in bags)
            {
                if (bag == null) continue;
                float d = Vector3.Distance(bag.transform.position, pos);
                if (d < closestBagDist)
                {
                    closestBagDist = d;
                    closestBag = bag;
                }
            }
            if (closestBag != null)
            {
                Inventory inv = closestBag.GetComponent<Inventory>();
                if (inv != null)
                {
                    ModRuntime.LegacyInfo($"[Container] FindInventoryByPos: found DeathDrop inventory at {closestBagDist:F2}m from {pos}");
                    return inv;
                }
            }

            ModRuntime.LegacyInfo($"[Container] FindInventoryByPos: no inventory at {pos} (1m overlap + 10m scan + DeathDrop fallback)");
            return null;
        }

        private static Door FindDoorByPos(Vector3 pos)
        {
            // Tracker first (tight), then looser match, then physics overlap (B7).
            Door d = Sync.DoorTracker.FindByPosition(pos, 0.5f);
            if (d != null) return d;
            d = Sync.DoorTracker.FindByPosition(pos, 1.5f);
            if (d != null) return d;

            Collider[] nearby = Physics.OverlapSphere(pos, 2f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Door door = nearby[i].GetComponentInParent<Door>();
                if (door != null) return door;
                // MeleeSensor uses Door.getDoorScript(parent) for Door-tagged colliders
                if (nearby[i].CompareTag("Door") && nearby[i].transform.parent != null)
                {
                    door = Door.getDoorScript(nearby[i].transform.parent);
                    if (door != null) return door;
                }
            }
            return null;
        }

        private static Window FindWindowByPos(Vector3 pos)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, 2f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Window w = nearby[i].GetComponentInParent<Window>();
                if (w != null) return w;
            }
            Collider2D[] nearby2d = Physics2D.OverlapCircleAll(pos, 2f);
            for (int i = 0; i < nearby2d.Length; i++)
            {
                if (nearby2d[i] == null) continue;
                Window w = nearby2d[i].GetComponentInParent<Window>();
                if (w != null) return w;
            }
            return null;
        }

        private float _physicsSendTimer;
        private int _physicsRecvLogCounter;
        private const float PhysicsSendInterval = 0.1f;

        private float _timeSyncTimer;
        private const float TimeSyncInterval = 2f;

        private short _nextShadowId;
        private readonly Dictionary<short, ShadowCreature> _shadowTracked = new Dictionary<short, ShadowCreature>();
        private float _shadowBroadcastTimer;
        private const float ShadowBroadcastInterval = 0.3f;

        public short GetNextShadowId()
        {
            _nextShadowId++;
            if (_nextShadowId >= 9999) _nextShadowId = 1;
            return _nextShadowId;
        }

        public void RegisterShadow(short id, ShadowCreature sc)
        {
            _shadowTracked[id] = sc;
        }

        public void UnregisterShadow(short id)
        {
            // Emit a final dead update so clients play Death1 and drop the lookup
            // before we forget the id (BroadcastShadowStates used to drop silently).
            if (_shadowTracked.TryGetValue(id, out ShadowCreature sc) && sc != null)
            {
                Vector3 p = sc.transform.position;
                SendShadowStateUpdate(new ShadowStateUpdateMessage
                {
                    ShadowId = id,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    RotY = sc.transform.rotation.eulerAngles.y,
                    DistanceToPlayer = sc.distanceToPlayer,
                    Flags = 2 // dead
                });
            }
            _shadowTracked.Remove(id);
        }

        public void SendDoorState(DoorState door)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            var msg = new PhysicsStateMessage { Doors = new[] { door } };
            Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendTrapState(TrapState ts)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            var msg = new PhysicsStateMessage { Traps = new[] { ts } };
            ModRuntime.LegacyInfo("[TrapSync] sending trap triggered at " + ts.PosX + "," + ts.PosY + "," + ts.PosZ);
            Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendItemSpawn(ItemSpawnMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            ModRuntime.LegacyInfo("[ItemSpawn] sending " + msg.ItemType + " at " + msg.PosX + "," + msg.PosY + "," + msg.PosZ);
            Broadcast(NetMessageType.ItemSpawn, w => msg.Serialize(w));
        }

        public void SendShadowEvent(ShadowEventMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.ShadowEvent, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendShadowSpawn(ShadowSpawnMessage msg)
        {
            if (!IsConnected) return;
            Broadcast(NetMessageType.ShadowSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            if (!IsConnected) return;
            // Alive ticks: Unreliable OK. Death flag must be reliable so clients
            // do not keep ghost shadows after UnregisterShadow.
            bool isDead = (msg.Flags & 2) != 0;
            Broadcast(NetMessageType.ShadowStateUpdate, w => msg.Serialize(w),
                isDead ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        private void BroadcastShadowStates()
        {
            if (_role != NetworkRole.Host) return;
            if (!IsConnected) return;
            if (_shadowTracked.Count == 0) return;

            // Clean dead/null shadows (send death first), broadcast living ones.
            List<short> deadIds = null;
            foreach (var kvp in _shadowTracked)
            {
                if (kvp.Value == null || kvp.Value.dead)
                {
                    if (deadIds == null) deadIds = new List<short>();
                    deadIds.Add(kvp.Key);
                    if (kvp.Value != null)
                    {
                        Vector3 p = kvp.Value.transform.position;
                        SendShadowStateUpdate(new ShadowStateUpdateMessage
                        {
                            ShadowId = kvp.Key,
                            PosX = p.x,
                            PosY = p.y,
                            PosZ = p.z,
                            RotY = kvp.Value.transform.rotation.eulerAngles.y,
                            DistanceToPlayer = kvp.Value.distanceToPlayer,
                            Flags = 2
                        });
                    }
                    continue;
                }

                var sc = kvp.Value;
                var msg = new ShadowStateUpdateMessage
                {
                    ShadowId = kvp.Key,
                    PosX = sc.transform.position.x,
                    PosY = sc.transform.position.y,
                    PosZ = sc.transform.position.z,
                    RotY = sc.transform.rotation.eulerAngles.y,
                    DistanceToPlayer = sc.distanceToPlayer,
                    Flags = 0
                };
                SendShadowStateUpdate(msg);
            }

            if (deadIds != null)
            {
                for (int i = 0; i < deadIds.Count; i++)
                    _shadowTracked.Remove(deadIds[i]);
            }
        }

        /// <summary>
        /// Host: re-send ShadowEvent + all tracked shadows to a late joiner.
        /// </summary>
        internal void SendShadowsTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            if (_shadowTracked.Count == 0) return;

            SendBulkOrAll(NetMessageType.ShadowEvent,
                w => new ShadowEventMessage().Serialize(w), targetPlayerId);

            int sent = 0;
            foreach (var kvp in _shadowTracked)
            {
                ShadowCreature sc = kvp.Value;
                if (sc == null || sc.dead) continue;

                var info = sc.GetComponent<ShadowSyncInfo>();
                byte shadowType = info != null ? info.ShadowType : (byte)0;
                Vector3 p = sc.transform.position;
                var msg = new ShadowSpawnMessage
                {
                    ShadowId = kvp.Key,
                    ShadowType = shadowType,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    RotY = sc.transform.rotation.eulerAngles.y,
                    DistanceToPlayer = sc.distanceToPlayer,
                    Flags = 0
                };
                SendBulkOrAll(NetMessageType.ShadowSpawn, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} shadows to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} shadows to all clients");
        }

        public void SendScenarioSync(ScenarioSyncMessage msg)
        {
            if (!IsConnected) return;
            Broadcast(NetMessageType.ScenarioSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendScenarioEventFired(int nightId, int eventIndex)
        {
            if (!IsConnected) return;
            var msg = new ScenarioEventFiredMessage { NightId = nightId, EventIndex = eventIndex };
            Broadcast(NetMessageType.ScenarioEventFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendEntityBurning(short entityId, bool isBurning, float burnTime = 0, float modifier = 0, float interval = 0)
        {
            if (!IsConnected) return;
            var msg = new EntityBurningMessage { EntityId = entityId, IsBurning = isBurning, BurnTime = burnTime, Modifier = modifier, Interval = interval };
            Broadcast(NetMessageType.EntityBurning, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendLiquidStopBurning(Vector3 pos)
        {
            if (!IsConnected) return;
            var msg = new LiquidStopBurningMessage { PosX = pos.x, PosY = pos.y, PosZ = pos.z };
            Broadcast(NetMessageType.LiquidStopBurning, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendPlayerBurning(bool isBurning, float burnTime = 0)
        {
            if (!IsConnected) return;
            var msg = new PlayerBurningMessage { IsBurning = isBurning, BurnTime = burnTime };
            Broadcast(NetMessageType.PlayerBurning, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendExplosionSpawnObject(string prefabName, Vector3 pos, Vector3 rot)
        {
            if (!IsConnected) return;
            var msg = new ExplosionSpawnObjectMessage { PrefabName = prefabName, PosX = pos.x, PosY = pos.y, PosZ = pos.z, RotX = rot.x, RotY = rot.y, RotZ = rot.z };
            Broadcast(NetMessageType.ExplosionSpawnObject, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendConstructible(ConstructibleMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.ConstructibleConstruction, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendInteractiveItemSwitch(InteractiveItemSwitchMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.InteractiveItemSwitch, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendPadlockUnlock(PadlockUnlockMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.PadlockUnlock, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendLockedUnlock(LockedUnlockMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.LockedUnlock, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendGameEventsFired(GameEventsFiredMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.GameEventsFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.HideoutUpgrade, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendGeneratorState(GeneratorState gs)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            var msg = new PhysicsStateMessage { Generators = new[] { gs } };
            Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendLightState(LightStateMessage ls)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            // Sticky world light state — lost Unreliable packets permanently desync lamps.
            Broadcast(NetMessageType.LightState, w => ls.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendWorldObjectRemoved(WorldObjectRemovedMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.WorldObjectRemoved, w => msg.Serialize(w));
        }

        public void SendPlayerLightState(PlayerLightStateMessage msg, DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.PlayerLightState, w => msg.Serialize(w), method);
        }

        public void SyncCurrentLightState()
        {
            Player local = Player.Instance;
            if (local == null) return;
            var msg = LightStateHelper.BuildLightState(local);

            // Also capture ambient light from hotbar items (e.g. lantern in slot,
            // not held).  Vanilla stores the current radius in lightDot.
            var t = HarmonyLib.Traverse.Create(local);
            Light2D lightDot = t.Field("lightDot").GetValue<Light2D>();
            float defaultRadius = t.Field("lightDotDefaultRadius").GetValue<float>();
            if (lightDot != null && lightDot.LightRadius > defaultRadius)
            {
                msg.HasAmbientLight = true;
                msg.LightRadius = lightDot.LightRadius;
                if (!msg.LightOn && !msg.HasLightEmitter)
                {
                    msg.LightOn = true;
                    msg.LightIntensity = 1f;
                    msg.LightColorR = 1f;
                    msg.LightColorG = 1f;
                    msg.LightColorB = 1f;
                }
            }

            // Fallback: scan activated items for ambient light (lantern in hotbar,
            // not held).  This catches the case where lightDot radius equals the
            // default because the lantern was activated before connect (save-loaded)
            // and modifyLightDot never ran during this session.
            if (!msg.HasAmbientLight && !msg.HasLightEmitter)
            {
                try
                {
                    foreach (var activeItem in Player.Instance.activeItems)
                    {
                        if (activeItem != null && activeItem.baseClass != null &&
                            activeItem.baseClass.lightRadius > 0f)
                        {
                            msg.HasAmbientLight = true;
                            msg.LightOn = true;
                            msg.LightRadius = activeItem.baseClass.lightRadius;
                            msg.LightIntensity = 1f;
                            msg.LightColorR = 1f;
                            msg.LightColorG = 1f;
                            msg.LightColorB = 1f;
                            break;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning($"[Light] activeItems scan failed: {ex.Message}");
                }
            }

            SendPlayerLightState(msg, DeliveryMethod.ReliableOrdered);
        }

        public void SendThrowableSpawn(ThrowableSpawnMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.ThrowableSpawn, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void SendExplosionTrigger(ExplosionTriggerMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            // Must be reliable — lost barrel/molotov triggers desync combat and FX.
            Broadcast(NetMessageType.ExplosionTrigger, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        public void SendPlayerAudio(PlayerAudioMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            // One-shots and stop signals must not drop under lossy LAN.
            Broadcast(NetMessageType.PlayerAudio, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendGasTrailSpawn(GasTrailSpawnMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.GasTrailSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendMeleeWorldHit(MeleeWorldHitMessage msg)
        {
            if (!IsConnected) return;
            if (_role != NetworkRole.Client) return;
            Broadcast(NetMessageType.MeleeWorldHit, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendGasIgnite(GasIgniteMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.GasIgnite, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.DroppedItemSpawn, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void SendDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            if (!string.IsNullOrEmpty(msg.Guid))
                _consumedDropGuids.Add(msg.Guid); // local consume before peers process
            Broadcast(NetMessageType.DroppedItemPickup, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>True if this GUID was already picked up (local or network).</summary>
        public static bool IsDropGuidConsumed(string guid)
        {
            return !string.IsNullOrEmpty(guid) && _consumedDropGuids.Contains(guid);
        }

        /// <summary>
        /// Host: send all live GUID-tagged ground drops to a late-joining peer.
        /// </summary>
        private void SyncExistingDroppedItems(int targetPlayerId)
        {
            if (_role != NetworkRole.Host || targetPlayerId <= 0) return;

            var list = new System.Collections.Generic.List<Players.DroppedItemIdentifier>(32);
            Players.DroppedItemIdentifier.CopyAll(list);
            int sent = 0;
            foreach (var ident in list)
            {
                if (ident == null || string.IsNullOrEmpty(ident.Id) || ident.gameObject == null)
                    continue;
                if (_consumedDropGuids.Contains(ident.Id))
                    continue;

                Inventory inv = ident.GetComponent<Inventory>();
                InvItemClass item = null;
                if (inv != null && inv.slots != null && inv.slots.Count > 0)
                    item = inv.slots[0].invItem;
                if (InvItemClass.isNull(item))
                    continue;

                Vector3 pos = ident.transform.position;
                Vector3 euler = ident.transform.eulerAngles;
                string prefab = "Items/DroppedItem";
                string n = ident.gameObject.name ?? "";
                if (n.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    prefab = "Items/DroppedItem_water";

                int ammo = 0;
                if (item.baseClass != null && item.baseClass.hasAmmo)
                    ammo = item.ammo;

                var msg = new DroppedItemSpawnMessage
                {
                    Guid = ident.Id,
                    PrefabPath = prefab,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = euler.x,
                    RotY = euler.y,
                    RotZ = euler.z,
                    ItemType = item.type,
                    Amount = item.amount,
                    Durability = item.durability,
                    Ammo = ammo
                };
                SendToPlayer(targetPlayerId, NetMessageType.DroppedItemSpawn,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0)
                ModRuntime.LegacyInfo($"[DroppedItem] Synced {sent} existing drop(s) to player {targetPlayerId}");
        }

        public void SendSawState(SawStateMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.SawState, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void SendPlayerAnimation(PlayerAnimationMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.PlayerAnimation, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendPlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            // Reliable: weapon sprite library must not drop (one-shot equip event)
            Broadcast(NetMessageType.PlayerAnimLibrary, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendBulletImpact(BulletImpactMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.BulletImpact, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public void SendPlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            Broadcast(NetMessageType.PlayerFiredWeapon, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Deliver damage to a specific remote player. Prefer this over broadcast —
        /// multi-client broadcast would apply the same hit to every peer.
        /// </summary>
        public void SendDamagePlayer(int victimPlayerId, DamagePlayerMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            if (victimPlayerId <= 0) return;
            SendToPlayer(victimPlayerId, NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Legacy broadcast — only safe for single-client sessions. Prefer the player-id overload.</summary>
        public void SendDamagePlayer(DamagePlayerMessage msg)
        {
            if (!IsConnected) return;
            if (IsApplyingRemoteState) return;
            // Route to the only connected client when possible; otherwise no-op for multi-peer.
            if (_peers.Count == 1)
            {
                foreach (var kvp in _peers)
                {
                    SendToPlayer(kvp.Key, NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                    return;
                }
            }
            if (_peers.Count > 1)
            {
                ModRuntime.Log?.LogWarning("[DamagePlayer] broadcast skipped — use SendDamagePlayer(playerId, msg) for multi-client");
                return;
            }
            Broadcast(NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Sends a save-sync trigger to the remote peer.</summary>
        public void SendSaveSync()
        {
            if (!IsConnected) return;
            if (_isRemoteSaveInProgress) return;
            ModRuntime.LegacyInfo("[SaveSync] sending save trigger to remote");
            Broadcast(NetMessageType.SaveSync, w => new SaveSyncMessage().Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Sends the client's inventory/skills/state backup to the host.</summary>
        public void SendClientStateBackup()
        {
            if (!IsConnected) return;
            if (_role != NetworkRole.Client) return;
            try
            {
                var data = ClientStateBackup.CollectBackupData();
                string json = ClientStateBackup.SerializeToJson(data);
                Broadcast(NetMessageType.ClientStateBackup, w => new ClientStateBackupMessage { JsonData = json }.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo("[ClientBackup] sent backup to host (" + (data.InventoryItems?.Count ?? 0) + " items, " + (data.Skills?.Count ?? 0) + " skills)");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to send: " + ex);
            }
        }

        /// <summary>Handles a save-sync trigger from the remote peer.</summary>
        private void HandleSaveSync()
        {
            if (_isRemoteSaveInProgress) return;
            ModRuntime.LegacyInfo("[SaveSync] received save trigger from remote, saving locally");
            _isRemoteSaveInProgress = true;
            try
            {
                // Show saving indicator immediately so the local player sees feedback
                try
                {
                    if (Singleton<UI>.Instance != null)
                        Singleton<UI>.Instance.showSavingIndicator();
                }
                catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
            }

                // Perform the actual save. The showSavingIndicator parameter defaults to true
                // so finishSaving will also call it, but we already showed it above.
                SaveManager save = Singleton<SaveManager>.Instance;
                if (save != null)
                {
                    save.Save(doJson: false, doSaveProfile: true, force: true);

                    // Ensure lastTimeSaved is updated so the "time since last save" timer
                    // on both sides shows roughly the same value.
                    // (SaveManager sets this only inside the non-early-return path.)
                    var t = HarmonyLib.Traverse.Create(save);
                    t.Field("lastTimeSaved").SetValue(System.DateTime.Now);
                }

                // Also sync the profile's timeSaved string so the save selection screen
                // shows consistent timestamps across players.
                if (Core.currentProfile != null)
                    Core.currentProfile.timeSaved = System.DateTime.Now.ToString();

                // Client just saved (triggered by host) — send backup to host
                if (_role == NetworkRole.Client)
                    SendClientStateBackup();
            }
            finally
            {
                _isRemoteSaveInProgress = false;
            }
        }

        /// <summary>
        /// Handles a client state backup from a remote client. Host stores per-PlayerId
        /// so multi-client saves do not overwrite each other.
        /// </summary>
        private void HandleClientStateBackup(ClientStateBackupMessage msg)
        {
            if (_role != NetworkRole.Host)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] received backup but not host, ignoring");
                return;
            }
            if (string.IsNullOrEmpty(msg.JsonData))
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] received empty backup data");
                return;
            }

            int playerId = _currentReceivePlayerId;
            if (playerId <= 0)
            {
                // Prefer id embedded in JSON if peer map was stale
                try
                {
                    var parsed = ClientStateBackup.DeserializeFromJson(msg.JsonData);
                    if (parsed != null && parsed.PlayerId > 0)
                        playerId = parsed.PlayerId;
                }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[ClientBackup] could not parse PlayerId from json: " + ex.Message);
                }
            }

            if (playerId <= 0)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] cannot key backup — unknown sender player id");
                return;
            }

            ClientStateBackup.SaveBackupFile(msg.JsonData, playerId);
        }

        /// <summary>
        /// Handles a flag sync from the host. Applies the flag change locally
        /// so dialog progression and world state stay consistent.
        /// </summary>
        private void HandleFlagSync(FlagSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Name))
                return;

            // Host receives client→host story flag deltas (audit H1), applies, rebroadcasts.
            if (_role == NetworkRole.Host)
            {
                if (Singleton<Flags>.Instance == null)
                {
                    QueuePendingFlagDelta(msg);
                    return;
                }
                ApplyFlagSyncMessage(msg);
                // Fan-out to other clients (exclude originator if known).
                int from = _currentReceivePlayerId;
                if (from > 0)
                    SendToAllExcept(from, NetMessageType.FlagSync, w => msg.Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                else
                    Broadcast(NetMessageType.FlagSync, w => msg.Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                return;
            }

            if (_role != NetworkRole.Client)
            {
                ModRuntime.Log?.LogWarning("[FlagSync] unexpected role for flag sync");
                return;
            }

            if (Singleton<Flags>.Instance == null)
            {
                // Client may still be on main menu / loading when host sends deltas.
                QueuePendingFlagDelta(msg);
                return;
            }

            ApplyFlagSyncMessage(msg);
        }

        private void ApplyFlagSyncMessage(FlagSyncMessage msg)
        {
            // Always apply under NetworkApplyGuard so FlagSyncPatches Postfix does not
            // re-Send/Broadcast (client echo / double fan-out). Intentional host fan-out
            // in HandleFlagSync runs *after* this method returns.
            using (new NetworkApplyGuard())
            {
                if (msg.IsInt)
                    Singleton<Flags>.Instance.setFlag(msg.Name, msg.IntValue);
                else
                    Singleton<Flags>.Instance.setFlag(msg.Name, msg.BoolValue);
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[FlagSync] applied flag '{msg.Name}' isInt={msg.IsInt} boolVal={msg.BoolValue} intVal={msg.IntValue}");
        }

        private void QueuePendingFlagDelta(FlagSyncMessage msg)
        {
            // Keep latest value per flag name (bool and int share the same name key via IsInt)
            for (int i = _pendingFlagDeltas.Count - 1; i >= 0; i--)
            {
                if (_pendingFlagDeltas[i].Name == msg.Name && _pendingFlagDeltas[i].IsInt == msg.IsInt)
                    _pendingFlagDeltas.RemoveAt(i);
            }
            if (_pendingFlagDeltas.Count >= MaxPendingFlagDeltas)
                _pendingFlagDeltas.RemoveAt(0);
            _pendingFlagDeltas.Add(msg);
        }

        /// <summary>
        /// Handles a trade sync from either peer. Removes the purchased items
        /// from the local trader's inventory so the assortment stays shared.
        /// </summary>
        private void HandleTradeSync(TradeSyncMessage msg)
        {
            Patches.TradeSyncHandler.HandleTradeSync(msg);
        }

        private void HandleTradeInventorySync(TradeInventorySyncMessage msg)
        {
            Patches.TradeInventorySync.Handle(msg);
        }

        /// <summary>Queue absolute trader stock until the NPC exists in the scene.</summary>
        internal void QueuePendingTradeInventory(TradeInventorySyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;
            _pendingTradeInventories[msg.NpcName] = msg;
            ModRuntime.LegacyInfo($"[TradeSync] queued inventory for '{msg.NpcName}' (NPC not loaded)");
        }

        private void TryFlushPendingTradeInventories()
        {
            if (_pendingTradeInventories.Count == 0) return;

            var applied = new List<string>();
            foreach (var kvp in _pendingTradeInventories)
            {
                NPC npc = Patches.TradeInventorySync.FindNpcByName(kvp.Key);
                if (npc == null) continue;
                Patches.TradeInventorySync.ApplyToNpc(npc, kvp.Value);
                applied.Add(kvp.Key);
            }
            for (int i = 0; i < applied.Count; i++)
                _pendingTradeInventories.Remove(applied[i]);
        }

        /// <summary>Host: push absolute shop stock for every loaded trader NPC.</summary>
        internal void SendTradeInventoriesTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;

            NPC[] all = UnityEngine.Object.FindObjectsOfType<NPC>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                NPC npc = all[i];
                if (npc == null || !npc.trader || npc.inventory == null) continue;
                if (string.IsNullOrEmpty(npc.name)) continue;

                var msg = Patches.TradeInventorySync.BuildMessage(npc);
                SendBulkOrAll(NetMessageType.TradeInventorySync, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} trader inventories to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} trader inventories to all clients");
        }

        /// <summary>
        /// Client dialogue choice → host applies world story outcomes authoritatively.
        /// Prefer TargetDialogueName (works when host is not in the same UI);
        /// personal give/remove item paths are suppressed (DialogHostApplyGuard / audit C2).
        /// </summary>
        private void HandleDialogOutcomeSync(DialogOutcomeSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName) || _role != NetworkRole.Host) return;

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw == null) return;

            // Path A: dest dialogue node (reliable for solo-client conversations).
            if (!string.IsNullOrEmpty(msg.TargetDialogueName))
            {
                NPC npc = FindNpcByName(msg.NpcName);
                if (npc == null)
                {
                    ModRuntime.Log?.LogWarning($"[DialogOutcome] NPC '{msg.NpcName}' not found for target={msg.TargetDialogueName}");
                    return;
                }

                bool hostWasInThisTalk = dw.npc != null && dw.npc.name == msg.NpcName
                    && dw.displayingDialogue;

                dw.npc = npc;
                if (Player.Instance != null)
                    Player.Instance.talkedToNPC = npc;

                ModRuntime.LegacyInfo(
                    $"[DialogOutcome] Host world-only displayDialogue target={msg.TargetDialogueName} " +
                    $"NPC={msg.NpcName} (wasInTalk={hostWasInThisTalk})");

                DialogHostApplyGuard.BeginWorldOnly();
                try
                {
                    dw.displayDialogue(msg.TargetDialogueName);
                }
                finally
                {
                    DialogHostApplyGuard.EndWorldOnly();
                }

                // Host Flags dialogue tree advanced — fan out tree so peers converge
                // even if speaker's close packet races or is lost.
                try { DWMPHorde.Sync.DialogTreeSync.TryBroadcastFromNpc(npc); }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[DialogOutcome] tree flush: " + ex.Message);
                }

                // If host wasn't conversing, close UI so we only apply world side-effects
                // without stealing the host's screen.
                if (!hostWasInThisTalk && dw.displayingDialogue)
                {
                    try { dw.close(); }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[DialogOutcome] close after apply: " + ex.Message);
                    }
                }
                return;
            }

            // Path B: legacy index click when host UI matches exactly (also world-only).
            if (dw.npc == null || dw.npc.name != msg.NpcName) return;
            if (dw.currentDialogue == null || dw.currentDialogue.fullName != msg.DialogueName) return;

            int currentBoard = Traverse.Create(dw).Field("currentBoard").GetValue<int>();
            if (currentBoard != msg.BoardIndex) return;
            if (msg.DecisionIndex < 0 || msg.DecisionIndex >= dw.menuOptions.Count) return;

            var btn = dw.menuOptions[msg.DecisionIndex];
            if (btn != null)
            {
                ModRuntime.LegacyInfo($"[DialogOutcome] Host world-only click decision index={msg.DecisionIndex} NPC={msg.NpcName}");
                DialogHostApplyGuard.BeginWorldOnly();
                try
                {
                    btn.getClicked();
                }
                finally
                {
                    DialogHostApplyGuard.EndWorldOnly();
                }
            }
        }

        private void HandleDialogTreeState(DialogTreeStateMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Payload)) return;

            DWMPHorde.Sync.DialogTreeSync.ApplyPayload(msg.Payload);

            // Host: fan-out to other peers after apply.
            if (_role == NetworkRole.Host)
            {
                int from = _currentReceivePlayerId;
                if (from > 0)
                {
                    SendToAllExcept(from, NetMessageType.DialogTreeState,
                        w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void HandleDialogNpcLock(DialogNpcLockMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;

            if (_role == NetworkRole.Host)
            {
                if (msg.Release)
                {
                    NpcDialogueLock.HostRelease(this, msg.NpcName, msg.OwnerPlayerId);
                    return;
                }
                if (msg.IsRequest || !msg.Granted)
                {
                    int owner = msg.OwnerPlayerId > 0 ? msg.OwnerPlayerId : _currentReceivePlayerId;
                    NpcDialogueLock.HostTryGrant(this, msg.NpcName, owner);
                }
                return;
            }

            // Client: mirror host lock state (grant/deny/release).
            if (msg.Release)
            {
                NpcDialogueLock.Release(msg.NpcName, msg.OwnerPlayerId);
                return;
            }

            if (msg.Granted)
            {
                // Peer (or self) holds the lock — track so we block dual talk.
                NpcDialogueLock.TryAcquire(msg.NpcName, msg.OwnerPlayerId);
                return;
            }

            // Denied for the requestor only — other clients ignore.
            if (msg.OwnerPlayerId != LocalPlayerId)
                return;

            NpcDialogueLock.Release(msg.NpcName, msg.OwnerPlayerId);
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                if (dw != null && dw.npc != null && dw.npc.name == msg.NpcName && dw.opened)
                    dw.close();
                if (Player.Instance != null)
                    Player.Instance.displayMessage("Someone is already talking to them…");
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogLock] deny close: " + ex.Message);
            }
        }

        private static NPC FindNpcByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            NPC[] all = UnityEngine.Object.FindObjectsOfType<NPC>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name)
                    return all[i];
            }
            return null;
        }

        /// <summary>
        /// Handles a map marker placed by the remote player.
        /// Both host and client can place markers, so this is handled by either side.
        /// </summary>
        private void HandleMapMarker(MapMarkerMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            int playerId = msg.PlayerId > 0 ? msg.PlayerId : _currentReceivePlayerId;
            if (playerId <= 0) return;
            if (playerId == LocalPlayerId) return; // never treat own marker as remote
            Sync.MultiplayerMapManager.AddRemoteMarker(playerId, pos);
            ModRuntime.LegacyInfo($"[MapMarker] player {playerId} marker at {pos:F1}");
        }

        private void HandleMapMarkerRemove(MapMarkerRemoveMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            int playerId = msg.PlayerId > 0 ? msg.PlayerId : _currentReceivePlayerId;
            if (playerId <= 0) return;
            if (playerId == LocalPlayerId) return;
            Sync.MultiplayerMapManager.RemoveRemoteMarker(playerId, pos);
            ModRuntime.LegacyInfo($"[MapMarker] player {playerId} marker removed at {pos:F1}");
        }

        /// <summary>
        /// Handles a MapElement discovery notification from the remote peer.
        /// Both host and client can discover locations, so this is bidirectional.
        /// </summary>
        private void HandleMapElementDiscovered(MapElementDiscoveredMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ElementName)) return;
            Sync.MultiplayerMapManager.OnRemoteElementDiscovered(msg.ElementName);
        }

        private void HandleOxygenTankStash(OxygenTankStashMessage msg)
        {
            // Any peer: grant empty tank if local player lacks one.
            Patches.OxygenTankStashHandler.Handle();
        }

        private void HandleCompressorTankConvert(CompressorTankConvertMessage msg)
        {
            // Host and clients both convert local empty→full when a peer uses
            // the compressor (sender excluded by Forwardable / no self-receive).
            Patches.CompressorTankConvertHandler.Handle();
        }

        private void HandleJournalBulkSync(JournalBulkSyncMessage msg)
        {
            if (_role != NetworkRole.Client) return;

            // Always queue while still on title / loading — journal exists as a stub and
            // addJournalEntry NREs (user log: Journal.DMD addJournalEntry on title join).
            if (!ClientCanApplyWorldBulk())
            {
                _pendingJournalBulk = msg;
                _hasPendingJournalBulk = true;
                ModLog.Event(LogCat.Session, "Journal bulk queued until client is in-world");
                return;
            }

            try
            {
                Journal journal = Singleton<UI>.Instance?.journal;
                if (journal == null || journal.notesDict == null || journal.keysDict == null
                    || journal.itemsDict == null || journal.journalEntriesDict == null)
                {
                    _pendingJournalBulk = msg;
                    _hasPendingJournalBulk = true;
                    return;
                }

                ApplyJournalBulkSync(msg);
            }
            catch (Exception ex)
            {
                _pendingJournalBulk = msg;
                _hasPendingJournalBulk = true;
                ModLog.Warn(LogCat.Session, "Journal bulk deferred after error: " + ex.Message);
            }
        }

        private void ApplyJournalBulkSync(JournalBulkSyncMessage msg)
        {
            if (!ClientCanApplyWorldBulk())
                return;

            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null || journal.notesDict == null || journal.keysDict == null
                || journal.itemsDict == null || journal.journalEntriesDict == null)
                return;

            if (msg.NoteTypes != null)
            {
                for (int i = 0; i < msg.NoteTypes.Length; i++)
                {
                    string type = msg.NoteTypes[i];
                    if (string.IsNullOrEmpty(type) || journal.notesDict.ContainsKey(type)) continue;
                    Journal.Note note = new Journal.Note();
                    note.type = type;
                    note.timePickedUp = Singleton<Controller>.Instance != null
                        ? Singleton<Controller>.Instance.CurrentTime : 0;
                    journal.notesDict.Add(type, note);
                }
            }

            if (msg.KeyTypes != null)
            {
                for (int i = 0; i < msg.KeyTypes.Length; i++)
                {
                    string type = msg.KeyTypes[i];
                    if (string.IsNullOrEmpty(type) || journal.keysDict.ContainsKey(type)) continue;
                    Journal.Key key = new Journal.Key();
                    key.type = type;
                    journal.keysDict.Add(type, key);
                }
            }

            if (msg.QuestItemTypes != null)
            {
                for (int i = 0; i < msg.QuestItemTypes.Length; i++)
                {
                    string type = msg.QuestItemTypes[i];
                    if (string.IsNullOrEmpty(type) || journal.itemsDict.ContainsKey(type)) continue;
                    Journal.Item item = new Journal.Item();
                    item.type = type;
                    journal.itemsDict.Add(type, item);
                }
            }

            if (msg.JournalEntryTypes != null)
            {
                for (int i = 0; i < msg.JournalEntryTypes.Length; i++)
                {
                    string type = msg.JournalEntryTypes[i];
                    if (string.IsNullOrEmpty(type)) continue;
                    if (journal.journalEntriesDict.ContainsKey(type)) continue;
                    try
                    {
                        // Needs full in-game UI/Controller — never call on title.
                        journal.addJournalEntry(type, noPopup: true);
                    }
                    catch (Exception ex)
                    {
                        ModLog.Warn(LogCat.Session,
                            "addJournalEntry skipped for '" + type + "': " + ex.Message);
                    }
                }
            }

            // Late join: remove world pickups already claimed by the host journal.
            _needsJournalWorldCleanup = true;
            TryJournalWorldCleanup();
            ModRuntime.LegacyInfo(
                $"[BulkSync] Journal applied notes={msg.NoteTypes?.Length ?? 0} keys={msg.KeyTypes?.Length ?? 0} " +
                $"quest={msg.QuestItemTypes?.Length ?? 0} entries={msg.JournalEntryTypes?.Length ?? 0}");
        }

        /// <summary>
        /// Apply journal bulk queued while on title, and despawn collected world pickups
        /// once the client scene has spawned them.
        /// </summary>
        private void TryFlushPendingJournal()
        {
            if (_role != NetworkRole.Client)
                return;

            if (_hasPendingJournalBulk && ClientCanApplyWorldBulk()
                && Singleton<UI>.Instance?.journal != null)
            {
                var pending = _pendingJournalBulk;
                try
                {
                    _hasPendingJournalBulk = false;
                    _pendingJournalBulk = default;
                    ApplyJournalBulkSync(pending);
                    // If still not actually applied (gate / null), re-queue
                    if (!ClientCanApplyWorldBulk())
                    {
                        _pendingJournalBulk = pending;
                        _hasPendingJournalBulk = true;
                    }
                }
                catch (Exception ex)
                {
                    _pendingJournalBulk = pending;
                    _hasPendingJournalBulk = true;
                    ModLog.Warn(LogCat.Session, "Journal flush retry later: " + ex.Message);
                }
            }

            if (_needsJournalWorldCleanup && ClientCanApplyWorldBulk())
                TryJournalWorldCleanup();
        }

        /// <summary>
        /// Destroy ground notes/keys/quest items already present in the local journal
        /// so late joiners do not see (or re-pick) claimed pickups.
        /// </summary>
        private void TryJournalWorldCleanup()
        {
            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null) return;

            // Wait until a world context exists; menu has no pickups to clean.
            if (Player.Instance == null && Singleton<Controller>.Instance == null)
                return;

            if (journal.notesDict != null)
            {
                foreach (var type in journal.notesDict.Keys)
                    DestroyWorldJournalObject(JournalItemKind.Note, type);
            }
            if (journal.keysDict != null)
            {
                foreach (var type in journal.keysDict.Keys)
                    DestroyWorldJournalObject(JournalItemKind.Key, type);
            }
            if (journal.itemsDict != null)
            {
                foreach (var type in journal.itemsDict.Keys)
                    DestroyWorldJournalObject(JournalItemKind.QuestItem, type);
            }

            // One pass after Player/Controller exists is enough for currently loaded
            // locations; live JournalItem messages cover further pickups. Objects in
            // not-yet-streamed chunks are cleaned when the peer re-interacts or when
            // a later live message arrives — rare for same-session late join.
            _needsJournalWorldCleanup = false;
        }

        private void SendJournalBulkSync() => SendJournalBulkSyncTo(-1);

        private void SendJournalBulkSyncTo(int targetPlayerId)
        {
            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null || journal.notesDict == null || journal.keysDict == null
                || journal.itemsDict == null || journal.journalEntriesDict == null)
                return;

            var msg = new JournalBulkSyncMessage();

            var notes = journal.notesDict.Keys;
            msg.NoteTypes = new string[notes.Count];
            int idx = 0;
            foreach (var key in notes)
                msg.NoteTypes[idx++] = key;

            var keys = journal.keysDict.Keys;
            msg.KeyTypes = new string[keys.Count];
            idx = 0;
            foreach (var key in keys)
                msg.KeyTypes[idx++] = key;

            var questItems = journal.itemsDict.Keys;
            msg.QuestItemTypes = new string[questItems.Count];
            idx = 0;
            foreach (var key in questItems)
                msg.QuestItemTypes[idx++] = key;

            var journalEntries = journal.journalEntriesDict.Keys;
            msg.JournalEntryTypes = new string[journalEntries.Count];
            idx = 0;
            foreach (var key in journalEntries)
                msg.JournalEntryTypes[idx++] = key;

            SendBulkOrAll(NetMessageType.JournalBulkSync, w => msg.Serialize(w), targetPlayerId);
        }



        /// <summary>
        /// Peer started/finished vaulting (jumpThroughWindow). Only that player's
        /// proxy ignores Jumpable collisions — not every remote body (3+ safe).
        /// PlayerState JumpWindow clip also disables that proxy's colliders.
        /// </summary>
        private void HandleVaultState(VaultStateMessage msg)
        {
            int playerId = msg.PlayerId > 0 ? msg.PlayerId : _currentReceivePlayerId;
            if (playerId <= 0)
                return;

            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null)
            {
                EnsureRemoteProxy(playerId);
                proxy = GetProxy(playerId);
            }
            if (proxy == null)
                return;

            int jumpableLayer = LayerMask.NameToLayer("Jumpable");
            int layerMask = jumpableLayer >= 0 ? (1 << jumpableLayer) : 16384;

            var proxyCols = proxy.GetComponentsInChildren<Collider>(true);
            var jumpableCols = Physics.OverlapSphere(proxy.transform.position, 500f, layerMask);
            foreach (var proxyCol in proxyCols)
            {
                if (proxyCol == null) continue;
                foreach (var jumpable in jumpableCols)
                {
                    if (jumpable == null) continue;
                    Physics.IgnoreCollision(proxyCol, jumpable, msg.IsVaulting);
                }
            }
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Vault] player {playerId} Jumpable collision {(msg.IsVaulting ? "ignored" : "restored")}");
        }

        /// <summary>Broadcast the host's current weather (rain/fog/lightning) state to all clients.</summary>
        internal void SendWeatherSync() => SendWeatherSyncTo(-1);

        /// <summary>Send weather to one client (targetPlayerId &gt; 0) or all (targetPlayerId &lt;= 0).</summary>
        internal void SendWeatherSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            var rain = Singleton<Rain>.Instance;
            if (rain == null) return;

            var msg = new WeatherSyncMessage
            {
                Raining = rain.Raining,
                RainToday = rain.rainToday,
                TimeToStart = rain.timeToStart,
                LightningTime = rain.lightningTime,
                PreRainLightning = rain.preRainLightning,
                PreRainLightningTime = rain.preRainLightningTime,
                Duration = rain.duration,
                FogFadedOutToday = rain.fogFadedOutToday,
                FogIsActive = rain.fogIsActive,
            };
            if (rain.timeToFadeInFog != null)
            {
                msg.TimeToFadeInFog_Hours = rain.timeToFadeInFog.time;
                msg.TimeToFadeInFog_Day = rain.timeToFadeInFog.day;
            }
            if (rain.timeToFadeOutFog != null)
            {
                msg.TimeToFadeOutFog_Hours = rain.timeToFadeOutFog.time;
                msg.TimeToFadeOutFog_Day = rain.timeToFadeOutFog.day;
            }

            SendBulkOrAll(NetMessageType.WeatherSync, w => msg.Serialize(w), targetPlayerId);
        }

        private void HandleWeatherSync(WeatherSyncMessage msg)
        {
            // Host is authoritative; only clients apply weather packets.
            if (_role != NetworkRole.Client)
                return;

            var rain = Singleton<Rain>.Instance;
            if (rain == null) return;

            bool wasRaining = rain.Raining;
            bool wasFogActive = rain.fogIsActive;

            // Timers / flags first — do NOT pre-set private raining/fogIsActive.
            // startRain() early-outs when raining is already true, which skipped visuals
            // when we wrote the field before calling the Raining setter.
            rain.rainToday = msg.RainToday;
            rain.timeToStart = msg.TimeToStart;
            rain.lightningTime = msg.LightningTime;
            rain.preRainLightning = msg.PreRainLightning;
            rain.preRainLightningTime = msg.PreRainLightningTime;
            rain.duration = msg.Duration;
            rain.fogFadedOutToday = msg.FogFadedOutToday;

            if (rain.timeToFadeInFog == null)
                rain.timeToFadeInFog = new TimeAndDay((int)msg.TimeToFadeInFog_Hours, msg.TimeToFadeInFog_Day);
            else
            {
                rain.timeToFadeInFog.time = (int)msg.TimeToFadeInFog_Hours;
                rain.timeToFadeInFog.day = msg.TimeToFadeInFog_Day;
            }
            if (rain.timeToFadeOutFog == null)
                rain.timeToFadeOutFog = new TimeAndDay((int)msg.TimeToFadeOutFog_Hours, msg.TimeToFadeOutFog_Day);
            else
            {
                rain.timeToFadeOutFog.time = (int)msg.TimeToFadeOutFog_Hours;
                rain.timeToFadeOutFog.day = msg.TimeToFadeOutFog_Day;
            }

            // Visual transitions via public API (startRain / stopRain / fog)
            if (msg.Raining != wasRaining)
            {
                if (msg.Raining)
                    rain.Raining = true;
                else
                    rain.Raining = false;
            }

            // startRain may randomize lightningTime / timeToStart — re-assert host values
            rain.timeToStart = msg.TimeToStart;
            rain.lightningTime = msg.LightningTime;
            rain.duration = msg.Duration;
            rain.preRainLightning = msg.PreRainLightning;
            rain.preRainLightningTime = msg.PreRainLightningTime;

            if (msg.FogIsActive != wasFogActive)
            {
                if (msg.FogIsActive)
                    rain.startFog();
                else
                    rain.stopFog();
            }
            else
            {
                rain.fogIsActive = msg.FogIsActive;
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[WeatherSync] rain={msg.Raining} fog={msg.FogIsActive} today={msg.RainToday}");
        }

        private void SendWorldSnapshot()
        {
            if (!IsConnected)
                return;

            if (Sync.WorldPhysicsSyncService.TryBuildWorldSnapshot(out var msg))
                Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w));
        }

        private void HandlePhysicsState(PhysicsStateMessage state)
        {
            string fromPeer = (_role == NetworkRole.Host) ? "client" : "host";

            // Client event snapshots (SendDoorState / SendTrapState / SendGeneratorState)
            // only carry those arrays and must be applied + fan-out so co-op peers stay
            // in sync. Bulk free-body snapshots from a client may still include stale
            // door/trap/gen copies — strip those so they cannot fight host ownership.
            bool isClientOrigin = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
            int ocPre = state.Objects?.Length ?? 0;
            bool isEventStyle = isClientOrigin && ocPre == 0
                && ((state.Doors != null && state.Doors.Length > 0)
                    || (state.Traps != null && state.Traps.Length > 0)
                    || (state.Generators != null && state.Generators.Length > 0));

            if (isClientOrigin && !isEventStyle)
            {
                if ((state.Doors != null && state.Doors.Length > 0)
                    || (state.Traps != null && state.Traps.Length > 0)
                    || (state.Generators != null && state.Generators.Length > 0))
                {
                    state.Doors = System.Array.Empty<DoorState>();
                    state.Traps = System.Array.Empty<TrapState>();
                    state.Generators = System.Array.Empty<GeneratorState>();
                }
            }

            int oc = state.Objects?.Length ?? 0;
            int dc = state.Doors?.Length ?? 0;
            int tc = state.Traps?.Length ?? 0;
            int gc = state.Generators?.Length ?? 0;
            if ((oc > 0 || dc > 0 || tc > 0 || gc > 0) && ++_physicsRecvLogCounter % 30 == 0 && ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[Physics] objects=" + oc + " doors=" + dc + " traps=" + tc + " gens=" + gc + " from " + fromPeer);
            Sync.WorldPhysicsSyncService.ApplySnapshot(state, fromPeer);

            // Forward client-originated free-body physics and event-style door/trap/gen
            // snapshots to other clients (3+ support).
            if (isClientOrigin && (oc > 0 || isEventStyle))
                SendToAllExcept(_currentReceivePlayerId, NetMessageType.PhysicsState, w => state.Serialize(w));
        }

        private void HandleItemSpawn(ItemSpawnMessage msg)
        {
            string fromPeer = (_role == NetworkRole.Host) ? "client" : "host";
            ModRuntime.LegacyInfo("[ItemSpawn] received " + msg.ItemType + " at " + msg.PosX + "," + msg.PosY + "," + msg.PosZ + " from " + fromPeer);

            if (Singleton<ItemsDatabase>.Instance == null)
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] ItemsDatabase not available");
                return;
            }

            if (!Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] unknown item type: " + msg.ItemType);
                return;
            }

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(msg.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] no prefab for " + msg.ItemType);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            GameObject go = Core.AddPrefab(itemDef.item, pos, rot, null);
            if (go != null)
            {
                Trigger trig = go.GetComponent<Trigger>();
                if (trig != null)
                    trig.setByPlayer = true;
            }
            else
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] Core.AddPrefab returned null for " + msg.ItemType);
            }

            // Forward client-placed items to other clients (3+ support)
            if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                SendToAllExcept(_currentReceivePlayerId, NetMessageType.ItemSpawn, w => msg.Serialize(w));
        }

        private void HandleLightState(LightStateMessage ls)
        {
            string fromPeer = (_role == NetworkRole.Host) ? "client" : "host";
            Sync.WorldPhysicsSyncService.ApplyLightState(ls, fromPeer);
        }

        /// <summary>
        /// Local Player must be active in a loaded chapter. Title + LoadScene have an
        /// inactive/null Player — cloning then spams logs and freezes both dual-box installs.
        /// </summary>
        private static bool CanSpawnRemoteProxies()
        {
            try
            {
                if (Core.mainMenu || Core.loadingGame)
                    return false;
                Player p = Player.Instance;
                if (p == null || p.gameObject == null || !p.gameObject.activeInHierarchy)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureRemoteProxy(int playerId)
        {
            if (playerId <= 0)
                return;
            if (_remoteProxies.ContainsKey(playerId))
                return;
            // Silent skip — do not log every network tick during join load.
            if (!CanSpawnRemoteProxies())
                return;

            RemotePlayerProxy.Spawn(ModRuntime.Log, out RemotePlayerProxy proxy);
            if (proxy != null)
            {
                proxy.PlayerId = playerId;
                _remoteProxies[playerId] = proxy;
                proxy.OnFootstep += (pId, running) => HandleProxyFootstep(pId, running);
                RemoveClonedEmitters(proxy.transform);

                if (Player.Instance != null)
                {
                    var rb = proxy.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.position = Player.Instance.transform.position;
                }

                ModRuntime.LegacyInfo($"[Proxy] Created proxy for player {playerId}");
            }
        }

        public RemotePlayerProxy GetProxy(int playerId)
        {
            _remoteProxies.TryGetValue(playerId, out var proxy);
            return proxy;
        }

        public int RemotePlayerCount => _remoteProxies.Count;

        public IEnumerable<RemotePlayerProxy> GetAllProxies()
        {
            return _remoteProxies.Values;
        }

        /// <summary>
        /// Teleports a remote proxy to an exact world position instantly (no lerp).
        /// Used when entering/exiting dreams so the proxy appears immediately at the
        /// dream spawn instead of slowly lerping across the map.
        /// </summary>
        public void TeleportRemoteProxyTo(Vector3 position, float rotY = 0f, int playerId = -1)
        {
            // Default to all proxies if no player specified
            if (playerId < 0)
            {
                foreach (var kvp in _remoteProxies)
                {
                    TeleportRemoteProxyTo(position, rotY, kvp.Key);
                }
                return;
            }

            if (!_remoteProxies.TryGetValue(playerId, out var proxy))
            {
                EnsureRemoteProxy(playerId);
                if (!_remoteProxies.TryGetValue(playerId, out proxy)) return;
            }

            var rb = proxy.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = position;
                rb.velocity = Vector3.zero;
            }
            proxy.transform.eulerAngles = new Vector3(90f, rotY, 0f);

            proxy.ApplyNetworkState(new PlayerStateNet
            {
                Position = position,
                TorsoFacingY = (short)rotY,
                Locomotion = SecondPlayerAnimController.LocomotionState.Idle,
                FlipX = false,
                LegFacingY = (short)rotY,
                ReverseLegs = false,
                TorsoClip = "Idle",
                LegsClip = ""
            });
        }

        private void DestroyRemoteProxy(int playerId)
        {
            if (!_remoteProxies.TryGetValue(playerId, out var proxy))
                return;

            Destroy(proxy.gameObject);
            _remoteProxies.Remove(playerId);
            ModRuntime.LegacyInfo($"[Proxy] Destroyed proxy for player {playerId}");
        }

        private void HandleProxyFootstep(int playerId, bool running)
        {
            if (!_remoteProxies.TryGetValue(playerId, out var proxy)) return;
            Transform proxyT = proxy.transform;
            float range = running ? 350f : 150f;
            Character.alertInArea(proxyT.position, range, false, 1f);
            PlayProxyFootstepSound(proxy, running);
        }

        private void SendPlayerEffects()
        {
            Player local = Player.Instance;
            if (local == null) return;

            CharBase localCb = local;
            var msg = new PlayerEffectSyncMessage
            {
                HasShadowWard = local.effects != null
                    && local.effects.hasEffectType(CharacterEffectType.shadowWard),
                HasForestSpiritWard = local.effects != null
                    && local.effects.hasEffectType(CharacterEffectType.forestSpiritWard),
                FriendOfTheForest = local.skills != null && local.skills.FriendOfTheForest,
                EnemyOfTheForest = local.skills != null && local.skills.EnemyOfTheForest,
                Invisible = local.invisible,
                IgnoreMe = local.ignoreMe,
                Poisoned = localCb != null && localCb.poisoned,
                Bleeding = localCb != null && localCb.bleeding
            };
            Broadcast(NetMessageType.PlayerEffectSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        private void HandlePlayerEffectSync(PlayerEffectSyncMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;

            proxy.RemoteHasShadowWard = msg.HasShadowWard;
            proxy.RemoteHasForestSpiritWard = msg.HasForestSpiritWard;
            proxy.RemoteHasFriendOfTheForest = msg.FriendOfTheForest;
            proxy.RemoteHasEnemyOfTheForest = msg.EnemyOfTheForest;
            proxy.RemotePoisoned = msg.Poisoned;
            proxy.RemoteBleeding = msg.Bleeding;

            CharBase cb = proxy.GetComponent<CharBase>();
            if (cb != null)
            {
                cb.invisible = msg.Invisible;
                cb.ignoreMe = msg.IgnoreMe;
                // Visual flags only — DoT stays local on owning player.
                cb.poisoned = msg.Poisoned;
                cb.bleeding = msg.Bleeding;
            }
        }

        /// <summary>
        /// Plays a 3D-positioned footstep sound at the proxy's transform.
        /// Detects ground type via the proxy's CharBase, then plays the
        /// appropriate footstep clip using the local player's sound IDs.
        /// Silently returns if the proxy is beyond listen cull distance.
        /// </summary>
        private static void PlayProxyFootstepSound(RemotePlayerProxy proxy, bool running)
        {
            Transform proxyT = proxy.transform;
            Player local = Player.Instance;
            if (local == null) return;
            if (!LocalAudioService.IsNearListener(proxyT.position, LocalAudioService.DefaultMaxAudioDistance))
                return;

            CharacterSounds cs = local.GetComponent<CharacterSounds>();
            if (cs == null) return;

            CharBase proxyCB = proxy.GetComponent<CharBase>();
            if (proxyCB != null)
                proxyCB.checkGround();
            GroundType gt = proxyCB != null ? proxyCB.groundType : GroundType.grass;

            string soundID = null;
            switch (gt)
            {
                case GroundType.grass: soundID = cs.footstepGrass; break;
                case GroundType.wood: soundID = cs.footstepWood; break;
                case GroundType.tiles: soundID = cs.footstepTiles; break;
                case GroundType.bridge: soundID = cs.footstepBridge; break;
                case GroundType.rug: soundID = cs.footstepCarpet; break;
                case GroundType.water: soundID = cs.footstepWater; break;
                case GroundType.infection: soundID = cs.footstepInfection; break;
                default: soundID = cs.footstepGrass; break;
            }

            float volumeModifier = running ? 1.3f : 0.7f;
            float vol = cs.footstepVolume * volumeModifier;

            if (!string.IsNullOrEmpty(soundID))
                AudioController.Play(soundID, proxyT, vol);

            AudioController.Play("walk_clothes_noises", proxyT, vol);

            if (UnityEngine.Random.Range(0f, 1f) > 1f - cs.footHitGroundSoundChance)
            {
                string addSound = gt == GroundType.wood ? "footsteps_wood_add" : "footstep_branches_add";
                AudioController.Play(addSound, proxyT, 1f);
            }
        }

        private void HandlePlayerSound(PlayerSoundMessage msg)
        {
            if (_role != NetworkRole.Host) return;
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;

            Transform proxyT = proxy.transform;
            Vector3 proxyPos = proxyT.position;
            float range = msg.Range;

            // Primary: Physics.OverlapSphere-based alert (finds entities with active colliders)
            Character.alertInArea(proxyPos, range, msg.DangerousSound, msg.Volume, msg.Gunshot);

            // Fallback: directly alert all tracked characters within range, even if their
            // colliders or chunks are briefly inactive when the message arrives.
            Character[] all = CharacterTracker.GetAll();
            for (int i = 0; i < all.Length; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                if (c.deaf || !c.alive) continue;
                if (c.name.Contains("Player") || c.name.Contains("RemotePlayer"))
                    continue;

                float dist = Vector3.Distance(c.transform.position, proxyPos);
                if (dist <= range)
                {
                    if (!c.gameObject.activeSelf)
                        c.gameObject.SetActive(true);
                    if (!c.enabled)
                        c.enabled = true;

                    c.heardSound(proxyPos, range, msg.DangerousSound, msg.Volume, msg.Gunshot);
                }
            }
        }

        private void HandlePlayerScare(PlayerScareMessage msg)
        {
            if (_role != NetworkRole.Host) return;
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;

            Transform proxyT = proxy.transform;
            Character.scareInArea(proxyT.position, msg.Range);
        }

        /// <summary>Host→all clients: current day/time/after-night (periodic).</summary>
        private void SendTimeSync() => SendTimeSyncTo(-1);

        /// <summary>
        /// Host→one peer (targetPlayerId &gt; 0) or all (≤ 0).
        /// Join path uses this so a new client does not wait up to TimeSyncInterval.
        /// </summary>
        internal void SendTimeSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host || !IsConnected)
                return;

            var ctrl = Singleton<Controller>.Instance;
            var msg = new TimeSyncMessage
            {
                CurrentTime = ctrl != null ? ctrl.CurrentTime : 0,
                Day = ctrl != null ? ctrl.day : 1,
                IsAfterNight = ctrl != null && ctrl.isAfterNight
            };
            // Reliable: after-night transitions must not be dropped (client wrongly
            // reporting AfterNightActive=false can clear host morning freeze).
            if (targetPlayerId > 0)
            {
                SendToPlayer(targetPlayerId, NetMessageType.TimeSync, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
            else
            {
                Broadcast(NetMessageType.TimeSync, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }

        private void HandleTimeSync(TimeSyncMessage msg)
        {
            if (_role != NetworkRole.Client)
                return;

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;

            // Apply full isAfterNight from host (true and false).
            // Do NOT call startAfterNight / endAfterNight — those spawn traders,
            // grant rep, and destroy NPCs; host owns that. Client only mirrors
            // freeze flag + timeFreeze VFX so PlayerState.AfterNightActive matches.
            if (msg.IsAfterNight && !ctrl.isAfterNight)
            {
                ctrl.isAfterNight = true;
                try
                {
                    if (Player.Instance != null && Player.Instance.effects != null)
                        ctrl.addAfterNightEffect();
                }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[TimeSync] addAfterNightEffect: " + ex.Message);
                }
            }
            else if (!msg.IsAfterNight && ctrl.isAfterNight)
            {
                ctrl.isAfterNight = false;
                try
                {
                    ctrl.removeAfterNightEffect();
                }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[TimeSync] removeAfterNightEffect: " + ex.Message);
                }
            }

            ctrl.CurrentTime = msg.CurrentTime;
            ctrl.day = msg.Day;

            // Audit C1: never call refreshTime() here — it fires startDay / startAfterNight /
            // night scenario setMe on the client. Clock UI + ambient only.
            if (CoopTimePolicy.ShouldUseRefreshTimeNoLogicOnClientSync)
            {
                try { ctrl.refreshTimeNoLogic(); }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[TimeSync] refreshTimeNoLogic: " + ex.Message);
                }
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[TimeSync] synced day={msg.Day} time={msg.CurrentTime} isAfterNight={msg.IsAfterNight} (no day-chain)");
        }

        private void HandleEntitySound(EntitySoundMessage msg)
        {
            // Host already plays live AI audio; only remote peers apply.
            // (Keep host out even if a packet is mis-routed / Forwardable echo.)
            if (_role == NetworkRole.Host) return;

            Character c = CharacterTracker.FindByStableId(msg.HostId);
            if (c == null || c.sounds == null) return;

            if (!LocalAudioService.IsNearListener(c)) return;

            // Prevent CharacterSounds → AudioController patches from re-forwarding.
            TraverseHack.ApplyingFromNetwork = true;
            TraverseHack.InsideCharacterSounds = true;
            try
            {
                switch (msg.SoundType)
                {
                    case EntitySoundType.Growl:
                        c.sounds.playGrowl();
                        break;
                    case EntitySoundType.Curious:
                        if (!string.IsNullOrEmpty(c.sounds.curious))
                            c.sounds.playSingleInstance(c.sounds.curious);
                        break;
                    case EntitySoundType.Aggressive:
                        if (!string.IsNullOrEmpty(c.sounds.aggressive))
                            c.sounds.playSingleInstance(c.sounds.aggressive);
                        break;
                    case EntitySoundType.Defensive:
                        if (!string.IsNullOrEmpty(c.sounds.defensive))
                            c.sounds.playSingleInstance(c.sounds.defensive);
                        break;
                    case EntitySoundType.Idle:
                        if (string.IsNullOrEmpty(msg.LoopName))
                            c.sounds.destroySounds();
                        else
                            c.sounds.playIdleLoop(msg.LoopName);
                        break;
                    case EntitySoundType.Escaping:
                        c.sounds.playEscapingLoop();
                        break;
                    case EntitySoundType.Attack1:
                        if (!string.IsNullOrEmpty(c.sounds.attack1))
                            c.sounds.play(c.sounds.attack1);
                        break;
                    case EntitySoundType.Attack2:
                        if (!string.IsNullOrEmpty(c.sounds.attack2))
                            c.sounds.play(c.sounds.attack2);
                        break;
                    case EntitySoundType.Death:
                        if (!string.IsNullOrEmpty(c.sounds.death))
                            c.sounds.play(c.sounds.death);
                        break;
                    case EntitySoundType.GetHit:
                        c.sounds.playGetHitByAxe1();
                        break;
                    default:
                        ModRuntime.Log?.LogWarning($"[EntitySound] Unhandled EntitySoundType: {msg.SoundType}");
                        break;
                }
            }
            finally
            {
                TraverseHack.InsideCharacterSounds = false;
                TraverseHack.ApplyingFromNetwork = false;
            }
        }

        private void HandleWorldObjectRemoved(WorldObjectRemovedMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ModRuntime.LegacyInfo("[ObjectRemove] received destroy request for \"" + msg.ObjectName + "\" at " + pos);
            Sync.WorldPhysicsSyncService.DestroyObjectByPos(pos, msg.ObjectName);

            // Forward client-originated removal to other clients (3+ support)
            if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                SendToAllExcept(_currentReceivePlayerId, NetMessageType.WorldObjectRemoved, w => msg.Serialize(w));
        }

        private void HandlePlayerLightState(PlayerLightStateMessage msg)
        {
            ModRuntime.LegacyInfo($"[Light] entered: on={msg.LightOn} type={msg.ItemType} flash={msg.IsFlashlight} emit={msg.HasLightEmitter} itemLight={msg.HasItemLight} ambient={msg.HasAmbientLight}");

            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null)
            {
                ModRuntime.LegacyInfo($"[Light] proxy for player {playerId} is null, can't apply light state");
                return;
            }

            // ---- Flashlight (directional cone) ----
            Transform flashT = proxy.transform.Find("Flashlight");
            if (flashT != null)
            {
                flashT.gameObject.SetActive(msg.IsFlashlight && msg.LightOn);
                if (msg.IsFlashlight && msg.LightOn && msg.LightRadius > 0f)
                {
                    Light2D lt = flashT.GetComponent<Light2D>();
                    if (lt != null)
                    {
                        lt.LightRadius = msg.LightRadius;
                        lt.LightColor = new Color(msg.LightColorR, msg.LightColorG, msg.LightColorB, 0f);
                        if (msg.LightIntensity > 0f)
                            lt.LightIntensity = msg.LightIntensity;
                    }
                }
            }

            // ---- Held item light (candles etc.) — flares are continuous-only (B+), never here ----
            bool itemTypeIsFlare = !string.IsNullOrEmpty(msg.ItemType)
                && msg.ItemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (msg.HasItemLight && !itemTypeIsFlare)
            {
                if (msg.LightOn && msg.LightRadius > 0f)
                {
                    var itemLightState = GetOrCreateState(playerId);
                    GameObject itemLight = itemLightState.ItemLight;
                    if (itemLight == null)
                    {
                        itemLight = new GameObject($"RemoteItemLight_P{playerId}");
                        itemLight.transform.SetParent(proxy.transform);
                        itemLight.transform.localPosition = Vector3.zero;
                        var lt = itemLight.AddComponent<Light2D>();
                        if (lt.LightMaterial == null)
                            lt.LightMaterial = Resources.Load("RadialLight") as Material;
                        lt.lightsPlayer = true;
                        lt.updateGraph = true;
                        itemLightState.ItemLight = itemLight;
                        ModRuntime.LegacyInfo($"[Light] created item light for player {playerId} type={msg.ItemType}");
                    }
                    Light2D itemLt = itemLight.GetComponent<Light2D>();
                    if (itemLt != null)
                    {
                        itemLt.LightRadius = msg.LightRadius;
                        itemLt.LightIntensity = msg.LightIntensity > 0f ? msg.LightIntensity : 1f;
                        itemLt.LightColor = new Color(msg.LightColorR, msg.LightColorG, msg.LightColorB);
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null && !ctrl.logicLights.Contains(itemLt))
                            ctrl.logicLights.Add(itemLt);
                    }
                }
                else
                {
                    DestroyRemoteItemLight(playerId);
                }
            }
            else if (!msg.IsFlashlight && !msg.HasLightEmitter)
            {
                // Switching to a non-light item — destroy any lingering item light
                DestroyRemoteItemLight(playerId);
            }

            // ---- Ambient light dot (PlayerLightDot radius) ----
            // Player component is stripped on the proxy, so access PlayerLightDot
            // directly via its transform name instead of Player.lightDot.
            // HasAmbientLight covers hotbar-only items (lantern in slot, not held).
            // For HasLightEmitter (held lantern), ONLY the emitter provides light;
            // PlayerLightDot is left disabled to prevent double-light vision bugs.
            Transform lightDotT = proxy.transform.Find("PlayerLightDot");
            if (lightDotT == null)
            {
                lightDotT = new GameObject("PlayerLightDot").transform;
                lightDotT.SetParent(proxy.transform, false);
                Light2D newDot = lightDotT.gameObject.AddComponent<Light2D>();
                Material radial = Resources.Load("RadialLight") as Material;
                if (radial != null) newDot.LightMaterial = radial;
            }
            Light2D lightDot = lightDotT.GetComponent<Light2D>();
            if (lightDot != null)
            {
                // HasLightEmitter (held lantern/torch): the emitter provides the
                // correct visual (radial light + particles).  Activating PlayerLightDot
                // on top of it causes a double-light vision cone bug.
                if (msg.HasAmbientLight && msg.LightOn && msg.LightRadius > 0f && !msg.HasLightEmitter)
                {
                    lightDotT.gameObject.SetActive(true);
                    lightDot.LightRadius = msg.LightRadius;
                    lightDot.lightsPlayer = true;
                    lightDot.updateGraph = true;
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null && !ctrl.logicLights.Contains(lightDot))
                        ctrl.logicLights.Add(lightDot);
                }
                else if (!msg.LightOn && !msg.HasAmbientLight && !msg.HasLightEmitter)
                {
                    if (lightDot.lightsPlayer || lightDotT.gameObject.activeInHierarchy)
                    {
                        lightDot.LightRadius = 0f;
                        lightDot.unlightGraphNodes();
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(lightDot);
                        lightDot.lightsPlayer = false;
                        lightDot.updateGraph = false;
                    }
                    lightDotT.gameObject.SetActive(false);
                }
                // Flashlights (IsFlashlight=true): deactivate PlayerLightDot
                // to prevent it from rendering with a stale default radius.
                else if (msg.IsFlashlight)
                {
                    if (lightDot.lightsPlayer || lightDotT.gameObject.activeInHierarchy)
                    {
                        lightDot.LightRadius = 0f;
                        lightDot.unlightGraphNodes();
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(lightDot);
                        lightDot.lightsPlayer = false;
                        lightDot.updateGraph = false;
                    }
                    lightDotT.gameObject.SetActive(false);
                }
            }

            // Clean up torch/lantern emitters when switching to non-emitter item
            // (flashlight, empty hand, etc.) — the HasLightEmitter branch below only
            // cleans before spawning, and !LightOn only cleans if emitterRoot is found.
            if (!msg.HasLightEmitter)
                RemoveAllItemEmitters(proxy.transform);

            // ---- Torch / Lantern light emitter ----
            Transform emitterRoot = proxy.transform.Find("ItemLightEmitter");
            if (msg.HasLightEmitter && msg.LightOn)
            {
                // Full cleanup before spawning: remove mod-named AND cloned emitters
                RemoveAllItemEmitters(proxy.transform);

                // Look up the item in the database to get actual prefab references
                if (!string.IsNullOrEmpty(msg.ItemType))
                {
                    InvItem itemDef = Singleton<ItemsDatabase>.Instance?.getItem(msg.ItemType, instantiate: false);
                    if (itemDef != null && itemDef.lightEmitter != null)
                    {
                        GameObject emitter = Core.AddPrefab(
                            itemDef.lightEmitter,
                            Vector3.zero,
                            Quaternion.Euler(90f, 0f, 0f),
                                proxy.gameObject);
                        if (emitter != null)
                        {
                            emitter.name = "ItemLightEmitter";
                            Collider ec = emitter.GetComponent<Collider>();
                            if (ec != null)
                                ec.enabled = false;

                            EnsureEmitterVisible(emitter);
                            SetupLight2D(emitter);
                            ModRuntime.LegacyInfo("[Light] spawned emitter for " + msg.ItemType);
                        }

                        if (itemDef._particleEmitter != null)
                        {
                            // RemoveAllItemEmitters above uses Core.RemovePooledPrefab
                            // (non-immediate Destroy).  Find() may still return the stale
                            // emitter, making a null-guard skip the re-spawn.  Destroy
                            // immediate here so we always spawn a fresh one.
                            Transform staleP = proxy.transform.Find("ItemParticleEmitter");
                            if (staleP != null)
                                UnityEngine.Object.DestroyImmediate(staleP.gameObject);

                            GameObject pe = Core.AddPrefab(
                                itemDef._particleEmitter,
                                Vector3.zero,
                                Quaternion.Euler(90f, 0f, 0f),
                            proxy.gameObject);
                            if (pe != null)
                            {
                                pe.name = "ItemParticleEmitter";

                                foreach (var ad in pe.GetComponentsInChildren<AutoDestroyParticles>(true))
                                    UnityEngine.Object.Destroy(ad);

                                EnsureEmitterVisible(pe);
                                PlayAllParticleSystems(pe);
                                SetupParticleSorting(pe);

                                ModRuntime.LegacyInfo("[Light] spawned particle emitter for " + msg.ItemType);
                            }
                        }

                        // Wire up per-frame emitter positioning and force an immediate
                        // update so emitters snap to the correct position even before
                        // the first LateUpdate runs.
                        var animCtrl = proxy.GetComponent<Players.SecondPlayerAnimController>();
                        if (animCtrl != null)
                        {
                            animCtrl.SetEmittedItem(itemDef);
                            animCtrl.UpdateEmitterPosition();
                        }
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning("[LightSync] item def or lightEmitter is null for: " + msg.ItemType);
                    }
                }
            }
            else if (!msg.LightOn && emitterRoot != null)
            {
                RemoveAllItemEmitters(proxy.transform);
            }
        }

        /// <summary>
        /// Ensures all Renderers on the emitter GameObject and its children are enabled,
        /// so the fire particle effect and light are actually drawn.
        /// </summary>
        private static void EnsureEmitterVisible(GameObject emitter)
        {
            foreach (Renderer r in emitter.GetComponentsInChildren<Renderer>(true))
                r.enabled = true;
        }

        /// <summary>
        /// Explicitly plays all ParticleSystem components on the emitter and its children.
        /// Core.AddPrefab just calls Instantiate(); if playOnAwake is false on the prefab,
        /// the particles won't start without an explicit Play() call.
        /// </summary>
        private static void PlayAllParticleSystems(GameObject emitter)
        {
            foreach (ParticleSystem ps in emitter.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (!ps.isPlaying)
                    ps.Play(true);
            }
        }

        /// <summary>
        /// Sets the ParticleSystemRenderer sorting order so torch/lantern fire
        /// renders above the player sprite. Without this, particles can appear
        /// behind the player's body (sortingOrder 0) and be invisible.
        /// </summary>
        private static void SetupParticleSorting(GameObject emitter)
        {
            foreach (ParticleSystemRenderer pr in emitter.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                pr.sortingOrder = 300;
                pr.sortingLayerName = "Default";
            }
        }

        /// <summary>
        /// Configures a Light2D emitter so it renders correctly on the proxy.
        /// Sets lightsPlayer=true and registers with Controller.logicLights so
        /// the light mesh is drawn each frame.
        /// </summary>
        private static void SetupLight2D(GameObject emitter)
        {
            Light2D lt = emitter.GetComponent<Light2D>();
            if (lt == null)
                return;

            if (!lt.lightsPlayer)
            {
                lt.lightsPlayer = true;
                lt.updateGraph = true;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !ctrl.logicLights.Contains(lt))
                    ctrl.logicLights.Add(lt);
            }
        }

        private static void RemoveAllItemEmitters(Transform proxyRoot)
        {
            var animCtrl = proxyRoot.GetComponent<Players.SecondPlayerAnimController>();
            if (animCtrl != null)
                animCtrl.ClearEmittedItem();

            Transform emitter = proxyRoot.Find("ItemLightEmitter");
            if (emitter != null)
            {
                Light2D lt = emitter.GetComponent<Light2D>();
                if (lt != null)
                {
                    lt.unlightGraphNodes();
                    if (lt.lightsPlayer)
                    {
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(lt);
                    }
                }
                Core.RemovePooledPrefab(emitter);
            }
            Transform particle = proxyRoot.Find("ItemParticleEmitter");
            if (particle != null)
                Core.RemovePooledPrefab(particle);

            Transform flare = proxyRoot.Find("FlareLight");
            if (flare != null)
            {
                Light2D fl = flare.GetComponent<Light2D>();
                if (fl != null)
                {
                    fl.unlightGraphNodes();
                    if (fl.lightsPlayer)
                    {
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(fl);
                    }
                }
                UnityEngine.Object.DestroyImmediate(flare.gameObject);
            }

            RemoveClonedEmitters(proxyRoot);
        }

        private static void RemoveClonedEmitters(Transform proxyRoot)
        {
            if (proxyRoot == null) return;

            HashSet<string> preserved = new HashSet<string>
            {
                "Flashlight",
                "PlayerLightDot",
                "PlayerFOVLight",
                "PlayerFOVLogic",
                "PlayerFOVLightDot",
                "PlayerShadow",
                "Shadow",
                "ItemLightEmitter",
                "ItemParticleEmitter",
                "FlareLight"
            };

            List<Transform> toDestroy = new List<Transform>();

            foreach (Transform child in proxyRoot)
            {
                if (preserved.Contains(child.name))
                    continue;

                if (child.GetComponent<Light2D>() != null || child.GetComponent<ParticleSystem>() != null)
                    toDestroy.Add(child);
            }

            foreach (Transform t in toDestroy)
                Core.RemovePooledPrefab(t);
        }

        private void HandleThrowableSpawn(ThrowableSpawnMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            Transform sourceT = proxy != null ? proxy.transform : null;
            bool visualOnly = (_role == NetworkRole.Client);
            Sync.WorldPhysicsSyncService.SpawnThrownItem(msg, sourceT, visualOnly);
        }

        private void HandleExplosionTrigger(ExplosionTriggerMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (_role == NetworkRole.Host)
            {
                Sync.WorldPhysicsSyncService.TriggerExplosion(pos, msg.ObjectName, msg.Flaming);
            }
            else
            {
                Sync.WorldPhysicsSyncService.SpawnExplosionVisual(pos, msg.ObjectName, msg.PrefabName, msg.SoundId);
            }
        }

        private void HandlePlayerAudio(PlayerAudioMessage msg)
        {
            if (msg.IsStopSignal)
            {
                // Intentional remote stop — force-stop native + MOS (vanilla 0.5s fade only).
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(msg.ObjectName);
                Sync.WorldPhysicsSyncService.TryStopBodyPushSound(msg.ObjectName);
                return;
            }

            if (string.IsNullOrEmpty(msg.SoundId)) return;

            // Body-push / scrape with ObjectName: single-owner path.
            if (!string.IsNullOrEmpty(msg.ObjectName))
            {
                if (DWMPHorde.Audio.ItemMovingSoundHelper.IsScrapeSuppressed(msg.ObjectName))
                    return;
                // Local free-body pusher hears native ItemSounds only — never arm MOS/PlayerAudio.
                if (DWMPHorde.Audio.ItemMovingSoundHelper.IsLocalOwnedScrape(msg.ObjectName))
                    return;
                // Already playing via PhysicsState→MOS: ignore redundant start (T2).
                if (DWMPHorde.Audio.MovingObjectSoundService.IsPlaying(msg.ObjectName))
                    return;

                Vector3 bodyPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (!float.IsNaN(msg.PosX)
                    && !LocalAudioService.IsNearListener(bodyPos, LocalAudioService.DefaultMaxAudioDistance))
                    return;

                GameObject go = GameObject.Find(msg.ObjectName);
                if (go != null)
                {
                    ItemSounds sounds = go.GetComponent<ItemSounds>();
                    if (sounds != null)
                    {
                        DWMPHorde.Audio.MovingObjectSoundService.NoteMoving(go, msg.ObjectName, sounds);
                        return;
                    }
                    // Fallback when ItemSounds missing: MOS EnsurePlaying by SoundId.
                    float vol = Mathf.Clamp01(msg.Volume);
                    DWMPHorde.Audio.MovingObjectSoundService.EnsurePlaying(go, msg.ObjectName, msg.SoundId, vol);
                    return;
                }
                // Object not found locally — fall through to positional one-shot (legacy).
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool hasPos = !float.IsNaN(msg.PosX);

            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);

            // If no valid position, fall back to proxy transform
            if (!hasPos)
            {
                if (proxy == null) return;
                pos = proxy.transform.position;
            }

            if (!LocalAudioService.IsNearListener(pos, LocalAudioService.DefaultMaxAudioDistance))
                return;

            TraverseHack.ApplyingFromNetwork = true;
            try
            {
                // Parent the AudioObject to the proxy so the game's occlusion system
                // (which may key off parent transforms like CharacterSounds does) can
                // apply proper wall-muffling to forwarded player sounds.
                Transform parent = proxy != null ? proxy.transform : null;
                var audioObj = AudioController.Play(msg.SoundId, pos, parent, Mathf.Clamp01(msg.Volume));
                if (audioObj != null)
                {
                    // Force 3D spatial blend since this sound plays at a world position.
                    audioObj.primaryAudioSource.spatialBlend = 1f;

                    // Enforce a minimum audible range for forwarded sounds.
                    // AudioItems in vanilla are designed for single-player where the
                    // listener IS the player — ranges can be very small (1-5f) or even
                    // 2D. In multiplayer the listener is the remote player, so we need
                    // a wider floor so the sound is audible at typical engagement distances.
                    // Respect AudioItem if its range is already larger than our floor.
                    AudioItem item = AudioController.GetAudioItem(msg.SoundId);
                    float itemMin = (item != null && item.overrideAudioSourceSettings)
                        ? item.audioSource_MinDistance : LocalAudioService.DefaultMinSpatialDistance;
                    float itemMax = (item != null && item.overrideAudioSourceSettings)
                        ? item.audioSource_MaxDistance : LocalAudioService.DefaultMaxSpatialDistance;
                    audioObj.primaryAudioSource.minDistance = Mathf.Max(itemMin, LocalAudioService.DefaultMinSpatialDistance);
                    audioObj.primaryAudioSource.maxDistance = Mathf.Max(itemMax, 100f);
                    audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
                }
            }
            finally { TraverseHack.ApplyingFromNetwork = false; }
        }

        private void HandleMeleeWorldHit(MeleeWorldHitMessage msg)
        {
            if (_role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy attackingProxy = GetProxy(playerId);
            Transform attackerT = attackingProxy != null
                ? attackingProxy.transform
                : (Player.Instance != null ? Player.Instance.transform : null);

            int damage = SanitizePeerDamage(msg.Damage, "MeleeWorldHit");

            // Debounce check for doors/windows: suppress FX for rapid successive
            // hits (shotgun pellets) but still apply damage (normalHit=false).
            bool suppressed = false;
            if (msg.TargetType == 0 || msg.TargetType == 1)
            {
                string key = $"{playerId}_{msg.TargetType}_{pos.x:F1}_{pos.y:F1}_{pos.z:F1}";
                float now = Time.time;
                if (_meleeHitDebounce.TryGetValue(key, out float lastTime) &&
                    (now - lastTime) < MELEE_HIT_DEBOUNCE_SEC)
                    suppressed = true;
                _meleeHitDebounce[key] = now;
            }

            if (msg.TargetType == 0)
            {
                Door door = FindDoorByPos(pos);
                if (door == null) return;
                door.getHit(damage, attackerT, !suppressed, false);
                return;
            }

            if (msg.TargetType == 1)
            {
                Window window = FindWindowByPos(pos);
                if (window == null) return;
                window.getHit(damage, attackerT, !suppressed);
                return;
            }

            if (msg.TargetType == 2)
            {
                Collider[] nearby = Physics.OverlapSphere(pos, 1f);
                for (int i = 0; i < nearby.Length; i++)
                {
                    if (nearby[i] == null) continue;
                    Item item = nearby[i].GetComponentInParent<Item>();
                    if (item == null || !item.destructible) continue;
                    item.getHit(damage, attackerT, true);
                    return;
                }
            }
        }

        private void HandleGasTrailSpawn(GasTrailSpawnMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Sync.WorldPhysicsSyncService.SpawnGasTrail(pos);
        }

        private void HandleGasIgnite(GasIgniteMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Sync.WorldPhysicsSyncService.IgniteGasAtPos(pos);
        }

        private void HandleEntityBurning(EntityBurningMessage msg)
        {
            var entity = Sync.CharacterTracker.FindByStableId(msg.EntityId);
            if (entity == null) return;
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (msg.IsBurning)
                {
                    var burn = entity.GetComponent<Burn>();
                    if (burn == null)
                    {
                        entity.gameObject.AddComponent<Burn>().burnTime = msg.BurnTime;
                    }
                }
                else
                {
                    var burn = entity.GetComponent<Burn>();
                    if (burn != null)
                    {
                        burn.stop();
                    }
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        private void HandlePlayerBurning(PlayerBurningMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (msg.IsBurning)
                {
                    var burn = proxy.GetComponent<Burn>();
                    if (burn == null)
                    {
                        burn = proxy.gameObject.AddComponent<Burn>();
                        burn.burnTime = msg.BurnTime;
                        ModRuntime.LegacyInfo($"[PlayerBurnSync] applied Burn to proxy for player {playerId}");
                    }
                }
                else
                {
                    var burn = proxy.GetComponent<Burn>();
                    if (burn != null)
                    {
                        burn.stop();
                        ModRuntime.LegacyInfo($"[PlayerBurnSync] removed Burn from proxy for player {playerId}");
                    }
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        /// <summary>
        /// Host: push existing flammable liquid trails (+ burning state) to a joiner
        /// so late join sees poured gasoline already in the world.
        /// </summary>
        internal void SendGasStateTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;

            Liquid[] all = UnityEngine.Object.FindObjectsOfType<Liquid>();
            int trails = 0, burning = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Liquid liquid = all[i];
                if (liquid == null || !liquid.flammable) continue;

                Vector3 p = liquid.transform.position;
                // Cap bulk so join flood stays reasonable (trails are dense when pouring).
                if (trails >= 256) break;

                var trailMsg = new GasTrailSpawnMessage { PosX = p.x, PosY = p.y, PosZ = p.z };
                SendBulkOrAll(NetMessageType.GasTrailSpawn, w => trailMsg.Serialize(w), targetPlayerId);
                trails++;

                if (liquid.burning)
                {
                    var igniteMsg = new GasIgniteMessage { PosX = p.x, PosY = p.y, PosZ = p.z };
                    SendBulkOrAll(NetMessageType.GasIgnite, w => igniteMsg.Serialize(w), targetPlayerId);
                    burning++;
                }
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {trails} gas trails ({burning} burning) to player {targetPlayerId}"
                : $"[BulkSync] Sent {trails} gas trails ({burning} burning) to all clients");
        }

        private void HandleLiquidStopBurning(LiquidStopBurningMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            var hits = Physics.OverlapSphere(pos, 1.5f);
            for (int i = 0; i < hits.Length; i++)
            {
                var liq = hits[i].GetComponent<Liquid>();
                if (liq != null)
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    try
                    {
                        Traverse.Create(liq).Method("stopBurning").GetValue();
                    }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                    break;
                }
            }
        }

        private void HandleExplosionSpawnObject(ExplosionSpawnObjectMessage msg)
        {
            if (string.IsNullOrEmpty(msg.PrefabName)) return;
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            // Local Explodes already ran spawnObjects (stomp or SpawnExplosionVisual) —
            // skip host-echoed secondaries so the stomper/remote doesn't double debris.
            if (ExplosionSpawnFlagTracker.ShouldSkipExplosionSpawnObject(pos))
            {
                ModRuntime.LegacyInfo("[ExplosionSpawnRecv] skip (local FX recent) " + msg.PrefabName + " at " + pos);
                return;
            }
            // SpawnObject often arrives before ExplosionTrigger (same-frame host onActivate).
            // If a local Explodes with secondaries still exists, let SpawnExplosionVisual
            // own spawnObjects() — applying both piles white debris twice on remotes.
            Explodes localExpl = null;
            Collider[] nearFx = Physics.OverlapSphere(pos, 1.5f);
            for (int i = 0; i < nearFx.Length; i++)
            {
                if (nearFx[i] == null) continue;
                Explodes e = nearFx[i].GetComponentInParent<Explodes>();
                if (e != null) { localExpl = e; break; }
            }
            if (localExpl != null && localExpl.spawnObject != null)
            {
                ModRuntime.LegacyInfo("[ExplosionSpawnRecv] skip (local Explodes owns secondaries) " + msg.PrefabName + " at " + pos);
                return;
            }
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                string[] prefixes = { "", "Items/", "FX/", "Environment/", "Particles/", "Dummies/", "Fire/", "Weapons/" };
                UnityEngine.Object prefab = null;
                string foundPath = null;
                foreach (var prefix in prefixes)
                {
                    string path = "Prefabs/" + prefix + msg.PrefabName;
                    prefab = Resources.Load(path);
                    if (prefab != null)
                    {
                        foundPath = path;
                        ModRuntime.LegacyInfo("[ExplosionSpawnRecv] found prefab at " + path);
                        break;
                    }
                }
                if (prefab != null)
                {
                    Core.AddPrefab(prefab, pos, rot, null, false);
                    ModRuntime.LegacyInfo("[ExplosionSpawnRecv] spawned " + msg.PrefabName + " at " + pos + " rot=" + rot.eulerAngles + " (loaded from " + foundPath + ")");
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[ExplosionSpawnRecv] prefab=" + msg.PrefabName + " NOT FOUND in any prefix at " + pos + " rot=" + rot.eulerAngles + " — falling back to Core.AddPrefab(" + msg.PrefabName + ")");
                    Core.AddPrefab(msg.PrefabName, pos, rot, null, false);
                }
            }
            finally { TraverseHack.SetExplicitFlag(prevHack); }
        }

        private void HandlePlayerAnimation(PlayerAnimationMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;
            var animComp = proxy.GetComponent<SecondPlayerAnimController>();
            if (animComp == null) return;

            var prev = Sync.WorldPhysicsSyncService._suppressBroadcast;
            Sync.WorldPhysicsSyncService._suppressBroadcast = true;
            try
            {
                if (!string.IsNullOrEmpty(msg.TorsoClip))
                {
                    try
                    {
                        HarmonyLib.Traverse.Create(animComp).Method("PlayTorso", new object[] { msg.TorsoClip }).GetValue();
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                // Don't restart walk legs while torso is vault/beartrap/etc. (vanilla hides legs).
                bool hideLegs = !string.IsNullOrEmpty(msg.TorsoClip)
                    && SecondPlayerAnimController.ShouldHideLegsForTorso(msg.TorsoClip);
                if (!hideLegs && !string.IsNullOrEmpty(msg.LegsClip))
                {
                    try
                    {
                        HarmonyLib.Traverse.Create(animComp).Method("PlayLegs", new object[] { msg.LegsClip }).GetValue();
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                Sync.WorldPhysicsSyncService._suppressBroadcast = prev;
            }
        }

        private void HandlePlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) return;
            if (string.IsNullOrEmpty(msg.LibraryName)) return;

            var lib = Resources.Load(msg.LibraryName, typeof(tk2dSpriteAnimation)) as tk2dSpriteAnimation;
            if (lib == null)
            {
                ModRuntime.Log?.LogWarning("[AnimLib] library not found: " + msg.LibraryName);
                return;
            }

            tk2dSpriteAnimator anim = proxy.GetComponent<tk2dSpriteAnimator>();
            if (anim == null) return;

            var prev = Sync.WorldPhysicsSyncService._suppressBroadcast;
            Sync.WorldPhysicsSyncService._suppressBroadcast = true;
            try
            {
                HarmonyLib.Traverse.Create(anim).Property("Library").SetValue(lib);
                ModRuntime.Log?.LogDebug("[AnimLib] applied library: " + msg.LibraryName);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError("[AnimLib] failed to set library: " + ex);
            }
            finally
            {
                Sync.WorldPhysicsSyncService._suppressBroadcast = prev;
            }
        }

        /// <summary>
        /// Dispatches a player-specific message that was forwarded from another
        /// client. _currentReceivePlayerId has already been set to the original
        /// sender's PlayerId before this is called.
        /// </summary>
        private void DispatchRemotePlayerForward(NetMessageType innerType, byte[] innerPayload)
        {
            switch (innerType)
            {
                case NetMessageType.PlayerLightState:
                    HandlePlayerLightState(PlayerLightStateMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAudio:
                    HandlePlayerAudio(PlayerAudioMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerFiredWeapon:
                    HandlePlayerFiredWeapon(PlayerFiredWeaponMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAnimation:
                    HandlePlayerAnimation(PlayerAnimationMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerBurning:
                    HandlePlayerBurning(PlayerBurningMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerDied:
                    HandlePlayerDied(PlayerDiedMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerEffectSync:
                    HandlePlayerEffectSync(PlayerEffectSyncMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAnimLibrary:
                    HandlePlayerAnimLibrary(PlayerAnimLibraryMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.ThrowableSpawn:
                    HandleThrowableSpawn(ThrowableSpawnMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.DreamEnded:
                    HandleDreamEnded(DreamEndedMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.FinalDreamsceneDeath:
                    HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage.Deserialize(new NetReader(innerPayload)));
                    break;
            }
        }

        private void HandleBulletImpact(BulletImpactMessage msg)
        {
            if (string.IsNullOrEmpty(msg.PrefabName)) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[BulletFX] HandleBulletImpact: {msg.PrefabName} pool={msg.PoolName} pos={pos}");

            // Wrap in ApplyingFromNetwork to prevent HitscanBloodPatch and similar
            // patches from re-forwarding this blood back to the sender.
            TraverseHack.ApplyingFromNetwork = true;
            try
            {
                if (string.IsNullOrEmpty(msg.PoolName))
                    Core.AddPrefab(msg.PrefabName, pos, rot, null);
                else
                    Core.AddPooledPrefab(msg.PoolName, msg.PrefabName, pos, rot);
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }

            // Play bullet impact sound on the remote so the receiver hears it
            AudioController.Play("bullet_hit_1", pos);
        }

        private void HandlePlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            RemotePlayerProxy proxy = GetProxy(playerId);
            if (proxy == null) { ModRuntime.LegacyInfo($"[WeaponFire] handle: no proxy for player {playerId}"); return; }

            InvItem itemDef = null;
            try { itemDef = Singleton<ItemsDatabase>.Instance?.getItem(msg.ItemType, instantiate: false); }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Network] getItem: " + ex.Message);
            }
            if (itemDef == null) { ModRuntime.LegacyInfo("[WeaponFire] handle: item not found: " + msg.ItemType); return; }
            if (!itemDef.isFirearm) { ModRuntime.LegacyInfo("[WeaponFire] handle: not a firearm: " + msg.ItemType); return; }

            Transform proxyT = proxy.transform;

            Vector3 muzzlePos = proxyT.position
                + proxyT.up * itemDef.muzzleOffset.y
                + proxyT.right * itemDef.muzzleOffset.x;
            Quaternion muzzleRot = Quaternion.Euler(90f, msg.AimY, 0f);

            ModRuntime.LegacyInfo("[WeaponFire] handle: spawning muzzle for " + msg.ItemType + " count=" + msg.ProjectileCount + " aimY=" + msg.AimY);

            if (itemDef.muzzlePrefab != null)
            {
                string name = itemDef.muzzlePrefab.name;
                if (!string.IsNullOrEmpty(name))
                    Core.AddPooledPrefab("FX", name, muzzlePos, muzzleRot);
            }

            if (itemDef.muzzleParticles != null)
            {
                string name = itemDef.muzzleParticles.name;
                if (!string.IsNullOrEmpty(name))
                    Core.AddPooledPrefab("FX", name, muzzlePos, muzzleRot);
            }

            if (!itemDef.noMuzzleFlash)
            {
                Core.AddPrefab("FX/Muzzle/PistolFlash", proxyT.position + proxyT.up, muzzleRot, null, worldSpace: true);
            }

            // Shot audio: local shooter plays parentless attackSound; peers need it here
            // (HandlePlayerFiredWeapon is VFX-only otherwise). ApplyingFromNetwork prevents
            // PlayerAudio re-forward loops.
            if (!string.IsNullOrEmpty(itemDef.attackSound))
            {
                Vector3 shotPos = proxyT.position;
                if (LocalAudioService.IsNearListener(shotPos, LocalAudioService.DefaultMaxAudioDistance))
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    try
                    {
                        var audioObj = AudioController.Play(itemDef.attackSound, shotPos, proxyT, 1f);
                        if (audioObj != null && audioObj.primaryAudioSource != null)
                        {
                            audioObj.primaryAudioSource.spatialBlend = 1f;
                            audioObj.primaryAudioSource.minDistance =
                                Mathf.Max(audioObj.primaryAudioSource.minDistance, LocalAudioService.DefaultMinSpatialDistance);
                            audioObj.primaryAudioSource.maxDistance =
                                Mathf.Max(audioObj.primaryAudioSource.maxDistance, 100f);
                            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
                        }
                    }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                }
            }

            // Friendly-fire damage is applied only via ProxyDamagePatch (real collider hits
            // on the remote proxy → DamagePlayer / FriendlyFire). Cone damage here used to
            // double-apply with that path and could hit every peer incorrectly.
        }

        private void HandleDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Guid) || string.IsNullOrEmpty(msg.PrefabPath) || string.IsNullOrEmpty(msg.ItemType))
                return;
            if (Players.DroppedItemIdentifier.FindById(msg.Guid) != null)
                return;

            // Only the host's drops are authoritative. Client drops are local-only.
            // Receiving a client drop on the host would spawn a second copy,
            // causing item multiplication (both sides can pick up their copy).
            // Allow client drops — GUID-based system (DroppedItemIdentifier +
            // _consumedDropGuids) prevents multiplication: when one player picks
            // up, the other player's copy is destroyed via DroppedItemPickupMessage.
            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] unknown item type: " + msg.ItemType);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            GameObject go = Core.AddPrefab(msg.PrefabPath, pos, rot, Core.ItemContainer);
            if (go == null) return;

            Inventory inv = go.GetComponent<Inventory>();
            if (inv == null || inv.slots == null || inv.slots.Count == 0) return;

            InvSlot slot = inv.slots[0];
            slot.inventory = inv;

            InvItemClass item = new InvItemClass(msg.ItemType, 1f, msg.Amount);
            if (item.baseClass == null)
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] failed to assign baseClass for " + msg.ItemType);
                return;
            }

            slot.createItem(item);

            if (!InvItemClass.isNull(slot.invItem))
            {
                slot.invItem.durability = msg.Durability;
                if (slot.invItem.baseClass.hasAmmo)
                    slot.invItem.ammo = msg.Ammo;
            }

            Core.addToSaveable(go, isDynamic: true);
            Singleton<WorldGrid>.Instance?.registerToNode(go);

            var ident = go.AddComponent<Players.DroppedItemIdentifier>();
            ident.Id = msg.Guid;
            Players.DroppedItemIdentifier.Register(ident);

            ModRuntime.LegacyInfo("[DroppedItemSpawn] " + msg.ItemType + " x" + msg.Amount + " guid=" + msg.Guid);
        }

        /// <summary>Resets consumed GUID tracking (call on scene change / disconnect).</summary>
        public static void ResetConsumedDropGuids()
        {
            _consumedDropGuids.Clear();
        }

        private void HandleDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Guid)) return;

            // Mark consumed even if we already did (idempotent). Always try destroy
            // so a late packet still removes a lingering world copy.
            bool first = _consumedDropGuids.Add(msg.Guid);
            if (!first)
                ModRuntime.LegacyInfo("[DroppedItemPickup] already consumed: " + msg.Guid);

            var ident = Players.DroppedItemIdentifier.FindById(msg.Guid);
            if (ident == null || ident.gameObject == null) return;

            ModRuntime.LegacyInfo("[DroppedItemPickup] removing guid=" + msg.Guid);
            UnityEngine.Object.Destroy(ident.gameObject);
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        // ====== Bulk Sync Methods ======

        /// <summary>
        /// Host helper: send reliable bulk payload to one peer, or all peers if targetPlayerId &lt;= 0.
        /// </summary>
        private void SendBulkOrAll(NetMessageType type, System.Action<NetWriter> writeBody, int targetPlayerId)
        {
            if (targetPlayerId > 0)
                SendToPlayer(targetPlayerId, type, writeBody, LiteNetLib.DeliveryMethod.ReliableOrdered);
            else
                SendToAll(type, writeBody, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send all current game flags to all clients.</summary>
        internal void SendFlagBulkSync() => SendFlagBulkSyncTo(-1);

        internal void SendFlagBulkSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            var dict = flags.flagsDict;
            int count = Mathf.Min(dict.Count, 4096);
            var msg = new FlagBulkSyncMessage
            {
                FlagCount = count,
                FlagNames = new string[count],
                FlagIsTrue = new bool[count],
                FlagAmounts = new int[count]
            };
            int i = 0;
            foreach (var kvp in dict)
            {
                if (i >= count) break;
                msg.FlagNames[i] = kvp.Key;
                msg.FlagIsTrue[i] = kvp.Value.isTrue;
                msg.FlagAmounts[i] = kvp.Value.amount;
                i++;
            }
            SendBulkOrAll(NetMessageType.FlagBulkSync, w => msg.Serialize(w), targetPlayerId);
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {count} flags to player {targetPlayerId}"
                : $"[BulkSync] Sent {count} flags to all clients");
        }

        private void HandleFlagBulkSync(FlagBulkSyncMessage msg)
        {
            if (_role != NetworkRole.Client)
                return;

            if (!ClientCanApplyWorldBulk() || Singleton<Flags>.Instance == null)
            {
                // Join bulk often arrives before the client has loaded into a world.
                _pendingFlagBulk = msg;
                _hasPendingFlagBulk = true;
                ModRuntime.LegacyInfo($"[BulkSync] Flags queued (in-world=" + ClientCanApplyWorldBulk() + ")");
                return;
            }

            ApplyFlagBulkSync(msg);
        }

        private void ApplyFlagBulkSync(FlagBulkSyncMessage msg)
        {
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            for (int i = 0; i < msg.FlagCount; i++)
            {
                string name = msg.FlagNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (flags.flagsDict.TryGetValue(name, out var flag))
                {
                    flag.isTrue = msg.FlagIsTrue[i];
                    flag.amount = msg.FlagAmounts[i];
                }
                else
                {
                    flags.setFlag(name, msg.FlagIsTrue[i]);
                    flags.setFlag(name, msg.FlagAmounts[i]);
                }
            }
            ModRuntime.LegacyInfo($"[BulkSync] Applied {msg.FlagCount} flags");
        }

        /// <summary>
        /// Apply flag bulk/deltas that arrived before <see cref="Flags"/> existed (menu/load).
        /// Called every frame after network poll. Gate: not on title menu.
        /// </summary>
        private void TryFlushPendingFlags()
        {
            if (_role != NetworkRole.Client)
                return;
            // Same gate as journal — Flags may exist on title but world not ready.
            if (!ClientCanApplyWorldBulk())
                return;
            if (Singleton<Flags>.Instance == null)
                return;

            if (_hasPendingFlagBulk)
            {
                _hasPendingFlagBulk = false;
                try
                {
                    ApplyFlagBulkSync(_pendingFlagBulk);
                }
                catch (Exception ex)
                {
                    _hasPendingFlagBulk = true;
                    ModLog.Warn(LogCat.Session, "Flag bulk flush retry later: " + ex.Message);
                    return;
                }
                _pendingFlagBulk = default;
            }

            if (_pendingFlagDeltas.Count > 0)
            {
                for (int i = 0; i < _pendingFlagDeltas.Count; i++)
                    ApplyFlagSyncMessage(_pendingFlagDeltas[i]);
                _pendingFlagDeltas.Clear();
            }
        }

        /// <summary>Send all NPC reputations to all clients.</summary>
        internal void SendReputationBulkSync() => SendReputationBulkSyncTo(-1);

        internal void SendReputationBulkSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            int count = flags.npcStates.Count;
            var msg = new ReputationBulkSyncMessage
            {
                NpcCount = count,
                NpcNames = new string[count],
                Reputations = new int[count],
                Dead = new bool[count]
            };
            for (int i = 0; i < count; i++)
            {
                msg.NpcNames[i] = flags.npcStates[i].name;
                msg.Reputations[i] = flags.npcStates[i].reputation;
                msg.Dead[i] = flags.npcStates[i].dead;
            }
            SendBulkOrAll(NetMessageType.ReputationBulkSync, w => msg.Serialize(w), targetPlayerId);
        }

        private void HandleReputationBulkSync(ReputationBulkSyncMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            if (msg.NpcNames == null) return;

            for (int i = 0; i < msg.NpcCount && i < msg.NpcNames.Length; i++)
            {
                string name = msg.NpcNames[i];
                if (string.IsNullOrEmpty(name)) continue;

                var state = flags.getNPCState(name);
                if (state == null)
                {
                    // Create state for shared NPCs not yet in local list (dead flag + rep).
                    state = new Flags.NPCState
                    {
                        name = name,
                        wantsToTalk = true
                    };
                    flags.npcStates.Add(state);
                }

                // Dead is world/story state — apply for all NPCs.
                if (msg.Dead != null && i < msg.Dead.Length)
                    state.dead = msg.Dead[i];

                // Model C: never overwrite morning-trader standing with host bulk.
                if (Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(name))
                    continue;

                if (msg.Reputations != null && i < msg.Reputations.Length)
                    state.reputation = msg.Reputations[i];
            }
            ModRuntime.LegacyInfo($"[BulkSync] Reputation bulk applied ({msg.NpcCount} entries, night-traders skipped for rep)");
        }

        /// <summary>Send current night scenario + active event to all clients.</summary>
        internal void SendScenarioBulkSync() => SendScenarioBulkSyncTo(-1);

        internal void SendScenarioBulkSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null || ns.currentScenario == null) return;
            SendBulkOrAll(NetMessageType.ScenarioStateSync,
                w => new ScenarioSyncMessage { ScenarioName = ns.currentScenario.name }.Serialize(w),
                targetPlayerId);
        }

        private void HandleScenarioStateSync(ScenarioSyncMessage msg)
        {
            // Join bulk used ScenarioStateSync but never applied the scenario name
            // (only logged). Same payload as live ScenarioSync — apply it.
            ApplyScenarioSync(msg);
            ModRuntime.LegacyInfo($"[BulkSync] Scenario applied: {msg.ScenarioName}");
        }

        /// <summary>Send hideout oven enable states to all clients.</summary>
        internal void SendHideoutStateSync() => SendHideoutStateSyncTo(-1);

        internal void SendHideoutStateSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            var ovens = UnityEngine.Object.FindObjectsOfType<ExperienceMachine>(true);
            int count = Mathf.Min(ovens.Length, 128);
            var msg = new HideoutStateSyncMessage
            {
                OvenCount = count,
                PosX = new float[count],
                PosY = new float[count],
                PosZ = new float[count],
                IsOn = new bool[count]
            };
            for (int i = 0; i < count; i++)
            {
                msg.PosX[i] = ovens[i].transform.position.x;
                msg.PosY[i] = ovens[i].transform.position.y;
                msg.PosZ[i] = ovens[i].transform.position.z;
                msg.IsOn[i] = ovens[i].isOn;
            }
            SendBulkOrAll(NetMessageType.HideoutStateSync, w => msg.Serialize(w), targetPlayerId);
        }

        private void HandleHideoutStateSync(HideoutStateSyncMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (msg.OvenCount <= 0 || msg.PosX == null) return;

            var machines = UnityEngine.Object.FindObjectsOfType<ExperienceMachine>(true);
            for (int i = 0; i < msg.OvenCount; i++)
            {
                Vector3 pos = new Vector3(msg.PosX[i], msg.PosY[i], msg.PosZ[i]);
                bool wantOn = msg.IsOn != null && i < msg.IsOn.Length && msg.IsOn[i];
                for (int j = 0; j < machines.Length; j++)
                {
                    var em = machines[j];
                    if (em == null) continue;
                    if (Vector3.Distance(em.transform.position, pos) >= 1f) continue;
                    if (wantOn && !em.isOn)
                        em.enable();
                    else if (!wantOn && em.isOn)
                        em.disable();
                    break;
                }
            }
            ModRuntime.LegacyInfo($"[BulkSync] Hideout ovens applied count={msg.OvenCount}");
        }

        /// <summary>Send current workbench level to all clients.</summary>
        internal void SendWorkbenchLevelSync() => SendWorkbenchLevelSyncTo(-1);

        internal void SendWorkbenchLevelSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            int level = Singleton<Controller>.Instance != null
                ? Singleton<Controller>.Instance.workbenchLevel : 1;
            SendBulkOrAll(NetMessageType.WorkbenchLevelSync,
                w => new WorkbenchLevelMessage { Level = level }.Serialize(w),
                targetPlayerId);
        }

        private void HandleWorkbenchLevelSync(WorkbenchLevelMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            ApplyWorkbenchLevel(msg.Level);
        }

        /// <summary>Send map markers and discoveries to all clients.</summary>
        internal void SendMapStateSync() => SendMapStateSyncTo(-1);

        internal void SendMapStateSyncTo(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;

            // Host local markers as player 1 + all known remote markers keyed by owner.
            var positions = new List<Vector3>(64);
            var owners = new List<int>(64);

            foreach (var p in Sync.MultiplayerMapManager.LocalMarkers)
            {
                positions.Add(p);
                owners.Add(1); // host LocalPlayerId
            }
            foreach (var kvp in Sync.MultiplayerMapManager.RemoteMarkers)
            {
                int pid = kvp.Key;
                if (pid <= 0) continue;
                foreach (var p in kvp.Value)
                {
                    positions.Add(p);
                    owners.Add(pid);
                }
            }

            int mc = Mathf.Min(positions.Count, 4096);
            var msg = new MapStateSyncMessage
            {
                MarkerCount = mc,
                MarkerPosX = new float[mc],
                MarkerPosY = new float[mc],
                MarkerPosZ = new float[mc],
                MarkerPlayerIds = new int[mc],
                MarkerTexts = new string[mc],
                DiscoveryCount = 0,
                DiscoveryElementNames = new string[0]
            };
            for (int i = 0; i < mc; i++)
            {
                msg.MarkerPosX[i] = positions[i].x;
                msg.MarkerPosY[i] = positions[i].y;
                msg.MarkerPosZ[i] = positions[i].z;
                msg.MarkerPlayerIds[i] = owners[i];
                msg.MarkerTexts[i] = "";
            }
            SendBulkOrAll(NetMessageType.MapStateSync, w => msg.Serialize(w), targetPlayerId);
        }

        private void HandleMapStateSync(MapStateSyncMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            for (int i = 0; i < msg.MarkerCount; i++)
            {
                Vector3 pos = new Vector3(msg.MarkerPosX[i], msg.MarkerPosY[i], msg.MarkerPosZ[i]);
                int pid = msg.MarkerPlayerIds != null && i < msg.MarkerPlayerIds.Length
                    ? msg.MarkerPlayerIds[i] : 1;
                if (pid <= 0 || pid == LocalPlayerId) continue;
                Sync.MultiplayerMapManager.AddRemoteMarker(pid, pos);
            }
            // Discoveries reserved for future MapStateSync population
            for (int i = 0; i < msg.DiscoveryCount; i++)
            {
                string name = msg.DiscoveryElementNames?[i];
                if (!string.IsNullOrEmpty(name))
                    Sync.MultiplayerMapManager.OnRemoteElementDiscovered(name);
            }
        }

        private void HandlePlayerSkillsSync(PlayerSkillsSyncMessage msg)
        {
            // Domain 2.10: skills/XP are intentionally per-player (not shared).
            // Nothing in the mod sends this type; handler is a permanent no-op so a
            // stale or third-party packet cannot overwrite local progression.
            // Persistence: ClientStateBackup (XP, level, chosen skills, skill points).
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo(
                    $"[SkillsSync] ignored peer XP={msg.Experience} lvl={msg.CurrentLevel} (per-player; use ClientStateBackup)");
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (_role == NetworkRole.Host)
            {
                // P3.7: reject joins mid-dream unless config allows (default: reject)
                bool allowDreamJoin = Config.ModConfig.AllowJoinDuringDream != null
                    && Config.ModConfig.AllowJoinDuringDream.Value;
                if (!allowDreamJoin && Sync.DreamSession.ShouldRejectNewConnections)
                {
                    ModLog.Event(LogCat.Network, "Rejecting connection — dream session active");
                    request.Reject();
                    return;
                }

                int maxPlayers = Config.ModConfig.MaxPlayers != null ? Config.ModConfig.MaxPlayers.Value : 8;
                // Host counts as 1; _peers are clients
                if (_peers.Count + 1 >= maxPlayers)
                {
                    ModLog.Event(LogCat.Network, $"Rejecting connection — max players ({maxPlayers}) reached");
                    request.Reject();
                    return;
                }

                request.AcceptIfKey(Config.ModConfig.GetConnectionKey());
                ModLog.Event(LogCat.Network, $"Connection accepted (will be peer #{_peers.Count + 1})");
            }
            else
            {
                request.Reject();
            }
        }

        /// <summary>Clear host session maps that are not covered by NetworkResetRegistry.</summary>
        internal void ResetSessionNetworkState()
        {
            _shadowTracked.Clear();
            _nextShadowId = 0;
            if (_clientShadowLookups != null)
                _clientShadowLookups.Clear();
            _pendingContainerRemoves.Clear();
            _hasPendingFlagBulk = false;
            _pendingFlagBulk = default;
            _pendingFlagDeltas.Clear();
            _hasPendingJournalBulk = false;
            _pendingJournalBulk = default;
            _needsJournalWorldCleanup = false;
            _awaitingLateJoinBulk.Clear();
            _pendingTradeInventories.Clear();
            _constructedSites.Clear();
            _pendingConstructibles.Clear();
            _pendingSawStates.Clear();
            _pendingBarricadeEvents.Clear();
            _hasPendingScenarioSync = false;
            _pendingScenarioSync = default;
            _hasPendingScenarioEvent = false;
            _pendingScenarioEvent = default;
            Patches.ScenarioPendingEventState.PendingEventIndex = -1;
            Patches.ScenarioPendingEventState.PendingScenario = null;
            _pendingGameEvents.Clear();
            _pendingInteractive.Clear();
            _pendingPadlocks.Clear();
            _pendingLocked.Clear();
            _remoteOutsideLocation.Clear();
        }
    }
}
