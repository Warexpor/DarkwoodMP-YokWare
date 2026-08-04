using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// CustomCursorAction ("Lie down", custom UI actions) → EventTrigger.onActivate →
    /// one-shot GameEvents. Client one-shots are blocked by GameEventsFiredPatch, so
    /// the press was a silent no-op (dream bed → GE "item" → endDream dream_underground_bed).
    /// Mirror ExaminableSync: client defers onActivate to host.
    ///
    /// Location-enter actions (med_bunker_enter_*_enter) are per-player transport —
    /// host must NOT activate() (that TPs the host). Host resolves dest and sends
    /// LocationTransport so the requester runs createLocation locally.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(bool) })]
    public static class CustomCursorActionActivateSyncPatch
    {
        private static bool Prefix(GameObject destGO, EventTrigger.Type triggerType)
        {
            return CustomCursorActionSync.TryDeferClientActivate(destGO, triggerType);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) })]
    public static class CustomCursorActionActivateSyncValuePatch
    {
        private static bool Prefix(GameObject destGO, EventTrigger.Type triggerType)
        {
            return CustomCursorActionSync.TryDeferClientActivate(destGO, triggerType);
        }
    }

    internal static class CustomCursorActionSync
    {
        internal static bool TryDeferClientActivate(GameObject destGO, EventTrigger.Type triggerType)
        {
            if (destGO == null) return true;
            if (triggerType != EventTrigger.Type.onActivate) return true;
            if (destGO.GetComponent<CustomCursorAction>() == null) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Client) return true;

            Vector3 p = destGO.transform.position;
            net.Send(NetMessageType.ActivateCursorAction,
                w => new ActivateCursorActionMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = destGO.name ?? ""
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[CursorActionSync] client request {destGO.name} at {p}");
            return false;
        }

        internal static bool IsLocationEnterAction(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return objectName.IndexOf("_enter", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Find transportPlayerToObject dest → OutsideLocation name (vanilla GameEvent path).
        /// </summary>
        internal static bool TryResolveLocationEnterName(CustomCursorAction action, out string locationName)
        {
            locationName = null;
            if (action == null) return false;

            var geList = new List<GameEvents>(8);
            CollectGameEvents(action.gameObject, geList);

            for (int g = 0; g < geList.Count; g++)
            {
                GameEvents ge = geList[g];
                if (ge == null || ge.events == null) continue;
                for (int e = 0; e < ge.events.Count; e++)
                {
                    GameEvent evt = ge.events[e];
                    if (evt == null || evt.type != GameEvent.Type.transportPlayerToObject)
                        continue;

                    GameObject target = ResolveFirstTransportTarget(evt);
                    if (target == null) continue;

                    Location destLoc = Location.getAtPos(target.transform.position);
                    if (destLoc == null)
                        destLoc = target.transform.GetLocation();
                    if (destLoc == null || !destLoc.isOutsideLocation)
                        continue;

                    locationName = Core.getTrueLocationName(destLoc.name);
                    if (!string.IsNullOrEmpty(locationName))
                        return true;
                }
            }
            return false;
        }

        private static void CollectGameEvents(GameObject go, List<GameEvents> into)
        {
            if (go == null) return;

            EventTriggers ets = go.GetComponent<EventTriggers>();
            if (ets == null)
                ets = go.GetComponentInParent<EventTriggers>();
            if (ets != null && ets.eventTriggers != null)
            {
                for (int i = 0; i < ets.eventTriggers.Count; i++)
                {
                    EventTrigger et = ets.eventTriggers[i];
                    if (et == null || et.type != EventTrigger.Type.onActivate)
                        continue;
                    if (et.gameEvents != null && !into.Contains(et.gameEvents))
                        into.Add(et.gameEvents);
                    if (et.getGameEventsFromMe)
                    {
                        GameEvents self = ets.GetComponent<GameEvents>();
                        if (self != null && !into.Contains(self))
                            into.Add(self);
                    }
                }
            }

            GameEvents onGo = go.GetComponent<GameEvents>();
            if (onGo != null && !into.Contains(onGo))
                into.Add(onGo);

            GameEvents[] children = go.GetComponentsInChildren<GameEvents>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] != null && !into.Contains(children[i]))
                    into.Add(children[i]);
            }
        }

        private static GameObject ResolveFirstTransportTarget(GameEvent evt)
        {
            if (evt.targetGameObjects != null)
            {
                for (int i = 0; i < evt.targetGameObjects.Count; i++)
                {
                    if (evt.targetGameObjects[i] != null)
                        return evt.targetGameObjects[i];
                }
            }

            if (evt.targetUniqueObjects != null
                && Singleton<UniqueObjects>.Instance != null)
            {
                for (int i = 0; i < evt.targetUniqueObjects.Count; i++)
                {
                    string key = evt.targetUniqueObjects[i];
                    if (string.IsNullOrEmpty(key)) continue;
                    GameObject uo = Singleton<UniqueObjects>.Instance.getObject(key);
                    if (uo != null) return uo;
                }
            }

            if (evt.targetTransform != null)
                return evt.targetTransform.gameObject;

            return null;
        }
    }
}

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private string _cursorEnterDebounceKey;
        private float _cursorEnterDebounceUntil;

        private void HandleActivateCursorAction(ActivateCursorActionMessage msg)
        {
            if (_role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            CustomCursorAction best = FindCustomCursorAction(pos, msg.ObjectName);
            if (best == null)
            {
                ModRuntime.Log?.LogWarning(
                    $"[CursorActionSync] host: no CustomCursorAction near {pos} name={msg.ObjectName}");
                return;
            }

            string actionName = best.name ?? msg.ObjectName ?? "";
            int requesterId = _currentReceivePlayerId;

            // Location enter is per-player transport — never activate() on host (TPs host).
            if (DWMPHorde.Patches.CustomCursorActionSync.IsLocationEnterAction(actionName))
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
                    ModRuntime.Log?.LogWarning(
                        $"[CursorActionSync] location enter {actionName} but no requester id");
                    return;
                }

                if (!DWMPHorde.Patches.CustomCursorActionSync.TryResolveLocationEnterName(best, out string locName)
                    || string.IsNullOrEmpty(locName))
                {
                    ModRuntime.Log?.LogWarning(
                        $"[CursorActionSync] location enter {actionName}: could not resolve dest");
                    return;
                }

                bool fromWorld = Singleton<WorldGrid>.Instance != null
                    && Singleton<WorldGrid>.Instance.currentGrid != null
                    && Singleton<WorldGrid>.Instance.currentGrid.name == "World";

                SendToPlayer(requesterId, NetMessageType.LocationTransport,
                    w => new LocationTransportMessage
                    {
                        LocationName = locName,
                        FromWorld = fromWorld
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo(
                    $"[CursorActionSync] LocationTransport → p{requesterId} '{locName}' (from {actionName})");
                return;
            }

            ModRuntime.LegacyInfo(
                $"[CursorActionSync] host activate {best.name} at {best.transform.position}");
            best.activate();
        }

        private void HandleLocationTransport(LocationTransportMessage msg)
        {
            if (_role != NetworkRole.Client) return;
            if (string.IsNullOrEmpty(msg.LocationName)) return;

            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null)
            {
                ModRuntime.Log?.LogWarning(
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

            ModRuntime.LegacyInfo(
                $"[LocationTransport] createLocation '{msg.LocationName}' fromWorld={msg.FromWorld}");
            // Vanilla createLocation: loading screen + spawn-if-needed + transport.
            ol.createLocation(msg.LocationName);
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

            CustomCursorAction[] all = Object.FindObjectsOfType<CustomCursorAction>(true);
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
