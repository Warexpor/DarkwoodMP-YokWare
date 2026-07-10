using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Makes Darkwood's procedural world generation deterministic.
///
/// All of WorldGenerator's layout decisions go through UnityEngine.Random
/// (verified in IL: createNextWorldChunk, populateNextWorldChunk,
/// getLandmarkPos, getRandomPlayerSpawn all call Random.Range). By re-seeding
/// the RNG at the start of every generation step with (seed + step counter),
/// the world becomes identical for everyone using the same seed - and immune
/// to frame-timing differences, because each step gets its own seed anchor
/// instead of sharing one long random stream.
///
/// Applied at mod startup (before any world is generated), NOT on connect:
/// players start a NEW GAME with the same configured seed, then play together.
/// </summary>
public static class WorldGenSeed_Patch
{
    private static readonly HarmonyLib.Harmony _harmony = new("com.darkwoodmp.mod.worldseed");
    private static int _baseSeed;
    private static int _counter;
    private static bool _applied;

    /// <summary>Configured world seed (0 = not set). Other patches derive their anchors from this.</summary>
    internal static int BaseSeed => _baseSeed;

    /// <summary>True once the deterministic-generation patches are installed.</summary>
    internal static bool IsArmed => _applied;

    /// <summary>
    /// Adopt a world seed received over the network from the time-authority.
    /// This is what keeps the ONGOING deterministic rolls (daily crate/corpse/
    /// beartrap spawns, weather, seeded traders) in sync when the two machines
    /// have different — or unset — configured WorldSeeds. It is essential for the
    /// world-DOWNLOAD path: the transfer copies the host's static save (so the
    /// CURRENT day's objects match), but every future day re-rolls those objects
    /// from the seed, and without adopting the host's seed the client rolls its
    /// own local seed (or none) and the objects diverge again.
    ///
    /// If the local seed patches were never armed (config WorldSeed = 0, common
    /// when relying on the world download), this installs them now — late
    /// installation is harmless: the one-time generateWorld anchors simply never
    /// fire again (the world is already loaded), while the per-day spawn anchors
    /// take effect on the next day boundary.
    /// </summary>
    internal static void AdoptNetworkSeed(int seed)
    {
        if (seed == 0) return;
        if (!_applied)
        {
            ModLogger.Msg($"[WorldGenSeed] Adopting authority seed {seed} (local seed was unset) - arming deterministic spawns");
            Apply(seed);
            return;
        }
        if (_baseSeed != seed)
        {
            ModLogger.Msg($"[WorldGenSeed] Adopting authority seed {seed} (was {_baseSeed}) - future daily spawns will match the host");
            _baseSeed = seed;
        }
    }

    /// <summary>
    /// The seed the time-authority should publish so every machine's ongoing
    /// deterministic rolls agree. If a WorldSeed is configured, that is it. If it
    /// is 0 (host relied on the world download and never set one), a non-zero seed
    /// is minted once and the deterministic patches are armed with it, so the
    /// authority's OWN daily spawns become deterministic and shareable. Returns 0
    /// only if there is genuinely nothing to publish (should not happen once
    /// called on an authority in a live world).
    /// </summary>
    internal static int GetPublishSeed()
    {
        if (_baseSeed != 0) return _baseSeed;
        // Mint a stable-for-this-session non-zero seed and arm with it. The value
        // is shared over the wire, so it does not need to be reproducible across
        // machines independently - only identical once transmitted.
        var minted = Environment.TickCount ^ (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
        if (minted == 0) minted = 1;
        ModLogger.Msg($"[WorldGenSeed] No configured WorldSeed - minting {minted} for daily-spawn sync");
        Apply(minted);
        return _baseSeed;
    }

    /// <summary>Current in-game day (0 if the world is not up yet).</summary>
    internal static int CurrentDay => GetCurrentDay();

    // Synchronous generation steps - each gets a deterministic seed anchor.
    // (Coroutines are anchored via the synchronous methods they call.)
    // NOTE: createNextWorldChunk is NOT here - it decides each chunk's BIOME and
    // is anchored by chunk GRID COORDS instead (ChunkCreateSeedPrefix), which is
    // immune to the shared-counter drift that made whole worlds differ.
    private static readonly string[] _anchorMethods =
    {
        "populateNextWorldChunk",
        "spawnMiscObjects",
        "getLandmarkPos",
        "getRandomPlayerSpawn",
        "assignMustSpawnLocations",
        "respawnAllEnemies",
    };

    public static void Apply(int seed)
    {
        if (_applied) return;
        _baseSeed = seed;

        var generatorType = GameTypes.GetType("WorldGenerator");
        if (generatorType == null)
        {
            ModLogger.Warning("[WorldGenSeed] WorldGenerator type not found - seeding disabled");
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // Entry point: reset the step counter and seed the base state
        var generate = generatorType.GetMethod("generateWorld", flags);
        if (generate == null)
        {
            ModLogger.Warning("[WorldGenSeed] WorldGenerator.generateWorld not found - seeding disabled");
            return;
        }
        _harmony.Patch(generate, prefix: new HarmonyMethod(typeof(WorldGenSeed_Patch)
            .GetMethod(nameof(GenerateWorldPrefix), BindingFlags.Static | BindingFlags.NonPublic)));

        // Daily random spawns (crates & other random objects): Controller.startDay
        // calls spawnRandomObjects every in-game day with UNSEEDED RNG, so the
        // spawned objects differ per machine ("crate only visible on host").
        // Anchor them to (seed ^ day) - the day number is synced, so both
        // machines roll identical spawns. A counter anchor would NOT work here:
        // it drifts when one player restarts their game mid-save.
        var dailyPrefix = new HarmonyMethod(typeof(WorldGenSeed_Patch)
            .GetMethod(nameof(DailySpawnPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        foreach (var name in new[] { "spawnRandomObjects", "spawnNightObjects" })
        {
            var method = generatorType.GetMethod(name, flags);
            if (method == null) continue;
            _harmony.Patch(method, prefix: dailyPrefix);
        }

        // v0.5.1 (playtest fix): the daily random objects (crates, corpses,
        // beartraps, ...) are spawned CHUNK BY CHUNK from one shared RNG
        // stream and a global "unspawned" pool - a single divergence (e.g. a
        // physics-dependent placement retry near moved furniture) cascaded
        // into every later chunk, scattering different objects on each
        // machine. Re-seeding per chunk (seed ^ day ^ chunk position)
        // contains any drift to that one chunk.
        var chunkType = GameTypes.GetType("WorldChunk");
        if (chunkType != null)
        {
            var chunkPrefix = new HarmonyMethod(typeof(WorldGenSeed_Patch)
                .GetMethod(nameof(ChunkSpawnSeedPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var name in new[] { "spawnRandomObjects", "spawnNightObjects" })
            {
                var method = chunkType.GetMethod(name, flags);
                if (method != null) _harmony.Patch(method, prefix: chunkPrefix);
            }

            // Playtest fix: the INITIAL world layout - ground sprites + trees
            // (WorldChunk.populate -> createGroundSprites, both synchronous) and
            // scattered objects (WorldChunk.spawnMiscObjects -> distributeObjects)
            // - was only anchored at the WorldGenerator PHASE level (one reseed
            // for ALL chunks). Generation is a multi-frame coroutine, so per-chunk
            // Random drift (or ANY non-worldgen Random call between frames)
            // cascaded into every later chunk from the shared stream -> different
            // trees/objects on each machine even with the same seed. Anchor each
            // chunk's content by its POSITION so it is a pure function of
            // (seed, chunkPos), independent of generation order/timing. This is
            // the same per-chunk trick v0.5.1 applied to the DAILY spawns, now
            // extended to the one-time world generation (both methods are
            // non-recursive and run fully synchronously per chunk).
            var chunkGenPrefix = new HarmonyMethod(typeof(WorldGenSeed_Patch)
                .GetMethod(nameof(ChunkGenSeedPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var name in new[] { "populate", "spawnMiscObjects" })
            {
                var method = chunkType.GetMethod(name, flags);
                if (method != null) _harmony.Patch(method, prefix: chunkGenPrefix);
            }
        }

        // Weather: Rain.onNewDay / setUpNextRain roll "does it rain today"
        // with unseeded RNG - one player in a storm, the other in sunshine.
        // Same day-anchored trick, own salt so the stream differs from the
        // object-spawn stream.
        var rainType = GameTypes.GetType("Rain");
        if (rainType != null)
        {
            var rainPrefix = new HarmonyMethod(typeof(WorldGenSeed_Patch)
                .GetMethod(nameof(RainSeedPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var name in new[] { "onNewDay", "setUpNextRain" })
            {
                var method = rainType.GetMethod(name, flags);
                if (method != null) _harmony.Patch(method, prefix: rainPrefix);
            }
        }

        // Biome per chunk: anchor by the chunk's GRID coordinates (chunkColumn/
        // chunkRow, read from the generator at method entry) instead of the
        // shared step counter. The counter is incremented by every anchored
        // phase; if the phases ever interleave differently between machines, the
        // biome pick diverges and the ENTIRE world differs. Grid coords are a
        // deterministic function of the fixed chunk grid, so both machines pick
        // the same biome for the same chunk regardless of timing/order.
        var createChunk = generatorType.GetMethod("createNextWorldChunk", flags);
        if (createChunk != null)
            _harmony.Patch(createChunk, prefix: new HarmonyMethod(typeof(WorldGenSeed_Patch)
                .GetMethod(nameof(ChunkCreateSeedPrefix), BindingFlags.Static | BindingFlags.NonPublic)));

        // Diagnostic: after the player is spawned the world is fully built - log a
        // signature (hash of every chunk's grid position + biome). Both players
        // compare this ONE number: equal => identical world layout; different =>
        // the layout itself diverged (biome/preset), narrowing the search.
        var spawnPlayer = generatorType.GetMethod("spawnPlayer", flags);
        if (spawnPlayer != null)
            _harmony.Patch(spawnPlayer, postfix: new HarmonyMethod(typeof(WorldGenSeed_Patch)
                .GetMethod(nameof(WorldSignaturePostfix), BindingFlags.Static | BindingFlags.NonPublic)));

        var anchored = 0;
        var anchorPrefix = new HarmonyMethod(typeof(WorldGenSeed_Patch)
            .GetMethod(nameof(StepPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        foreach (var name in _anchorMethods)
        {
            MethodInfo method = null;
            foreach (var m in generatorType.GetMethods(flags))
            {
                if (m.Name == name) { method = m; break; }
            }
            if (method == null)
            {
                ModLogger.Warning($"[WorldGenSeed] WorldGenerator.{name} not found, skipping anchor");
                continue;
            }
            _harmony.Patch(method, prefix: anchorPrefix);
            anchored++;
        }

        _applied = true;
        ModLogger.Msg($"[WorldGenSeed] Deterministic world generation ACTIVE (seed {seed}, {anchored + 1} anchors)");
        ModLogger.Msg("[WorldGenSeed] Start a NEW GAME on both machines with the same seed for identical worlds");
    }

    private static void GenerateWorldPrefix()
    {
        _counter = 0;
        UnityEngine.Random.InitState(_baseSeed);
        ModLogger.Msg($"[WorldGenSeed] generateWorld seeded with {_baseSeed}");
    }

    private static void StepPrefix()
    {
        // Each step gets its own derived seed - deterministic as long as the
        // sequence of generation steps is the same on both machines
        UnityEngine.Random.InitState(_baseSeed ^ (_counter++ * 486187739));
    }

    private static void DailySpawnPrefix()
    {
        UnityEngine.Random.InitState(_baseSeed ^ (GetCurrentDay() * 486187739) ^ 0x5EED);
    }

    private static void RainSeedPrefix()
    {
        UnityEngine.Random.InitState(_baseSeed ^ (GetCurrentDay() * 486187739) ^ 0x0A17);
    }

    private static void ChunkSpawnSeedPrefix(object __instance, MethodBase __originalMethod)
    {
        if (_baseSeed == 0) return;
        if (__instance is not Component chunk) return;
        var p = chunk.transform.position;
        var salt = __originalMethod.Name == "spawnNightObjects" ? 0x11A17 : 0x0B0B;
        UnityEngine.Random.InitState(_baseSeed
            ^ (GetCurrentDay() * 486187739)
            ^ ((int)UnityEngine.Mathf.Round(p.x) * 73856093)
            ^ ((int)UnityEngine.Mathf.Round(p.z) * 19349663)
            ^ salt);
    }

    private static FieldInfo _chunkColumnField;
    private static FieldInfo _chunkRowField;

    // Biome selection anchored by the chunk's grid coords (read live from the
    // generator). At createNextWorldChunk entry, chunkColumn/chunkRow hold THIS
    // chunk's coords (they are incremented only after the biome is assigned).
    private static void ChunkCreateSeedPrefix(object __instance)
    {
        if (_baseSeed == 0 || __instance == null) return;
        if (_chunkColumnField == null)
        {
            var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _chunkColumnField = __instance.GetType().GetField("chunkColumn", f);
            _chunkRowField = __instance.GetType().GetField("chunkRow", f);
        }
        var col = _chunkColumnField?.GetValue(__instance) is int c ? c : 0;
        var row = _chunkRowField?.GetValue(__instance) is int r ? r : 0;
        UnityEngine.Random.InitState(_baseSeed ^ (col * 73856093) ^ (row * 19349663) ^ 0x0C0F);
    }

    // World layout signature: hash every chunk's grid position + biome so both
    // machines can compare a single number. Enumerated in a deterministic order
    // (sorted by position) so the hash is order-independent.
    private static FieldInfo _biomeField;
    private static void WorldSignaturePostfix()
    {
        try
        {
            var chunkType = GameTypes.GetType("WorldChunk");
            if (chunkType == null) return;
            _biomeField ??= chunkType.GetField("biome",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var entries = new List<string>();
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(chunkType))
            {
                if (obj is not Component chunk) continue;
                var p = chunk.transform.position;
                var biome = _biomeField?.GetValue(chunk);
                var biomeName = biome is UnityEngine.Object uo ? uo.name : biome?.GetType().Name ?? "null";
                entries.Add($"{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.z)}={biomeName}");
            }
            entries.Sort(StringComparer.Ordinal);

            unchecked
            {
                var hash = 2166136261u;
                foreach (var e in entries)
                    foreach (var ch in e) { hash ^= ch; hash *= 16777619u; }
                ModLogger.Msg($"[WorldGenSeed] WORLD SIGNATURE seed={_baseSeed} chunks={entries.Count} hash={hash:X8}");
                ModLogger.Msg("[WorldGenSeed] Compare this hash on both machines: equal = identical layout.");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warning($"[WorldGenSeed] Signature failed: {ex.Message}");
        }
    }

    // Initial world-generation content, anchored by chunk position (NOT the day:
    // generation happens once at new-game). Distinct salt per phase so the ground
    // and object streams never correlate.
    private static void ChunkGenSeedPrefix(object __instance, MethodBase __originalMethod)
    {
        if (_baseSeed == 0) return;
        if (__instance is not Component chunk) return;
        var p = chunk.transform.position;
        var salt = __originalMethod.Name == "spawnMiscObjects" ? 0x51DE : 0x6817;
        UnityEngine.Random.InitState(_baseSeed
            ^ ((int)UnityEngine.Mathf.Round(p.x) * 73856093)
            ^ ((int)UnityEngine.Mathf.Round(p.z) * 19349663)
            ^ salt);
    }

    private static Type _controllerType;
    private static FieldInfo _dayField;

    private static int GetCurrentDay()
    {
        try
        {
            _controllerType ??= GameTypes.GetType("Controller");
            if (_controllerType == null) return 0;
            _dayField ??= _controllerType.GetField("day",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var controller = UnityEngine.Object.FindObjectOfType(_controllerType);
            if (controller != null && _dayField?.GetValue(controller) is int day)
                return day;
        }
        catch { /* fall through to the seed-only anchor */ }
        return 0;
    }
}
