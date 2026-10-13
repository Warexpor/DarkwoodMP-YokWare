using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative ObjectSpawner (interval / loop prefab place): the junkyard's deadly
    /// light, the mushroom granny's ghosts, a night event's enemy waves. Vanilla: Start →
    /// private spawnObject, which waits the entry's interval, places it (with a random offset
    /// when it has one) and moves on to the next.
    /// The host runs it and tells the clients each placement; a client's own copy of the spawner
    /// places the same entry at the same spot. Before, a client only skipped its spawners: a
    /// creature still reached it (entity sync), but a light, a ghost or any other object did
    /// not, so the junkyard's light hurt a client that never saw it.
    /// </summary>
    internal static class ObjectSpawnerSync
    {
        private static readonly MethodInfo SpawnObject = AccessTools.Method(typeof(ObjectSpawner), "spawnObject");

        /// <summary>Same spawner on both machines: world locations stand at the same spot for everyone.</summary>
        private const float SameSpawnerSq = 5f * 5f;

        /// <summary>Vanilla's placement of one entry, at a given spot.</summary>
        private static void Place(ObjectSpawner spawner, ObjectSpawner.ObjectToSpawn obj, Vector3 at)
        {
            if (!string.IsNullOrEmpty(obj.pool))
                Core.AddPooledPrefab(obj.pool, obj.prefab.name, at, Quaternion.Euler(90f, 0f, 0f));
            else
                Core.AddPrefab(obj.prefab, at, Quaternion.Euler(90f, 0f, 0f), obj.parentToMe ? spawner.gameObject : null, worldSpace: true);
        }

        /// <summary>A creature reaches the clients as an entity; everything else is placed by them.</summary>
        private static bool IsCreature(ObjectSpawner.ObjectToSpawn obj)
        {
            GameObject go = obj.prefab as GameObject;
            return go != null && go.GetComponent<Character>() != null;
        }

        /// <summary>Host: vanilla's spawnObject with the placement told to the clients.</summary>
        internal static void HostRun(ObjectSpawner spawner)
        {
            int index = spawner.currentId;
            ObjectSpawner.ObjectToSpawn obj = spawner.objectsToSpawn[index];
            spawner.startRoutine(delegate
            {
                Vector3 offset = Vector3.zero;
                Vector3 dest = Vector3.zero;
                if (obj.randomOffset)
                    offset = new Vector3(Random.Range(0f - obj.offsetRange, obj.offsetRange), 0f, Random.Range(0f - obj.offsetRange, obj.offsetRange));
                if (obj.destPosition != null)
                    dest = obj.destPosition.position;
                Vector3 at = dest + offset;
                Place(spawner, obj, at);
                HostSend(spawner, obj, index, at);
                spawner.currentId++;
                if (spawner.currentId >= spawner.objectsToSpawn.Count)
                    spawner.currentId = 0;
                if (spawner.loop)
                    SpawnObject.Invoke(spawner, null);
            }, obj.interval);
        }

        private static void HostSend(ObjectSpawner spawner, ObjectSpawner.ObjectToSpawn obj, int index, Vector3 at)
        {
            if (!NetGuard.ConnectedHost(out LanNetworkManager net) || IsCreature(obj))
                return;
            // A personal pad (the host's prologue, a solo dream) exists on its machine only.
            if (PersonalPads.Contains(spawner.transform))
                return;
            Vector3 sp = spawner.transform.position;
            var msg = new ObjectSpawnerFiredMessage
            {
                SpawnerName = spawner.gameObject.name,
                SpawnerX = sp.x, SpawnerY = sp.y, SpawnerZ = sp.z,
                Index = index,
                PosX = at.x, PosY = at.y, PosZ = at.z
            };
            net.Broadcast(NetMessageType.ObjectSpawnerFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client: this machine's copy of the spawner places the entry the host's just placed.</summary>
        internal static void ClientApply(LanNetworkManager net, ObjectSpawnerFiredMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client || string.IsNullOrEmpty(msg.SpawnerName))
                return;
            Vector3 sp = new Vector3(msg.SpawnerX, msg.SpawnerY, msg.SpawnerZ);
            ObjectSpawner found = null;
            float best = SameSpawnerSq;
            foreach (ObjectSpawner s in Object.FindObjectsOfType<ObjectSpawner>())
            {
                if (s == null || s.gameObject.name != msg.SpawnerName)
                    continue;
                float d = (s.transform.position - sp).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    found = s;
                }
            }
            // Not loaded here (a location this player is not in): nothing to place.
            if (found == null || found.objectsToSpawn == null || msg.Index < 0 || msg.Index >= found.objectsToSpawn.Count)
                return;
            ObjectSpawner.ObjectToSpawn obj = found.objectsToSpawn[msg.Index];
            if (obj == null || obj.prefab == null || IsCreature(obj))
                return;
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                Place(found, obj, new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                found.currentId = msg.Index + 1 >= found.objectsToSpawn.Count ? 0 : msg.Index + 1;
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.World, "[ObjectSpawner] placing " + msg.SpawnerName + " #" + msg.Index + ": " + ex.Message);
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prev);
            }
        }
    }

    [HarmonyPatch(typeof(ObjectSpawner), "spawnObject")]
    public static class ObjectSpawnerSpawnObjectPatch
    {
        private static bool Prefix(ObjectSpawner __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline)
                return true;
            if (net.Role == NetworkRole.Client)
                return false;
            if (__instance == null || __instance.objectsToSpawn == null
                || __instance.currentId < 0 || __instance.currentId >= __instance.objectsToSpawn.Count
                || __instance.objectsToSpawn[__instance.currentId] == null
                || __instance.objectsToSpawn[__instance.currentId].prefab == null)
                return true;
            ObjectSpawnerSync.HostRun(__instance);
            return false;
        }
    }
}
