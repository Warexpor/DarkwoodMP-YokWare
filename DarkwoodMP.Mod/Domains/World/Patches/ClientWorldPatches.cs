using System.Collections;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Blocks client-side spawning that the host should control.</summary>
    internal static class ClientWorldHelper
    {
        internal static bool IsClient => ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Client;
    }

    /// <summary>Blocks client-side night-time creature spawn (host spawns all nocturnal NPCs).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnNightChar")]
    public static class ClientDisableNightSpawnPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>Blocks client-side shadow spawn (shadows synced from host via ShadowSpawnMessage).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "waitToSpawnShadow")]
    public static class ClientDisableShadowSpawnPatch
    {
        // IEnumerator — assign empty when skipping so StartCoroutine is never null.
        private static bool Prefix(ref IEnumerator __result)
        {
            if (!ClientWorldHelper.IsClient)
                return true;
            __result = HarmonyCoroutineUtil.Empty();
            return false;
        }
    }

    /// <summary>Blocks client-side worm spawn. Host with peers uses a party-aware loop.</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "waitToSpawnWorm")]
    public static class ClientDisableWormSpawnPatch
    {
        // IEnumerator — CharacterSpawner.init StartCoroutines this; null __result NREs.
        private static bool Prefix(CharacterSpawner __instance, ref IEnumerator __result)
        {
            if (ClientWorldHelper.IsClient)
            {
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }

            // Vanilla only looks at Player.Instance. A warded host suppressed the
            // worm for an unwarned client, and attackPlayer() always hit the host.
            if (ModRuntime.Network != null
                && ModRuntime.Network.Role == NetworkRole.Host
                && ModRuntime.Network.IsConnected
                && PlayerPositionManager.HasRemotePlayer)
            {
                __result = HardNightPartySpawn.WormLoop(__instance);
                return false;
            }

            return true;
        }
    }

    /// <summary>Blocks client-side nocturnal character despawn (host controls despawning).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "despawnNocturnalCharacters")]
    public static class ClientDisableNocturnalDespawnPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>Blocks client-side forest spirit spawn (host-authoritative).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnForestSpirit")]
    public static class ClientDisableForestSpiritPatch
    {
        // IEnumerator — same StartCoroutine(null) class as GameEvent.fire / HelpMessage.
        private static bool Prefix(ref IEnumerator __result)
        {
            if (!ClientWorldHelper.IsClient)
                return true;
            __result = HarmonyCoroutineUtil.Empty();
            return false;
        }
    }

    /// <summary>
    /// Host-authoritative night random events: clients never fire RandomEvent.
    /// Host spawns (e.g. Redneck via spawnCharacterAround) and entity snapshots
    /// reach clients — dual fire would duplicate near each player.
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), "fire", typeof(bool), typeof(bool))]
    public static class ClientBlockRandomEventFirePatch
    {
        private static bool Prefix(RandomEvent __instance)
        {
            if (!ClientWorldHelper.IsClient) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            // Host told us this night scene fired. Play its local effects.
            // Creature spawns stay on the host.
            if (ClientRandomEventGate.PlayingHostLocationEvent
                && __instance != null
                && __instance.type == RandomEvent.Type.locationEvent)
                return true;
            return false;
        }
    }

    /// <summary>Set while a client replays a host night location event, including its delayed steps.</summary>
    internal static class ClientRandomEventGate
    {
        private static float _until;

        public static bool PlayingHostLocationEvent => UnityEngine.Time.unscaledTime < _until;

        public static void Arm(float seconds)
        {
            float until = UnityEngine.Time.unscaledTime + seconds;
            if (until > _until)
                _until = until;
        }
    }

    /// <summary>
    /// The location event's own prefab may try to spawn a creature. The host
    /// already did that and will send the body. Skip a second one here.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnCharacterAround")]
    [HarmonyPriority(Priority.First)]
    public static class ClientScenarioEventNoDuplicateSpawnPatch
    {
        private static bool Prefix()
        {
            return !ClientRandomEventGate.PlayingHostLocationEvent;
        }
    }

    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    [HarmonyPriority(Priority.First)]
    public static class ClientScenarioEventNoCharacterPrefabPatch
    {
        private static bool Prefix(string prefab, ref GameObject __result)
        {
            if (!ClientRandomEventGate.PlayingHostLocationEvent)
                return true;
            if (string.IsNullOrEmpty(prefab))
                return true;
            if (prefab.IndexOf("characters/", System.StringComparison.OrdinalIgnoreCase) < 0)
                return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Door), "getHit")]
    [HarmonyPriority(Priority.First)]
    public static class ClientScenarioEventNoDoorHitPatch
    {
        private static bool Prefix(Transform attackerTransform)
        {
            if (!ClientRandomEventGate.PlayingHostLocationEvent)
                return true;
            if (attackerTransform != null && Player.Instance != null
                && (attackerTransform == Player.Instance.transform
                    || attackerTransform.IsChildOf(Player.Instance.transform)))
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Window), "getHit")]
    [HarmonyPriority(Priority.First)]
    public static class ClientScenarioEventNoWindowHitPatch
    {
        private static bool Prefix(Transform attackerTransform)
        {
            if (!ClientRandomEventGate.PlayingHostLocationEvent)
                return true;
            if (attackerTransform != null && Player.Instance != null
                && (attackerTransform == Player.Instance.transform
                    || attackerTransform.IsChildOf(Player.Instance.transform)))
                return true;
            return false;
        }
    }

    /// <summary>
    /// Decompile <c>RandomEvent.randomizeStartTime</c> rolls <c>timeToStart</c>
    /// from <c>Events.initialize</c> and each <c>onNewDay</c>. Clients already
    /// skip <c>fire</c>; independent schedule rolls still diverge latch timing
    /// vs host (and vs late-join <c>ScenarioStateBulk</c>). WorldSaveShare /
    /// scenario bulk supply host times — do not re-roll on clients.
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), "randomizeStartTime")]
    public static class ClientBlockRandomEventRandomizeStartTimePatch
    {
        private static bool Prefix()
        {
            if (!ClientWorldHelper.IsClient) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            return false;
        }
    }

}
