using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// An NPC as a peer names it on the wire. NPC.name is not unique (every hideout's oven is
    /// "oven", every location's doctor "doctor"), so it travels with where the NPC stands and its
    /// world (dream-pad twin or overworld). No position: an older peer; the name alone decides.
    /// </summary>
    internal struct NpcRef
    {
        public string Name;
        public bool HasPos;
        public Vector3 Pos;
        public bool Dream;

        public static NpcRef Of(NPC npc)
        {
            if (npc == null) return default;
            return new NpcRef
            {
                Name = npc.name,
                HasPos = true,
                Pos = npc.transform.position,
                Dream = NpcDialogueLock.IsDreamWorldNpc(npc)
            };
        }

        public static NpcRef From(DialogNpcLockMessage m) => new NpcRef
        {
            Name = m.NpcName,
            HasPos = m.HasPos,
            Pos = new Vector3(m.PosX, m.PosY, m.PosZ),
            Dream = m.Dream
        };

        public static NpcRef From(DialogOutcomeSyncMessage m) => new NpcRef
        {
            Name = m.NpcName,
            HasPos = m.HasPos,
            Pos = new Vector3(m.PosX, m.PosY, m.PosZ),
            Dream = m.Dream
        };

        public bool IsValid => !string.IsNullOrEmpty(Name);

        /// <summary>Same talker (name, and spot when both are known).</summary>
        public bool Matches(NpcRef other)
            => NpcDialogueLockPolicy.IsSameNpc(Name, HasPos, Pos.x, Pos.z,
                other.Name, other.HasPos, other.Pos.x, other.Pos.z);

        public bool Matches(NPC npc) => npc != null && Matches(Of(npc));

        public override string ToString()
            => HasPos ? Name + "@(" + Pos.x.ToString("F0") + "," + Pos.z.ToString("F0") + ")" : (Name ?? "");
    }

    /// <summary>
    /// Host-authoritative one-speaker-per-NPC lock.
    /// Multiple NPCs may be spoken to in parallel; the same NPC is serialized. "Same NPC" is name,
    /// world and spot (<see cref="NpcRef"/>): two players at two hideouts' ovens talk at once.
    /// </summary>
    public static class NpcDialogueLock
    {
        private struct Hold
        {
            public NpcRef Npc;
            public int OwnerId;
            public float ExpireAt;
        }

        /// <summary>Seconds between lease renewals while a dialogue window stays open.</summary>
        private const float LeaseRenewIntervalSeconds = 30f;

        /// <summary>An NPC this close to the dream pad (or under it) is the pad twin.</summary>
        private const float DreamPadRadius = 250f;

        // One entry per held NPC. The bunker's door_underground (and every other dream NPC) exists
        // on the dream pad and in the overworld; one peer talking to the pad twin must not lock the
        // overworld one, and release must find the lock it took. The world travels on the wire
        // (DialogNpcLockMessage.Dream): each peer's own dream flags disagree while only some peers
        // are dreaming.
        private static readonly List<Hold> _locks = new List<Hold>();
        private static readonly HashSet<NPC> _renewing = new HashSet<NPC>();

        /// <summary>The hold on this NPC in its world (expired or not); -1 when none.</summary>
        private static int IndexOf(NpcRef npc)
        {
            for (int i = 0; i < _locks.Count; i++)
            {
                Hold h = _locks[i];
                if (h.Npc.Dream == npc.Dream && h.Npc.Matches(npc))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// The hold <paramref name="ownerId"/> took on an NPC of this name, in either world and at
        /// any spot (expired or not). A peer talks to one NPC at a time, so the name finds it even
        /// when a dream started or ended mid-talk or the NPC walked.
        /// </summary>
        private static int IndexOfOwned(string npcName, int ownerId, bool preferDream)
        {
            int other = -1;
            for (int i = 0; i < _locks.Count; i++)
            {
                Hold h = _locks[i];
                if (h.OwnerId != ownerId
                    || !string.Equals(h.Npc.Name, npcName, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (h.Npc.Dream == preferDream)
                    return i;
                other = i;
            }
            return other;
        }

        /// <summary>
        /// The world of a talk this peer is opening: the NPC itself is the dream-pad twin, or
        /// (no pad) this peer is itself dreaming. Sent as the sender's view on the wire.
        /// </summary>
        internal static bool IsDreamWorldNpc(NPC npc)
        {
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            if (pad == null || npc == null)
                return DreamSyncManager.IsLocalDreamActive;
            Transform t = npc.transform;
            return t.IsChildOf(pad) || Vector3.Distance(t.position, pad.position) <= DreamPadRadius;
        }

        /// <summary>Count of active (non-expired) locks for tests and diagnostics.</summary>
        public static int ActiveCount
        {
            get
            {
                float now = Time.unscaledTime;
                int n = 0;
                for (int i = 0; i < _locks.Count; i++)
                {
                    if (now < _locks[i].ExpireAt)
                        n++;
                }
                return n;
            }
        }

        public static void Reset()
        {
            _locks.Clear();
            _renewing.Clear();
        }

        internal static bool TryAcquire(NpcRef npc, int ownerPlayerId, float leaseSeconds = -1f)
        {
            if (!npc.IsValid || ownerPlayerId < 0)
                return false;

            float now = Time.unscaledTime;
            float lease = leaseSeconds > 0f ? leaseSeconds : NpcDialogueLockPolicy.DefaultLeaseSeconds;
            int idx = IndexOf(npc);

            int heldOwner = -1;
            float heldExpire = 0f;
            if (idx >= 0)
            {
                heldOwner = _locks[idx].OwnerId;
                heldExpire = _locks[idx].ExpireAt;
            }

            if (!NpcDialogueLockPolicy.CanAcquireNpcSlot(heldOwner, heldExpire, ownerPlayerId, now))
                return false;

            var hold = new Hold { Npc = npc, OwnerId = ownerPlayerId, ExpireAt = now + lease };
            if (idx >= 0)
                _locks[idx] = hold;
            else
                _locks.Add(hold);
            return true;
        }

        /// <summary>Drop <paramref name="ownerPlayerId"/>'s live hold on an NPC of this name (prefer the given world).</summary>
        /// <returns>The released hold's NPC (spot and world); <paramref name="npc"/> when none was held.</returns>
        internal static NpcRef Release(NpcRef npc, int ownerPlayerId)
        {
            if (!npc.IsValid) return npc;
            int idx = IndexOfOwned(npc.Name, ownerPlayerId, npc.Dream);
            if (idx < 0)
                return npc;
            Hold hold = _locks[idx];
            if (!NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, Time.unscaledTime))
                return npc;
            _locks.RemoveAt(idx);
            return hold.Npc;
        }

        internal static bool IsLockedByOther(NpcRef npc, int localPlayerId)
        {
            int owner = GetOwner(npc);
            return owner >= 0 && owner != localPlayerId;
        }

        /// <summary>Holder of this NPC (its spot, in its world); -1 when free / expired.</summary>
        internal static int GetOwner(NpcRef npc)
        {
            if (!npc.IsValid) return -1;
            int idx = IndexOf(npc);
            if (idx < 0) return -1;
            Hold hold = _locks[idx];
            if (Time.unscaledTime >= hold.ExpireAt) return -1;
            return hold.OwnerId;
        }

        /// <summary>Extend this owner's own hold (either world), or take this NPC's slot.</summary>
        internal static bool RenewLease(NPC npc, int ownerPlayerId)
        {
            if (npc == null || ownerPlayerId < 0) return false;
            NpcRef r = NpcRef.Of(npc);
            if (TryExtendOwned(r, ownerPlayerId, out _))
                return true;
            return TryAcquire(r, ownerPlayerId);
        }

        /// <summary>
        /// Host: extend lease when sender was the recorded holder (either world, including
        /// expired). Does not grant a new holder. Used at trade accept so long sessions stay valid.
        /// </summary>
        internal static void HostRenewLeaseForSender(NpcRef npc, int ownerPlayerId)
        {
            TryExtendOwned(npc, ownerPlayerId, out _);
        }

        private static bool TryExtendOwned(NpcRef npc, int ownerPlayerId, out NpcRef held)
        {
            held = npc;
            if (!npc.IsValid || ownerPlayerId < 0) return false;
            int idx = IndexOfOwned(npc.Name, ownerPlayerId, npc.Dream);
            if (idx < 0)
                return false;
            Hold hold = _locks[idx];
            hold.ExpireAt = Time.unscaledTime + NpcDialogueLockPolicy.DefaultLeaseSeconds;
            _locks[idx] = hold;
            held = hold.Npc;
            return true;
        }

        /// <summary>
        /// Host: the holder is still talking. Extend the lease and refresh every client's mirror
        /// (their copy expires on the same 90s clock). No onEnterDialogue replay.
        /// </summary>
        /// <returns>False when this owner holds no lock on the NPC (nothing renewed).</returns>
        internal static bool HostRenewHeld(LanNetworkManager net, NpcRef npc, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            if (!TryExtendOwned(npc, ownerPlayerId, out NpcRef held))
                return false;
            BroadcastState(net, held, ownerPlayerId, granted: true, release: false);
            return true;
        }

        /// <summary>
        /// The 90s lease was taken once at acquire and never renewed, so a conversation or trade
        /// longer than that let a second speaker in. While this peer's window stays open on the
        /// NPC, renew every 30s: the host extends locally and fans the grant out; a client sends
        /// a renewal request, which the host only ever extends (never a fresh grant).
        /// </summary>
        internal static void BeginLeaseRenewal(NPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            if (!_renewing.Add(npc)) return;
            ctrl.StartCoroutine(RenewWhileOpen(npc, NpcRef.Of(npc)));
        }

        private static System.Collections.IEnumerator RenewWhileOpen(NPC npc, NpcRef opened)
        {
            try
            {
                while (true)
                {
                    yield return new WaitForSecondsRealtime(LeaseRenewIntervalSeconds);

                    // Role, not IsConnected: a host talking alone (peers come and go) still owns the
                    // lock, and a joiner must not get a second speaker when the lease lapses.
                    var net = ModRuntime.Network;
                    if (net == null || net.Role == NetworkRole.Offline)
                        yield break;
                    var dw = Singleton<UI>.Instance?.dialogueWindow;
                    if (dw == null || npc == null || dw.npc != npc || !dw.opened)
                        yield break;

                    int localId = net.LocalPlayerId;
                    if (net.Role == NetworkRole.Host)
                    {
                        if (!HostRenewHeld(net, opened, localId))
                            HostTryGrant(net, opened, localId, fireEnterDialogue: false);
                    }
                    else
                    {
                        // The world and spot the talk was opened at, not whatever this peer sees now.
                        if (TryExtendOwned(opened, localId, out NpcRef held))
                            opened = held;
                        else
                            TryAcquire(opened, localId);
                        NpcRef send = opened;
                        net.Send(NetMessageType.DialogNpcLock,
                            w => BuildMessage(send, localId, granted: false, release: false,
                                isRequest: true, renewal: true).Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                    }
                }
            }
            finally
            {
                _renewing.Remove(npc);
            }
        }

        /// <summary>Host: attempt the lock on this NPC (its spot and world) and notify requestor (and peers).</summary>
        /// <param name="fireEnterDialogue">False for a renewal that lost its hold: re-take the slot
        /// without replaying the client's onEnterDialogue triggers.</param>
        internal static bool HostTryGrant(LanNetworkManager net, NpcRef npc, int ownerPlayerId,
            bool fireEnterDialogue = true)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            bool ok = TryAcquire(npc, ownerPlayerId);
            BroadcastState(net, npc, ownerPlayerId, granted: ok, release: false);
            if (ok)
            {
                ModLog.Event(LogCat.Session,
                    $"[DialogLock] granted NPC={npc} owner={ownerPlayerId} dream={npc.Dream}");
                // Host talkTo already fired onEnterDialogue. A client talk only runs it
                // locally, and client one-shot GameEvents are blocked.
                if (fireEnterDialogue && ownerPlayerId != net.LocalPlayerId)
                    FireRemoteEnterDialogue(npc, ownerPlayerId);
            }
            else
                ModLog.Event(LogCat.Session,
                    $"[DialogLock] denied NPC={npc} owner={ownerPlayerId} dream={npc.Dream} heldBy={GetOwner(npc)}");
            return ok;
        }

        /// <summary>
        /// Client opened talk. Replay vanilla NPC.talkTo's onEnterDialogue on the host
        /// so one-shot GameEvents fan out. Wrapped in the host apply guard so the
        /// inbound lock packet does not swallow the broadcast.
        /// </summary>
        private static void FireRemoteEnterDialogue(NpcRef npcRef, int ownerPlayerId)
        {
            // Strict: for a dream talk only the pad twin qualifies. The overworld twin shares
            // the name, and this method SetActive(true)s and fires triggers on whatever it gets.
            NPC npc = DialogOutcomeCloseNetHandlers.ResolveNpc(npcRef, strictPad: true);
            if (npc == null || npc.gameObject == null)
            {
                ModLog.WarnRate(LogCat.Session, "dlg-enter-npc-miss:" + npcRef.Name,
                    "[DialogLock] onEnterDialogue skip — NPC '" + npcRef + "' not found"
                    + (npcRef.Dream ? " on the dream pad" : ""));
                return;
            }
            if (npcRef.Dream)
            {
                Transform pad = DreamSyncManager.GetDreamLocationTransform();
                if (pad == null
                    || (!npc.transform.IsChildOf(pad)
                        && Vector3.Distance(npc.transform.position, pad.position) > DreamPadRadius))
                {
                    ModRuntime.LegacyInfo(
                        $"[DialogLock] skip onEnterDialogue — NPC '{npcRef}' not on the dream pad");
                    return;
                }
            }

            ModRuntime.LegacyInfo($"[DialogLock] host onEnterDialogue for {npcRef}");
            // Stamp dialogue owner before BeginWorldOnly so GameEventsFired carries the speaker.
            bool pushed = ownerPlayerId > 0;
            if (pushed) GeFireActorContext.Push(ownerPlayerId);
            DialogHostApplyGuard.BeginWorldOnly();
            try
            {
                if (!npc.gameObject.activeInHierarchy)
                {
                    try { npc.gameObject.SetActive(true); }
                    catch { /* ignore */ }
                }

                Core.sendTriggerInfo(npc.gameObject, EventTrigger.Type.onEnterDialogue);
                EventTriggers[] ets = npc.GetComponentsInChildren<EventTriggers>(true);
                for (int i = 0; i < ets.Length; i++)
                {
                    EventTriggers et = ets[i];
                    if (et == null || et.gameObject == npc.gameObject) continue;
                    try { et.fireEventTrigger(EventTrigger.Type.onEnterDialogue); }
                    catch { /* ignore */ }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DialogLock] onEnterDialogue: " + ex.Message);
            }
            finally
            {
                DialogHostApplyGuard.EndWorldOnly();
                if (pushed) GeFireActorContext.Pop();
            }
        }

        /// <returns>The released hold's NPC (its spot and world).</returns>
        internal static NpcRef HostRelease(LanNetworkManager net, NpcRef npc, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return npc;
            NpcRef released = Release(npc, ownerPlayerId);
            BroadcastState(net, released, ownerPlayerId, granted: true, release: true);
            return released;
        }

        /// <summary>
        /// Host disconnect: drop every NPC lock held by the leaver and fan release so
        /// remaining peers are not stuck "Someone is already talking…" for up to the
        /// 90s lease. Local-only <see cref="ReleaseAllForPlayer"/> covers roster prune.
        /// </summary>
        public static void HostReleaseAllForPlayer(LanNetworkManager net, int playerId)
        {
            if (playerId < 0 || _locks.Count == 0) return;

            int released = 0;
            for (int i = _locks.Count - 1; i >= 0; i--)
            {
                Hold hold = _locks[i];
                if (hold.OwnerId != playerId) continue;
                _locks.RemoveAt(i);
                released++;
                if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                    BroadcastState(net, hold.Npc, playerId, granted: true, release: true);
            }
            if (released == 0) return;
            ModLog.Event(LogCat.Session,
                "[DialogLock] released " + released + " NPC lock(s) for disconnect p" + playerId);
        }

        /// <summary>Local clear of locks owned by a pruned roster peer (clients).</summary>
        public static void ReleaseAllForPlayer(int playerId)
        {
            if (playerId < 0) return;
            for (int i = _locks.Count - 1; i >= 0; i--)
            {
                if (_locks[i].OwnerId == playerId)
                    _locks.RemoveAt(i);
            }
        }

        internal static DialogNpcLockMessage BuildMessage(NpcRef npc, int ownerPlayerId, bool granted,
            bool release, bool isRequest = false, bool renewal = false)
        {
            return new DialogNpcLockMessage
            {
                NpcName = npc.Name ?? "",
                OwnerPlayerId = ownerPlayerId,
                Granted = granted,
                Release = release,
                IsRequest = isRequest,
                Dream = npc.Dream,
                Renewal = renewal,
                HasPos = npc.HasPos,
                PosX = npc.Pos.x,
                PosY = npc.Pos.y,
                PosZ = npc.Pos.z
            };
        }

        private static void BroadcastState(LanNetworkManager net, NpcRef npc, int ownerPlayerId,
            bool granted, bool release)
        {
            if (net == null || !net.IsConnected) return;
            var msg = BuildMessage(npc, ownerPlayerId, granted, release);
            net.Broadcast(NetMessageType.DialogNpcLock, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
