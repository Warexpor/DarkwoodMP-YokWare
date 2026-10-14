using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// One player pushing another. Each player's body is run by that player's own game; another
    /// player is only a stand-in here. Before, walking into a stand-in moved it a few units on the
    /// pusher's screen (an offset taken from each contact's speed, flickering every step) and
    /// never moved the pushed player at all: a push only got through as the pusher's stand-in
    /// crept into the pushed player's body on that player's screen, one round trip at a time.
    /// It was hard and it shook. Now the pusher sends its walk force (30 a second) to the pushed
    /// player's game, which adds <see cref="Share"/> of it to that player's body, as two bodies of
    /// the same weight would share one walker's force (a little more, so a push gets through).
    /// On the pusher's screen the stand-in is moved ahead by what the push will have done before
    /// its next positions show it (<see cref="LeadOffset"/>), so it gives way at once and evenly.
    /// </summary>
    internal static class PeerPush
    {
        /// <summary>The share of the pusher's walk force the pushed player gets (0.5 is plain physics for two equal bodies).</summary>
        internal const float Share = 0.6f;
        private const float SendInterval = 1f / 30f;
        /// <summary>A push not refreshed within this has stopped (a few sends' worth of loss).</summary>
        private const float PushHold = 0.15f;
        /// <summary>Walking into them: input this close to the direction of their body (cosine).</summary>
        private const float IntoBodyCos = 0.3f;
        /// <summary>Gap between the two bodies still counted as touching (the stand-in gives way ahead of the pusher).</summary>
        private const float ContactMargin = 14f;
        /// <summary>A push from a player whose stand-in is further than this from the local player is stale (a teleport).</summary>
        private const float MaxPusherRange = 200f;
        private const float MinIntentSq = 0.01f;
        /// <summary>Round trip allowed on top of the stand-in's playback delay before the push shows in its positions.</summary>
        private const float LeadRoundTrip = 0.06f;
        private const float MaxLead = 0.4f;
        private const float LeadRate = 8f;
        private const float FallbackRadius = 21.88f;

        private sealed class Incoming
        {
            public Vector3 Intent;
            public float Until;
        }

        private static float _nextSendAt;
        private static readonly Dictionary<int, Incoming> _incoming = new Dictionary<int, Incoming>(4);
        private static readonly Dictionary<int, float> _endedAt = new Dictionary<int, float>(4);
        private static readonly List<int> _done = new List<int>(4); // process-scoped: scratch buffer, cleared before each use
        /// <summary>Stand-ins the local player pushes now: their id and the speed the push gives them.</summary>
        private static readonly Dictionary<int, Vector3> _pushing = new Dictionary<int, Vector3>(4);

        /// <summary>LateUpdate: the local player walking into another player's stand-in sends its push there.</summary>
        internal static void TickSend(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected)
                return;
            float now = Time.unscaledTime;
            if (now < _nextSendAt)
                return;
            _nextSendAt = now + SendInterval;
            _pushing.Clear();
            Player p = Player.Instance;
            if (p == null || p.Rigidbody == null || p.dragging || p.startingDragging || p.dying || p.immobilised)
                return;
            Vector3 intent = PlayerMoveForcePatch.Intent;
            intent.y = 0f;
            if (intent.sqrMagnitude < MinIntentSq)
                return;
            Vector3 dir = intent.normalized;
            Vector3 me = p.Rigidbody.position;
            float touch = 2f * Radius(p) + ContactMargin;
            foreach (RemotePlayerProxy proxy in net.EnumerateRemoteProxies())
            {
                if (proxy == null || proxy.PlayerId <= 0 || !proxy.CanBePushed)
                    continue;
                Vector3 to = proxy.BodyPosition - me;
                to.y = 0f;
                float d = to.magnitude;
                if (d > touch || d < 0.01f || Vector3.Dot(dir, to / d) < IntoBodyCos)
                    continue;
                _pushing[proxy.PlayerId] = intent * (Share * WalkSpeedPerIntent(p.Rigidbody));
                var msg = new CoHoldMessage
                {
                    Mode = CoHoldMessage.ModePlayerPush,
                    SenderId = net.LocalPlayerId,
                    TargetId = proxy.PlayerId,
                    ObjectName = "",
                    PosX = me.x,
                    PosY = me.y,
                    PosZ = me.z,
                    ForceX = intent.x,
                    ForceZ = intent.z
                };
                net.BroadcastHot(NetMessageType.CoHold, w => msg.Serialize(w), DeliveryMethod.Unreliable);
            }
        }

        /// <summary>
        /// Where the stand-in of <paramref name="playerId"/> is put ahead of its played-back
        /// position (FixedUpdate): the speed the local player's push gives it times the time
        /// before its own positions show that push, eased in and out.
        /// </summary>
        internal static Vector3 LeadOffset(int playerId, Vector3 current, float delay)
        {
            Vector3 target = Vector3.zero;
            if (_pushing.TryGetValue(playerId, out Vector3 v) && Time.unscaledTime < _nextSendAt + PushHold)
            {
                float lead = Mathf.Min(MaxLead, Mathf.Max(0f, delay) + LeadRoundTrip);
                target = v * lead;
            }
            return Vector3.Lerp(current, target, 1f - Mathf.Exp(-LeadRate * Time.fixedDeltaTime));
        }

        /// <summary>A walk at this input moves a body of this mass and drag this fast (units a second per unit of input).</summary>
        private static float WalkSpeedPerIntent(Rigidbody rb)
        {
            float damp = rb.mass * rb.drag;
            return damp > 0.0001f ? PlayerMoveForcePatch.VanillaForceFactor * Time.fixedDeltaTime / damp : 0f;
        }

        private static float Radius(Player p)
        {
            var cap = p.GetComponent<CapsuleCollider>();
            return cap != null ? cap.radius : FallbackRadius;
        }

        /// <summary>Another player pushing the local player (host-stamped sender, meant for us).</summary>
        internal static void HandlePush(CoHoldMessage m)
        {
            if (!_incoming.TryGetValue(m.SenderId, out Incoming a))
            {
                a = new Incoming();
                _incoming[m.SenderId] = a;
                if (!_endedAt.TryGetValue(m.SenderId, out float ended) || Time.unscaledTime - ended > 2f)
                    ModLog.Event(LogCat.Physics, $"[PeerPush] p{m.SenderId} pushing us");
            }
            a.Intent = new Vector3(m.ForceX, 0f, m.ForceZ);
            a.Until = Time.unscaledTime + PushHold;
        }

        /// <summary>Physics step: every live push on the local player, as a share of the pusher's walk force.</summary>
        internal static void FixedTick()
        {
            if (_incoming.Count == 0)
                return;
            float now = Time.unscaledTime;
            Player p = Player.Instance;
            var net = ModRuntime.Network;
            bool canMove = p != null && p.Rigidbody != null && !p.Rigidbody.isKinematic
                && !p.dying && !p.immobilised && !p.jumping && !p.wantToJumpThroughWindow;
            _done.Clear();
            foreach (var kv in _incoming)
            {
                if (now > kv.Value.Until)
                {
                    _done.Add(kv.Key);
                    continue;
                }
                if (!canMove || net == null)
                    continue;
                RemotePlayerProxy pusher = net.GetProxy(kv.Key);
                if (pusher == null)
                    continue;
                Vector3 gap = pusher.BodyPosition - p.Rigidbody.position;
                gap.y = 0f;
                if (gap.sqrMagnitude > MaxPusherRange * MaxPusherRange)
                    continue;
                p.Rigidbody.AddForce(kv.Value.Intent * (PlayerMoveForcePatch.VanillaForceFactor * Time.fixedDeltaTime * Share));
            }
            for (int i = 0; i < _done.Count; i++)
            {
                _endedAt[_done[i]] = now;
                _incoming.Remove(_done[i]);
            }
            _done.Clear();
        }

        internal static void Reset()
        {
            _incoming.Clear();
            _endedAt.Clear();
            _pushing.Clear();
            _done.Clear();
            _nextSendAt = 0f;
        }
    }
}
