using System;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Trap / door / generator locate + apply helpers (split from Apply for ownership).
    /// </summary>
    public static partial class WorldPhysicsSyncService
    {
        /// <summary>
        /// Finds a trap by position using the local overlap query. This avoids
        /// scene-wide searches on the packet path.
        /// </summary>
        internal static GameObject FindTrapByPos(Vector3 pos, string objectName = null)
        {
            GameObject hit = FindTrapInSphere(pos, 1.5f);
            if (hit != null) return hit;
            hit = FindTrapInSphere(pos, 8f);
            if (hit != null) return hit;
            hit = FindTrapInSphere(pos, 20f);
            if (hit != null) return hit;

            // Optional name, used only when an instance is already active.
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null && TrapNetworkId.IsWorldTrap(named) && HasTrapField(named)
                    && (named.transform.position - pos).sqrMagnitude <= 20f * 20f)
                    return named;
            }

            return null;
        }

        private static GameObject FindTrapInSphere(Vector3 pos, float radius)
        {
            int nearbyN = OverlapNear(pos, radius);
            GameObject best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null) continue;
                GameObject root = _overlap3D[i].gameObject;
                Rigidbody rb = _overlap3D[i].attachedRigidbody;
                if (rb != null) root = rb.gameObject;
                if (root == null || !TrapNetworkId.IsWorldTrap(root)) continue;
                if (!HasTrapField(root)) continue;
                float sq = (root.transform.position - pos).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = root;
                }
            }
            return best;
        }

        /// <summary>
        /// Applies the triggered/untriggered state to a trap GameObject.
        /// When triggering, plays the sound, spawns the visual prefab, alerts nearby characters,
        /// switches the sprite, and cleans up the Item/Inventory components.
        /// Skips re-triggering if the trap is already in the target state.
        /// </summary>
        /// <param name="go">The trap GameObject.</param>
        /// <param name="triggered">Whether the trap should be set to triggered.</param>
        /// <param name="silentDisarm">
        /// True = successful harvest/disarm: mirror vanilla <c>switchToTriggered</c> only
        /// (sprite/name, keep the GameObject). No explosion prefab or sound;
        /// the normal triggered path remains separate.
        /// </param>
        internal static void ApplyTrapState(GameObject go, bool triggered, bool silentDisarm = false)
        {
            if (go == null) return;

            // Silent disarm first: even if ReadTrapTriggered already true, peer may still
            // have active=true (catchable). Never early-out before forcing the belt.
            if (triggered && silentDisarm)
            {
                Trigger tSilent = go.GetComponent<Trigger>();
                if (tSilent != null)
                {
                    if (!tSilent.triggered)
                    {
                        bool prev = TraverseHack.ApplyingFromNetwork;
                        TraverseHack.ApplyingFromNetwork = true;
                        try { tSilent.switchToTriggered(); }
                        finally { TraverseHack.ApplyingFromNetwork = prev; }
                    }

                    // Belt: switchToTriggered should clear these; force so peers cannot re-catch.
                    tSilent.triggered = true;
                    tSilent.active = false;
                    tSilent.canDisarm = false;
                }
                else
                {
                    Component[] comps = go.GetComponents<Component>();
                    foreach (Component comp in comps)
                    {
                        if (comp == null) continue;
                        Traverse tr = Traverse.Create(comp);
                        if (TryWriteBool(tr, "triggered", true)) break;
                    }
                }
                // Client Item.disarm fired onDisarmed locally, where one-shots are blocked.
                // Host apply used switchToTriggered and skipped that trigger.
                if (ModRuntime.Network is LanNetworkManager trapNet && trapNet.Role == NetworkRole.Host)
                {
                    DialogHostApplyGuard.BeginWorldOnly();
                    try { Core.sendTriggerInfo(go, EventTrigger.Type.onDisarmed); }
                    catch (Exception ex)
                    {
                        ModRuntime.Log?.LogWarning("[TrapApply] onDisarmed: " + ex.Message);
                    }
                    finally { DialogHostApplyGuard.EndWorldOnly(); }
                }
                ModRuntime.LegacyInfo($"[TrapApply] silent disarm (no FX) {go.name}");
                return;
            }

            bool current = ReadTrapTriggered(go);
            if (current == triggered) return;

            Component[] allComponents = go.GetComponents<Component>();
            foreach (Component comp in allComponents)
            {
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                if (TryWriteBool(t, "triggered", triggered)) break;
                if (TryWriteBool(t, "snapped", triggered)) break;
                if (TryWriteBool(t, "sprung", triggered)) break;
                if (TryWriteBool(t, "isTriggered", triggered)) break;
            }

            if (triggered)
            {
                bool isHarvestable = go.name.ToLowerInvariant().Contains("mushroom");
                Trigger trig = go.GetComponent<Trigger>();
                Explodes expl = go.GetComponent<Explodes>();

                // Diagnostic: confirm what actually owns this mushroom's boom. World
                // Some mushrooms have a Trigger but no Explodes component,
                // their blast is the Trigger's prefabToSpawn, which the old isHarvestable
                // skip was hiding.
                if (isHarvestable && ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[TrapApply] mushroom comps name=" + go.name
                        + " hasExplodes=" + (expl != null)
                        + " prefabToSpawn=" + (trig != null && trig.prefabToSpawn != null ? trig.prefabToSpawn.name : "null")
                        + " activateSound=" + (trig != null ? trig.activateSound : ""));

                if (expl == null || !isHarvestable)
                {
                    // Generic trap VFX: activateSound + prefabToSpawn. Runs for every
                    // non-mushroom trap (unchanged) AND for mushrooms without an Explodes
                    // component. World mushrooms such as expObj_mushroom_interior_01
                    // boom is the Trigger's prefabToSpawn. The old `!isHarvestable`-only
                    // gate skipped the latter on the false assumption they used Explodes,
                    // which caused the silent snap.
                    if (trig != null && !string.IsNullOrEmpty(trig.activateSound))
                        AudioController.Play(trig.activateSound, go.transform);

                    // Spawn explosion visual prefab (blood splatter, etc.) at ground level.
                    // Dropped items have Y=-10.4 (below terrain), so raycast to find ground.
                    if (trig != null && trig.prefabToSpawn != null)
                    {
                        Vector3 spawnPos = go.transform.position + new Vector3(0f, 1f, 0f);
                        // Bump underground traps up to ground level
                        if (spawnPos.y < 0f)
                        {
                            RaycastHit hit;
                            if (Physics.Raycast(spawnPos + Vector3.up * 50f, Vector3.down, out hit, 100f))
                                spawnPos.y = hit.point.y + 0.5f;
                        }
                        try
                        {
                            Core.AddPrefab(trig.prefabToSpawn, spawnPos, Quaternion.Euler(90f, 0f, 0f), null);
                        }
                        catch (Exception ex)
                        {
                            if (ModRuntime.VerboseLogging)
                                ModRuntime.Log?.LogWarning($"[PhysicsSpawn] AddPrefab failed for {trig?.prefabToSpawn}: {ex.Message}");
                            UnityEngine.Object.Instantiate(trig.prefabToSpawn, spawnPos, Quaternion.Euler(90f, 0f, 0f));
                        }
                    }
                }
                else if (isHarvestable)
                {
                    // Explodes-based mushroom (rare): render the blast via the explosion
                    // visual path and own the end state (destroyOnExplode).
                    string prefabName = expl.explosionPrefab != null ? expl.explosionPrefab.name : "";
                    string soundId = ResolveExplosionSoundId(expl.explodeSound ?? "", go.name, expl) ?? "";
                    ModRuntime.LegacyInfo($"[TrapApply] mushroom blast VFX (Explodes) {go.name} prefab={prefabName} sound={soundId} at {go.transform.position}");
                    SpawnExplosionVisual(go.transform.position, go.name, prefabName, soundId);

                    if (trig != null && trig.alertRadius > 0f)
                        Character.alertInArea(go.transform.position, trig.alertRadius, dangerousSound: false, 1f);
                    return;
                }

                // Alert characters in radius
                if (trig != null && trig.alertRadius > 0f)
                    Character.alertInArea(go.transform.position, trig.alertRadius, dangerousSound: false, 1f);

                // Visual sprite + name change (vanilla OnAfterTrigger runs it a frame later); a trap
                // that does not stay after triggering is removed instead (end of this block).
                if (trig != null && (trig.staysAfterTriggering || trig.multipleTrigger))
                    trig.switchToTriggered();

                // Cancel disarm in progress
                Item item = go.GetComponent<Item>();
                if (item != null)
                {
                    try { item.onTriggerFire(); }
                    catch (System.Exception ex)
                    {
                        ModRuntime.Log?.LogWarning(
                            "[TrapApply] onTriggerFire failed on " + go.name + ": " + ex.Message);
                    }
                }

                // Vanilla OnAfterTrigger: drop the local player's selection of it, and the
                // custom cursor action (a sprung mimic no longer offers "Open").
                Player localSel = Player.Instance;
                if (localSel != null && localSel.selectedObject != null && localSel.selectedObject == go.transform)
                    localSel.deselectObject(force: true);
                if (trig != null && trig.removeCustomCursorActionAfterTrigger)
                    UnityEngine.Object.Destroy(go.GetComponent<CustomCursorAction>());
                if (trig != null && !trig.multipleTrigger)
                {
                    if (trig.removeSelectableAfterTrigger)
                        UnityEngine.Object.Destroy(go.GetComponent<Selectable>());
                    ObjectStages stages = go.GetComponent<ObjectStages>();
                    if (stages != null)
                        UnityEngine.Object.Destroy(stages);
                }

                // Destroy Item only if the prefab is configured to remove it
                // (if dontDestroyItemAfterTriggering is true, Item stays for hover/name display);
                // vanilla takes its BoxCollider with it.
                if (trig == null || !trig.dontDestroyItemAfterTriggering)
                {
                    if (item != null)
                        UnityEngine.Object.Destroy(item);
                    if (trig != null && !trig.multipleTrigger)
                        UnityEngine.Object.Destroy(go.GetComponent<BoxCollider>());
                }

                // Destroy Inventory only if configured to remove it
                if (trig == null || !trig.dontRemoveInventoryAfterTriggering)
                {
                    Inventory inv = go.GetComponent<Inventory>();
                    if (inv != null)
                        UnityEngine.Object.Destroy(inv);
                    if (item != null)
                        item.invItem = null;
                }

                if (trig != null && !trig.staysAfterTriggering && !trig.multipleTrigger)
                {
                    trig.active = false;
                    trig.canDisarm = false;
                    Core.RemovePooledPrefab(go.transform);
                }
            }
        }

        /// <summary>Tries to write a boolean field via Harmony Traverse without throwing.</summary>
        private static bool TryWriteBool(Traverse t, string field, bool val)
        {
            try
            {
                var f = t.Field(field);
                if (f.FieldExists())
                {
                    f.SetValue(val);
                    return true;
                }
            }
            catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }
            return false;
        }

        /// <summary>Finds a Door by position using the tracker and a scene-wide fallback.</summary>
        private static Door FindDoorByPos(Vector3 pos)
        {
            Door door = ListTracker<Door>.FindByPosition(pos);
            if (door != null && door.gameObject.activeInHierarchy)
                return door;

            // Fallback: search all Door instances to catch doors that were
            // spawned dynamically after the tracker's Awake patch ran, or
            // doors from world-grid chunks the host has loaded. Active before
            // inactive (vanilla keeps inactive twins at a live door's spot), then nearest.
            Door[] all = WorldQueryHelper.GetCachedSceneComponents<Door>();
            Door best = null;
            bool bestActive = false;
            float bestD = 2f;
            for (int i = 0; i < all.Length && i < 128; i++)
            {
                Door d = all[i];
                if (d == null) continue;
                float dist = Vector3.Distance(d.transform.position, pos);
                if (dist >= 2f) continue;
                bool active = d.gameObject.activeInHierarchy;
                if (best == null || (active && !bestActive) || (active == bestActive && dist < bestD))
                {
                    best = d;
                    bestActive = active;
                    bestD = dist;
                }
            }
            if (best != null && (bestActive || door == null))
            {
                ListTracker<Door>.Add(best);
                return best;
            }
            return door;
        }

        private static void QueuePendingGenerator(GeneratorState gs)
        {
            Vector3 p = new Vector3(gs.PosX, gs.PosY, gs.PosZ);
            for (int i = 0; i < _s.PendingGenerators.Count; i++)
            {
                GeneratorState q = _s.PendingGenerators[i];
                if ((new Vector3(q.PosX, q.PosY, q.PosZ) - p).sqrMagnitude < 4f)
                {
                    gs.FuelDelta += q.FuelDelta; // a client's pours add up
                    _s.PendingGenerators[i] = gs;
                    return;
                }
            }
            if (_s.PendingGenerators.Count >= 32)
            {
                _s.PendingGenerators.RemoveAt(0);
                _s.PendingGeneratorAt.RemoveAt(0);
            }
            _s.PendingGenerators.Add(gs);
            _s.PendingGeneratorAt.Add(Time.unscaledTime);
        }

        /// <summary>Apply generator states whose generator has appeared (its location spawned).</summary>
        internal static void TryFlushPendingGenerators()
        {
            if (_s.PendingGenerators.Count == 0 || Player.Instance == null || Core.loadingGame)
                return;
            if (Time.unscaledTime < _s.NextPendingGeneratorFlush)
                return;
            _s.NextPendingGeneratorFlush = Time.unscaledTime + 1f;
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                for (int i = _s.PendingGenerators.Count - 1; i >= 0; i--)
                {
                    GeneratorState gs = _s.PendingGenerators[i];
                    Generator gen = FindGeneratorByPos(new Vector3(gs.PosX, gs.PosY, gs.PosZ));
                    if (gen == null)
                    {
                        if (Time.unscaledTime - _s.PendingGeneratorAt[i] > 300f)
                        {
                            _s.PendingGenerators.RemoveAt(i);
                            _s.PendingGeneratorAt.RemoveAt(i);
                        }
                        continue;
                    }
                    if (gs.FuelDelta > 0.01f)
                        gen.addFuel(gs.FuelDelta);
                    ApplyGeneratorState(gen, gs.IsOn, gs.FuelDelta > 0.01f ? gen.fuel : gs.Fuel, gs.LowPower);
                    _s.PendingGenerators.RemoveAt(i);
                    _s.PendingGeneratorAt.RemoveAt(i);
                }
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prevNet);
            }
        }

        private static Generator FindGeneratorByPos(Vector3 pos)
        {
            Generator gen = ListTracker<Generator>.FindByPosition(pos);
            if (gen != null)
                return gen;

            // Fallback: search all loaded Generator instances to catch generators
            // that were spawned dynamically after the tracker's Start patch ran.
            Generator[] all = WorldQueryHelper.GetCachedSceneComponents<Generator>();
            for (int i = 0; i < all.Length; i++)
            {
                Generator g = all[i];
                if (g == null) continue;
                if (Vector3.Distance(g.transform.position, pos) < 2f)
                {
                    ListTracker<Generator>.Add(g);
                    return g;
                }
            }
            return null;
        }
    }
}
