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
            ModRuntime.LegacyInfo($"[ThrowableSpawn] spawned {msg.ItemType} throwId={msg.ThrowId} life={msg.LongevitySec} at {spawnPos} aimY={msg.AimY} dist={distance} vel={vel.magnitude.ToString("F1")} land={landTarget} grounded={grounded} visualOnly={visualOnly}");
        }

        /// <summary>
        /// Host: when remaining life elapses, broadcast despawn and start 2s fade (vanilla waitToDie).
        /// All roles: advance active fades.
        /// </summary>
    }
}
