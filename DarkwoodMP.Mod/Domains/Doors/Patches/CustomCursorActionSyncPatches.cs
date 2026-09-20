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
