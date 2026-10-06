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
        private readonly List<PendingSawState> _pendingSawStates = new List<PendingSawState>();

        /// <summary>Upper bound on planks a client may report per log consumed in one convert.</summary>
        private const int MaxWoodPerLog = 10;

        /// <summary>Deferred saw message plus the peer that sent it (0 = host).</summary>
        private struct PendingSawState
        {
            public SawStateMessage Msg;
            public int SenderId;
        }
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
            int senderId = _net.CurrentReceivePlayerId;
            // Host owns saw stock: a client message is a request, never relayed raw. The host
            // answers with its own absolute state (now or when the deferred apply runs).
            if (_net.Role == NetworkRole.Host && senderId > 0)
                _net.SuppressRelay();
            ApplySawState(msg, senderId, queueIfMissing: true);
        }

        /// <param name="senderId">Peer that sent <paramref name="msg"/>. Passed explicitly: the
        /// deferred flush runs from Update, where the receive id is not set.</param>
        internal void ApplySawState(SawStateMessage msg, int senderId, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Saw saw = FindSawByPos(pos);
            if (saw == null)
            {
                if (queueIfMissing)
                {
                    // Replace a pending absolute for the same approx position; deltas accumulate.
                    for (int i = _pendingSawStates.Count - 1; i >= 0; i--)
                    {
                        var p = _pendingSawStates[i].Msg;
                        if (msg.Kind == SawStateKind.Absolute && p.Kind == SawStateKind.Absolute
                            && Mathf.Abs(p.PosX - msg.PosX) < 0.5f
                            && Mathf.Abs(p.PosY - msg.PosY) < 0.5f
                            && Mathf.Abs(p.PosZ - msg.PosZ) < 0.5f)
                            _pendingSawStates.RemoveAt(i);
                    }
                    if (_pendingSawStates.Count >= MaxPendingSawStates)
                        _pendingSawStates.RemoveAt(0);
                    _pendingSawStates.Add(new PendingSawState { Msg = msg, SenderId = senderId });
                    ModRuntime.LegacyInfo($"[SawSync] queued (saw not loaded) at {pos}");
                }
                else
                {
                    ModRuntime.LegacyInfo($"[SawSync] saw not found at {pos}");
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

            if (_net.Role == NetworkRole.Host && senderId > 0)
            {
                ApplyClientSawRequest(saw, inv, msg, senderId, prevFuel, prevLogs, prevWood);
                return;
            }
            if (msg.Kind != SawStateKind.Absolute)
                return; // only the host consumes delta requests

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

        /// <summary>
        /// Host: validate a client's addFuel / convert delta against the host's own stock, apply
        /// it, and broadcast the resulting absolute state (the sender included). A rejected
        /// request snaps only the sender back to the host's stock.
        /// </summary>
        private void ApplyClientSawRequest(Saw saw, Inventory inv, SawStateMessage msg, int senderId,
            float curFuel, int curLogs, int curWood)
        {
            Vector3 pos = saw.transform.position;
            if (msg.Kind != SawStateKind.Delta)
            {
                ModLog.Warn(LogCat.World, "[SawSync] ignored absolute stock from p" + senderId + " at " + pos);
                Sync.SawSyncHelpers.SendAbsoluteTo(saw, senderId);
                return;
            }

            float fuelDelta = float.IsNaN(msg.FuelDelta) || float.IsInfinity(msg.FuelDelta) ? 0f : msg.FuelDelta;
            int logDelta = msg.WoodLogDelta;
            int woodDelta = msg.WoodDelta;

            // Pure pour: the client spent its own fuel item; accumulate (concurrent pours add up).
            if (logDelta == 0 && woodDelta == 0)
            {
                if (fuelDelta <= 0.01f)
                {
                    Sync.SawSyncHelpers.SendAbsoluteTo(saw, senderId);
                    return;
                }
                saw.addFuel(fuelDelta);
                Sync.SawSyncHelpers.BroadcastAbsoluteFromHost(saw, "addFuel-delta");
                SafeSawRefresh(saw);
                ModRuntime.LegacyInfo(
                    $"[SawSync] host-auth addFuel +{fuelDelta:F0} {curFuel:F0}→{saw.fuel:F0} at {pos}");
                return;
            }

            // Convert: logs → planks, paid with fuel, against the host's stock.
            string reject = null;
            if (inv == null)
                reject = "no saw inventory";
            else if (logDelta > 0 || woodDelta < 0 || fuelDelta > 0.01f)
                reject = "stock moved the wrong way";
            else if (curLogs + logDelta < 0)
                reject = "not enough logs (have " + curLogs + ")";
            else if ((long)woodDelta > (long)(-logDelta) * MaxWoodPerLog)
                reject = "planks out of proportion to logs";
            else if (curFuel + fuelDelta < -0.01f)
                reject = "not enough fuel (have " + curFuel.ToString("F1") + ")";

            if (reject != null)
            {
                ModLog.Warn(LogCat.World, "[SawSync] rejected p" + senderId + " convert fuel="
                    + fuelDelta.ToString("F1") + " logs=" + logDelta + " wood=" + woodDelta + ": " + reject);
                Sync.SawSyncHelpers.SendAbsoluteTo(saw, senderId);
                return;
            }

            saw.fuel = Mathf.Clamp(curFuel + fuelDelta, 0f, saw.maxFuel);
            InventorySyncUtil.SyncItemAmount(inv, "woodLog", curLogs + logDelta);
            InventorySyncUtil.SyncItemAmount(inv, "wood", curWood + woodDelta);
            SafeSawRefresh(saw);
            AudioController.Play("saw_wood_01", saw.transform.position);
            Sync.SawSyncHelpers.BroadcastAbsoluteFromHost(saw, "convert-delta");
            ModRuntime.LegacyInfo(
                $"[SawSync] host-auth convert p{senderId} logs {curLogs}→{curLogs + logDelta} wood {curWood}→{curWood + woodDelta} at {pos}");
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
                // The host's own prologue pads are not the world.
                if (PersonalPrologue.IsOnProloguePad(saw.transform)) continue;
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
            // Runs from Update, outside dispatch: the sender comes from the pending entry, the
            // relay flag is never touched, and oldest-first keeps queued deltas in order.
            for (int i = 0; i < _pendingSawStates.Count;)
            {
                PendingSawState pending = _pendingSawStates[i];
                Vector3 pos = new Vector3(pending.Msg.PosX, pending.Msg.PosY, pending.Msg.PosZ);
                if (FindSawByPos(pos) == null) { i++; continue; }
                _pendingSawStates.RemoveAt(i);
                using (new NetworkApplyGuard())
                    ApplySawState(pending.Msg, pending.SenderId, queueIfMissing: false);
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
                    ModRuntime.LegacyInfo($"[FeederSync] queued (feeder not loaded) at {pos}");
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
                if (f == null || PersonalPrologue.IsOnProloguePad(f.transform)) continue;
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
                if (lure == null || PersonalPrologue.IsOnProloguePad(lure.transform)) continue;
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
