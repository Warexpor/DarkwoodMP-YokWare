using System.Collections;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    // ─── spawnCharacterAround: around the player the step is for ──────

    /// <summary>
    /// A scripted step that spawns a creature around "the player" (GameEvent spawnCharacter on the
    /// player body; a night scene's visitor) comes around the player the scene plays for, as in that
    /// player's own game: the peer's stand-in when the host runs a peer's scene. It used to go
    /// around the host, or on a coin flip around some far peer, whoever the scene was for, so each
    /// player met about half of what vanilla sends it. The redneck ambush picks its own player
    /// (<see cref="HostRedneckPartyPatch"/>) and the hideout's night monsters their hideout.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnCharacterAround")]
    public static class SpawnCharacterAroundRedirectPatch
    {
        private static void Prefix(object[] __args)
        {
            if (ModRuntime.Network?.Role != NetworkRole.Host) return;
            if (NightSpawnGetFreeSpotPatch.InsideNightSpawn) return;
            if (HostRedneckPartyPatch.Placing) return;
            if (!PlayerPositionManager.HasRemotePlayer) return;

            Player host = Player.Instance;
            if (host == null) return;
            GameObject destGO = (GameObject)__args[0];
            if (destGO != host.gameObject) return;

            Transform actor = GeFireActorContext.ActorBody();
            if (actor == null || actor.gameObject == host.gameObject) return;
            __args[0] = actor.gameObject;
            ModRuntime.LegacyInfo($"[NightSpawnRedirect] spawnCharacterAround → actor P{GeFireActorContext.PeekOr(0)} at {actor.position}");
        }

        /// <summary>
        /// Vanilla copies host <c>whereAmI.bigLocation.waypoints</c> — event dogs then
        /// patrol toward the host macro map. Clear for temp MP spawns.
        /// </summary>
        private static void Postfix(Character __result)
        {
            if (__result == null || !__result.temporarySpawned) return;
            // Night monsters at the hideout keep the hideout's waypoints (vanilla).
            if (NightSpawnGetFreeSpotPatch.InsideNightSpawn) return;
            if (ModRuntime.Network?.Role != NetworkRole.Host) return;
            if (!PlayerPositionManager.HasRemotePlayer) return;
            if (__result.waypoints != null)
                __result.waypoints.Clear();
        }
    }

    // ─── Hard-night worms: one per exposed player ──────────────────────

    /// <summary>
    /// Hard-night worms (vanilla <c>CharacterSpawner.waitToSpawnWorm</c>): every 5 seconds of hard
    /// night a worm comes for the player unless it is warded. With peers every living, unwarded
    /// player gets its own, at vanilla's rate. Vanilla looks at the host body only (a warded host
    /// spared an exposed client, and every worm hunted the host); the old party loop picked one
    /// player per tick, so with two exposed players each met half as many. Runs as the host's worm
    /// loop from the start, since a client usually joins after the world loaded; with no peer a
    /// tick is vanilla's own.
    /// </summary>
    public static class HardNightPartySpawn
    {
        public static bool Placing { get; private set; }

        private struct Body
        {
            public Vector3 Pos;
            public Transform Attack;
        }

        private static readonly List<Body> _bodies = new List<Body>(4); // process-scoped: scratch, filled and emptied within one tick

        public static IEnumerator WormLoop(CharacterSpawner spawner)
        {
            var wait = new WaitForSeconds(5f);
            while (spawner != null)
            {
                yield return wait;
                // A host that became a client (host migration): the new host spawns them.
                if (ClientWorldHelper.IsClient)
                    continue;
                if (!NetGuard.ConnectedHost(out LanNetworkManager net) || !PlayerPositionManager.HasRemotePlayer)
                {
                    VanillaTick(spawner);
                    continue;
                }
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl == null || !ctrl.isHardNight || Core.isDay())
                    continue;
                if (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming)
                    continue;
                CollectUnwardedBodies(net);
                try
                {
                    for (int i = 0; i < _bodies.Count; i++)
                        SpawnFor(spawner, _bodies[i]);
                }
                finally
                {
                    _bodies.Clear();
                }
            }
        }

        /// <summary>Vanilla <c>waitToSpawnWorm</c>'s loop body.</summary>
        private static void VanillaTick(CharacterSpawner spawner)
        {
            Player p = Player.Instance;
            if (p == null || p.ignoreNightSickness || !Singleton<Controller>.Instance.isHardNight || Core.isDay()
                || p.effects.hasEffectType(CharacterEffectType.shadowWard) || Singleton<Dreams>.Instance.dreaming)
                return;
            Vector3 position = Core.randomPosAround(p._transform.position, 1500f, 2000f, canBeInside: true, mustBeInsideGraph: false);
            Character component = Core.AddPrefab("characters/fakechars/NightWorms_01", position,
                Quaternion.Euler(90f, Random.Range(0, 360), 0f), null).GetComponent<Character>();
            component.attackPlayer();
            spawner.nocturnalCharacters.Add(component.gameObject);
        }

        private static void SpawnFor(CharacterSpawner spawner, Body body)
        {
            Vector3 position = Core.randomPosAround(body.Pos, 1500f, 2000f, canBeInside: true, mustBeInsideGraph: false);
            GameObject go;
            Placing = true;
            try
            {
                go = Core.AddPrefab(
                    "characters/fakechars/NightWorms_01",
                    position,
                    Quaternion.Euler(90f, Random.Range(0, 360), 0f),
                    null);
            }
            finally
            {
                Placing = false;
            }

            if (go == null) return;
            Character component = go.GetComponent<Character>();
            if (component != null && body.Attack != null)
                PlayerTargetArbiter.Commit(component, body.Attack, "nightWorms");
            if (spawner.nocturnalCharacters != null)
                spawner.nocturnalCharacters.Add(go);
        }

        private static void CollectUnwardedBodies(LanNetworkManager net)
        {
            _bodies.Clear();
            Player host = Player.Instance;
            if (host != null && HostEligible(host))
            {
                Transform t = host._transform != null ? host._transform : host.transform;
                _bodies.Add(new Body { Pos = t.position, Attack = t });
            }

            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.RemoteHasShadowWard) continue;
                if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId)) continue;
                // Vanilla's worm comes for the player anywhere, inside a location too; only
                // a peer still loading has no body to hunt.
                if (!net.IsPeerReadyForGameplay(proxy.PlayerId)) continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive) continue;
                _bodies.Add(new Body { Pos = proxy.transform.position, Attack = proxy.transform });
            }
        }

        private static bool HostEligible(Player host)
        {
            if (host.ignoreNightSickness) return false;
            if (DeathStateTracker.LocalNightDeath) return false;
            if (host.effects != null && host.effects.hasEffectType(CharacterEffectType.shadowWard))
                return false;
            CharBase cb = host.GetComponent<CharBase>();
            return cb == null || cb.alive;
        }
    }
}
