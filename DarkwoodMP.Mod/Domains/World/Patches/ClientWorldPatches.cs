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

    /// <summary>
    /// Worms: a client spawns none (the host spawns them and sends them). The host's loop is the
    /// party one from the start (<see cref="HardNightPartySpawn"/>), vanilla's own while no peer is
    /// there, since a client usually joins after the world loaded.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "waitToSpawnWorm")]
    public static class ClientDisableWormSpawnPatch
    {
        // IEnumerator — CharacterSpawner.init StartCoroutines this; null __result NREs.
        private static bool Prefix(CharacterSpawner __instance, ref IEnumerator __result)
        {
            __result = ClientWorldHelper.IsClient
                ? HarmonyCoroutineUtil.Empty()
                : HardNightPartySpawn.WormLoop(__instance);
            return false;
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

        /// <summary>Client in a live session only: a stale window must never touch singleplayer or a host.</summary>
        public static bool PlayingHostLocationEvent =>
            _until > 0f
            && ClientWorldHelper.IsClient
            && ModRuntime.Network.IsConnected
            && UnityEngine.Time.unscaledTime < _until;

        public static void Arm(float seconds)
        {
            if (!ClientWorldHelper.IsClient)
                return;
            float until = UnityEngine.Time.unscaledTime + seconds;
            if (until > _until)
                _until = until;
        }

        /// <summary>Registered with NetworkResetRegistry so a replay window cannot outlive its session.</summary>
        public static void Reset() => _until = 0f;
    }

    /// <summary>
    /// While a client replays a host night location event, the event's own spawnCharacter /
    /// replaceCharacter steps must not create a second creature: the host already did and sends the
    /// body. Skip the whole GameEvent coroutine instead of nulling Core.AddPrefab /
    /// spawnCharacterAround results (vanilla callers dereference those unconditionally:
    /// GameEvent.fire .GetComponent&lt;Character&gt;(), Location trader spawn, Character gibs).
    /// </summary>
    [HarmonyPatch(typeof(GameEvent), "fire", typeof(GameObject))]
    public static class ClientScenarioEventNoCharacterSpawnPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(GameEvent __instance, ref IEnumerator __result)
        {
            if (!ClientRandomEventGate.PlayingHostLocationEvent)
                return true;
            if (__instance == null
                || (__instance.type != GameEvent.Type.spawnCharacter
                    && __instance.type != GameEvent.Type.replaceCharacter))
                return true;
            // IEnumerator — assign empty when skipping so StartCoroutine is never null.
            __result = HarmonyCoroutineUtil.Empty();
            return false;
        }
    }

    [HarmonyPatch(typeof(Door), "getHit")]
    public static class ClientScenarioEventNoDoorHitPatch
    {
        [HarmonyPriority(Priority.First)]
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
    public static class ClientScenarioEventNoWindowHitPatch
    {
        [HarmonyPriority(Priority.First)]
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
