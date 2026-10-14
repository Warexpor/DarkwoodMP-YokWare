using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Players pushing one body with their own bodies. Only one machine runs a pushed body
    /// (the pusher's; the others follow its states with the body held kinematic), and other
    /// players' stand-ins pass through pushable bodies there, so a second pusher added nothing:
    /// two players moved it as fast as one. Now a player walking into a body another machine
    /// moves sends its walk force (about 15 a second); the machine that runs the body applies it
    /// at the contact point. Scaled so a crew pushing the same way moves it as SharedDrag's crew
    /// rule says (1.75 times one player's speed for two, about 2.33 for three, 2.8 for four), and
    /// a player pushing alone on a body another machine still runs (its player touching it, not
    /// pushing) moves it at a lone pusher's speed. Also adds a push to a body
    /// another player drags (that player's machine runs it). Pushing off-centre turns the body.
    /// </summary>
    internal static class CoPush
    {
        private const float SendInterval = 1f / 15f;
        /// <summary>
        /// A push not refreshed within this has stopped. The pusher's body touches the body it
        /// pushes on and off as that body moves away on a delay; a shorter hold dropped the push
        /// between touches (it came and went several times a second).
        /// </summary>
        private const float AssistHold = 0.4f;
        /// <summary>A push that resumes within this of its end is the same push (one log line).</summary>
        private const float SamePushGap = 2f;
        /// <summary>Walking into it: input this close to the direction of the body's centre (cosine).</summary>
        private const float IntoBodyCos = 0.3f;
        private const float SameBodyRange = 60f;
        private const float MinIntentSq = 0.01f;

        private sealed class Assist
        {
            public Item Item;
            public Rigidbody Rb;
            public Vector3 Force;
            public Vector3 Local;
            public float Until;
        }

        private static float _nextSendAt;
        private static readonly Dictionary<int, Assist> _assists = new Dictionary<int, Assist>(4);
        private static readonly Dictionary<int, float> _endedAt = new Dictionary<int, float>(4);
        private static readonly List<int> _done = new List<int>(4); // process-scoped: scratch buffer, cleared before each use
        private static readonly HashSet<int> _sentThisTick = new HashSet<int>(); // process-scoped: scratch buffer, cleared before each use
        private static readonly Collider[] _near = new Collider[64];

        /// <summary>LateUpdate: the local player walking into a body another machine runs sends its push there.</summary>
        internal static void TickSend(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected)
                return;
            float now = Time.unscaledTime;
            if (now < _nextSendAt)
                return;
            _nextSendAt = now + SendInterval;
            Player p = Player.Instance;
            if (p == null || p.dragging || p.startingDragging || p.touchingColliders == null)
                return;
            Vector3 intent = PlayerMoveForcePatch.Intent;
            intent.y = 0f;
            if (intent.sqrMagnitude < MinIntentSq)
                return;
            Vector3 dir = intent.normalized;
            Vector3 me = p.transform.position;
            _sentThisTick.Clear();
            for (int i = 0; i < p.touchingColliders.Count; i++)
            {
                Collider c = p.touchingColliders[i];
                if (c == null)
                    continue;
                Item item = c.GetComponentInParent<Item>();
                Rigidbody rb = item != null ? item.GetComponent<Rigidbody>() : null;
                if (rb == null || !_sentThisTick.Add(item.GetInstanceID()))
                    continue;
                if (!RunElsewhere(net, item, rb))
                    continue;
                Vector3 to = item.transform.position - me;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f || Vector3.Dot(dir, to.normalized) < IntoBodyCos)
                    continue;
                Vector3 contact = c.ClosestPoint(me);
                Vector3 local = item.transform.InverseTransformPoint(contact);
                Vector3 pos = item.transform.position;
                var msg = new CoHoldMessage
                {
                    Mode = CoHoldMessage.ModePush,
                    SenderId = net.LocalPlayerId,
                    TargetId = 0,
                    ObjectName = item.gameObject.name,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ForceX = intent.x,
                    ForceZ = intent.z,
                    LocalX = local.x,
                    LocalY = local.y,
                    LocalZ = local.z
                };
                net.BroadcastHot(NetMessageType.CoHold, w => msg.Serialize(w), DeliveryMethod.Unreliable);
            }
        }

        /// <summary>The body follows another machine here: held kinematic for its states, or played back from a remote drag.</summary>
        private static bool RunElsewhere(LanNetworkManager net, Item item, Rigidbody rb)
        {
            if (!rb.isKinematic)
                return false;
            return WorldPhysicsSyncService.IsSyncLocked(rb)
                || net.PlayerInteractHandlers.RemoteDragItemIds.Contains(item.GetInstanceID());
        }

        /// <summary>Another player's push: kept when this machine runs that body (pushes or drags it).</summary>
        internal static void HandlePush(CoHoldMessage m)
        {
            Item item = FindRunHere(m.ObjectName, new Vector3(m.PosX, m.PosY, m.PosZ), out Rigidbody rb);
            if (item == null)
                return;
            bool fresh = !_assists.TryGetValue(m.SenderId, out Assist a) || a.Item != item;
            if (fresh)
            {
                a = new Assist { Item = item, Rb = rb };
                _assists[m.SenderId] = a;
                if (!_endedAt.TryGetValue(m.SenderId, out float ended) || Time.unscaledTime - ended > SamePushGap)
                    ModLog.Event(LogCat.Physics, $"[CoPush] p{m.SenderId} pushing {m.ObjectName} with us");
            }
            a.Force = new Vector3(m.ForceX, 0f, m.ForceZ);
            a.Local = new Vector3(m.LocalX, m.LocalY, m.LocalZ);
            a.Until = Time.unscaledTime + AssistHold;
        }

        private static Item FindRunHere(string name, Vector3 pos, out Rigidbody rb)
        {
            rb = null;
            if (string.IsNullOrEmpty(name))
                return null;
            Player p = Player.Instance;
            if (p != null && p.dragging && p.itemBeingDragged != null
                && string.Equals(p.itemBeingDragged.gameObject.name, name, System.StringComparison.Ordinal)
                && Flat(p.itemBeingDragged.transform.position - pos) <= SameBodyRange)
            {
                // A co-holder here does not run it: the dragger's machine does.
                if (SharedDrag.IsFollowingItem(p.itemBeingDragged))
                    return null;
                rb = p.itemBeingDragged.GetComponent<Rigidbody>();
                return rb != null && !rb.isKinematic ? p.itemBeingDragged : null;
            }
            if (!Audio.ItemMovingSoundHelper.IsLocalPushOrDragOwner(name))
                return null;
            int n = Physics.OverlapSphereNonAlloc(pos, SameBodyRange, _near);
            Item best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = _near[i];
                if (c == null)
                    continue;
                Item item = c.GetComponentInParent<Item>();
                if (item == null || !string.Equals(item.gameObject.name, name, System.StringComparison.Ordinal))
                    continue;
                Rigidbody body = item.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic)
                    continue;
                float d = Flat(item.transform.position - pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = item;
                    rb = body;
                }
            }
            return best;
        }

        /// <summary>Physics step: every live push from another player, at its contact point.</summary>
        internal static void FixedTick()
        {
            if (_assists.Count == 0)
                return;
            float now = Time.unscaledTime;
            _done.Clear();
            foreach (var kv in _assists)
            {
                Assist a = kv.Value;
                if (now > a.Until || a.Item == null || a.Rb == null || a.Rb.isKinematic)
                {
                    _done.Add(kv.Key);
                    continue;
                }
                Vector3 point = a.Item.transform.TransformPoint(a.Local);
                point.y = a.Rb.worldCenterOfMass.y;
                float scale = Scale(a);
                a.Rb.AddForceAtPosition(
                    a.Force * (PlayerMoveForcePatch.VanillaForceFactor * Time.fixedDeltaTime * scale), point);
            }
            for (int i = 0; i < _done.Count; i++)
            {
                _endedAt[_done[i]] = now;
                _assists.Remove(_done[i]);
            }
            _done.Clear();
        }

        /// <summary>
        /// The share of a vanilla walk force another player's push gets here, so the whole crew
        /// on the body (the local player pushing or dragging it, a shared drag's co-holders, and
        /// every remote pusher) moves it at <see cref="SharedDrag.Together"/> of its size: 1.75
        /// times one player's speed for two, about 2.33 for three, 2.8 for four. Nobody here
        /// pushing: the pushes move the body alone, at the body's share of the damping of it and
        /// a pusher (mass times drag of each). A dragged body whose dragger stands carries the
        /// dragger: the pushers drag it along.
        /// </summary>
        private static float Scale(Assist a)
        {
            Player p = Player.Instance;
            Vector3 mine = PlayerMoveForcePatch.Intent;
            mine.y = 0f;
            bool dragged = p != null && p.dragging && p.itemBeingDragged == a.Item;
            bool pushingToo = false;
            if (mine.sqrMagnitude >= MinIntentSq && p != null)
            {
                if (dragged)
                    pushingToo = true;
                else if (p.touchingColliders != null)
                {
                    Vector3 to = a.Item.transform.position - p.transform.position;
                    to.y = 0f;
                    if (to.sqrMagnitude > 0.0001f && Vector3.Dot(mine.normalized, to.normalized) >= IntoBodyCos)
                    {
                        for (int i = 0; i < p.touchingColliders.Count; i++)
                        {
                            Collider c = p.touchingColliders[i];
                            if (c != null && c.GetComponentInParent<Item>() == a.Item)
                            {
                                pushingToo = true;
                                break;
                            }
                        }
                    }
                }
            }
            int remote = 0;
            float now = Time.unscaledTime;
            foreach (var kv in _assists)
                if (kv.Value.Item == a.Item && now <= kv.Value.Until)
                    remote++;
            if (remote < 1)
                remote = 1;
            if (p == null || p.Rigidbody == null)
                return 1f;
            float body = a.Rb.mass * a.Rb.drag;
            float pusher = p.Rigidbody.mass * p.Rigidbody.drag;
            if (pushingToo)
                return SharedDrag.AssistShare(dragged ? SharedDrag.LocalCrew(a.Item) : 1, remote, body, pusher);
            if (dragged)
                return SharedDrag.Together(remote) / remote;
            return SharedDrag.AssistShare(0, remote, body, pusher);
        }

        internal static void Reset()
        {
            _assists.Clear();
            _endedAt.Clear();
            _done.Clear();
            _nextSendAt = 0f;
        }

        private static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }
    }
}
