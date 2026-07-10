using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(Core), "AddPrefab", typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool))]
    public static class CoreAddPrefabPhysicsSyncPatch
    {
        private static void Postfix(GameObject __result, object[] __args)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;
            if (__result == null)
                return;

            string path = (string)__args[0];
            if (string.IsNullOrEmpty(path))
                return;

            // Skip Characters — entity state broadcast + pending match handles them
            if (path.StartsWith("Characters/"))
                return;

            // Skip items already synced by dedicated patches (avoid double-spawn)
            if (path == "Items/GasolineTrail" ||
                path == "Items/DroppedItem" ||
                path == "Items/DroppedItem_water" ||
                path.StartsWith("Objects/_Unique/deathDrop"))
                return;

            // Catch all Items, Traps, Objects, and gameplay-critical FX
            if (!path.StartsWith("Items/") && !path.StartsWith("Traps/") &&
                !path.StartsWith("Objects/") && !path.StartsWith("FX/"))
                return;

            // Filter out pure cosmetic FX
            if (path.StartsWith("FX/"))
            {
                if (path.StartsWith("FX/Bloodsplats/") ||
                    path.StartsWith("FX/particles/") ||
                    path.StartsWith("FX/skills/") ||
                    path.StartsWith("FX/Muzzle/") ||
                    path.StartsWith("FX/WaterRipple"))
                    return;
            }

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            Vector3 pos = (Vector3)__args[1];
            Quaternion rot = (Quaternion)__args[2];

            // Broadcast: host → all clients; client → host (3+ peer fan-out on host receive).
            // Do not use Send() — that only reaches the first peer.
            net.Broadcast(NetMessageType.EntitySpawn,
                w => new EntitySpawnMessage
                {
                    PrefabPath = path,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = rot.eulerAngles.x,
                    RotY = rot.eulerAngles.y,
                    RotZ = rot.eulerAngles.z
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[PhysicsSpawnSync] sent spawn: {path} at {pos}");
        }
    }
}
