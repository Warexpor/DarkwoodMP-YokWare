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
        public static bool IsInFlightThrownItem(GameObject go)
        {
            if (go == null) return false;
            ThrownItem ti = go.GetComponent<ThrownItem>();
            return ti != null && ti.thrown && !ti.onGround;
        }

        /// <summary>
        /// A player's throw (this player's own, or a peer's copy spawned at that peer's stand-in).
        /// Every peer flies its own copy of it and vanilla <c>onCollide</c> lands it there: the
        /// blast look and sound, the collide sound and the self-destroy happen on each copy.
        /// Only the host's copy deals damage and lays the fire (its secondaries are sent).
        /// </summary>
        internal static bool IsPlayerThrowCopy(ThrownItem ti)
        {
            if (ti == null || ti.objectThatSpawnedMe == null)
                return false;
            Transform by = ti.objectThatSpawnedMe;
            Player local = Player.Instance;
            if (local != null && (by == local.transform || by == local._transform))
                return true;
            return by.GetComponentInParent<RemotePlayerProxy>() != null;
        }

        private static readonly HashSet<string> _throwableFactsLogged = new HashSet<string>(StringComparer.Ordinal); // process-scoped: log-once set

        /// <summary>
        /// Once per item type: the prefab's own sound and burn fields (flight loop,
        /// landing sound, blast sound, burn-out clock), read off a live throw. The prefabs are not
        /// in the decompile; this is what each throw's copies have to reproduce.
        /// </summary>
        internal static void LogThrowableFactsOnce(GameObject go, string itemType)
        {
            if (go == null || string.IsNullOrEmpty(itemType) || !_throwableFactsLogged.Add(itemType))
                return;
            ThrownItem ti = go.GetComponent<ThrownItem>();
            ItemSounds snd = go.GetComponent<ItemSounds>();
            Explodes ex = go.GetComponent<Explodes>();
            ModLog.Event(LogCat.World, "[ThrowableFacts] " + itemType
                + (ti != null
                    ? " flaming=" + ti.flaming + " burnTime=" + ti.burnTime.ToString("F1")
                      + " destroyOnBurnOut=" + ti.destroyOnBurnOut + " burntSprite=" + ti.switchSpriteOnBurnOut
                      + " destroyOnLand=" + ti.destroyOnLand + " collideSound=" + ti.collideSound
                    : " noThrownItem")
                + (snd != null
                    ? " movingSound=" + snd.movingSound + " loopSound=" + snd.loopSound
                      + " startSound=" + snd.startSound + " playOnSpawn=" + snd.playOnSpawn
                    : " noItemSounds")
                + (ex != null
                    ? " explodeSound=" + ex.explodeSound + " spawnObject=" + (ex.spawnObject != null ? ex.spawnObject.name : "-")
                      + " destroyOnExplode=" + ex.destroyOnExplode
                    : "")
                + " audioChildren=" + go.GetComponentsInChildren<AudioObject>(true).Length);
        }

        /// <summary>
        /// Marks a thrown GO as FX-only (client own throw + peer visualOnly copies).
        /// Host combat copy from <see cref="SpawnThrownItem"/> is never muted.
        /// </summary>
        public static bool IsMutedThrownFx(GameObject go)
        {
            return go != null && go.GetComponent<MutedThrownFxMarker>() != null;
        }

        /// <summary>
        /// Strips combat + world-mutation secondaries from a thrown projectile so it can still
        /// fly and play the main explosion VFX while the host alone applies damage, gas-trail
        /// scatter, and fire. Used for client remote copies and the client's own throw.
        ///
        /// Critical for gasBomb/molotov: vanilla <c>spawnObjects()</c> uses random offsets,
        /// if both peers scatter, flame cover is not 1:1 and the client looks "wild".
        /// Nulling <c>spawnObject</c> lets host <c>ExplosionSpawnObject</c> / GasTrail apply
        /// the authoritative puddle positions (see ExplosionSpawnRecv skip when local still
        /// has spawnObject).
        ///
        /// Also disables FX stick-into-char / land-spawn so only the host combat
        /// copy can invent inventory grants; host onCollide despawn fans WOR to
        /// clear pickable peer FX ghosts (see ThrownItemCombatDespawnSyncPatch).
        /// </summary>
        public static void MuteThrownCombat(GameObject go)
        {
            if (go == null) return;

            if (go.GetComponent<MutedThrownFxMarker>() == null)
                go.AddComponent<MutedThrownFxMarker>();

            ThrownItem ti = go.GetComponent<ThrownItem>();
            if (ti != null)
            {
                ti.damage = 0;
                // Stick-into-CharBase.addItem would invent a peer-only inventory copy.
                // Wall-stick parenting is also host-owned (combat SpawnThrownItem).
                ti.stickOnCollide = false;
                ti.prefabToSpawnOnLand = null;
                ti.dontSaveSpawnedPrefab = true;
            }

            Explodes expl = go.GetComponent<Explodes>();
            if (expl != null)
            {
                expl.damage = 0f;
                expl.affectsPlayer = false;
                expl.force = 0f;
                expl.hasEffect = false;
                // Keep explosionPrefab for boom VFX; kill random secondary scatter (gas puddles).
                expl.spawnObject = null;
                expl.objectAmount = 0;
            }
        }

        /// <summary>True when packet is a grounded late-join / re-sync (no flight).</summary>
        public static bool IsGroundedThrownSpawn(ThrowableSpawnMessage msg)
        {
            float velSq = msg.VelX * msg.VelX + msg.VelY * msg.VelY + msg.VelZ * msg.VelZ;
            return velSq < 0.01f && msg.Distance < 1f && !msg.HasLandTarget;
        }

        /// <summary>
        /// Ensures thrown flares have exactly one active Light2D (prefab Flare.light2D).
        /// Extra lights cause client-only "glare" halos host never had.
        /// </summary>
        private static void EnsureThrownFlareLight(GameObject go, string itemType)
        {
            if (go == null || string.IsNullOrEmpty(itemType)) return;
            if (itemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;

            Flare flare = go.GetComponentInChildren<Flare>(true);
            Light2D primary = flare != null ? flare.light2D : null;
            if (primary == null)
                primary = go.GetComponentInChildren<Light2D>(true);

            // Disable every other Light2D so we don't stack meshes / halos.
            foreach (var lt in go.GetComponentsInChildren<Light2D>(true))
            {
                if (primary == null)
                {
                    primary = lt;
                    continue;
                }
                if (lt != primary)
                {
                    lt.lightsPlayer = false;
                    lt.updateGraph = false;
                    try { lt.unlightGraphNodes(); } catch { /* ignore */ }
                    lt.gameObject.SetActive(false);
                }
            }

            if (primary == null)
            {
                var lightGo = new GameObject("ThrownFlareLight");
                lightGo.transform.SetParent(go.transform, false);
                lightGo.transform.localPosition = Vector3.zero;
                primary = lightGo.AddComponent<Light2D>();
                // A code-made light sits on Default, which the light camera never draws.
                PlayerLightFxAmbientNetHandlers.PutOnLightLayer(lightGo);
                if (primary.LightMaterial == null)
                    primary.LightMaterial = Resources.Load("RadialLight") as Material;
                primary.LightRadius = 650f;
                primary.LightIntensity = 1f;
                primary.LightColor = new Color(1f, 0.5f, 0.1f);
                ModRuntime.LegacyInfo($"[ThrowableSpawn] added fallback Light2D for {itemType}");
            }

            if (!primary.gameObject.activeSelf)
                primary.gameObject.SetActive(true);
            // Match vanilla prefab: lightsPlayer must draw the radial, but only once. Light2D.Start
            // (next frame, on a fresh copy) adds it to the logic lights itself; adding it here too
            // listed it twice.
            primary.lightsPlayer = true;
            primary.updateGraph = true;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[ThrowableSpawn] flare light ok {itemType} radius={primary.LightRadius} intensity={primary.LightIntensity}");
        }

        internal static List<GameObject> GetKnownTrapsSnapshot()
        {
            var list = new List<GameObject>(_s.KnownTraps.Count);
            foreach (var go in _s.KnownTraps.Values)
                if (go != null) list.Add(go);
            return list;
        }

        /// <summary>
        /// Resolve explode audio ID for network apply. Mushrooms often rely on
        /// explodeSound or clip ids like mushroom_explode_01 (Assets/AudioClip).
        /// </summary>
        public static string ResolveExplosionSoundId(string soundId, string objectName, Explodes target = null)
        {
            if (!string.IsNullOrEmpty(soundId))
                return soundId;
            if (target != null && !string.IsNullOrEmpty(target.explodeSound))
                return target.explodeSound;

            string n = (objectName ?? "").ToLowerInvariant();
            if (n.Contains("mushroom") || n.Contains("expobj_m") || n.Contains("exp_mushroom")
                || n.Contains("exp_bio") || n.Contains("nightmushroom"))
            {
                // Vanilla AudioToolkit ids matching exported clips.
                return "mushroom_explode_01";
            }
            return null;
        }

        /// <summary>
        /// Always-play explosion one-shot for peers. Independent of Explodes lifetime /
        /// already-activated state so mushrooms don't go silent when the object is gone.
        /// </summary>
    }
}
