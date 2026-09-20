using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        public static void PlayExplosionSound(Vector3 pos, string soundId, string objectName = null, Explodes target = null)
        {
            string id = ResolveExplosionSoundId(soundId, objectName, target);
            if (string.IsNullOrEmpty(id))
                return;

            // Distance cull (same budget as other world SFX).
            if (!LocalAudioService.IsNearListener(pos, LocalAudioService.DefaultMaxAudioDistance))
                return;

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                // Positional 3D play with no parent, matching vanilla explode().
                AudioObject ao = AudioController.Play(id, pos, null, 1f);
                if (ao == null && id == "mushroom_explode_01")
                {
                    // Alternate clip id seen in decompiled assets.
                    ao = AudioController.Play("expObj_mushroom_01", pos, null, 1f);
                    if (ao != null) id = "expObj_mushroom_01";
                }
                if (ao != null && ao.primaryAudioSource != null)
                    ao.primaryAudioSource.spatialBlend = 1f;

                ModRuntime.LegacyInfo("[ExplosionSound] play '" + id + "' at " + pos
                    + (ao != null ? " ok" : " (AudioController returned null)"));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ExplosionSound] failed: " + ex.Message);
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prev);
            }
        }

        public static void TriggerExplosion(Vector3 pos, string objectName, bool flaming = false, string soundId = null)
        {
            int nearbyN = OverlapNear(pos, 1.5f);
            Explodes target = null;
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null) continue;
                Explodes expl = _overlap3D[i].GetComponentInParent<Explodes>();
                if (expl != null)
                {
                    target = expl;
                    break;
                }
            }

            if (target == null && !string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null)
                    target = named.GetComponent<Explodes>();
            }

            if (target != null)
            {
                ModRuntime.LegacyInfo("[ExplosionTrigger] activating " + target.name + " at " + pos + " flaming=" + flaming);
                _suppressBroadcast = true;
                try
                {
                    if (flaming)
                        target.onActivate(true);
                    else
                        target.onActivate();
                }
                finally { _suppressBroadcast = false; }
                // explode() plays explodeSound when effect is set. If effect is null,
                // Vanilla skips sound, so play the message or fallback audio.
                if (target.effect == null)
                    PlayExplosionSound(pos, soundId, objectName, target);
                // Proxy damage handled by ExplosionFriendlyFirePatch.Postfix on Explodes.explode()
            }
            else
            {
                ModRuntime.Log?.LogWarning("[ExplosionTrigger] no Explodes found at " + pos + " name=" + objectName);
                // Object may already be destroyed or out of range; still play the host's boom.
                PlayExplosionSound(pos, soundId, objectName, null);
            }
        }

        /// <summary>
        /// Client-side explosion VFX without damage: mirrors vanilla onActivate visual half
        /// (spawnObjects + explosionPrefab + sound + optional destroy), never explode().
        /// Host ExplosionSpawnObject remains fallback when no local Explodes exists.
        /// </summary>
        public static void SpawnExplosionVisual(Vector3 pos, string objectName, string prefabName, string soundId)
        {
            Explodes target = null;

            // Try by object name first (works for static world objects like barrels)
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null)
                    target = named.GetComponent<Explodes>();
            }

            // Fallback: search by position
            if (target == null)
            {
                int nearbyN = OverlapNear(pos, 1.5f);
                for (int i = 0; i < nearbyN; i++)
                {
                    if (_overlap3D[i] == null) continue;
                    Explodes expl = _overlap3D[i].GetComponentInParent<Explodes>();
                    if (expl != null) { target = expl; break; }
                }
            }

            // Sound is independent of visual success. Play first so already-activated or
            // destroyed mushrooms still boom on peers.
            PlayExplosionSound(pos, soundId, objectName, target);

            if (target != null)
            {
                // Don't re-spawn VFX if already activated on this side
                try
                {
                    bool activated = (bool)Traverse.Create(target).Field("activated").GetValue();
                    if (activated)
                    {
                        ModRuntime.LegacyInfo("[ExplosionVisual] " + target.name + " already activated, VFX skip (sound already played)");
                        return;
                    }
                }
                catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }

                try
                {
                    // Vanilla order: activated first so re-entry / ExplosionSpawnObject won't
                    // run a second full onActivate path against this component.
                    Traverse.Create(target).Field("activated").SetValue(true);

                    // 1) Secondary debris/FX (mushroom white spawnObject)
                    if (target.spawnObject != null)
                    {
                        Traverse.Create(target).Method("spawnObjects").GetValue();
                        ModRuntime.LegacyInfo("[ExplosionVisual] spawnObjects() for " + target.name + " at " + pos);
                    }

                    // 2) Main boom VFX
                    if (target.explosionPrefab != null)
                    {
                        Core.AddPrefab(target.explosionPrefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo("[ExplosionVisual] spawned local prefab " + target.explosionPrefab.name + " at " + pos);
                    }

                    // Dedupe host ExplosionSpawnObject for the secondaries we just spawned
                    DWMPHorde.Patches.ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);

                    // 3) Match vanilla destroy without calling explode() (no double damage)
                    if (target.destroyOnExplode && target.gameObject != null)
                        target.gameObject.DestroyMe();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ExplosionVisual] failed: " + ex.Message);
                }

                return;
            }

            // Fallback: no local Explodes (already destroyed / never loaded).
            ModRuntime.LegacyInfo("[ExplosionVisual] no local Explodes for \"" + objectName + "\", using message data");

            if (!string.IsNullOrEmpty(prefabName))
            {
                try
                {
                    string[] prefixes = { "", "Items/", "FX/", "Environment/", "Particles/", "Dummies/", "Fire/", "Weapons/" };
                    UnityEngine.Object prefab = null;
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        prefab = Resources.Load("Prefabs/" + prefixes[i] + prefabName);
                        if (prefab != null) break;
                    }
                    // Also try particles/mushroom_explode style paths from ResourceManager.
                    if (prefab == null)
                        prefab = Resources.Load("Prefabs/Particles/" + prefabName);
                    if (prefab != null)
                    {
                        Core.AddPrefab(prefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo("[ExplosionVisual] fallback prefab " + prefabName + " at " + pos);
                    }
                    else
                    {
                        Core.AddPrefab(prefabName, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                    }
                    DWMPHorde.Patches.ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ExplosionVisual] fallback prefab failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Spawns a "Items/GasolineTrail" prefab at the given world position.
        /// Used when the remote peer reports a gasoline pour. Nest-safe apply flag
        /// (IgniteGasAtPos may call this while already applying).
        /// </summary>
        public static void SpawnGasTrail(Vector3 pos)
        {
            // Dedupe: slightly wider than host scatter step to avoid double puddles under jitter.
            if (FindFlammableLiquidNear(pos, 1.15f) != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[GasTrail] skip spawn — liquid already near " + pos);
                return;
            }

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                // Explicit flag so client GasolineTrail Prefix allows this network apply path.
                GameObject go = Core.AddPrefab("Items/GasolineTrail", pos, Quaternion.Euler(90f, 0f, 0f), Core.ItemContainer);
                if (go != null)
                {
                    Core.addToSaveable(go, isDynamic: true);
                    ModRuntime.LegacyInfo("[GasTrail] spawned at " + pos);
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[GasTrail] Core.AddPrefab returned null at " + pos);
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        /// <summary>
        /// Finds a Liquid component (gasoline puddle) near the given position and ignites it.
        /// Used when the remote peer reports a gasoline ignition event.
        /// </summary>
        public static void IgniteGasAtPos(Vector3 pos)
        {
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                Liquid liquid = FindFlammableLiquidNear(pos, 2.25f);
                if (liquid != null)
                {
                    if (!liquid.burning)
                    {
                        liquid.startBurning();
                        ModRuntime.LegacyInfo("[GasIgnite] ignited " + liquid.name + " at " + pos);
                    }
                    return;
                }

                // If packet reordering hid the trail, spawn it before igniting under the apply flag.
                SpawnGasTrail(pos);
                liquid = FindFlammableLiquidNear(pos, 2.25f);
                if (liquid != null && !liquid.burning)
                {
                    liquid.startBurning();
                    ModRuntime.LegacyInfo("[GasIgnite] spawned+ignited trail at " + pos);
                    return;
                }
                ModRuntime.Log?.LogWarning("[GasIgnite] no flammable Liquid found at " + pos + " (even after spawning)");
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        private static Liquid FindFlammableLiquidNear(Vector3 pos, float radius)
        {
            int nearbyN = OverlapNear(pos, radius);
            Liquid best = null;
            float bestD = radius + 1f;
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null) continue;
                Liquid liquid = _overlap3D[i].GetComponent<Liquid>();
                if (liquid == null) liquid = _overlap3D[i].GetComponentInParent<Liquid>();
                if (liquid == null || !liquid.flammable) continue;
                float d = Vector3.Distance(liquid.transform.position, pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = liquid;
                }
            }
            return best;
        }
    }
}
