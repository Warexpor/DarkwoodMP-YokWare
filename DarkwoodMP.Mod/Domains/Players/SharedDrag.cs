using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared drag (experimental, config <c>SharedDrag</c>): a second player takes hold of a body
    /// another player drags, and the two move it together. Vanilla hinges the body to the
    /// dragger's rigidbody and the dragger's walk force moves the pair; here the dragger's
    /// machine runs one body with both players on it:
    /// <list type="bullet">
    /// <item>Dragger (leader): its vanilla hinge, plus a hinge of the same kind from the
    /// co-holder's stand-in to the body. The stand-in stops following its network position and
    /// becomes a real body with the player's mass and drag, pushed by the co-holder's walk input
    /// (sent about 30 a second). Walls block either player; pulling apart turns the body.</item>
    /// <item>Co-holder (follower): no hinge and no walk force of its own. Its copy of the body
    /// plays back the dragger's poses like any remote drag, and its player's body is moved to the
    /// spot the dragger's machine has it at (carried in the same samples), on the same delay, so
    /// body and player stay together. Its push animation and legs are vanilla's.</item>
    /// </list>
    /// Speed: both players' walk forces are scaled by one factor chosen so that two players
    /// walking the same way move it at 1.5 times a lone dragger's speed, one walking while the
    /// other stands at 0.75 times (it drags the other along), and two pulling opposite ways
    /// hold it still. When the dragger lets go (or leaves), the co-holder keeps hold and drags on
    /// alone; when the co-holder lets go, nothing changes for the dragger. One co-holder at most.
    /// </summary>
    internal static class SharedDrag
    {
        /// <summary>Two players walking the same way move the body this much faster than one.</summary>
        internal const float TogetherSpeed = 1.5f;
        /// <summary>A co-holder's join not confirmed by the dragger's samples within this is given up.</summary>
        private const float JoinTimeout = 2f;
        /// <summary>The co-holder's input older than this counts as standing still.</summary>
        private const float InputStale = 0.5f;
        /// <summary>No input from the co-holder for this long: it is gone.</summary>
        private const float InputLost = 3f;
        /// <summary>Same body: the sender's copy within this of the local one (XZ, game units; a body is about 40 across).</summary>
        private const float SameBodyRange = 60f;
        /// <summary>The co-holder's body this far from the spot it holds is moved there at once.</summary>
        private const float FollowSnap = 80f;
        /// <summary>The co-holder's stand-in this far from the body: the hold broke.</summary>
        private const float HoldBreakRange = 160f;
        private const float StandInMaxSpeed = 600f;

        private sealed class Follow
        {
            public Item Item;
            public string Name;
            public int LeaderId;
            public bool JoinSent;
            public bool Confirmed;
            /// <summary>The dragger's clock on the first sample with this player on it (older samples arrive late, unreliable).</summary>
            public float ConfirmSendTime;
            public float JoinedAt;
            public bool HasTarget;
            public Vector3 Target;
            public readonly List<KeyValuePair<Collider, Collider>> Ignored = new List<KeyValuePair<Collider, Collider>>();
        }

        private sealed class Lead
        {
            public Item Item;
            public Rigidbody ItemRb;
            public string Name;
            public int FollowerId;
            public RemotePlayerProxy Proxy;
            public Rigidbody ProxyRb;
            public HingeJoint Joint;
            public float ProxyMass, ProxyDrag;
            public Vector3 Intent;
            public float LastInputAt;
            public readonly List<KeyValuePair<Collider, Collider>> Ignored = new List<KeyValuePair<Collider, Collider>>();
        }

        private static Follow _follow;
        private static Lead _lead;
        /// <summary>Observers: the co-holder of each body a remote player drags (from its samples).</summary>
        private static readonly Dictionary<string, int> _coHolders =
            new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        private static bool Enabled => ModConfig.SharedDrag == null || ModConfig.SharedDrag.Value;

        // ------------------------------------------------------------------ queries

        internal static bool IsFollowingItem(Item item)
            => item != null && _follow != null && _follow.Item == item;

        /// <summary>The local player co-holds <paramref name="name"/>, dragged by <paramref name="sender"/>.</summary>
        internal static bool IsFollowing(string name, int sender)
            => _follow != null && sender > 0 && _follow.LeaderId == sender
               && string.Equals(_follow.Name, name, System.StringComparison.Ordinal);

        /// <summary>
        /// Scale on the local player's walk force: the shared drag's factor for the dragger,
        /// nothing for a co-holder (its force is applied on the dragger's machine), else 1.
        /// </summary>
        internal static float LocalForceScale()
        {
            if (_follow != null)
                return 0f;
            if (_lead != null)
                return PairScale(_lead);
            return 1f;
        }

        /// <summary>
        /// One scale for both players' walk forces. The pair moves at the summed force over the
        /// summed damping (mass times drag of the body and both players): s × 2F / (B + 2P) for
        /// two pushing together against F / (B + P) alone, so s = 1.5 (B + 2P) / (2 (B + P)).
        /// </summary>
        private static float PairScale(Lead l)
        {
            Player p = Player.Instance;
            if (l.ItemRb == null || p == null || p.Rigidbody == null)
                return 1f;
            float b = l.ItemRb.mass * l.ItemRb.drag;
            float pl = p.Rigidbody.mass * p.Rigidbody.drag;
            if (b + pl <= 0.0001f)
                return 1f;
            float s = TogetherSpeed * (b + 2f * pl) / (2f * (b + pl));
            return Mathf.Clamp(s, TogetherSpeed * 0.5f, TogetherSpeed);
        }

        // ------------------------------------------------------------------ observers

        /// <summary>A remote drag sample: remember who co-holds that body (a third player is refused).</summary>
        internal static void NoteCoHolder(string name, int dragger, int coHolder)
        {
            if (string.IsNullOrEmpty(name))
                return;
            if (coHolder > 0)
                _coHolders[name] = coHolder;
            else
                _coHolders.Remove(name);
        }

        internal static void ClearCoHolder(string name, int dragger)
        {
            if (!string.IsNullOrEmpty(name))
                _coHolders.Remove(name);
        }

        // ------------------------------------------------------------------ co-holder side

        /// <summary>
        /// The local player tries to drag a body another player drags. True: take hold of it with
        /// them (vanilla's grab goes on). False: refused, with the text to show (null: default).
        /// </summary>
        internal static bool TryBeginFollow(Item item, LanNetworkManager net, out string refusal)
        {
            refusal = null;
            if (!Enabled || item == null || net == null || !net.IsConnected)
                return false;
            Player p = Player.Instance;
            if (p == null || p.dragging || p.startingDragging || _follow != null || _lead != null)
                return false;
            if (item.GetComponent<Rigidbody>() == null)
                return false;
            string name = item.gameObject.name;
            int leader = RemoteDragTimeline.SenderOf(item);
            if (leader <= 0 && net.PlayerInteractHandlers.DragClaims.TryGetValue(name, out int claim))
                leader = claim;
            if (leader <= 0 || leader == net.LocalPlayerId)
                return false;
            if (_coHolders.TryGetValue(name, out int other) && other > 0 && other != net.LocalPlayerId)
            {
                refusal = "Two players are already moving this";
                return false;
            }
            _follow = new Follow
            {
                Item = item,
                Name = name,
                LeaderId = leader,
                JoinedAt = Time.unscaledTime
            };
            ModLog.Event(LogCat.Physics, $"[SharedDrag] taking hold of {name} with p{leader}");
            return true;
        }

        /// <summary>After vanilla's <c>Item.startDragging</c>: a grab it turned down (too far) drops the hold.</summary>
        internal static void OnStartDraggingDone(Item item)
        {
            if (_follow != null && _follow.Item == item && !item.beingDragged)
            {
                ModLog.Event(LogCat.Physics, $"[SharedDrag] grab of {_follow.Name} refused by the game (too far)");
                _follow = null;
            }
        }

        /// <summary>
        /// PlayerState tick while the local player drags: a co-holder sends its join (once its
        /// grab animation is done), then its walk input. True: handled, no DragSync from here.
        /// </summary>
        internal static bool TickFollowerSend(LanNetworkManager net, Player local)
        {
            Follow f = _follow;
            if (f == null || local.itemBeingDragged != f.Item)
                return false;
            if (f.Item == null)
                return true;
            Transform it = f.Item.transform;
            Vector3 itemPos = it.position;
            var msg = new CoHoldMessage
            {
                SenderId = net.LocalPlayerId,
                TargetId = f.LeaderId,
                ObjectName = f.Name,
                PosX = itemPos.x,
                PosY = itemPos.y,
                PosZ = itemPos.z
            };
            if (!f.JoinSent)
            {
                Vector3 local3 = it.InverseTransformPoint(local.transform.position);
                msg.Mode = CoHoldMessage.ModeJoin;
                msg.LocalX = local3.x;
                msg.LocalY = local3.y;
                msg.LocalZ = local3.z;
                net.BroadcastHot(NetMessageType.CoHold, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                f.JoinSent = true;
                f.JoinedAt = Time.unscaledTime;
                IgnoreFollowerContacts(f, net);
                ModLog.Event(LogCat.Physics, $"[SharedDrag] join sent for {f.Name} to p{f.LeaderId} at local {local3}");
                return true;
            }
            if (!f.Confirmed && Time.unscaledTime - f.JoinedAt > JoinTimeout)
            {
                ModLog.Event(LogCat.Physics, $"[SharedDrag] p{f.LeaderId} never confirmed {f.Name} — letting go");
                StopFollowDrag("Could not take hold of it");
                return true;
            }
            Vector3 intent = PlayerMoveForcePatch.Intent;
            msg.Mode = CoHoldMessage.ModeInput;
            msg.ForceX = intent.x;
            msg.ForceZ = intent.z;
            net.BroadcastHot(NetMessageType.CoHold, w => msg.Serialize(w), DeliveryMethod.Unreliable);
            return true;
        }

        /// <summary>The dragger's sample for the body the local player co-holds.</summary>
        internal static void OnLeaderSample(DragSyncMessage msg)
        {
            Follow f = _follow;
            var net = ModRuntime.Network;
            if (f == null || net == null)
                return;
            if (msg.CoHolderId == net.LocalPlayerId)
            {
                if (!f.Confirmed)
                {
                    f.Confirmed = true;
                    f.ConfirmSendTime = msg.SendTime;
                    ModLog.Event(LogCat.Physics, $"[SharedDrag] p{f.LeaderId} confirmed the hold on {f.Name}");
                }
            }
            else if (f.Confirmed && msg.SendTime > f.ConfirmSendTime)
            {
                // The dragger's machine no longer has this player on it.
                ModLog.Event(LogCat.Physics, $"[SharedDrag] p{f.LeaderId} dropped the hold on {f.Name}");
                StopFollowDrag(null);
            }
        }

        /// <summary>Playback (LateUpdate): where the dragger's machine has the local player now.</summary>
        internal static void SetFollowTarget(Item item, Vector3 pos)
        {
            Follow f = _follow;
            if (f == null || f.Item != item || !f.Confirmed)
                return;
            f.Target = pos;
            f.HasTarget = true;
        }

        /// <summary>
        /// The dragger let go or left while the local player co-holds: it keeps hold and drags on
        /// alone. The body jumps to the pose the drag ended on (the STOP's), the player with it,
        /// then turns physical and is hinged to the player as vanilla does.
        /// </summary>
        internal static void PromoteFollower(DragSyncMessage msg)
        {
            Follow f = _follow;
            if (f == null)
                return;
            _follow = null;
            Item item = f.Item;
            RestoreContacts(f.Ignored);
            if (item == null)
                return;
            Player p = Player.Instance;
            if (msg.HasPose && p != null && p.Rigidbody != null)
            {
                Vector3 endPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Vector3 delta = endPos - item.transform.position;
                delta.y = 0f;
                if (delta.sqrMagnitude < FollowSnap * FollowSnap)
                {
                    item.transform.SetPositionAndRotation(endPos, Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ));
                    p.Rigidbody.position += delta;
                    p.transform.position += delta;
                }
            }
            RemoteDragTimeline.Drop(item);
            Audio.ItemMovingSoundHelper.ClearRemoteScrape(f.Name);
            Audio.MovingObjectSoundService.StopImmediate(f.Name);
            // Still in the grab animation: vanilla hinges it when that ends.
            if (p != null && p.dragging && p.itemBeingDragged == item && item.GetComponent<HingeJoint>() == null)
                item.addDraggingHingeJoint();
            ModLog.Event(LogCat.Physics, $"[SharedDrag] p{f.LeaderId} let go of {f.Name} — dragging it on alone");
        }

        /// <summary>The dragger's samples stopped (it is gone): drag on alone.</summary>
        internal static void OnLeaderLost(Item item, int leader)
        {
            if (_follow != null && _follow.Item == item && _follow.LeaderId == leader)
                PromoteFollower(default);
        }

        /// <summary>
        /// Vanilla released the local player's drag of <paramref name="item"/>. True for a
        /// co-holder (it tells the dragger; the usual drag end does not run). A dragger with a
        /// co-holder drops the co-holder's hinge; the co-holder drags on when the STOP reaches it.
        /// </summary>
        internal static bool OnLocalStop(Item item)
        {
            if (_lead != null && _lead.Item == item)
                EndLead("dragger let go", tellFollower: false);
            Follow f = _follow;
            if (f == null || f.Item != item)
                return false;
            _follow = null;
            RestoreContacts(f.Ignored);
            if (f.JoinSent && NetGuard.Connected(out LanNetworkManager net))
            {
                Vector3 pos = item.transform.position;
                var msg = new CoHoldMessage
                {
                    Mode = CoHoldMessage.ModeLeave,
                    SenderId = net.LocalPlayerId,
                    TargetId = f.LeaderId,
                    ObjectName = f.Name,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z
                };
                net.BroadcastHot(NetMessageType.CoHold, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            // Vanilla's let-go applied the body's rigidbody preset (free again); it still plays
            // back the dragger's drag here.
            RemoteDragTimeline.Rehold(item);
            ModLog.Event(LogCat.Physics, $"[SharedDrag] let go of {f.Name} (p{f.LeaderId} drags on)");
            return true;
        }

        private static void StopFollowDrag(string message)
        {
            Follow f = _follow;
            if (f == null)
                return;
            Item item = f.Item;
            Player p = Player.Instance;
            if (item != null && item.beingDragged)
            {
                // Through vanilla's release: OnLocalStop tells the dragger and clears the hold.
                item.stopDragging(force: true);
            }
            else
            {
                _follow = null;
                RestoreContacts(f.Ignored);
                if (p != null && (p.dragging || p.startingDragging) && p.itemBeingDragged == item)
                    p.stopDragging(immediately: true);
                if (item != null)
                    RemoteDragTimeline.Rehold(item);
            }
            _follow = null;
            if (!string.IsNullOrEmpty(message))
            {
                PersonalFlavorHud.BeginBypass();
                try { p?.displayMessage(Loc.T(message)); }
                finally { PersonalFlavorHud.EndBypass(); }
            }
        }

        /// <summary>
        /// The co-holder's player passes through the body and the dragger's stand-in: both come
        /// from the dragger's machine on a delay, and contact with them would shove the player off
        /// the spot it holds.
        /// </summary>
        private static void IgnoreFollowerContacts(Follow f, LanNetworkManager net)
        {
            Player p = Player.Instance;
            if (p == null || f.Item == null)
                return;
            Collider[] mine = p.GetComponentsInChildren<Collider>();
            Collider[] body = f.Item.GetComponentsInChildren<Collider>();
            Ignore(f.Ignored, mine, body);
            // No contact event follows an ignore: drop the touch the grab left, or it would count
            // as the local player pushing the body (no scrape from the dragger's machine).
            if (p.touchingColliders != null)
                for (int i = 0; i < body.Length; i++)
                    p.touchingColliders.Remove(body[i]);
            RemotePlayerProxy leader = net.GetProxy(f.LeaderId);
            if (leader != null)
                Ignore(f.Ignored, mine, leader.CachedColliders);
        }

        // ------------------------------------------------------------------ dragger side

        /// <summary>The dragger's sample: who co-holds the body and where the dragger's machine has it.</summary>
        internal static void FillLeaderFields(ref DragSyncMessage msg, Item dragged)
        {
            Lead l = _lead;
            if (l == null || l.Item != dragged || l.ProxyRb == null)
                return;
            Vector3 co = l.ProxyRb.position;
            msg.CoHolderId = l.FollowerId;
            msg.CoPosX = co.x;
            msg.CoPosY = co.y;
            msg.CoPosZ = co.z;
        }

        private static void HandleJoin(CoHoldMessage m, LanNetworkManager net)
        {
            Player p = Player.Instance;
            Item item = p != null && p.dragging ? p.itemBeingDragged : null;
            string why = null;
            if (!Enabled)
                why = "shared drag off here";
            else if (item == null || !string.Equals(item.gameObject.name, m.ObjectName, System.StringComparison.Ordinal))
                why = "not dragging it";
            else if (FlatDistance(item.transform.position, new Vector3(m.PosX, m.PosY, m.PosZ)) > SameBodyRange)
                why = "another body of that name";
            else if (_lead != null && _lead.FollowerId != m.SenderId)
                why = "already held by p" + _lead.FollowerId;
            else if (_follow != null)
                why = "co-holding something itself";
            RemotePlayerProxy proxy = why == null ? net.GetProxy(m.SenderId) : null;
            Rigidbody proxyRb = proxy != null ? proxy.GetComponent<Rigidbody>() : null;
            Rigidbody itemRb = item != null ? item.GetComponent<Rigidbody>() : null;
            if (why == null && (proxyRb == null || proxy.FreezePosition))
                why = "no stand-in for p" + m.SenderId;
            if (why == null && itemRb == null)
                why = "no rigidbody";
            if (why != null)
            {
                ModLog.Event(LogCat.Physics, $"[SharedDrag] refused p{m.SenderId} on {m.ObjectName}: {why}");
                SendRefuse(net, m.SenderId, m.ObjectName);
                return;
            }
            if (_lead != null)
                EndLead("rejoin", tellFollower: false);

            // The stand-in where the co-holder stands relative to the body on its own machine.
            Transform it = item.transform;
            Vector3 spot = it.TransformPoint(new Vector3(m.LocalX, m.LocalY, m.LocalZ));
            spot.y = proxyRb.position.y;
            proxy.CoHeld = true;
            proxyRb.position = spot;
            proxy.transform.position = spot;
            proxyRb.velocity = Vector3.zero;

            var l = new Lead
            {
                Item = item,
                ItemRb = itemRb,
                Name = m.ObjectName,
                FollowerId = m.SenderId,
                Proxy = proxy,
                ProxyRb = proxyRb,
                ProxyMass = proxyRb.mass,
                ProxyDrag = proxyRb.drag,
                LastInputAt = Time.unscaledTime
            };
            // A player's body: the same mass and drag as the local player's.
            if (p.Rigidbody != null)
            {
                proxyRb.mass = p.Rigidbody.mass;
                proxyRb.drag = p.Rigidbody.drag;
            }
            // Vanilla's hinge (Item.addDraggingHingeJoint), seen from the stand-in: pinned at the
            // body's centre, turning about the body's own axis, 15 degrees of play.
            Transform pt = proxy.transform;
            HingeJoint joint = proxy.gameObject.AddComponent<HingeJoint>();
            joint.autoConfigureConnectedAnchor = true;
            joint.anchor = pt.InverseTransformPoint(it.position);
            joint.axis = pt.InverseTransformDirection(it.forward);
            joint.connectedBody = itemRb;
            joint.useLimits = true;
            JointLimits limits = joint.limits;
            limits.min = -15f;
            limits.max = 0f;
            joint.limits = limits;
            joint.enableCollision = true;
            joint.enablePreprocessing = false;
            l.Joint = joint;
            // The two players do not shove each other while both hold it.
            Ignore(l.Ignored, p.GetComponentsInChildren<Collider>(), proxy.CachedColliders);
            _lead = l;
            ModLog.Event(LogCat.Physics,
                $"[SharedDrag] p{m.SenderId} took hold of {m.ObjectName} with us at {spot} (scale {PairScale(l):F2})");
        }

        private static void EndLead(string why, bool tellFollower)
        {
            Lead l = _lead;
            if (l == null)
                return;
            _lead = null;
            // At once: the stand-in follows its network position again from the next step.
            if (l.Joint != null)
                Object.DestroyImmediate(l.Joint);
            if (l.ProxyRb != null)
            {
                l.ProxyRb.mass = l.ProxyMass;
                l.ProxyRb.drag = l.ProxyDrag;
            }
            if (l.Proxy != null)
                l.Proxy.CoHeld = false;
            RestoreContacts(l.Ignored);
            if (tellFollower && NetGuard.Connected(out LanNetworkManager net))
                SendRefuse(net, l.FollowerId, l.Name);
            ModLog.Event(LogCat.Physics, $"[SharedDrag] p{l.FollowerId} off {l.Name}: {why}");
        }

        private static void SendRefuse(LanNetworkManager net, int to, string name)
        {
            var msg = new CoHoldMessage
            {
                Mode = CoHoldMessage.ModeRefuse,
                SenderId = net.LocalPlayerId,
                TargetId = to,
                ObjectName = name ?? ""
            };
            net.BroadcastHot(NetMessageType.CoHold, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        // ------------------------------------------------------------------ both

        /// <summary>Inbound <see cref="CoHoldMessage"/> (host-stamped sender).</summary>
        internal static void Handle(CoHoldMessage m)
        {
            var net = ModRuntime.Network;
            if (net == null || m.SenderId == net.LocalPlayerId)
                return;
            if (m.Mode == CoHoldMessage.ModePush)
            {
                CoPush.HandlePush(m);
                return;
            }
            if (m.TargetId != net.LocalPlayerId)
                return;
            switch (m.Mode)
            {
                case CoHoldMessage.ModeJoin:
                    HandleJoin(m, net);
                    break;
                case CoHoldMessage.ModeInput:
                    if (_lead != null && _lead.FollowerId == m.SenderId)
                    {
                        _lead.Intent = new Vector3(m.ForceX, 0f, m.ForceZ);
                        _lead.LastInputAt = Time.unscaledTime;
                    }
                    break;
                case CoHoldMessage.ModeLeave:
                    if (_lead != null && _lead.FollowerId == m.SenderId)
                        EndLead("co-holder let go", tellFollower: false);
                    break;
                case CoHoldMessage.ModeRefuse:
                    if (_follow != null && _follow.LeaderId == m.SenderId)
                    {
                        ModLog.Event(LogCat.Physics, $"[SharedDrag] p{m.SenderId} refused or ended the hold on {m.ObjectName}");
                        StopFollowDrag(_follow.Confirmed ? null : "Could not take hold of it");
                    }
                    break;
            }
        }

        /// <summary>Physics step (LanNetworkManager.FixedUpdate).</summary>
        internal static void FixedTick()
        {
            if (_lead != null)
                TickLead(_lead);
            if (_follow != null)
                TickFollow(_follow);
        }

        private static void TickLead(Lead l)
        {
            Player p = Player.Instance;
            if (l.Item == null || p == null || !p.dragging || p.itemBeingDragged != l.Item)
            {
                EndLead("drag ended", tellFollower: true);
                return;
            }
            if (l.Proxy == null || l.ProxyRb == null || l.Joint == null)
            {
                EndLead("stand-in gone", tellFollower: false);
                return;
            }
            float since = Time.unscaledTime - l.LastInputAt;
            if (since > InputLost)
            {
                EndLead("co-holder silent", tellFollower: true);
                return;
            }
            if (FlatDistance(l.ProxyRb.position, l.Item.transform.position) > HoldBreakRange)
            {
                EndLead("hold broke", tellFollower: true);
                return;
            }
            if (since <= InputStale && l.Intent.sqrMagnitude > 0.0001f)
            {
                // Vanilla's walk force for that player, applied on this step, times the pair's scale.
                l.ProxyRb.AddForce(l.Intent * (PlayerMoveForcePatch.VanillaForceFactor * Time.fixedDeltaTime * PairScale(l)));
            }
        }

        private static void TickFollow(Follow f)
        {
            Player p = Player.Instance;
            if (f.Item == null)
            {
                ModLog.Event(LogCat.Physics, $"[SharedDrag] {f.Name} is gone — letting go");
                StopFollowDrag(null);
                return;
            }
            if (p != null && p.itemBeingDragged != f.Item)
            {
                // The player's drag ended past Item.stopDragging (vanilla's Player.stopDragging
                // alone, as a hit during the grab animation does): the hold ends with it, or the
                // player would stay without a walk force. The body is not left marked as held.
                Item held = f.Item;
                OnLocalStop(held);
                held.beingDragged = false;
                return;
            }
            if (p == null || p.Rigidbody == null || !f.Confirmed || !f.HasTarget)
                return;
            Rigidbody rb = p.Rigidbody;
            Vector3 d = f.Target - rb.position;
            d.y = 0f;
            if (d.sqrMagnitude > FollowSnap * FollowSnap)
            {
                Vector3 snap = new Vector3(f.Target.x, rb.position.y, f.Target.z);
                rb.position = snap;
                rb.velocity = Vector3.zero;
                return;
            }
            Vector3 v = d / Time.fixedDeltaTime;
            if (v.sqrMagnitude > StandInMaxSpeed * StandInMaxSpeed)
                v = v.normalized * StandInMaxSpeed;
            rb.velocity = v;
        }

        /// <summary>A player left: a shared drag with it ends (its partner drags on alone, or is let go of).</summary>
        internal static void OnPeerLeft(int playerId)
        {
            if (_lead != null && _lead.FollowerId == playerId)
                EndLead("co-holder left", tellFollower: false);
            if (_follow != null && _follow.LeaderId == playerId)
                PromoteFollower(default);
        }

        internal static void Reset()
        {
            if (_lead != null)
                EndLead("session reset", tellFollower: false);
            // A co-holder lets go: without the dragger's machine nothing moves its hold.
            if (_follow != null)
                StopFollowDrag(null);
            _coHolders.Clear();
            CoPush.Reset();
        }

        // ------------------------------------------------------------------ helpers

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static void Ignore(List<KeyValuePair<Collider, Collider>> into, Collider[] a, Collider[] b)
        {
            if (a == null || b == null)
                return;
            for (int i = 0; i < a.Length; i++)
            {
                Collider x = a[i];
                if (x == null || x.isTrigger)
                    continue;
                for (int j = 0; j < b.Length; j++)
                {
                    Collider y = b[j];
                    if (y == null || y.isTrigger || y == x)
                        continue;
                    Physics.IgnoreCollision(x, y, true);
                    into.Add(new KeyValuePair<Collider, Collider>(x, y));
                }
            }
        }

        private static void RestoreContacts(List<KeyValuePair<Collider, Collider>> pairs)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                Collider a = pairs[i].Key, b = pairs[i].Value;
                if (a != null && b != null)
                    Physics.IgnoreCollision(a, b, false);
            }
            pairs.Clear();
        }
    }
}
