using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host-authoritative exclusive workbench open (one crafter at a time).
    /// Mirrors NpcDialogueLock shape; key is stable world pos.
    /// </summary>
    public static class WorkbenchOpenLock
    {
        private struct Hold
        {
            public int OwnerId;
            public float ExpireAt;
        }

        private static readonly Dictionary<string, Hold> _locks = new Dictionary<string, Hold>();

        public const float DefaultLeaseSeconds = 120f;

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

        public static string KeyFor(Workbench wb)
        {
            if (wb == null) return "";
            Vector3 p = wb.transform.position;
            float x = Mathf.Round(p.x * 10f) / 10f;
            float z = Mathf.Round(p.z * 10f) / 10f;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}:{1:0.0}", x, z);
        }

        public static void Reset()
        {
            _locks.Clear();
        }

        public static bool TryAcquire(string key, int ownerPlayerId, float leaseSeconds = -1f)
        {
            if (string.IsNullOrEmpty(key) || ownerPlayerId < 0)
                return false;

            float now = Time.unscaledTime;
            float lease = leaseSeconds > 0f ? leaseSeconds : DefaultLeaseSeconds;

            int heldOwner = -1;
            float heldExpire = 0f;
            if (_locks.TryGetValue(key, out Hold existing))
            {
                heldOwner = existing.OwnerId;
                heldExpire = existing.ExpireAt;
            }

            if (!NpcDialogueLockPolicy.CanAcquireNpcSlot(heldOwner, heldExpire, ownerPlayerId, now))
                return false;

            _locks[key] = new Hold
            {
                OwnerId = ownerPlayerId,
                ExpireAt = now + lease
            };
            return true;
        }

        public static void Release(string key, int ownerPlayerId)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_locks.TryGetValue(key, out Hold hold))
                return;

            float now = Time.unscaledTime;
            if (!NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, now))
                return;

            _locks.Remove(key);
        }

        public static bool IsLockedByOther(string key, int localPlayerId)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!_locks.TryGetValue(key, out Hold hold))
                return false;
            float now = Time.unscaledTime;
            if (now >= hold.ExpireAt) return false;
            return hold.OwnerId != localPlayerId;
        }

        public static int GetOwner(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            if (!_locks.TryGetValue(key, out Hold hold)) return -1;
            if (Time.unscaledTime >= hold.ExpireAt) return -1;
            return hold.OwnerId;
        }

        public static bool HostTryGrant(LanNetworkManager net, string key, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            bool ok = TryAcquire(key, ownerPlayerId);
            BroadcastState(net, key, ownerPlayerId, granted: ok, release: false);
            if (ok)
                ModLog.Event(LogCat.Session, $"[WorkbenchLock] granted key={key} owner={ownerPlayerId}");
            else
                ModLog.Event(LogCat.Session,
                    $"[WorkbenchLock] denied key={key} owner={ownerPlayerId} heldBy={GetOwner(key)}");
            return ok;
        }

        public static void HostRelease(LanNetworkManager net, string key, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return;
            if (string.IsNullOrEmpty(key)) return;
            int before = GetOwner(key);
            // Host is authority: drop the key if requester owns it, or force when ownerPlayerId
            // matches the recorded hold (disconnect path).
            if (_locks.TryGetValue(key, out Hold hold))
            {
                if (ownerPlayerId <= 0
                    || hold.OwnerId == ownerPlayerId
                    || Time.unscaledTime >= hold.ExpireAt)
                    _locks.Remove(key);
                else
                    Release(key, ownerPlayerId); // no-op if not owner
            }
            BroadcastState(net, key, ownerPlayerId > 0 ? ownerPlayerId : before, granted: true, release: true);
            ModLog.Event(LogCat.Session,
                $"[WorkbenchLock] released key={key} by={ownerPlayerId} was={before}");
        }

        /// <summary>Host: drop every lock held by a disconnecting player.</summary>
        public static void HostReleaseAllForPlayer(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0) return;
            if (_locks.Count == 0) return;
            var keys = new System.Collections.Generic.List<string>();
            foreach (var kv in _locks)
            {
                if (kv.Value.OwnerId == playerId)
                    keys.Add(kv.Key);
            }
            for (int i = 0; i < keys.Count; i++)
                HostRelease(net, keys[i], playerId);
        }

        private static void BroadcastState(LanNetworkManager net, string key, int ownerPlayerId, bool granted, bool release)
        {
            if (net == null || !net.IsConnected) return;
            net.Broadcast(NetMessageType.WorkbenchLock,
                w => new WorkbenchLockMessage
                {
                    WorkbenchKey = key ?? "",
                    OwnerPlayerId = ownerPlayerId,
                    Granted = granted,
                    Release = release,
                    IsRequest = false
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
