using System.Collections;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A scripted step that spawns something at "the player" (UniqueObjects key "player": the gore
    /// floor and its stand-still check under the player's feet) put it at this machine's own body,
    /// whoever the step played for. The host plays a night scene once per occupied hideout with
    /// that hideout's player as the actor: a peer's copy dropped its pieces at the host's feet in
    /// another hideout, and their later fires went to the peer, who had nothing there ("no
    /// GameEvents near ... name='check' after 60s, dropped"). While such a step runs for another
    /// player, "player" resolves to that player's body: a step that hangs its piece on the player
    /// (activeModifier2) gets the body itself; otherwise a marker at the body, in that player's
    /// location, so the piece lies there and its own steps find that location (vanilla falls back
    /// to the local player's location: "Location not found for gevent check" with the host away).
    /// </summary>
    internal static class ActorSpawnAnchor
    {
        private static GameEvent _current; // process-scoped: set and cleared within one MoveNext
        private static GameObject _marker; // process-scoped: one reused marker, recreated if destroyed

        internal static IEnumerator Wrap(GameEvent ge, IEnumerator inner)
        {
            if (inner == null || ge == null || ge.type != GameEvent.Type.gameObject
                || ge.gameObjectModifyType != GameEvent.GameObjectModify.spawn
                || !GameEventPersonalActorPatch.TargetsPlayerBody(ge)
                || ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return inner;
            return new Scoped(inner, ge);
        }

        /// <summary>UniqueObjects.getObject("player") inside such a step, for another player: its marker.</summary>
        internal static GameObject Resolve(GameObject vanilla)
        {
            if (_current == null)
                return vanilla;
            Player p = Player.Instance;
            Transform body = GeFireActorContext.ActorBody();
            if (p == null || body == null || body == p._transform)
                return vanilla;
            if (_current.activeModifier2)
                return body.gameObject;
            if (_marker == null)
                _marker = new GameObject("DWMP_ActorSpawnAnchor");
            Location loc = GeFireActorContext.ActorBigLocation();
            _marker.transform.SetParent(loc != null ? loc.transform : p._transform.parent, worldPositionStays: false);
            _marker.transform.SetPositionAndRotation(body.position, body.rotation);
            return _marker;
        }

        private sealed class Scoped : IEnumerator
        {
            private readonly IEnumerator _inner;
            private readonly GameEvent _ge;

            internal Scoped(IEnumerator inner, GameEvent ge)
            {
                _inner = inner;
                _ge = ge;
            }

            public object Current => _inner.Current;

            public void Reset() => _inner.Reset();

            public bool MoveNext()
            {
                GameEvent prev = _current;
                _current = _ge;
                try { return _inner.MoveNext(); }
                finally { _current = prev; }
            }
        }
    }

    [HarmonyPatch(typeof(UniqueObjects), nameof(UniqueObjects.getObject))]
    public static class ActorSpawnAnchorGetPatch
    {
        private static void Postfix(string type, ref GameObject __result)
        {
            if (type == "player")
                __result = ActorSpawnAnchor.Resolve(__result);
        }
    }
}
