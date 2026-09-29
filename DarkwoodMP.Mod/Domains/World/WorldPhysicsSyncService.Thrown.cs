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

        /// <summary>
        /// Record Flare.Start time (vanilla burn clock starts on aim when heldItem is spawned).
        /// Total light life = longevity + <see cref="FlareBurnoutFadeSec"/>.
        /// </summary>
        public static void NoteFlareBurnStart(GameObject go, float longevity)
        {
            if (go == null) return;
            float lon = longevity > 0.05f ? longevity : 3f;
            _flareBurnStarts[go.GetInstanceID()] = new FlareBurnStart
            {
                StartTime = Time.time,
                Longevity = lon
            };
        }

        /// <summary>
        /// Remaining seconds until light is fully dark (longevity + fade − elapsed).
        /// Packet LongevitySec uses this; track ExpireAt = now + (remain − fade) so fade starts on time.
        /// </summary>
        public static float GetFlareRemainingUntilDark(GameObject go, float longevityFallback = 3f)
        {
            float lon = longevityFallback > 0.05f ? longevityFallback : 3f;
            float total = lon + FlareBurnoutFadeSec;
            if (go == null)
                return total;
            if (!_flareBurnStarts.TryGetValue(go.GetInstanceID(), out FlareBurnStart b))
                return total;
            float elapsed = Time.time - b.StartTime;
            float remain = (b.Longevity + FlareBurnoutFadeSec) - elapsed;
            return Mathf.Max(0.15f, remain);
        }

        /// <summary>Seconds from now until fade should begin, given remaining-until-dark budget.</summary>
        public static float UntilFadeStart(float remainingUntilDark)
        {
            return Mathf.Max(0.05f, remainingUntilDark - FlareBurnoutFadeSec);
        }

        /// <summary>
        /// Host: track a local thrower's projectile so TickThrownLightExpiry can despawn peers
        /// (host never receives its own ThrowableSpawn).
        /// </summary>
        /// <param name="remainingUntilDark">Seconds until light is fully out (includes fade).</param>
        public static void RegisterLocalThrownLight(int throwId, GameObject go, float remainingUntilDark, string itemType)
        {
            if (go == null || throwId <= 0) return;
            ClaimFlareLifetime(go);
            float expireAt = Time.time + UntilFadeStart(remainingUntilDark);
            var track = new ThrownLightTrack
            {
                ThrowId = throwId,
                Go = go,
                ExpireAt = expireAt,
                ItemType = itemType ?? ""
            };
            // Replace existing same throwId
            for (int i = _thrownLights.Count - 1; i >= 0; i--)
            {
                if (_thrownLights[i].ThrowId == throwId)
                    _thrownLights.RemoveAt(i);
            }
            _thrownLights.Add(track);
            _thrownById[throwId] = track;
            ModRuntime.LegacyInfo("[ThrowableTrack] host local throwId=" + throwId
                + " type=" + itemType
                + " untilFade=" + (expireAt - Time.time).ToString("F2")
                + " untilDark=" + remainingUntilDark.ToString("F2"));
            // Event so Public/Support presets still see flare track (LegacyInfo is Dev-only).
            Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableTrack] host local throwId=" + throwId
                + " type=" + itemType
                + " untilFade=" + (expireAt - Time.time).ToString("F1")
                + "s untilDark=" + remainingUntilDark.ToString("F1") + "s claimedLifetime=1");
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
                if (primary.LightMaterial == null)
                    primary.LightMaterial = Resources.Load("RadialLight") as Material;
                primary.LightRadius = 650f;
                primary.LightIntensity = 1f;
                primary.LightColor = new Color(1f, 0.5f, 0.1f);
                ModRuntime.LegacyInfo("[ThrowableSpawn] added fallback Light2D for " + itemType);
            }

            if (!primary.gameObject.activeSelf)
                primary.gameObject.SetActive(true);
            // Match vanilla prefab: lightsPlayer must draw the radial, but only once.
            primary.lightsPlayer = true;
            primary.updateGraph = true;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && !ctrl.logicLights.Contains(primary))
                ctrl.logicLights.Add(primary);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[ThrowableSpawn] flare light ok " + itemType
                    + " radius=" + primary.LightRadius
                    + " intensity=" + primary.LightIntensity);
        }

        internal static List<GameObject> GetKnownTrapsSnapshot()
        {
            var list = new List<GameObject>(_knownTraps.Count);
            foreach (var go in _knownTraps.Values)
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
