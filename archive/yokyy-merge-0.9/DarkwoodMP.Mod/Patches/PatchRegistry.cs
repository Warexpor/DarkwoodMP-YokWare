using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Registry for all Harmony patches. Resolves game types at runtime and applies patches.
/// All patch targets are verified against Darkwood's Assembly-CSharp
/// (Player, Door, Character, Controller, Inventory, InteractiveItem).
/// </summary>
public class PatchRegistry
{
    private readonly List<(HarmonyLib.Harmony harmony, MethodInfo method)> _patched = new();
    private readonly List<HarmonyLib.Harmony> _harmonies = new();
    private readonly HashSet<string> _applied = new();

    public void ApplyPatches(bool isHost = false)
    {
        // Send-side gating (e.g. only the host broadcasts day/night) happens
        // inside the individual patches; everyone gets the same set applied.
        // v0.7: host-authoritative enemy sync (registry + client AI disable)
        ApplyDynamic<CharacterRegistry_Patch>();
        ApplyDynamic<ClientAIDisable_Patch>();

        ApplyDynamic<Controller_Time_Patch>();
        ApplyDynamic<Door_Patch>();
        ApplyDynamic<PlayerHealth_Patch>();
        ApplyDynamic<CharDamage_Patch>();
        ApplyDynamic<Inventory_Patch>();
        ApplyDynamic<InteractiveItem_Patch>();
        ApplyDynamic<ItemSwitch_Patch>();
        ApplyDynamic<GeneratorFuel_Patch>();
        ApplyDynamic<ItemDrag_Patch>();
        ApplyDynamic<PlayerDrop_Patch>();
        ApplyDynamic<ItemActive_Patch>();
        ApplyDynamic<Throw_Patch>();
        ApplyDynamic<Build_Patch>();
        ApplyDynamic<Trigger_Patch>();
        ApplyDynamic<Barricade_Patch>();
        ApplyDynamic<Gasoline_Patch>();
        ApplyDynamic<PvpHit_Patch>();
        ApplyDynamic<ItemPickup_Patch>();
        ApplyDynamic<Disarm_Patch>();
        ApplyDynamic<Dream_Patch>();
        ApplyDynamic<Flags_Patch>();
        ApplyDynamic<Container_Patch>();
        ApplyDynamic<Workbench_Patch>();
        ApplyDynamic<Journal_Patch>();
        ApplyDynamic<JournalRef_Patch>();

        // v0.4 + gap G: reliable EntitySpawn for dynamic spawns; EntityState for motion
        ApplyDynamic<EnemySpawn_Patch>();
        ApplyDynamic<Burn_Patch>();
        ApplyDynamic<ExplosionSpawn_Patch>();
        ApplyDynamic<GameEvents_Patch>();
        ApplyDynamic<EventTriggers_Patch>();
        ApplyDynamic<DialogOutcome_Patch>();
        ApplyDynamic<Lock_Patch>();
        ApplyDynamic<SiegeDamage_Patch>();
        ApplyDynamic<Trader_Patch>();
        ApplyDynamic<Station_Patch>();
        ApplyDynamic<Porter_Patch>();
        ApplyDynamic<Sleep_Patch>();
        ApplyDynamic<WorkbenchLevel_Patch>();

        // v0.5: co-op death, chapter notice
        ApplyDynamic<Death_Patch>();
        ApplyDynamic<Chapter_Patch>();
        // YokWare Branch Phase 4: night scenario identity
        ApplyDynamic<Scenario_Patch>();
        // Phase 6: pause suppress, loot scale, epilogue, examine
        ApplyDynamic<PauseSuppression_Patch>();
        ApplyDynamic<LootScale_Patch>();
        ApplyDynamic<Epilogue_Patch>();
        ApplyDynamic<Examine_Patch>();
        ApplyDynamic<Infection_Patch>();
        ApplyDynamic<NamedNpcScale_Patch>();
        ApplyDynamic<EntitySound_Patch>();
        ApplyDynamic<DreamAudio_Patch>();
        // Full-merge residual domains
        ApplyDynamic<Location_Patch>();
        ApplyDynamic<Vault_Patch>();
        ApplyDynamic<Cutscene_Patch>();
        ApplyDynamic<DreamDoor_Patch>();
        ApplyDynamic<WorldHarvest_Patch>();
        ApplyDynamic<MorningRep_Patch>();
        ApplyDynamic<Reputation_Patch>();
        ApplyDynamic<Hideout_Patch>();
        ApplyDynamic<Compressor_Patch>();
        ApplyDynamic<MapSync_Patch>();
        ApplyDynamic<WorldMelee_Patch>();
        ApplyDynamic<ScenarioEvent_Patch>();
        ApplyDynamic<PlayerEffect_Patch>();
        ApplyDynamic<SpectatorCulling_Patch>();

        // v0.7: World NPC dialogue node/consumed state + save-on-authority-beat
        ApplyDynamic<Dialogue_Patch>();
        ApplyDynamic<Save_Patch>();

        // v0.7: exclusive interaction lock (one player per container/NPC/workbench)
        ApplyDynamic<InteractionLock_Patch>();

        // v0.6: death loot drop (audit find)
        ApplyDynamic<DeathDrop_Patch>();

        // v0.6.2: ranged firearm FX (ported from the BepInEx mod) - cosmetic
        // muzzle flash + bullet/blood impact splats. Damage already synced.
        ApplyDynamic<WeaponFire_Patch>();
        ApplyDynamic<BulletFX_Patch>();
        ApplyDynamic<PlayerNoise_Patch>();
        ApplyDynamic<PlayerAudio_Patch>();
        ApplyDynamic<Flare_Patch>();
        ApplyDynamic<Weather_Patch>();

        ModLogger.Msg("[PatchRegistry] All patches applied");
    }

    private void ApplyDynamic<T>() where T : class, IPatch, new()
    {
        var patch = new T();
        var harmony = new HarmonyLib.Harmony($"com.darkwoodmp.mod.{typeof(T).Name}");
        _harmonies.Add(harmony);

        foreach (var (typeName, methodName) in patch.TargetMethods())
        {
            var key = $"{typeof(T).Name}:{typeName}.{methodName}";
            if (_applied.Contains(key)) continue;

            var gameType = GameTypes.GetType(typeName);
            if (gameType == null)
            {
                ModLogger.Warning($"[PatchRegistry] {typeName} not found, skipping {typeof(T).Name}");
                continue;
            }

            // Manual lookup: GetMethod(name) throws on ambiguous overloads
            MethodInfo method = null;
            foreach (var m in gameType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name == methodName) { method = m; break; }
            }
            if (method == null)
            {
                ModLogger.Warning($"[PatchRegistry] {typeName}.{methodName} not found");
                continue;
            }

            try
            {
                var (appliedHarmony, appliedMethod) = patch.Apply(harmony, method);
                _patched.Add((appliedHarmony, appliedMethod));
                _applied.Add(key);
                ModLogger.Msg($"[PatchRegistry] Applied {typeof(T).Name} -> {typeName}.{methodName}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[PatchRegistry] Failed to patch {typeName}.{methodName}: {ex.Message}");
            }
        }
    }

    public void RemovePatches()
    {
        // UnpatchSelf removes EVERYTHING each per-class Harmony instance applied,
        // including extra overloads a patch registered itself (e.g. Flags_Patch
        // patches both setFlag overloads) - the _patched record only knows the
        // first one, which used to leak patches across reconnects.
        foreach (var h in _harmonies)
        {
            try
            {
                h.UnpatchSelf();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[PatchRegistry] Unpatch failed: {ex.Message}");
            }
        }
        _harmonies.Clear();
        _patched.Clear();
        _applied.Clear();
        ModLogger.Msg("[PatchRegistry] All patches removed");
    }
}

/// <summary>
/// Interface for patches that can target multiple methods dynamically.
/// </summary>
public interface IPatch
{
    /// <summary>
    /// Apply patches for a single target method. Returns the Harmony instance and patched method.
    /// </summary>
    (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target);

    /// <summary>
    /// Return list of (typeName, methodName) pairs for multi-method patches.
    /// </summary>
    IEnumerable<(string typeName, string methodName)> TargetMethods();
}
