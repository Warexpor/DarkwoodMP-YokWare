using System.Collections;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Vanilla <c>EventTrigger.fire</c> and <c>GameEvent.fire</c> are coroutines that wait (at least a
    /// frame, even with no delay) before doing anything. Every scope the mod opens around starting
    /// them was gone by the time their body ran: on the host, who the action belongs to (so a
    /// client's trigger fanned out as the host's, and personal rewards like recipes, items or a
    /// teleport landed on the host) and the host-apply scope; on a client, the replay scope (so a
    /// replayed event's flag or door change echoed back to the host) and the "not your reward"
    /// scope. The scope present when the coroutine is created is captured here and re-entered
    /// around each of its steps.
    /// </summary>
    internal static class EventCoroutineScope
    {
        private struct Scope
        {
            public int Actor;
            public bool HostApply;
            public bool ClientReplay;
            public bool SuppressPersonal;
            public bool PlayerUse;

            public bool Any => Actor > 0 || HostApply || ClientReplay || SuppressPersonal || PlayerUse;
        }

        private static Scope Capture()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return default;
            var s = new Scope();
            if (net.Role == NetworkRole.Host)
            {
                s.Actor = GeFireActorContext.PeekOr(0);
                s.HostApply = HostApplyGuard.Active;
                s.PlayerUse = PlayerUseScope.Active;
            }
            else
            {
                s.Actor = GeFireActorContext.PeekOr(0);
                s.ClientReplay = NetworkApplyGuard.IsActive || LanNetworkManager.IsApplyingRemoteState;
                s.SuppressPersonal = GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer;
            }
            return s;
        }

        internal static IEnumerator Wrap(IEnumerator inner)
        {
            if (inner == null)
                return null;
            Scope s = Capture();
            return s.Any ? new ScopedEnumerator(inner, s) : inner;
        }

        private sealed class ScopedEnumerator : IEnumerator
        {
            private readonly IEnumerator _inner;
            private readonly Scope _scope;

            internal ScopedEnumerator(IEnumerator inner, Scope scope)
            {
                _inner = inner;
                _scope = scope;
            }

            public object Current => _inner.Current;

            public void Reset() => _inner.Reset();

            public bool MoveNext()
            {
                if (_scope.Actor > 0)
                    GeFireActorContext.Push(_scope.Actor);
                if (_scope.HostApply)
                    HostApplyGuard.Begin();
                if (_scope.PlayerUse)
                    PlayerUseScope.Depth++;
                NetworkApplyGuard replay = _scope.ClientReplay ? new NetworkApplyGuard() : null;
                bool prevSuppress = GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer;
                if (_scope.SuppressPersonal)
                    GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer = true;
                try
                {
                    return _inner.MoveNext();
                }
                finally
                {
                    GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer = prevSuppress;
                    replay?.Dispose();
                    if (_scope.PlayerUse)
                        PlayerUseScope.Depth--;
                    if (_scope.HostApply)
                        HostApplyGuard.End();
                    if (_scope.Actor > 0)
                        GeFireActorContext.Pop();
                }
            }
        }
    }

    /// <summary>
    /// Host: a player used or examined something. Repeatable (multipleFire) event sets are not
    /// fanned out as a rule, because clients run their own ambient and area copies; but a
    /// client's use and examine never run locally (they go to the host), so the events they set
    /// off must be sent like one-shots or nobody, the user included, sees them.
    /// </summary>
    internal static class PlayerUseScope
    {
        internal static int Depth; // process-scoped: call-scoped, balanced by the patch Finalizer and ScopedEnumerator

        internal static bool Active => Depth > 0;
    }

    [HarmonyPatch(typeof(EventTriggers), nameof(EventTriggers.fireEventTrigger))]
    public static class PlayerUseScopePatch
    {
        private static void Prefix(EventTrigger.Type TriggerType, out bool __state)
        {
            __state = (TriggerType == EventTrigger.Type.onActivate || TriggerType == EventTrigger.Type.onExamine)
                && NetGuard.Host(out _);
            if (__state)
                PlayerUseScope.Depth++;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                PlayerUseScope.Depth--;
        }
    }

    [HarmonyPatch(typeof(GameEvent), nameof(GameEvent.fire))]
    public static class GameEventFireScopePatch
    {
        /// <summary>
        /// Client replaying the host's event: character spawns, scripted hits, and
        /// steps that ADD to a value (a world flag counter, shared NPC reputation) already happened
        /// on the host, which sends the result. Doing them again here doubled or fought it.
        /// </summary>
        private static bool Prefix(GameEvent __instance, ref IEnumerator __result)
        {
            if (__instance == null || !NetGuard.Connected(out LanNetworkManager net) || net.Role != NetworkRole.Client)
                return true;
            // A clock tween: the host's runs and its clock reaches everyone. A client's own run of a
            // repeatable step (entering the doctor's house sets the hour, on every entry) turned this
            // clock alone to night until the next TimeSync; on a replay it fought every TimeSync.
            if (__instance.type == GameEvent.Type.tweenTime)
            {
                __result = DWMPHorde.Harmony.HarmonyCoroutineUtil.Empty();
                return false;
            }
            if (!NetworkApplyGuard.IsActive && !LanNetworkManager.IsApplyingRemoteState)
                return true;
            bool relativeFlag = __instance.type == GameEvent.Type.worldFlag
                && __instance.activeModifier2 && __instance.relentlessPursuit;
            bool relativeSharedRep = __instance.type == GameEvent.Type.modifyCharacter
                && __instance.characterModifyType == GameEvent.CharacterModify.reputation
                && __instance.activeModifier
                && !ReputationSyncUtil.IsPerPlayerReputationNpcName(__instance.Value);
            // The host spawned (or replaced) the creature and sends its body; the replay's own spawn
            // was a second, local-only creature at its own random spot (as the night location
            // event replay already skips: ClientScenarioEventNoCharacterSpawnPatch).
            bool characterSpawn = __instance.type == GameEvent.Type.spawnCharacter
                || __instance.type == GameEvent.Type.replaceCharacter;
            // A scripted hit: on a creature here it ran vanilla's own death on the copy (loot
            // rolled again, death events fired here) beside the host's real one; doors, windows
            // and breakables take the host's result through their own sync. A hit on the player
            // body is that player's own (the host's copy skips it for a peer's scene): the
            // personal-step rules run it on the actor only.
            bool scriptedHit = __instance.type == GameEvent.Type.gameObject
                && __instance.gameObjectModifyType == GameEvent.GameObjectModify.getHit
                && !GameEventPersonalActorPatch.TargetsPlayerBody(__instance);
            // A scripted spawn of a creature or an item: the host's copy is the real one and
            // reaches this peer through its own sync; a second local one was a phantom creature
            // or an extra pickup. Plain props and decor still spawn here.
            Transform spawned = __instance.targetTransform;
            // A world object the save keeps (the night mushroom) is the host's too: it sends the
            // one it spawned at its own spot (ScriptedSpawnSync).
            bool scriptedSpawn = __instance.type == GameEvent.Type.gameObject
                && __instance.gameObjectModifyType == GameEvent.GameObjectModify.spawn
                && spawned != null
                && (spawned.GetComponent<Character>() != null || spawned.GetComponent<Item>() != null
                    || ScriptedSpawnSync.IsWorldObject(spawned));
            // The night scene's door step ("a door opens by itself") picks a random door of the
            // location: the replay opened a different door here. The host's open reaches this
            // peer as DoorOpen. Only the night scene: elsewhere (the dialogue doors) the replay
            // is the door's own path.
            bool sceneDoor = ClientRandomEventGate.PlayingHostLocationEvent
                && __instance.type == GameEvent.Type.modifyDoor
                && (__instance.doorModifyType == GameEvent.DoorModify.open
                    || __instance.doorModifyType == GameEvent.DoorModify.close);
            if (!relativeFlag && !relativeSharedRep && !characterSpawn && !scriptedHit && !scriptedSpawn && !sceneDoor)
                return true;
            __result = DWMPHorde.Harmony.HarmonyCoroutineUtil.Empty();
            return false;
        }

        private static void Postfix(GameEvent __instance, ref IEnumerator __result)
            => __result = EventCoroutineScope.Wrap(ScriptedSpawnSync.WrapHost(__instance, __result));
    }

    [HarmonyPatch(typeof(EventTrigger), nameof(EventTrigger.fire))]
    public static class EventTriggerFireScopePatch
    {
        private static void Postfix(ref IEnumerator __result) => __result = EventCoroutineScope.Wrap(__result);
    }
}

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A location's activation runs over a few frames (vanilla activateOverTime) before its
    /// on-enter events fire; the "who entered" scope around the host's enter for a peer was gone
    /// by then, so the events credited the host. The scope rides along like the events' own.
    /// </summary>
    [HarmonyPatch(typeof(Location), "activateOverTime")]
    public static class LocationActivateScopePatch
    {
        private static void Postfix(ref System.Collections.IEnumerator __result) => __result = EventCoroutineScope.Wrap(__result);
    }
}
