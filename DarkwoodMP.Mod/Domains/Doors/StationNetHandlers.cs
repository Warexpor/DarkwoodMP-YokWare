using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Saw / feeder / lure station sync handlers composed for 0.8.</summary>
    internal sealed partial class StationNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingSawStates = 16;
        internal const int MaxPendingStationStates = 16;
        private readonly List<SawStateMessage> _pendingSawStates = new List<SawStateMessage>();
        private readonly List<FeederStateMessage> _pendingFeederStates = new List<FeederStateMessage>();
        private readonly List<LureStateMessage> _pendingLureStates = new List<LureStateMessage>();

        private float _nextPendingSawFlushTime;
        private float _nextPendingFeederFlushTime;
        private float _nextPendingLureFlushTime;
        private const float PendingStationFlushInterval = 1f;
        private const float PendingLureFlushInterval = 1f;

        internal int PendingSawCount => _pendingSawStates.Count;
        internal int PendingFeederCount => _pendingFeederStates.Count;
        internal int PendingLureCount => _pendingLureStates.Count;

        internal StationNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingStations()
        {
            _pendingSawStates.Clear();
            _pendingFeederStates.Clear();
            _pendingLureStates.Clear();
        }

        internal void HandleSawState(SawStateMessage msg)
        {
            ApplySawState(msg, queueIfMissing: true);
        }

        internal void ApplySawState(SawStateMessage msg, bool queueIfMissing)
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

            // Host: client FuelDelta accumulates (concurrent pour). Suppress Forwardable
            // raw delta fan-out and rebroadcast absolute so peers converge.
            bool hostDelta = _net.Role == NetworkRole.Host
                && _net.CurrentReceivePlayerId > 0
                && msg.FuelDelta > 0.01f;
            if (hostDelta)
            {
                float delta = msg.FuelDelta;
                saw.addFuel(delta);
                _net._suppressForwardThisMessage = true;
                Sync.SawSyncHelpers.BroadcastAbsoluteFromHost(saw, "addFuel-delta");
                SafeSawRefresh(saw);
                ModRuntime.LegacyInfo(
                    $"[SawSync] host-auth addFuel +{delta:F0} {prevFuel:F0}→{saw.fuel:F0} at {pos}");
                return;
            }

            saw.fuel = Mathf.Clamp(msg.Fuel, 0f, saw.maxFuel);

            if (inv != null)
            {
                InventorySyncUtil.SyncItemAmount(inv, "woodLog", msg.WoodLogAmount);
                InventorySyncUtil.SyncItemAmount(inv, "wood", msg.WoodAmount);
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
            if (_net.Role != NetworkRole.Host) return;

            Saw[] all = WorldQueryHelper.GetCachedSceneComponents<Saw>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Saw saw = all[i];
                if (saw == null) continue;
                var msg = Sync.SawSyncHelpers.BuildMessage(saw);
                _net.SendBulkOrAll(NetMessageType.SawState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} saw state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} saw state(s) to all clients");
        }

        internal void TryFlushPendingSawStates()
        {
            if (_pendingSawStates.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextPendingSawFlushTime) return;
            _nextPendingSawFlushTime = now + PendingStationFlushInterval;
            for (int i = _pendingSawStates.Count - 1; i >= 0; i--)
            {
                var msg = _pendingSawStates[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (FindSawByPos(pos) == null) continue;
                _pendingSawStates.RemoveAt(i);
                ApplySawState(msg, queueIfMissing: false);
            }
        }

        internal void HandleFeederState(FeederStateMessage msg)
        {
            ApplyFeederState(msg, queueIfMissing: true);
        }

        internal void ApplyFeederState(FeederStateMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Feeder feeder = WorldQueryHelper.FindNearest<Feeder>(pos, 2f);
            if (feeder == null)
            {
                if (queueIfMissing)
                {
                    for (int i = _pendingFeederStates.Count - 1; i >= 0; i--)
                    {
                        var p = _pendingFeederStates[i];
                        if (Mathf.Abs(p.PosX - msg.PosX) < 0.5f &&
                            Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pendingFeederStates.RemoveAt(i);
                    }
                    if (_pendingFeederStates.Count >= MaxPendingStationStates)
                        _pendingFeederStates.RemoveAt(0);
                    _pendingFeederStates.Add(msg);
                    ModRuntime.LegacyInfo("[FeederSync] queued (feeder not loaded) at " + pos);
                }
                return;
            }

            // Only share inactive flip (buff is personal). Remote apply must not re-broadcast.
            if (!msg.Active && feeder.Active)
            {
                using (new NetworkApplyGuard())
                {
                    try { feeder.makeInactive(); }
                    catch (System.Exception ex)
                    {
                        ModRuntime.Log?.LogWarning("[FeederSync] makeInactive: " + ex.Message);
                    }
                }
                ModRuntime.LegacyInfo($"[FeederSync] applied inactive at {pos}");
            }
        }

        internal void TryFlushPendingFeederStates()
        {
            if (_pendingFeederStates.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextPendingFeederFlushTime) return;
            _nextPendingFeederFlushTime = now + PendingStationFlushInterval;
            for (int i = _pendingFeederStates.Count - 1; i >= 0; i--)
            {
                var msg = _pendingFeederStates[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<Feeder>(pos, 2f) == null) continue;
                _pendingFeederStates.RemoveAt(i);
                ApplyFeederState(msg, queueIfMissing: false);
            }
        }

        /// <summary>Host: push feeder Active state for joiners (inactive ones matter most).</summary>
        internal void SendFeederStatesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            Feeder[] all = WorldQueryHelper.GetCachedSceneComponents<Feeder>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Feeder f = all[i];
                if (f == null) continue;
                Vector3 p = f.transform.position;
                var msg = new FeederStateMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    Active = f.Active
                };
                _net.SendBulkOrAll(NetMessageType.FeederState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} feeder state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} feeder state(s) to all clients");
        }

        internal void HandleLureState(LureStateMessage msg)
        {
            ApplyLureState(msg, queueIfMissing: true);
        }

        internal void ApplyLureState(LureStateMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // Far map lures (host AI eating across the forest) must not scan every second.
            // Client logs showed repeated scene scans while the host stayed clean.
            if (_net.Role == NetworkRole.Client
                && !ClientEntityInterpolationService.IsInClientInterest(pos))
                return;

            // Lure often has no collider, so OverlapSphere misses; a cached scan is
            // acceptable while it is in interest.
            Lure lure = WorldQueryHelper.FindNearest<Lure>(pos, 2f);
            if (lure == null)
            {
                // Destroyed lure with health<=0 is already converged.
                if (msg.Health <= 0) return;
                if (queueIfMissing)
                {
                    for (int i = _pendingLureStates.Count - 1; i >= 0; i--)
                    {
                        var p = _pendingLureStates[i];
                        if (Mathf.Abs(p.PosX - msg.PosX) < 0.5f &&
                            Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pendingLureStates.RemoveAt(i);
                    }
                    if (_pendingLureStates.Count >= MaxPendingStationStates)
                        _pendingLureStates.RemoveAt(0);
                    _pendingLureStates.Add(msg);
                    ModLog.Trace(LogCat.World, "[LureSync] queued (lure not loaded) at " + pos);
                }
                return;
            }

            if (lure.health == msg.Health) return;
            if (lure.health < msg.Health) return; // never heal from net (host absolute lower)

            using (new NetworkApplyGuard())
            {
                try
                {
                    // Absolute set for intermediate ticks; removeHealth only on death so
                    // we do not re-run eater/gore path every 1s (log spam + apply cost).
                    if (msg.Health <= 0)
                    {
                        try
                        {
                            var ctrl = Singleton<Controller>.Instance;
                            int day = ctrl != null ? ctrl.day : 0;
                            UnityEngine.Random.InitState(day
                                ^ ((int)Mathf.Round(pos.x) * 73856093)
                                ^ ((int)Mathf.Round(pos.z) * 19349663)
                                ^ lure.health);
                        }
                        catch { /* ignore */ }
                        lure.removeHealth(lure.health + 1, null);
                    }
                    else
                    {
                        lure.health = msg.Health;
                    }
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[LureSync] apply: " + ex.Message);
                }
            }
            // Trace only; frequent LegacyInfo output obscures real hitches.
            ModLog.Trace(LogCat.World, $"[LureSync] applied at {pos} health→{msg.Health}");
        }

        internal void TryFlushPendingLureStates()
        {
            if (_pendingLureStates.Count == 0) return;
            // Unloaded lures used to scan every frame via FindNearest → hitch loop.
            float now = Time.unscaledTime;
            if (now < _nextPendingLureFlushTime) return;
            _nextPendingLureFlushTime = now + PendingLureFlushInterval;

            for (int i = _pendingLureStates.Count - 1; i >= 0; i--)
            {
                var msg = _pendingLureStates[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (_net.Role == NetworkRole.Client
                    && !ClientEntityInterpolationService.IsInClientInterest(pos))
                {
                    _pendingLureStates.RemoveAt(i);
                    continue;
                }
                if (WorldQueryHelper.FindNearest<Lure>(pos, 2f) == null)
                    continue;
                _pendingLureStates.RemoveAt(i);
                ApplyLureState(msg, queueIfMissing: false);
            }
        }

        internal void SendLureStatesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            Lure[] all = WorldQueryHelper.GetCachedSceneComponents<Lure>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Lure lure = all[i];
                if (lure == null) continue;
                Vector3 p = lure.transform.position;
                var msg = new LureStateMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    Health = lure.health
                };
                _net.SendBulkOrAll(NetMessageType.LureState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} lure state(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} lure state(s) to all clients");
        }
    }
}
