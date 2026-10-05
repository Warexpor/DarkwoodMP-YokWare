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

            // The thrower's weapon itself, as vanilla throwItem puts it in the thrown object's slot,
            // so whoever picks it back up gets that weapon (its wear and upgrades), not a new one.
            if (msg.Recoverable)
            {
                Inventory inv = go.GetComponent<Inventory>();
                if (inv != null && inv.slots != null && inv.slots.Count > 0)
                {
                    InvSlot slot = inv.slots[0];
                    slot.inventory = inv;
                    InvItemClass created = slot.createItem(msg.ItemType, 1, 1f, InvItem.ModifierQuality.none, false);
                    if (!InvItemClass.isNull(created))
                    {
                        InvItemTransferApply.ApplyMeta(created, msg.Durability, 0, false);
                        InvItemUpgradeWire.Apply(created, msg.Upgrades);
                    }
                }
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
                    // Already landed elsewhere, like an item loaded from a save: vanilla init still
                    // runs onCollide for an onGround item, and only loadedFromSave keeps that from
                    // replaying the landing (collide sound, AI alert, lighting the gasoline under it).
                    ThrownLoadedFromSave(ti) = true;
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
                    _s.ClientKinematic.Remove(go.GetInstanceID());

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

            // The flare runs vanilla's own clock here, started at the thrower's age: same glow,
            // same burn-out moment on every machine (FlareClock); nothing to despawn later.
            if (isFlareItem)
            {
                FlareClock.MakeCopy(go, msg.FlareAge >= 0f ? msg.FlareAge : 0f);
                NoteThrownFlare(go);
                if (msg.FlareAge >= 0f)
                    AlignThrownBurnClock(ti, msg.FlareAge, grounded);
            }

            if (!visualOnly)
                Core.addToSaveable(go, isDynamic: true);
            ModRuntime.LegacyInfo($"[ThrowableSpawn] spawned {msg.ItemType} flareAge={msg.FlareAge:F1} at {spawnPos} aimY={msg.AimY} dist={distance} vel={vel.magnitude.ToString("F1")} land={landTarget} grounded={grounded} visualOnly={visualOnly}");
        }

        private static readonly AccessTools.FieldRef<ThrownItem, bool> ThrownLoadedFromSave =
            AccessTools.FieldRefAccess<ThrownItem, bool>("loadedFromSave");

        private static readonly System.Reflection.MethodInfo ThrownWaitToStopBurning =
            AccessTools.Method(typeof(ThrownItem), "waitToStopBurning");

        /// <summary>
        /// A burning throwable (a flare) has a second vanilla clock besides <see cref="Flare"/>:
        /// <c>ThrownItem.init → waitToStopBurning</c>, which after <c>burnTime</c> removes its
        /// lights and the flare itself (<c>destroyOnBurnOut</c>) or swaps its sprite. On the
        /// thrower it runs from when the flare was lit in the hand; a copy started it at its own
        /// spawn, <paramref name="age"/> seconds late, so the flare vanished on the thrower while
        /// the copies still showed its body through the fade and after. Starts the copy's clock
        /// that far in. A flying copy's init (next frame) reads the shortened burnTime; a grounded
        /// copy's init lands it instead and never starts the clock, so it is started here.
        /// </summary>
        private static void AlignThrownBurnClock(ThrownItem ti, float age, bool grounded)
        {
            if (ti == null || !ti.flaming || ti.burnTime <= 0f)
                return;
            // Kept above zero: init only starts the clock while burnTime > 0.
            ti.burnTime = Mathf.Max(0.01f, ti.burnTime - age);
            if (!grounded || ThrownWaitToStopBurning == null)
                return;
            if (ThrownWaitToStopBurning.Invoke(ti, null) is System.Collections.IEnumerator routine)
                ti.StartCoroutine(routine);
        }
    }
}
