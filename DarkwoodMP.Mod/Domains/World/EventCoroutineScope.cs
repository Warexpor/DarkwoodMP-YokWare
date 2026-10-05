using System.Collections;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using HarmonyLib;

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
        /// Client replaying the host's event: character spawns, and steps that ADD to a value (a world flag counter,
        /// shared NPC reputation) already happened on the host, which sends the resulting value.
        /// Adding again here gave double (or, racing the host's value, wrong) totals.
        /// </summary>
        private static bool Prefix(GameEvent __instance, ref IEnumerator __result)
        {
            if (__instance == null || !NetGuard.Connected(out LanNetworkManager net) || net.Role != NetworkRole.Client)
                return true;
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
            if (!relativeFlag && !relativeSharedRep && !characterSpawn)
                return true;
            __result = DWMPHorde.Harmony.HarmonyCoroutineUtil.Empty();
            return false;
        }

        private static void Postfix(ref IEnumerator __result) => __result = EventCoroutineScope.Wrap(__result);
    }

    [HarmonyPatch(typeof(EventTrigger), nameof(EventTrigger.fire))]
    public static class EventTriggerFireScopePatch
    {
        private static void Postfix(ref IEnumerator __result) => __result = EventCoroutineScope.Wrap(__result);
    }
}
