using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
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
        /// Host: remember a one-shot that will (or did) destroy its shell so late
        /// joiners still receive it in GameEventsBulk (136).
        /// </summary>
        internal void RecordDestroyedOnFireGameEvent(GameEventsFiredMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
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
                        EventName = eventName
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
                if (ApplyGameEventsFired(one, queueIfMissing: true))
                    applied++;
                else
                    queuedOrSkipped++;
            }
            ModLog.Event(LogCat.Session,
                "[BulkSync] GameEvents bulk applied=" + applied
                + " pendingOrUnresolved=" + queuedOrSkipped
                + " total=" + msg.EventCount);
        }
    }
}
