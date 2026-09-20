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
        /// Strips combat + world-mutation secondaries from a thrown projectile so it can still
        /// fly and play the main explosion VFX while the host alone applies damage, gas-trail
        /// scatter, and fire. Used for client remote copies and the client's own throw.
        ///
        /// Critical for gasBomb/molotov: vanilla <c>spawnObjects()</c> uses random offsets,
        /// if both peers scatter, flame cover is not 1:1 and the client looks "wild".
        /// Nulling <c>spawnObject</c> lets host <c>ExplosionSpawnObject</c> / GasTrail apply
        /// the authoritative puddle positions (see ExplosionSpawnRecv skip when local still
        /// has spawnObject).
        /// </summary>
        public static void MuteThrownCombat(GameObject go)
        {
            if (go == null) return;

            ThrownItem ti = go.GetComponent<ThrownItem>();
            if (ti != null)
                ti.damage = 0;

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

        public static void SpawnThrownItem(ThrowableSpawnMessage msg, Transform proxyT, bool visualOnly = false)
        {
            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] unknown item type: " + msg.ItemType);
                return;
            }

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(msg.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] no prefab for " + msg.ItemType);
                return;
            }

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null) return;

            bool grounded = IsGroundedThrownSpawn(msg);
            bool isFlareItem = !string.IsNullOrEmpty(msg.ItemType)
                && msg.ItemType.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0;
            // Vanilla throwItem: heldItem.transform.position = player.position (no Y lift).
            Vector3 throwOrigin = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Vector3 spawnPos = throwOrigin;
            GameObject go = Core.AddPrefab(prefab, spawnPos, Quaternion.Euler(90f, msg.AimY, 0f), null);
            if (go == null)
                go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.Euler(90f, msg.AimY, 0f));

            if (go == null)
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] failed to spawn " + msg.ItemType);
                return;
            }

            // Mirror ThrownItem.Awake ignorePlayerCollisions + avoid proxy / local player clips
            // that fire onCollide / fireOnCollideOnAnyCollision at spawn (molotov/match).
            Collider itemCol = go.GetComponent<Collider>();
            if (itemCol != null)
            {
                if (Player.Instance != null)
                {
                    Collider pc = Player.Instance.GetComponent<Collider>();
                    if (pc != null) Physics.IgnoreCollision(itemCol, pc);
                }
                if (proxyT != null && !grounded)
                {
                    Collider[] proxyCols = proxyT.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < proxyCols.Length; i++)
                    {
                        if (proxyCols[i] != null)
                            Physics.IgnoreCollision(itemCol, proxyCols[i]);
                    }
                }
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            ThrownItem ti = go.GetComponent<ThrownItem>();
            float distance = grounded ? 0f : Mathf.Clamp(msg.Distance, 10f, 370f);
            Vector3 vel = new Vector3(msg.VelX, msg.VelY, msg.VelZ);
            Vector3 landTarget = spawnPos;

            if (grounded)
            {
                // Late-join / re-sync: already on ground, no flight.
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.drag = 15f;
                    rb.angularDrag = 15f;
                }
                if (ti != null)
                {
                    ti.thrown = false;
                    ti.onGround = true;
                    ti.landTarget = spawnPos;
                    ti.objectThatSpawnedMe = proxyT;
                }
            }
            else
            {
                // Vanilla Player.throwItem order (decompiled):
                //  pos = player; parent=null; drag=2; dist=clamp(cursor,10,370);
                //  dir ≈ player.up (aim); landTarget = pos + dir * dist;
                //  vel = dir * dist * 2.5 (or dir * initialVelocity);
                //  thrown=true; setFallSpeed(dist); objectThatSpawnedMe = player.
                bool haveLand = msg.HasLandTarget
                    && (msg.LandX * msg.LandX + msg.LandY * msg.LandY + msg.LandZ * msg.LandZ) > 0.01f;

                Vector3 dir;
                if (haveLand)
                {
                    landTarget = new Vector3(msg.LandX, msg.LandY, msg.LandZ);
                    Vector3 toLand = landTarget - throwOrigin;
                    toLand.y = 0f;
                    float landDist = toLand.magnitude;
                    if (landDist > 1f)
                    {
                        distance = Mathf.Clamp(landDist, 10f, 370f);
                        dir = toLand / landDist;
                    }
                    else if (vel.sqrMagnitude > 0.01f)
                        dir = vel.normalized;
                    else
                        dir = Quaternion.Euler(0f, msg.AimY, 0f) * Vector3.forward;
                }
                else if (vel.sqrMagnitude > 0.01f)
                {
                    dir = vel.normalized;
                    landTarget = throwOrigin + dir * distance;
                }
                else
                {
                    dir = Quaternion.Euler(0f, msg.AimY, 0f) * Vector3.forward;
                    landTarget = throwOrigin + dir * distance;
                }

                // Vanilla throwItem force: initialVelocity==0 → dir * distance * 2.5f.
                // Always rebuild from distance + land dir so peer arc matches thrower formula.
                // (Live packet vel can disagree after drag/one-frame capture and looked "weaker".)
                float initV = ti != null ? ti.initialVelocity : 0f;
                float expectedSpeed = initV > 0f ? initV : distance * 2.5f;
                // Prefer packet speed when close to vanilla expected (thrower authority);
                // otherwise force exact formula so both sides share the same kick.
                float pktSpeed = vel.magnitude;
                if (pktSpeed < expectedSpeed * 0.85f || pktSpeed > expectedSpeed * 1.15f
                    || vel.sqrMagnitude < 1f)
                    vel = dir * expectedSpeed;
                else
                    vel = dir * pktSpeed; // keep magnitude, lock direction to land

                // Must not be kinematic. PhysicsState used to force it mid-flight
                // (now excluded). Explicit unlock so a stale lock cannot kill the arc.
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.drag = 2f; // vanilla throwItem
                    // Leave prefab angularDrag unchanged so the spin feel is preserved.
                    rb.velocity = vel;

                    float rotForce = ti != null ? ti.initialRotationForce : 225f;
                    if (rotForce == 0f)
                        rb.angularVelocity = Vector3.zero;
                    else
                        rb.AddTorque(0f, rotForce * 1000f, 0f);
                }

                if (ti != null)
                {
                    // Same field order as vanilla throwItem.
                    ti.landTarget = landTarget;
                    ti.thrown = true;
                    ti.onGround = false;
                    ti.objectThatSpawnedMe = proxyT;
                    // flyTime = distance/150; lands when near landTarget OR time > flyTime.
                    ti.setFallSpeed(distance);
                }

                // Drop any free-body interp that already latched onto this GO by name.
                RemoveObjectFromInterpolation(go);
                if (rb != null)
                    _clientKinematic.Remove(go.GetInstanceID());

                // ThrownItem.Awake schedules init() next frame and can zero or overwrite velocity.
                // Re-assert vanilla flight state after init so peer force matches thrower.
                Vector3 velHold = vel;
                Vector3 landHold = landTarget;
                float distHold = distance;
                Transform proxyHold = proxyT;
                GameObject goHold = go;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                {
                    ctrl.waitFramesAndRun(() =>
                    {
                        if (goHold == null) return;
                        Rigidbody rb2 = goHold.GetComponent<Rigidbody>();
                        ThrownItem ti2 = goHold.GetComponent<ThrownItem>();
                        if (rb2 != null)
                        {
                            rb2.isKinematic = false;
                            rb2.drag = 2f;
                            rb2.velocity = velHold;
                        }
                        if (ti2 != null)
                        {
                            ti2.landTarget = landHold;
                            ti2.thrown = true;
                            ti2.onGround = false;
                            ti2.objectThatSpawnedMe = proxyHold;
                            ti2.setFallSpeed(distHold);
                        }
                        RemoveObjectFromInterpolation(goHold);
                    }, 1);
                }
            }

            Explodes expl = go.GetComponent<Explodes>();
            if (expl != null)
                expl.objectThatSpawnedMe = proxyT;

            // Client-side / visual copies: keep trajectory + FX, host alone applies combat.
            if (visualOnly)
                MuteThrownCombat(go);

            // Thrown flare: ensure ground light is visible on peers (prefab may arrive disabled).
            EnsureThrownFlareLight(go, msg.ItemType);

            // Lifetime parity: track expire for flare lights (host despawns for all).
            // LongevitySec = remaining burn including fade, from thrower's aim-start clock.
            // Keep Flare for flicker/rotation; ClaimFlareLifetime skips waitToDie (V3/V4).
            if (isFlareItem)
            {
                ClaimFlareLifetime(go);
                // Peer spawn runs Flare.Start → tweenIntensity 1→4 (ignite pulse). Mid-life
                // throws looked over-glared vs host stick already at cruise intensity (~2).
                foreach (var fl in go.GetComponentsInChildren<Flare>(true))
                {
                    if (fl != null)
                        fl.tweenIntensity = 2f;
                }
                // LongevitySec = remaining until fully dark; expire clock starts the 2s fade.
                float untilDark = msg.LongevitySec > 0.05f ? msg.LongevitySec : (3f + FlareBurnoutFadeSec);
                int throwId = msg.ThrowId;
                var track = new ThrownLightTrack
                {
                    ThrowId = throwId,
                    Go = go,
                    ExpireAt = Time.time + UntilFadeStart(untilDark),
                    ItemType = msg.ItemType
                };
                _thrownLights.Add(track);
                if (throwId > 0)
                    _thrownById[throwId] = track;
            }

            if (!visualOnly)
                Core.addToSaveable(go, isDynamic: true);
            ModRuntime.LegacyInfo("[ThrowableSpawn] spawned " + msg.ItemType
                + " throwId=" + msg.ThrowId + " life=" + msg.LongevitySec
                + " at " + spawnPos + " aimY=" + msg.AimY + " dist=" + distance
                + " vel=" + vel.magnitude.ToString("F1")
                + " land=" + landTarget
                + " grounded=" + grounded
                + " visualOnly=" + visualOnly);
        }

        /// <summary>
        /// Host: when remaining life elapses, broadcast despawn and start 2s fade (vanilla waitToDie).
        /// All roles: advance active fades.
        /// </summary>
        public static void TickThrownLightExpiry(LanNetworkManager net)
        {
            TickThrownLightFades();

            if (net == null || !net.IsConnected) return;
            if (net.Role != NetworkRole.Host) return;
            if (_thrownLights.Count == 0) return;

            float now = Time.time;
            for (int i = _thrownLights.Count - 1; i >= 0; i--)
            {
                var t = _thrownLights[i];
                if (t.Go == null)
                {
                    if (t.ThrowId > 0) _thrownById.Remove(t.ThrowId);
                    _thrownLights.RemoveAt(i);
                    continue;
                }
                if (now < t.ExpireAt)
                    continue;

                Vector3 pos = t.Go.transform.position;
                // Broadcast first so peers fade in parallel with host.
                if (t.ThrowId > 0)
                {
                    net.SendThrowableDespawn(new ThrowableDespawnMessage
                    {
                        ThrowId = t.ThrowId,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z
                    });
                    _thrownById.Remove(t.ThrowId);
                }
                BeginThrownLightFade(t.Go, FlareBurnoutFadeSec);
                _thrownLights.RemoveAt(i);
                Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableDespawn] host expired throwId=" + t.ThrowId
                    + " type=" + t.ItemType + " pos=" + pos);
            }
        }

        public static void ApplyThrownDespawn(ThrowableDespawnMessage msg)
        {
            GameObject go = null;
            if (msg.ThrowId > 0 && _thrownById.TryGetValue(msg.ThrowId, out var track))
            {
                go = track.Go;
                _thrownById.Remove(msg.ThrowId);
            }
            if (go == null)
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Collider[] hits = Physics.OverlapSphere(pos, 3f);
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i] == null) continue;
                    GameObject root = hits[i].attachedRigidbody != null
                        ? hits[i].attachedRigidbody.gameObject
                        : hits[i].gameObject;
                    string n = root.name.ToLowerInvariant();
                    if (n.Contains("flare") || root.GetComponentInChildren<Light2D>(true) != null)
                    {
                        go = root;
                        break;
                    }
                }
            }

            for (int i = _thrownLights.Count - 1; i >= 0; i--)
            {
                if (_thrownLights[i].ThrowId == msg.ThrowId || _thrownLights[i].Go == go)
                    _thrownLights.RemoveAt(i);
            }

            if (go != null)
            {
                Logging.ModLog.Event(Logging.LogCat.World,
                    "[ThrowableDespawn] peer fade throwId=" + msg.ThrowId + " go=" + go.name);
                BeginThrownLightFade(go, FlareBurnoutFadeSec);
            }
            else
                Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableDespawn] no go for throwId=" + msg.ThrowId);
        }

        /// <summary>Vanilla waitToDie: ramp intensity to 0 over fadeSec, then kill lights/particles/lightFlare.</summary>
        public static void BeginThrownLightFade(GameObject go, float fadeSec = FlareBurnoutFadeSec,
            GameObject siblingDestroy = null)
        {
            if (go == null) return;
            // Already fading?
            for (int i = 0; i < _thrownLightFades.Count; i++)
            {
                if (_thrownLightFades[i].Go == go)
                    return;
            }

            Light2D[] lights = go.GetComponentsInChildren<Light2D>(true);
            float startI = 1f;
            if (lights != null && lights.Length > 0 && lights[0] != null)
                startI = Mathf.Max(0.01f, lights[0].LightIntensity);

            Logging.ModLog.Event(Logging.LogCat.World,
                "[ThrowableFade] begin go=" + go.name
                + " lights=" + (lights != null ? lights.Length : 0)
                + " startI=" + startI.ToString("F2")
                + " fadeSec=" + fadeSec.ToString("F1"));

            // Stop looping audio gently (vanilla AudioObject.Stop(1f)).
            try
            {
                foreach (var ao in go.GetComponentsInChildren<AudioObject>(true))
                {
                    if (ao != null)
                        ao.Stop(1f);
                }
                if (siblingDestroy != null)
                {
                    foreach (var ao in siblingDestroy.GetComponentsInChildren<AudioObject>(true))
                    {
                        if (ao != null)
                            ao.Stop(1f);
                    }
                }
            }
            catch { /* optional */ }

            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            if (siblingDestroy != null)
            {
                foreach (var ps in siblingDestroy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps != null)
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (fadeSec <= 0.05f)
            {
                ExtinguishThrownLightImmediate(go);
                if (siblingDestroy != null)
                    UnityEngine.Object.Destroy(siblingDestroy);
                return;
            }

            _thrownLightFades.Add(new ThrownLightFade
            {
                Go = go,
                EndTime = Time.time + fadeSec,
                Duration = fadeSec,
                StartIntensity = startI,
                Lights = lights,
                SiblingDestroy = siblingDestroy
            });
        }

        private static void TickThrownLightFades()
        {
            if (_thrownLightFades.Count == 0) return;
            float now = Time.time;
            for (int i = _thrownLightFades.Count - 1; i >= 0; i--)
            {
                var f = _thrownLightFades[i];
                if (f.Go == null)
                {
                    if (f.SiblingDestroy != null)
                        UnityEngine.Object.Destroy(f.SiblingDestroy);
                    _thrownLightFades.RemoveAt(i);
                    continue;
                }
                float left = f.EndTime - now;
                float t = 1f - Mathf.Clamp01(left / Mathf.Max(0.01f, f.Duration));
                float inten = Mathf.Lerp(f.StartIntensity, 0f, t);
                float alphaScale = 1f - t;
                if (f.Lights != null)
                {
                    for (int j = 0; j < f.Lights.Length; j++)
                    {
                        if (f.Lights[j] != null)
                            f.Lights[j].LightIntensity = inten;
                    }
                }
                // Dim lightFlare sprites with the light (vanilla waitToDie fades then destroys).
                DimFlareSprites(f.Go, alphaScale);
                if (f.SiblingDestroy != null)
                    DimFlareSprites(f.SiblingDestroy, alphaScale);

                if (now >= f.EndTime)
                {
                    ExtinguishThrownLightImmediate(f.Go);
                    if (f.SiblingDestroy != null)
                    {
                        ExtinguishThrownLightImmediate(f.SiblingDestroy);
                        UnityEngine.Object.Destroy(f.SiblingDestroy);
                    }
                    _thrownLightFades.RemoveAt(i);
                }
            }
        }

        private static void DimFlareSprites(GameObject go, float alphaScale)
        {
            if (go == null) return;
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl != null && fl.lightFlare != null)
                {
                    Color c = fl.lightFlare.color;
                    c.a = Mathf.Clamp01(alphaScale);
                    fl.lightFlare.color = c;
                }
            }
        }

        private static void ExtinguishThrownLightImmediate(GameObject go)
        {
            if (go == null) return;

            // V2: destroy glow sprites like vanilla waitToDie Destroy(lightFlare).
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl == null) continue;
                if (fl.lightFlare != null)
                {
                    UnityEngine.Object.Destroy(fl.lightFlare.gameObject);
                    fl.lightFlare = null;
                }
            }

            foreach (var lt in go.GetComponentsInChildren<Light2D>(true))
            {
                try
                {
                    lt.unlightGraphNodes();
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(lt);
                }
                catch { /* ignore */ }
                lt.LightIntensity = 0f;
                lt.gameObject.SetActive(false);
            }
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
                UnityEngine.Object.Destroy(fl);
            foreach (var auth in go.GetComponentsInChildren<NetworkFlareLifetime>(true))
                UnityEngine.Object.Destroy(auth);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableFade] extinguished go=" + go.name);
        }

        /// <summary>True if this GO (or child) is already in an active light fade.</summary>
        public static bool IsThrownLightFading(GameObject go)
        {
            if (go == null) return false;
            for (int i = 0; i < _thrownLightFades.Count; i++)
            {
                if (_thrownLightFades[i].Go == go || _thrownLightFades[i].SiblingDestroy == go)
                    return true;
            }
            return false;
        }

        /// <summary>Compatibility name for the fade-then-extinguish path.</summary>
        private static void ExtinguishThrownLight(GameObject go)
        {
            BeginThrownLightFade(go, FlareBurnoutFadeSec);
        }

        /// <summary>Late-join: re-send still-burning thrown flares as grounded burns (no flight).</summary>
        public static void SendActiveThrownLightsTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            int sent = 0;
            float now = Time.time;
            for (int i = 0; i < _thrownLights.Count; i++)
            {
                var t = _thrownLights[i];
                if (t.Go == null || now >= t.ExpireAt) continue;
                Vector3 p = t.Go.transform.position;
                // Peer needs remaining-until-dark (= until fade start + fade).
                float remain = Mathf.Max(0.15f, (t.ExpireAt - now) + FlareBurnoutFadeSec);
                // Distance=0 + zero vel + no land → grounded spawn branch (F7).
                var msg = new ThrowableSpawnMessage
                {
                    ItemType = t.ItemType ?? "flare",
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    AimY = 0f,
                    Distance = 0f,
                    VelX = 0f,
                    VelY = 0f,
                    VelZ = 0f,
                    ThrowId = t.ThrowId,
                    LongevitySec = remain,
                    HasLandTarget = false
                };
                net.SendToPlayer(playerId, NetMessageType.ThrowableSpawn, w => msg.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                sent++;
            }
            if (sent > 0)
                DWMPHorde.Logging.ModLog.Event(DWMPHorde.Logging.LogCat.Session,
                    "[BulkSync] Thrown lights (grounded) → p" + playerId + ": " + sent);
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
