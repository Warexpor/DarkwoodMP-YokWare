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
    /// LocationTransport so the requester runs prepareLocation locally. An "_enter" action
    /// whose live triggers move nobody (the closed church hatch) is activated as usual.
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
        /// <summary>Client: the object of its last deferred use (the host's LocationTransport answers it).</summary>
        internal static GameObject LastRequested; // process-scoped: scene object ref, replaced by each request

        internal static bool TryDeferClientActivate(GameObject destGO, EventTrigger.Type triggerType)
        {
            if (destGO == null) return true;
            if (triggerType != EventTrigger.Type.onActivate) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;

            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client) return true;

            bool cursor = destGO.GetComponent<CustomCursorAction>() != null;
            bool worldUse = destGO.GetComponent<Item>() != null
                || destGO.GetComponent<EventTriggers>() != null;
            if (!cursor && !worldUse) return true;

            Vector3 p = destGO.transform.position;
            LastRequested = destGO;
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
            // Do not latch the one-shot locally. CustomCursorAction stops here.
            // A plain Item still continues activate() after this call (chest UI, switch).
            return false;
        }

        internal static bool IsLocationEnterAction(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            return objectName.IndexOf("_enter", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The outside location this action takes its user into, read from what vanilla's own
        /// onActivate would fire: the triggers whose requirements pass (the church hatch has two,
        /// gated by the church dream's outcome flags) and their transport step.
        /// transportToOutsideLocation names it in Value (vanilla prepareLocation(Value,
        /// sourceTransform)); transportPlayerToObject names a target inside it. False when the
        /// action moves nobody (a locked hatch, a dream hole): it is activated like any other.
        /// </summary>
        internal static bool TryResolveLocationEnter(CustomCursorAction action, int actorPlayerId,
            out string locationName, out Transform source)
        {
            locationName = null;
            source = null;
            if (action == null) return false;

            var geList = new List<GameEvents>(8);
            // Requirements read the body of the player using it (location, health, items).
            bool pushed = actorPlayerId > 0;
            if (pushed) GeFireActorContext.Push(actorPlayerId);
            try
            {
                CollectActivateGameEvents(action.gameObject, geList);
            }
            finally
            {
                if (pushed) GeFireActorContext.Pop();
            }

            for (int g = 0; g < geList.Count; g++)
            {
                GameEvents ge = geList[g];
                if (ge == null || ge.events == null) continue;
                for (int e = 0; e < ge.events.Count; e++)
                {
                    GameEvent evt = ge.events[e];
                    if (evt == null || evt.disabled) continue;
                    if (evt.type == GameEvent.Type.transportToOutsideLocation)
                    {
                        if (string.IsNullOrEmpty(evt.Value)) continue;
                        locationName = evt.Value;
                        source = evt.sourceTransform;
                        return true;
                    }
                    // activeModifier = chapter jump (GameEventPersonalActorPatch.IsChapterJump).
                    if (evt.type != GameEvent.Type.transportPlayerToObject || evt.activeModifier)
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

        /// <summary>
        /// Vanilla <c>Core.sendTriggerInfo(go, onActivate)</c> → <c>EventTriggers.fireEventTrigger</c>:
        /// the set's own requirements, then each onActivate trigger that is enabled, not latched,
        /// and whose requirements pass. An object without onActivate triggers falls back to the
        /// events on it and its children.
        /// </summary>
        private static void CollectActivateGameEvents(GameObject go, List<GameEvents> into)
        {
            if (go == null) return;

            EventTriggers ets = go.GetComponent<EventTriggers>();
            if (ets != null && ets.eventTriggers != null)
            {
                bool anyActivate = false;
                for (int i = 0; i < ets.eventTriggers.Count; i++)
                {
                    EventTrigger et = ets.eventTriggers[i];
                    if (et != null && et.type == EventTrigger.Type.onActivate)
                    {
                        anyActivate = true;
                        break;
                    }
                }
                if (anyActivate)
                {
                    if (!SetRequirementsMet(ets))
                        return;
                    for (int i = 0; i < ets.eventTriggers.Count; i++)
                    {
                        EventTrigger et = ets.eventTriggers[i];
                        if (et == null || et.type != EventTrigger.Type.onActivate || et.disabled)
                            continue;
                        if ((et.fired && !et.multipleFire) || !et.requirementsMet())
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
                    return;
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

        /// <summary>Vanilla <c>EventTriggers.requirementsMet</c> (private): the set's own requirements.</summary>
        private static bool SetRequirementsMet(EventTriggers ets)
        {
            if (ets.eventRequirements == null) return true;
            for (int i = 0; i < ets.eventRequirements.Count; i++)
            {
                EventTriggerRequirement req = ets.eventRequirements[i];
                if (req == null) continue;
                if (req.requirementsMet() == ets.inverseRequirements)
                    return false;
            }
            return true;
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
