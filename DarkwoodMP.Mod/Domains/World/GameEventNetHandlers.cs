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
    internal sealed class GameEventNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal const int MaxPendingGameEvents = 64;
        private readonly List<GameEventsFiredMessage> _pendingGameEvents = new List<GameEventsFiredMessage>();

        internal GameEventNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingGameEvents()
        {
            _pendingGameEvents.Clear();
            _pendingGameEventQueuedAt.Clear();
        }

        internal void HandleGameEventsFired(GameEventsFiredMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            ApplyGameEventsFired(msg, queueIfMissing: true);
        }

        /// <summary>
        /// Host late-join: scan components that already have <c>fired &amp;&amp; !multipleFire</c>
        /// (vanilla one-shot latch). Skips ephemeral FX, saved-delayed shells, and dream-named
        /// GEs when no dream is active — avoids pad/overworld mis-resolve and night-scenario
        /// replay (scenario bulk stays deferred separately).
        /// </summary>
        internal void SendGameEventsBulkTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected)
                return;

            GameEvents[] all;
            try
            {
                all = UnityEngine.Object.FindObjectsOfType<GameEvents>(true);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[BulkSync] GameEvents scan failed: " + ex.Message);
                return;
            }
            if (all == null || all.Length == 0)
                return;

            bool dreamActive = DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming);

            var list = new List<GameEventsFiredMessage>(Mathf.Min(all.Length, GameEventsBulkMessage.MaxEvents));
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
                : "[BulkSync] Fired GameEvents → all: " + list.Count);
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

        /// <returns>True when the event is resolved (fired, already fired, or intentionally skipped).</returns>
        private bool ApplyGameEventsFired(GameEventsFiredMessage msg, bool queueIfMissing)
        {
            // Post-teardown dream GEs (e.g. fov_trigger_* after LocationExit) must not apply.
            if (!string.IsNullOrEmpty(msg.EventName)
                && msg.EventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0
                && !DreamSyncManager.IsDreamActive
                && (Dreams.Instance == null || !Dreams.Instance.dreaming))
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] drop post-dream GE '{msg.EventName}'");
                return true;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            GameEvents best = null;

            // Resolve dream events only under the active pad. A name-only
            // fallback can select the overworld bunker copy, so queue events
            // until the pad is loaded and finishedLoading is true.
            // Unnamed pad effects are also identified by their pad coordinates.
            bool dreamNamed = !string.IsNullOrEmpty(msg.EventName)
                && msg.EventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool padCoords = ClientStateBackup.IsDreamPadCoordinate(pos);
            bool isDreamEnter = dreamNamed
                && msg.EventName.IndexOf("onEnterLocation", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool dreamSceneFx = padCoords && !string.IsNullOrEmpty(msg.EventName)
                && (msg.EventName.IndexOf("def_glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("def_shadow", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("podmiana", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("carousel", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("karuzela", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("newborn", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("dimLight", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("SWITCH_", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("Lamp_dream", System.StringComparison.OrdinalIgnoreCase) >= 0);
            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            Location dreamLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
            bool padNotReady = DreamSyncManager.IsDreamActive
                && (dreamRoot == null
                    || dreamLoc == null || !dreamLoc.finishedLoading
                    || Dreams.Instance == null || !Dreams.Instance.dreaming);
            if ((dreamNamed || dreamSceneFx || (padCoords && DreamSyncManager.IsDreamActive))
                && padNotReady
                && (isDreamEnter || dreamNamed || dreamSceneFx || padCoords))
            {
                if (queueIfMissing)
                {
                    QueuePendingGameEvent(msg);
                    return false;
                }
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] defer dream GE (pad not ready) '{msg.EventName}'");
                return false;
            }

            // Dream footstep and sound-area events parent audio to the local
            // Player. Proxy OnFootstep owns peer footsteps.
            if (DreamSyncManager.IsDreamActive
                && !string.IsNullOrEmpty(msg.EventName)
                && (msg.EventName.IndexOf("footsteps", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("area_footsteps", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("soundarea", System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] skip dream ambient GE '{msg.EventName}' (proxy footsteps own peer steps)");
                return true;
            }

            // Dream bunker dialogue events sit at location origin far from door body;
            // use wide name search first, then position.
            float nameR = DreamSyncManager.IsDreamActive ? 80f : 8f;
            float posR = DreamSyncManager.IsDreamActive ? 12f : 2.5f;
            if (!string.IsNullOrEmpty(msg.EventName))
                best = WorldQueryHelper.FindNearestByName<GameEvents>(pos, msg.EventName, nameR);
            if (best == null)
                best = WorldQueryHelper.FindNearest<GameEvents>(pos, posR);

            // Soft name matching handles Clone and suffix drift, with a strict
            // distance cap to avoid selecting an overworld copy.
            if (best == null && !string.IsNullOrEmpty(msg.EventName))
            {
                GameEvents[] all = UnityEngine.Object.FindObjectsOfType<GameEvents>(true);
                string want = msg.EventName;
                float bestD = float.MaxValue;
                float softMax = DreamSyncManager.IsDreamActive ? 250f : nameR;
                for (int i = 0; i < all.Length; i++)
                {
                    GameEvents ge = all[i];
                    if (ge == null) continue;
                    string n = ge.name ?? "";
                    if (!n.Equals(want, System.StringComparison.OrdinalIgnoreCase)
                        && n.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) < 0
                        && want.IndexOf(n.Replace("(Clone)", "").Trim(), System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (dreamRoot != null
                        && !ge.transform.IsChildOf(dreamRoot)
                        && Vector3.Distance(ge.transform.position, dreamRoot.position) > 250f)
                        continue;
                    float d = Vector3.Distance(ge.transform.position, pos);
                    if (d > softMax) continue;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = ge;
                    }
                }
            }

            if (best == null)
            {
                // Host-spawned spirit FX (def_glow / def_shadow) often have no durable
                // GameEvents on the client. Queuing them would call FindObjectsOfType every 0.5s
                        // for 90 seconds, causing periodic stutters and dream-end failure.
                if (IsEphemeralDreamFxEvent(msg.EventName)
                    && DreamSyncManager.IsDreamActive
                    && !padNotReady)
                {
                    ModRuntime.LegacyInfo(
                        "[GameEventsSync] drop missing ephemeral FX '" + msg.EventName
                        + "' (no client GE — not queued)");
                    return true;
                }

                if (queueIfMissing)
                {
                    QueuePendingGameEvent(msg);
                    return false;
                }
                ModRuntime.Log?.LogWarning(
                    $"[GameEventsSync] no GameEvents near {pos} name='{msg.EventName}'");
                return false;
            }

            // Must run under NetworkApplyGuard on every apply path (receive + pending flush).
            // Without it GameEventsFiredPatch blocks client one-shots and fire() is a no-op.
            // Vanilla fired+!multipleFire guard prevents double-fire if client already ran it.
            bool wasFired = best.fired;
            if (wasFired && !best.multipleFire)
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] skip already-fired '{best.name}' at {best.transform.position}");
                return true;
            }

            string geName = best.name ?? msg.EventName ?? "";
            bool leaveDoor = geName.IndexOf("onLeaveDoor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || geName.IndexOf("DoorDialogue", System.StringComparison.OrdinalIgnoreCase) >= 0;

            using (new NetworkApplyGuard())
            {
                best.fire();
            }
            if (!best.fired && !best.multipleFire)
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] fire no-op '{best.name}' at {best.transform.position}");
                return false;
            }
            ModRuntime.LegacyInfo(
                $"[GameEventsSync] applied '{best.name}' wasFired={wasFired} firedNow={best.fired} at {best.transform.position}");

            // After GE (which owns openSound): mute late DoorOpen / skip ForceOpen.
            if (!leaveDoor)
            {
                DWMPHorde.Patches.DialogueDoorAftermath.OnClientGameEventsApplied(
                    geName, best.transform.position);
            }
            else
            {
                DWMPHorde.Patches.DialogueDoorAftermath.OnClientLeaveDoorGameEventsApplied(
                    geName, best.transform.position);
            }
            return true;
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

        /// <summary>Drop queued dream GEs so they cannot re-fire after pad teardown.</summary>
        internal void ClearPendingDreamGameEvents()
        {
            if (_pendingGameEvents.Count == 0) return;
            int removed = 0;
            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingGameEvents[i];
                string n = msg.EventName ?? "";
                bool dreamName = n.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool ephemeral = IsEphemeralDreamFxEvent(n);
                bool padPos = ClientStateBackup.IsDreamPadCoordinate(
                    new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                if (dreamName || ephemeral || padPos)
                {
                    _pendingGameEvents.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
                ModRuntime.LegacyInfo($"[GameEventsSync] cleared {removed} pending dream GE(s) on dream end");
        }

        private static bool IsEphemeralDreamFxEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            return eventName.IndexOf("def_glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("def_shadow", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private float _nextPendingGameEventsFlushTime;
        private const float PendingGameEventsFlushInterval = 0.5f;
        private const float PendingGameEventsMaxAge = 60f;
        private const float PendingDreamGameEventsMaxAge = 90f;
        private readonly System.Collections.Generic.Dictionary<int, float> _pendingGameEventQueuedAt
            = new System.Collections.Generic.Dictionary<int, float>(16);

        /// <summary>After remote dream pad spawn, apply queued entry events.</summary>
        internal void TryFlushPendingGameEventsAfterDreamLoad()
        {
            _nextPendingGameEventsFlushTime = 0f;
            TryFlushPendingGameEvents();
        }

        internal void TryFlushPendingGameEvents()
        {
            if (_net.Role != NetworkRole.Client || _pendingGameEvents.Count == 0)
                return;
            // Throttle scans for events whose scene objects are not loaded yet.
            float now = Time.unscaledTime;
            if (now < _nextPendingGameEventsFlushTime) return;
            _nextPendingGameEventsFlushTime = now + PendingGameEventsFlushInterval;

            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingGameEvents[i];
                int key = msg.EventName != null ? msg.EventName.GetHashCode() : 0;
                key ^= (int)(msg.PosX * 10f) ^ ((int)(msg.PosZ * 10f) << 10);
                if (!_pendingGameEventQueuedAt.ContainsKey(key))
                    _pendingGameEventQueuedAt[key] = now;

                bool dreamish = (!string.IsNullOrEmpty(msg.EventName)
                        && msg.EventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    || ClientStateBackup.IsDreamPadCoordinate(
                        new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                float maxAge = dreamish ? PendingDreamGameEventsMaxAge : PendingGameEventsMaxAge;
                if (now - _pendingGameEventQueuedAt[key] > maxAge)
                {
                    _pendingGameEvents.RemoveAt(i);
                    _pendingGameEventQueuedAt.Remove(key);
                    continue;
                }

                // Dream onEnterLocation / pad FX: keep queued until pad finished.
                bool dreamEnter = !string.IsNullOrEmpty(msg.EventName)
                    && msg.EventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0
                    && msg.EventName.IndexOf("onEnterLocation", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (dreamEnter || dreamish)
                {
                    Location dLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
                    if (DreamSyncManager.IsDreamActive
                        && (dLoc == null || !dLoc.finishedLoading
                            || Dreams.Instance == null || !Dreams.Instance.dreaming))
                        continue;
                }

                // Always go through Apply (soft name search); pre-find skipped def_glow.
                if (ApplyGameEventsFired(msg, queueIfMissing: false))
                {
                    _pendingGameEvents.RemoveAt(i);
                    _pendingGameEventQueuedAt.Remove(key);
                }
            }
        }

    
    }
}
