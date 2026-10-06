using System.Collections.Generic;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Who triggered the GameEvents about to fire on the host. Postfix stamps
    /// this into GameEventsFiredMessage.ActorPlayerId so peers only run
    /// Player.Instance personal effects for that actor.
    /// </summary>
    public static class GeFireActorContext
    {
        private static readonly Stack<int> _stack = new Stack<int>(4);

        public static int Depth => _stack.Count;

        public static void Push(int actorPlayerId)
        {
            if (actorPlayerId > 0)
                _stack.Push(actorPlayerId);
        }

        public static void Pop()
        {
            if (_stack.Count > 0)
                _stack.Pop();
        }

        public static int PeekOr(int fallback)
            => _stack.Count > 0 ? _stack.Peek() : fallback;

        public static void Reset() => _stack.Clear();

        /// <summary>
        /// The body of the player the running scripted step belongs to: that peer's stand-in when
        /// it is someone else (host applying a peer's action, or a client replaying a peer's event),
        /// else the local player. Vanilla story functions measure from <c>Player.Instance</c>.
        /// </summary>
        public static UnityEngine.Transform ActorBody()
        {
            Player p = Player.Instance;
            int actor = PeekOr(0);
            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (actor > 0 && net != null && net.IsConnected && actor != net.LocalPlayerId)
            {
                var proxy = net.GetProxy(actor);
                if (proxy != null)
                    return proxy.transform;
            }
            return p != null ? p._transform : null;
        }

        /// <summary>The location (big location) the acting player stands in.</summary>
        public static Location ActorBigLocation()
        {
            Player p = Player.Instance;
            UnityEngine.Transform body = ActorBody();
            if (p != null && body == p._transform && p.whereAmI != null)
            {
                p.whereAmI.checkWhereAmI();
                return p.whereAmI.bigLocation;
            }
            if (body == null)
                return null;
            Location at = Location.getAtPos(body.position);
            return at != null && at.bigLocation != null ? at.bigLocation : at;
        }
    }
}
