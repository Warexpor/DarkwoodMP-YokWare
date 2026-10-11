using System.Collections;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A broken destructible lies in the same place for everyone. The blow that breaks a wardrobe
    /// (a shotgun blast) shoves the wreck after it broke, on the machine that broke it only; the
    /// others break their copy where it stood (BarricadeEvent). The free-body stream did not bring
    /// that slide over reliably, so the wreck lay apart by ten units and turned differently, and
    /// anything found by position (its loot, its repair) missed it. The breaking machine now waits
    /// until its wreck has come to rest and sends that pose (ItemPose); the others move their wreck
    /// there.
    /// </summary>
    internal static class WreckPoseSync
    {
        /// <summary>Speed under which a wreck counts as at rest.</summary>
        private const float RestSpeed = 0.1f;
        /// <summary>Checks in a row at rest before the pose is sent.</summary>
        private const int RestChecks = 3;
        /// <summary>A wreck still moving after this long is sent as it is.</summary>
        private const float MaxWaitSeconds = 5f;

        /// <summary>The item just broke here from a hit of this machine's own: send where it comes to rest.</summary>
        internal static void WatchLocalBreak(Item item, Vector3 breakPos)
        {
            if (item == null || !item.destructible || !item.destroyed)
                return;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            ctrl.StartCoroutine(WaitAndSend(item, breakPos));
        }

        private static IEnumerator WaitAndSend(Item item, Vector3 breakPos)
        {
            float start = Time.time;
            int quiet = 0;
            Rigidbody rb = item != null ? item.GetComponent<Rigidbody>() : null;
            yield return new WaitForSeconds(0.3f);
            while (item != null && item.destroyed && Time.time - start < MaxWaitSeconds)
            {
                bool still = rb == null || rb.isKinematic
                    || (rb.velocity.sqrMagnitude < RestSpeed * RestSpeed
                        && rb.angularVelocity.sqrMagnitude < RestSpeed * RestSpeed);
                quiet = still ? quiet + 1 : 0;
                if (quiet >= RestChecks)
                    break;
                yield return new WaitForSeconds(0.1f);
            }
            if (item == null || !item.destroyed)
                yield break;
            if (!NetGuard.Connected(out LanNetworkManager net))
                yield break;
            Transform t = item.transform;
            Vector3 p = t.position;
            Vector3 r = t.eulerAngles;
            var msg = new ItemPoseMessage
            {
                SaveId = WorldQueryHelper.FurnitureSaveId(item.gameObject),
                Name = item.gameObject.name,
                BreakX = breakPos.x, BreakY = breakPos.y, BreakZ = breakPos.z,
                PosX = p.x, PosY = p.y, PosZ = p.z,
                RotX = r.x, RotY = r.y, RotZ = r.z
            };
            net.Broadcast(NetMessageType.ItemPose, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[WreckPose] TX {msg.Name} id={msg.SaveId} rest at {p} rot={r.y:F0} (broke at {breakPos})");
        }

        internal static void Handle(ItemPoseMessage msg)
        {
            Vector3 breakPos = new Vector3(msg.BreakX, msg.BreakY, msg.BreakZ);
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Item item = WorldQueryHelper.FindFurnitureBySaveId<Item>(msg.SaveId, breakPos);
            if (item == null || item.gameObject.name != msg.Name)
                item = WorldQueryHelper.FindDestructibleItemByName(msg.Name, breakPos, WorldPhysicsSyncService.ResolvedNameMaxDist);
            if (item == null)
            {
                ModLog.WarnRate(LogCat.World, "wreck-pose-miss",
                    "[WreckPose] no " + msg.Name + " (id " + msg.SaveId + ") near " + breakPos);
                return;
            }
            if (!item.destroyed)
            {
                ModRuntime.LegacyInfo($"[WreckPose] {item.name} is not broken here — pose not applied");
                return;
            }
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            Vector3 from = item.transform.position;
            item.transform.SetPositionAndRotation(pos, rot);
            Rigidbody rb = item.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = pos;
                rb.rotation = rot;
                if (!rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            WorldPhysicsSyncService.NoteRemoteRelocated(item.gameObject);
            ModRuntime.LegacyInfo($"[WreckPose] RX {item.name} moved {Vector3.Distance(from, pos):F1} to {pos} rot={msg.RotY:F0}");
        }
    }
}
