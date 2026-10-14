using System;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <remarks>Applied from <see cref="CoreAddPrefabStringPatch"/> (one detour for all features).</remarks>
    public static class CoreAddPrefabPhysicsSyncPatch
    {
        internal static void OnAddPrefab(GameObject __result, string path, Vector3 pos, Quaternion rot)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;
            if (__result == null)
                return;
            // A new world's generation and a save's load place their prefabs through here too:
            // those are the world itself, which a joiner gets in the world package. Sent live, a
            // joiner on the title built each into the menu scene.
            if (Core.loadingGame || !Core.worldGenFinished())
                return;

            if (string.IsNullOrEmpty(path))
                return;

            // Skip Characters — entity state broadcast + pending match handles them
            if (path.StartsWith("Characters/", StringComparison.Ordinal))
                return;

            // Skip items already synced by dedicated patches (avoid double-spawn)
            if (path == "Items/GasolineTrail" ||
                path == "Items/DroppedItem" ||
                path == "Items/DroppedItem_water" ||
                path.StartsWith("Objects/_Unique/deathDrop", StringComparison.Ordinal))
                return;

            // Catch all Items, Traps, Objects, and gameplay-critical FX
            if (!path.StartsWith("Items/", StringComparison.Ordinal) && !path.StartsWith("Traps/", StringComparison.Ordinal) &&
                !path.StartsWith("Objects/", StringComparison.Ordinal) && !path.StartsWith("FX/", StringComparison.Ordinal))
                return;

            // Filter out pure cosmetic FX
            if (path.StartsWith("FX/", StringComparison.Ordinal))
            {
                if (path.StartsWith("FX/Bloodsplats/", StringComparison.Ordinal) ||
                    path.StartsWith("FX/particles/", StringComparison.Ordinal) ||
                    path.StartsWith("FX/skills/", StringComparison.Ordinal) ||
                    path.StartsWith("FX/Muzzle/", StringComparison.Ordinal) ||
                    path.StartsWith("FX/WaterRipple", StringComparison.Ordinal))
                    return;
            }

            var net = ModRuntime.Network;
            if (net == null) return;

            Vector3 euler = rot.eulerAngles;
            // Broadcast: host → all clients; client → host (3+ peer fan-out on host receive).
            // Do not use Send() — that only reaches the first peer.
            net.Broadcast(NetMessageType.EntitySpawn,
                w => new EntitySpawnMessage
                {
                    PrefabPath = path,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = euler.x,
                    RotY = euler.y,
                    RotZ = euler.z
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[PhysicsSpawnSync] sent spawn: {path} at {pos}");
        }
    }
}
