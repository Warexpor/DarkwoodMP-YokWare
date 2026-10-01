using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>GameEventsFired + late-join GameEventsBulk handlers composed for 0.8.</summary>
    internal sealed partial class GameEventNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingGameEvents = 64;
        /// <summary>
        /// Host-only: one-shot GEs with <c>destroyOnFire</c> destroy their GO after
        /// the event delay (decompile <c>GameEvents.fire</c>), so a late-join
        /// <c>FindObjectsOfType</c> scan cannot see them. Record identity at fire
        /// time and merge into <see cref="SendGameEventsBulkTo"/>.
        /// </summary>
        internal const int MaxDestroyedFiredGameEvents = 512;
        private readonly List<GameEventsFiredMessage> _pendingGameEvents = new List<GameEventsFiredMessage>();
        private readonly List<GameEventsFiredMessage> _destroyedFiredGameEvents =
            new List<GameEventsFiredMessage>(64);

        internal GameEventNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingGameEvents()
        {
            _pendingGameEvents.Clear();
            _pendingGameEventQueuedAt.Clear();
            _destroyedFiredGameEvents.Clear();
            _geSoftIndexSource = null;
            _geSoftByNormName.Clear();
        }

        /// <summary>
        /// Remember a one-shot that will (or did) destroy its shell so late joiners still receive
        /// it in GameEventsBulk (136). Recorded on the host at fire time and on clients from the
        /// events they apply: only the host used to keep this list, so a promoted host (migration)
        /// had none and late joiners saw those shells as never fired.
        /// </summary>
        internal void RecordDestroyedOnFireGameEvent(GameEventsFiredMessage msg)
        {
            if (_net.Role != NetworkRole.Host && _net.Role != NetworkRole.Client)
                return;
            for (int i = 0; i < _destroyedFiredGameEvents.Count; i++)
            {
                var e = _destroyedFiredGameEvents[i];
                if (Mathf.Abs(e.PosX - msg.PosX) < 0.05f
                    && Mathf.Abs(e.PosY - msg.PosY) < 0.05f
                    && Mathf.Abs(e.PosZ - msg.PosZ) < 0.05f
                    && string.Equals(e.EventName, msg.EventName, StringComparison.Ordinal))
                    return;
            }
            if (_destroyedFiredGameEvents.Count >= MaxDestroyedFiredGameEvents)
                _destroyedFiredGameEvents.RemoveAt(0);
            _destroyedFiredGameEvents.Add(msg);
        }

        internal void HandleGameEventsFired(GameEventsFiredMessage msg)
        {
            // Client burn-crawl cannot run the one-shot locally. Fire it here so the
            // host Postfix broadcasts EpilogueOutcomes to every peer.
            if (_net.Role == NetworkRole.Host)
            {
                if (string.Equals(msg.EventName, EpilogueNetHandlers.EpilogueCameraPanEvent,
                        StringComparison.Ordinal)
                    && EpilogueNetHandlers.IsLocalInEpilogue())
                {
                    var events = Singleton<Events>.Instance;
                    if (events != null)
                    {
                        ModRuntime.LegacyInfo(
                            $"[Epilogue] Host firing crawl pan from p{_net.CurrentReceivePlayerId}");
                        events.fireWorldEvent(EpilogueNetHandlers.EpilogueCameraPanEvent);
                        // Inbound dispatch holds NetworkApplyGuard, so the fire Postfix
                        // and SendGameEventsFired both skip. Fan out explicitly.
                        _net.Broadcast(NetMessageType.GameEventsFired,
                            w => msg.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                    }
                }
                return;
            }

            if (_net.Role != NetworkRole.Client)
                return;
            ApplyGameEventsFired(msg, queueIfMissing: true);
        }

        /// <summary>
        /// Host late-join: scan components that already have <c>fired &amp;&amp; !multipleFire</c>
        /// (vanilla one-shot latch), then merge host-recorded <c>destroyOnFire</c> identities
        /// whose shells are gone from the scan. Skips ephemeral FX, saved-delayed shells,
        /// and dream-named GEs when no dream is active — avoids pad/overworld mis-resolve
        /// and night-scenario replay (scenario bulk stays deferred separately).
        /// </summary>
        internal void SendGameEventsBulkTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected)
                return;

            GameEvents[] all = null;
            try
            {
                // Late-join bulk is rare; still use the shared TTL cache so a burst of
                // joins does not re-scan the whole scene each time.
                all = WorldQueryHelper.GetCachedSceneComponents<GameEvents>();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[BulkSync] GameEvents scan failed: " + ex.Message);
            }

            bool dreamActive = DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming);

            int scanCap = all != null ? all.Length : 0;
            var list = new List<GameEventsFiredMessage>(
                Mathf.Min(scanCap + _destroyedFiredGameEvents.Count, GameEventsBulkMessage.MaxEvents));
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (list.Count >= GameEventsBulkMessage.MaxEvents)
                        break;
                    GameEvents ge = all[i];
                    if (ge == null || ge.transform == null)
                        continue;
                    // Conservative: only one-shots that have already latched on the host.
                    if (!ge.fired || ge.multipleFire)
                        continue;
                    if (ge.isSavedDelayedEvent)
                        continue;

                    string eventName = ge.name ?? "";
                    if (IsEphemeralDreamFxEvent(eventName))
                        continue;
                    if (!dreamActive
                        && eventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    Vector3 p = ge.transform.position;
                    list.Add(new GameEventsFiredMessage
                    {
                        PosX = Mathf.Round(p.x * 10f) / 10f,
                        PosY = Mathf.Round(p.y * 10f) / 10f,
                        PosZ = Mathf.Round(p.z * 10f) / 10f,
                        EventName = eventName,
                        ActorPlayerId = 0
                    });
                }
            }

            int fromDestroyed = 0;
            for (int i = 0; i < _destroyedFiredGameEvents.Count; i++)
            {
                if (list.Count >= GameEventsBulkMessage.MaxEvents)
                    break;
                var destroyed = _destroyedFiredGameEvents[i];
                string eventName = destroyed.EventName ?? "";
                if (IsEphemeralDreamFxEvent(eventName))
                    continue;
                if (!dreamActive
                    && eventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (BulkListContains(list, destroyed))
                    continue;
                // Late-join: world latch only — never re-grant personal bag/teleport.
                destroyed.ActorPlayerId = 0;
                list.Add(destroyed);
                fromDestroyed++;
            }

            if (list.Count == 0)
            {
                ModRuntime.LegacyInfo(targetPlayerId > 0
                    ? "[BulkSync] Fired GameEvents → p" + targetPlayerId + ": 0"
                    : "[BulkSync] Fired GameEvents → all: 0");
                return;
            }

            var msg = new GameEventsBulkMessage
            {
                EventCount = list.Count,
                PosX = new float[list.Count],
                PosY = new float[list.Count],
                PosZ = new float[list.Count],
                EventNames = new string[list.Count]
            };
            for (int i = 0; i < list.Count; i++)
            {
                msg.PosX[i] = list[i].PosX;
                msg.PosY[i] = list[i].PosY;
                msg.PosZ[i] = list[i].PosZ;
                msg.EventNames[i] = list[i].EventName;
            }

            _net.SendBulkOrAll(NetMessageType.GameEventsBulk, w => msg.Serialize(w), targetPlayerId);
            ModLog.Event(LogCat.Session, targetPlayerId > 0
                ? "[BulkSync] Fired GameEvents → p" + targetPlayerId + ": " + list.Count
                    + (fromDestroyed > 0 ? " (destroyOnFire+" + fromDestroyed + ")" : "")
                : "[BulkSync] Fired GameEvents → all: " + list.Count
                    + (fromDestroyed > 0 ? " (destroyOnFire+" + fromDestroyed + ")" : ""));
        }

        /// <summary>
        /// Host: first-enter pad resync — fired one-shot GEs under/near <paramref name="loc"/>
        /// only (not a second full join). Client pending from late-join ages out at 60s and
        /// misses virgin-prefab setActive/remove. ActorPlayerId stays 0 (world geometry;
        /// no personal bag/teleport re-grant). Skips <c>multipleFire</c>. Dream-named GEs
        /// omitted unless a dream is active; dream pads use dream-root SoftMatch on apply.
        /// </summary>
        /// <returns>Number of events packed into the bulk (0 = nothing sent).</returns>
        internal int SendFiredGameEventsNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected
                || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = WorldLateJoinNetHandlers.PadResyncMaxDistSqr;

            bool dreamActive = DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming);
            Transform dreamRoot = dreamActive
                ? DreamSyncManager.GetDreamLocationTransform()
                : null;

            GameEvents[] all = null;
            try
            {
                all = WorldQueryHelper.GetCachedSceneComponents<GameEvents>();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning(
                    "[LocationSync] pad GE scan failed: " + ex.Message);
                return 0;
            }

            int scanCap = all != null ? all.Length : 0;
            var list = new List<GameEventsFiredMessage>(
                Mathf.Min(scanCap + _destroyedFiredGameEvents.Count, GameEventsBulkMessage.MaxEvents));

            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (list.Count >= GameEventsBulkMessage.MaxEvents)
                        break;
                    GameEvents ge = all[i];
                    if (ge == null || ge.transform == null)
                        continue;
                    if (!ge.fired || ge.multipleFire)
                        continue;
                    if (ge.isSavedDelayedEvent)
                        continue;
                    if (!IsUnderOrNearLocation(ge.transform, root, anchor, maxDistSqr))
                        continue;
                    // Dream active: only pad children / near dream root (never overworld twin).
                    if (dreamRoot != null
                        && !ge.transform.IsChildOf(dreamRoot)
                        && (ge.transform.position - dreamRoot.position).sqrMagnitude
                            > 250f * 250f)
                        continue;

                    string eventName = ge.name ?? "";
                    if (IsEphemeralDreamFxEvent(eventName))
                        continue;
                    if (!dreamActive
                        && eventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    Vector3 p = ge.transform.position;
                    list.Add(new GameEventsFiredMessage
                    {
                        PosX = Mathf.Round(p.x * 10f) / 10f,
                        PosY = Mathf.Round(p.y * 10f) / 10f,
                        PosZ = Mathf.Round(p.z * 10f) / 10f,
                        EventName = eventName,
                        ActorPlayerId = 0
                    });
                }
            }

            int fromDestroyed = 0;
            for (int i = 0; i < _destroyedFiredGameEvents.Count; i++)
            {
                if (list.Count >= GameEventsBulkMessage.MaxEvents)
                    break;
                var destroyed = _destroyedFiredGameEvents[i];
                string eventName = destroyed.EventName ?? "";
                if (IsEphemeralDreamFxEvent(eventName))
                    continue;
                if (!dreamActive
                    && eventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                Vector3 dPos = new Vector3(destroyed.PosX, destroyed.PosY, destroyed.PosZ);
                if (!IsNearLocationAnchor(dPos, root, anchor, maxDistSqr))
                    continue;
                if (dreamRoot != null
                    && (dPos - dreamRoot.position).sqrMagnitude > 250f * 250f)
                    continue;
                if (BulkListContains(list, destroyed))
                    continue;
                destroyed.ActorPlayerId = 0;
                list.Add(destroyed);
                fromDestroyed++;
            }

            if (list.Count == 0)
                return 0;

            var msg = new GameEventsBulkMessage
            {
                EventCount = list.Count,
                PosX = new float[list.Count],
                PosY = new float[list.Count],
                PosZ = new float[list.Count],
                EventNames = new string[list.Count]
            };
            for (int i = 0; i < list.Count; i++)
            {
                msg.PosX[i] = list[i].PosX;
                msg.PosY[i] = list[i].PosY;
                msg.PosZ[i] = list[i].PosZ;
                msg.EventNames[i] = list[i].EventName;
            }

            _net.SendBulkOrAll(NetMessageType.GameEventsBulk, w => msg.Serialize(w), targetPlayerId);
            ModLog.Event(LogCat.Session,
                "[LocationSync] pad fired GE → p" + targetPlayerId + ": " + list.Count
                + (fromDestroyed > 0 ? " (destroyOnFire+" + fromDestroyed + ")" : "")
                + " loc=" + (loc.gameObject != null ? loc.gameObject.name : loc.name));
            return list.Count;
        }

        private static bool IsUnderOrNearLocation(
            Transform t, Transform root, Vector3 anchor, float maxDistSqr)
        {
            if (t == null) return false;
            if (root != null && (t == root || t.IsChildOf(root)))
                return true;
            return IsNearLocationAnchor(t.position, root, anchor, maxDistSqr);
        }

        private static bool IsNearLocationAnchor(
            Vector3 pos, Transform root, Vector3 anchor, float maxDistSqr)
        {
            if (root != null)
            {
                float dxRoot = pos.x - root.position.x;
                float dzRoot = pos.z - root.position.z;
                if (dxRoot * dxRoot + dzRoot * dzRoot <= maxDistSqr)
                    return true;
            }
            float dx = pos.x - anchor.x;
            float dz = pos.z - anchor.z;
            return dx * dx + dz * dz <= maxDistSqr;
        }

        private static bool BulkListContains(List<GameEventsFiredMessage> list, GameEventsFiredMessage msg)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (Mathf.Abs(e.PosX - msg.PosX) < 0.05f
                    && Mathf.Abs(e.PosY - msg.PosY) < 0.05f
                    && Mathf.Abs(e.PosZ - msg.PosZ) < 0.05f
                    && string.Equals(e.EventName, msg.EventName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        internal void HandleGameEventsBulk(GameEventsBulkMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            if (msg.EventCount <= 0 || msg.EventNames == null)
                return;

            int applied = 0;
            int queuedOrSkipped = 0;
            for (int i = 0; i < msg.EventCount; i++)
            {
                var one = new GameEventsFiredMessage
                {
                    PosX = msg.PosX != null && i < msg.PosX.Length ? msg.PosX[i] : 0f,
                    PosY = msg.PosY != null && i < msg.PosY.Length ? msg.PosY[i] : 0f,
                    PosZ = msg.PosZ != null && i < msg.PosZ.Length ? msg.PosZ[i] : 0f,
                    EventName = msg.EventNames[i]
                };
                // Same apply path as live GameEventsFired (NetworkApplyGuard + pending queue).
                // One throwing event must not abort the rest of the bulk (or the pending flush
                // that follows it), so each item is isolated.
                try
                {
                    if (ApplyGameEventsFired(one, queueIfMissing: true))
                        applied++;
                    else
                        queuedOrSkipped++;
                }
                catch (Exception ex)
                {
                    queuedOrSkipped++;
                    ModLog.WarnRate(LogCat.Session, "ge-bulk-item-throw",
                        "[BulkSync] GameEvent '" + one.EventName + "' apply threw: " + ex.Message);
                }
            }
            ModLog.Event(LogCat.Session,
                "[BulkSync] GameEvents bulk applied=" + applied
                + " pendingOrUnresolved=" + queuedOrSkipped
                + " total=" + msg.EventCount);
        }
    }
}
