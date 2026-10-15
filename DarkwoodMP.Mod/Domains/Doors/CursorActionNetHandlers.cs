using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Cursor action + location transport handlers composed for 0.8.</summary>
    internal sealed class CursorActionNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CursorActionNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        private string _cursorEnterDebounceKey;
        private float _cursorEnterDebounceUntil;

        internal void HandleActivateCursorAction(ActivateCursorActionMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            CustomCursorAction best = FindCustomCursorAction(pos, msg.ObjectName);
            if (best == null)
            {
                if (TryFirePlainItemActivate(pos, msg.ObjectName))
                    return;
                ModLog.WarnRate(LogCat.World, "cursor-no-activate:" + msg.ObjectName,
                    $"[CursorActionSync] host: no activate target near {pos} name={msg.ObjectName}");
                return;
            }

            string actionName = best.name ?? msg.ObjectName ?? "";
            int requesterId = _net.CurrentReceivePlayerId;

            // Location enter is per-player transport — never activate() on host (TPs host).
            // Only when the action's live triggers do move its user: a locked entrance's
            // "can't open" was dropped here with "could not resolve dest". Any action that carries
            // its user into an outside location goes this way, not only the "_enter" ones: the
            // village shortcut hole by the hideout (transportPlayerToObject onto a point in the
            // village) was replayed on the client, where the village was never spawned, and left
            // the player in an empty village of its own at another pad slot. Here the client spawns
            // the pad at the host's slot and lands on the same point. A move inside the location
            // the user already stands in stays a plain activate.
            if (DWMPHorde.Patches.CustomCursorActionSync.TryResolveLocationEnter(
                    best, requesterId, out string locName, out _, out string targetKey)
                && (DWMPHorde.Patches.CustomCursorActionSync.IsLocationEnterAction(actionName)
                    || !RequesterIn(requesterId, locName)))
            {
                string debounceKey = actionName + "@" + requesterId;
                float now = Time.time;
                if (debounceKey == _cursorEnterDebounceKey && now < _cursorEnterDebounceUntil)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo(
                            $"[CursorActionSync] debounced location enter {actionName} p{requesterId}");
                    return;
                }
                _cursorEnterDebounceKey = debounceKey;
                _cursorEnterDebounceUntil = now + 2f;

                if (requesterId <= 0)
                {
                    ModLog.WarnRate(LogCat.World, "cursor-loc-no-requester:" + actionName,
                        $"[CursorActionSync] location enter {actionName} but no requester id");
                    return;
                }

                bool fromWorld = Singleton<WorldGrid>.Instance != null
                    && Singleton<WorldGrid>.Instance.currentGrid != null
                    && Singleton<WorldGrid>.Instance.currentGrid.name == "World";

                _net.SendToPlayer(requesterId, NetMessageType.LocationTransport,
                    w => new LocationTransportMessage
                    {
                        LocationName = locName,
                        FromWorld = fromWorld,
                        TargetKey = targetKey
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo(
                    $"[CursorActionSync] LocationTransport → p{requesterId} '{locName}' (from {actionName}"
                    + (string.IsNullOrEmpty(targetKey) ? ")" : ", onto " + targetKey + ")"));
                return;
            }

            ModRuntime.LegacyInfo(
                $"[CursorActionSync] host activate {best.name} at {best.transform.position}");
            DialogHostApplyGuard.RunHostWorldFanoutForPlayer(requesterId, () => best.activate());
        }

        /// <summary>Host: the requester already stands in that outside location (its last LocationEnter).</summary>
        private bool RequesterIn(int requesterId, string locationName)
        {
            if (!_net.RemoteOutsideLocation.TryGetValue(requesterId, out string at) || string.IsNullOrEmpty(at))
                return false;
            return string.Equals(Core.getTrueLocationName(at), locationName, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Item.activate sends onActivate on the using client, where one-shots are blocked.
        /// Replay only the trigger. Do not call activate() or the host opens the chest UI.
        /// </summary>
        private static bool TryFirePlainItemActivate(Vector3 pos, string name)
        {
            Item item = null;
            if (!string.IsNullOrEmpty(name))
                item = WorldQueryHelper.FindNearestByName<Item>(pos, name, 3f);
            if (item == null)
                item = WorldQueryHelper.FindNearest<Item>(pos, 2.5f);
            if (item == null) return false;

            if (DreamSyncManager.IsDreamActive)
            {
                Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
                if (dreamRoot != null
                    && !item.transform.IsChildOf(dreamRoot)
                    && !DreamSyncManager.IsAtDreamPad(item.transform.position))
                    return false;
            }

            ModRuntime.LegacyInfo($"[CursorActionSync] host onActivate {item.name} at {pos}");
            Item target = item;
            int actorId = ModRuntime.Network?.CurrentReceivePlayerId ?? 0;
            DialogHostApplyGuard.RunHostWorldFanoutForPlayer(actorId, () =>
                Core.sendTriggerInfo(target.gameObject, EventTrigger.Type.onActivate));

            // Vanilla Item.activate also calls useTimeSkip for beds / wait-until-evening.
            // Do not call activate() here (opens chest UI on host). Adopt the clock only.
            if (item.GetComponent<TimeSkip>() != null)
            {
                Controller ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !ctrl.isHardNight)
                {
                    int before = ctrl.CurrentTime;
                    ctrl.useTimeSkip();
                    if (ctrl.CurrentTime != before)
                    {
                        var lan = ModRuntime.Network;
                        lan?.SendTimeSyncTo(-1);
                        ModRuntime.LegacyInfo(
                            $"[CursorActionSync] host TimeSkip {item.name} time {before}→{ctrl.CurrentTime}");
                    }
                }
            }
            return true;
        }

        internal void HandleLocationTransport(LocationTransportMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (string.IsNullOrEmpty(msg.LocationName)) return;

            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null)
            {
                ModLog.WarnRate(LogCat.World, "loc-transport-no-ol",
                    $"[LocationTransport] OutsideLocations null for '{msg.LocationName}'");
                return;
            }

            // Already inside this location — ignore (debounce / duplicate).
            if (ol.playerInOutsideLocation
                && string.Equals(
                    DreamSyncManager.CanonicalDreamLocationName(ol.currentLocationName ?? ""),
                    DreamSyncManager.CanonicalDreamLocationName(msg.LocationName),
                    System.StringComparison.OrdinalIgnoreCase))
            {
                ModRuntime.LegacyInfo(
                    $"[LocationTransport] already in '{msg.LocationName}' — skip");
                return;
            }

            // Where in the location to land (a shortcut's exit point), once the pad has settled.
            _pendingTargetKey = string.IsNullOrEmpty(msg.TargetKey) ? null : msg.TargetKey;
            _pendingTargetLocation = msg.LocationName;
            _pendingTargetUntil = Time.realtimeSinceStartup + 60f;

            ModRuntime.LegacyInfo(
                $"[LocationTransport] prepareLocation '{msg.LocationName}' fromWorld={msg.FromWorld}"
                + (_pendingTargetKey != null ? " onto " + _pendingTargetKey : ""));
            // Vanilla prepareLocation: black screen, spawn the pad if needed, then transport.
            // (createLocation only spawns: the client was never moved, and an already spawned pad
            // threw on the duplicate spawnedLocations key.)
            ol.prepareLocation(msg.LocationName, RequestedTransportSource(msg.LocationName));
        }

        private static string _pendingTargetKey; // reset-in: ResetPendingTarget
        private static string _pendingTargetLocation; // reset-in: ResetPendingTarget
        private static float _pendingTargetUntil; // reset-in: ResetPendingTarget

        internal static void ResetPendingTarget()
        {
            _pendingTargetKey = null;
            _pendingTargetLocation = null;
            _pendingTargetUntil = 0f;
        }

        /// <summary>
        /// Client, the pad it was sent into has settled: stand on the point the host's transport
        /// named, as vanilla transportPlayerToObject does after its transportToLocation.
        /// </summary>
        internal static void OnLocalSettled(string locationName)
        {
            string key = _pendingTargetKey;
            if (key == null)
                return;
            bool live = Time.realtimeSinceStartup < _pendingTargetUntil;
            bool here = string.Equals(Core.getTrueLocationName(locationName ?? ""),
                Core.getTrueLocationName(_pendingTargetLocation ?? ""), System.StringComparison.OrdinalIgnoreCase);
            if (live && !here)
                return;
            _pendingTargetKey = null;
            _pendingTargetLocation = null;
            if (!live)
                return;
            GameObject target = Singleton<UniqueObjects>.Instance != null ? Singleton<UniqueObjects>.Instance.getObject(key) : null;
            Player p = Player.Instance;
            if (target == null || p == null)
            {
                ModLog.Warn(LogCat.World, $"[LocationTransport] landing point '{key}' not found in '{locationName}' — left at the entrance");
                return;
            }
            p.teleportTo(target.transform.position, target.transform.rotation);
            Singleton<WorldGrid>.Instance?.refreshPosition(p.transform.position, instant: true, force: true);
            ModRuntime.LegacyInfo($"[LocationTransport] landed on '{key}' at {target.transform.position}");
        }

        /// <summary>
        /// The entrance object vanilla passes to prepareLocation (the step's sourceTransform; where
        /// a death inside leaves its map marker): the same step on this player's own request.
        /// </summary>
        private Transform RequestedTransportSource(string locationName)
        {
            GameObject req = DWMPHorde.Patches.CustomCursorActionSync.LastRequested;
            CustomCursorAction action = req != null ? req.GetComponent<CustomCursorAction>() : null;
            if (action == null)
                return null;
            if (!DWMPHorde.Patches.CustomCursorActionSync.TryResolveLocationEnter(
                    action, _net.LocalPlayerId, out string name, out Transform source))
                return null;
            return string.Equals(name, locationName, System.StringComparison.Ordinal) ? source : null;
        }

        private static CustomCursorAction FindCustomCursorAction(Vector3 pos, string name)
        {
            Transform dreamRoot = DreamSyncManager.IsDreamActive
                ? DreamSyncManager.GetDreamLocationTransform()
                : null;

            bool OnPad(CustomCursorAction c) =>
                dreamRoot == null
                || c.transform.IsChildOf(dreamRoot)
                || c.transform == dreamRoot;

            CustomCursorAction Pick(CustomCursorAction c) =>
                c != null && OnPad(c) ? c : null;

            CustomCursorAction byName = null;
            if (!string.IsNullOrEmpty(name))
                byName = Pick(
                    WorldQueryHelper.FindNearestByName<CustomCursorAction>(pos, name, 3f));
            if (byName != null) return byName;

            CustomCursorAction nearest = Pick(
                WorldQueryHelper.FindNearest<CustomCursorAction>(pos, 2.5f));
            if (nearest != null) return nearest;

            if (dreamRoot == null) return null;

            CustomCursorAction[] all = WorldQueryHelper.GetCachedSceneComponents<CustomCursorAction>();
            CustomCursorAction padBest = null;
            float bestDist = 3f;
            for (int i = 0; i < all.Length; i++)
            {
                CustomCursorAction c = all[i];
                if (c == null || !OnPad(c)) continue;
                if (!string.IsNullOrEmpty(name)
                    && !c.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    padBest = c;
                }
            }
            if (padBest != null) return padBest;

            // Name mismatch (parent vs leaf) — nearest on pad within range.
            for (int i = 0; i < all.Length; i++)
            {
                CustomCursorAction c = all[i];
                if (c == null || !OnPad(c)) continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    padBest = c;
                }
            }
            return padBest;
        }
    
    }
}
