using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host-authoritative one-speaker-per-NPC lock (audit H5).
    /// Multiple NPCs may be spoken to in parallel (Dictionary); same NPC is serialized.
    /// </summary>
    public static class NpcDialogueLock
    {
        private struct Hold
        {
            public int OwnerId;
            public float ExpireAt;
        }

        private static readonly Dictionary<string, Hold> _locks = new Dictionary<string, Hold>();

        /// <summary>Count of active (non-expired) locks — for tests / diagnostics.</summary>
        public static int ActiveCount
        {
            get
            {
                float now = Time.unscaledTime;
                int n = 0;
                foreach (var kvp in _locks)
                {
                    if (now < kvp.Value.ExpireAt)
                        n++;
                }
                return n;
            }
        }

        public static void Reset()
        {
            _locks.Clear();
        }

        public static bool TryAcquire(string npcName, int ownerPlayerId, float leaseSeconds = -1f)
        {
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0)
                return false;

            float now = Time.unscaledTime;
            float lease = leaseSeconds > 0f ? leaseSeconds : NpcDialogueLockPolicy.DefaultLeaseSeconds;

            int heldOwner = -1;
            float heldExpire = 0f;
            if (_locks.TryGetValue(npcName, out Hold existing))
            {
                heldOwner = existing.OwnerId;
                heldExpire = existing.ExpireAt;
            }

            if (!NpcDialogueLockPolicy.CanAcquireNpcSlot(heldOwner, heldExpire, ownerPlayerId, now))
                return false;

            _locks[npcName] = new Hold
            {
                OwnerId = ownerPlayerId,
                ExpireAt = now + lease
            };
            return true;
        }

        public static void Release(string npcName, int ownerPlayerId)
        {
            if (string.IsNullOrEmpty(npcName)) return;
            if (!_locks.TryGetValue(npcName, out Hold hold))
                return;

            float now = Time.unscaledTime;
            if (!NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, now))
                return;

            _locks.Remove(npcName);
        }

        public static void ForceRelease(string reason = null)
        {
            if (_locks.Count > 0)
                ModLog.Event(LogCat.Session,
                    "[DialogLock] force release count=" + _locks.Count + " reason=" + (reason ?? ""));
            _locks.Clear();
        }

        public static bool IsLockedByOther(string npcName, int localPlayerId)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            if (!_locks.TryGetValue(npcName, out Hold hold))
                return false;
            float now = Time.unscaledTime;
            if (now >= hold.ExpireAt) return false;
            return hold.OwnerId != localPlayerId;
        }

        public static int GetOwner(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return -1;
            if (!_locks.TryGetValue(npcName, out Hold hold)) return -1;
            if (Time.unscaledTime >= hold.ExpireAt) return -1;
            return hold.OwnerId;
        }

        /// <summary>Host: attempt lock and notify requestor (and peers).</summary>
        public static bool HostTryGrant(LanNetworkManager net, string npcName, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            bool ok = TryAcquire(npcName, ownerPlayerId);
            BroadcastState(net, npcName, ownerPlayerId, granted: ok, release: false);
            if (ok)
                ModLog.Event(LogCat.Session, $"[DialogLock] granted NPC={npcName} owner={ownerPlayerId}");
            else
                ModLog.Event(LogCat.Session,
                    $"[DialogLock] denied NPC={npcName} owner={ownerPlayerId} heldBy={GetOwner(npcName)}");
            return ok;
        }

        public static void HostRelease(LanNetworkManager net, string npcName, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return;
            Release(npcName, ownerPlayerId);
            BroadcastState(net, npcName, ownerPlayerId, granted: true, release: true);
        }

        private static void BroadcastState(LanNetworkManager net, string npcName, int ownerPlayerId, bool granted, bool release)
        {
            if (net == null || !net.IsConnected) return;
            net.Broadcast(NetMessageType.DialogNpcLock,
                w => new DialogNpcLockMessage
                {
                    NpcName = npcName ?? "",
                    OwnerPlayerId = ownerPlayerId,
                    Granted = granted,
                    Release = release
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
