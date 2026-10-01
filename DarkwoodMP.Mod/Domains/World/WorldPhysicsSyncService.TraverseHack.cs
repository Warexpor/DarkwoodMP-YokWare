using System;
using System.Reflection;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Reflection helpers for Door / transient apply flags (split from Types for ownership).
    /// </summary>
    internal static class TraverseHack
    {
        private static bool _explicitApplyingFromNetwork;

        /// <summary>
        /// True while applying remote state. While <see cref="NetworkApplyGuard.IsActive"/>,
        /// always true even if nested code assigns false (prevents split-brain rebroadcast).
        /// </summary>
        public static bool ApplyingFromNetwork
        {
            get => _explicitApplyingFromNetwork || Networking.NetworkApplyGuard.IsActive;
            set => _explicitApplyingFromNetwork = value;
        }

        internal static bool GetExplicitFlag() => _explicitApplyingFromNetwork;
        internal static void SetExplicitFlag(bool value) => _explicitApplyingFromNetwork = value;

        /// <summary>
        /// Set true while inside a CharacterSounds method that EntitySoundSyncPatches
        /// already handles (playGrowl, playSingleInstance, playEscapingLoop, playIdleLoop).
        /// PlayerSoundSyncPatches checks this flag to avoid double-forwarding the
        /// AudioController.Play call that happens inside these methods.
        /// </summary>
        public static bool InsideCharacterSounds = false;

        /// <summary>
        /// Set true on client during a local Explodes.explode() call so
        /// ClientDamageRedirectPatch can redirect AOE splash damage to the host
        /// (the host re-enacts the explosion and applies damage authoritatively).
        /// </summary>
        public static bool IsInsideLocalExplosion = false;

        /// <summary>
        /// Set true on client while inside Bullet.onCollide for a player-fired
        /// projectile (objectThatSpawnedMe == null). ClientDamageRedirectPatch
        /// checks this to detect projectile weapon damage where the vanilla
        /// code never sets objectThatSpawnedMe on player bullets.
        /// </summary>
        public static bool IsInsidePlayerBulletCollision = false;

        /// <summary>
        /// True while <see cref="FastProjectile.FixedUpdate"/> is running its sweep
        /// raycast. HitscanImpactSyncPatch must not treat those as player hitscan FF
        /// (frozen/stalled pellets used to ghost-damage via that path).
        /// </summary>
        public static bool IsInsideFastProjectileRaycast = false;

        /// <summary>Clear all transient apply flags on network stop (stuck flags leak rebroadcast blocks).</summary>
        public static void ResetTransientFlags()
        {
            _explicitApplyingFromNetwork = false;
            WorldPhysicsSyncService._suppressBroadcast = false;
            InsideCharacterSounds = false;
            IsInsideLocalExplosion = false;
            IsInsidePlayerBulletCollision = false;
            IsInsideFastProjectileRaycast = false;
        }

        /// <summary>Reads the private "opened" field from a Door instance.</summary>
        public static bool ReadDoorOpened(Door door)
        {
            var t = Traverse.Create(door);
            return t.Field("opened").GetValue<bool>();
        }

        /// <summary>
        /// Opens or closes a door: invokes the original open/close method via reflection,
        /// ensures the "opened" field matches, and syncs the door body's rotation + angular velocity
        /// so the receiver's door swing matches the sender's physical swing.
        /// </summary>
        public static void SetDoorOpened(Door door, bool opened, Vector3 openerPos = default, float openForce = 0f, float bodyRotY = 0f, float angVelX = 0f, float angVelY = 0f, float angVelZ = 0f)
        {
            DialogHostApplyGuard.RunHostWorldFanout(() =>
                InvokeDoorMethod(door, opened ? "open" : "close", openerPos, openForce));

            var t = Traverse.Create(door);
            if (t.Field("opened").GetValue<bool>() != opened)
                t.Field("opened").SetValue(opened);

            if (door.body != null)
            {
                Rigidbody doorBodyRB = door.body.GetComponent<Rigidbody>();
                if (doorBodyRB != null)
                {
                    Vector3 currentEuler = door.body.eulerAngles;
                    bool closeSnap = !opened && Mathf.Abs(Mathf.DeltaAngle(currentEuler.y, bodyRotY)) > 5f;
                    if (closeSnap || (opened && bodyRotY != 0f))
                    {
                        Quaternion targetRot = Quaternion.Euler(currentEuler.x, bodyRotY, currentEuler.z);
                        door.body.rotation = targetRot;
                        if (opened)
                        {
                            doorBodyRB.velocity = Vector3.zero;
                            doorBodyRB.angularVelocity = Vector3.zero;
                        }
                        else
                        {
                            doorBodyRB.constraints = RigidbodyConstraints.FreezeAll;
                            doorBodyRB.isKinematic = true;
                        }
                    }

                    Vector3 senderAngVel = new Vector3(angVelX, angVelY, angVelZ);
                    if (senderAngVel.sqrMagnitude > 0f && opened)
                        doorBodyRB.angularVelocity = senderAngVel;
                }
            }
        }

        private static MethodInfo _doorOpenMethod; // process-scoped: reflection cache
        private static MethodInfo _doorCloseMethod; // process-scoped: reflection cache
        private static ParameterInfo[] _doorOpenPars = Array.Empty<ParameterInfo>(); // process-scoped: reflection cache
        private static ParameterInfo[] _doorClosePars = Array.Empty<ParameterInfo>(); // process-scoped: reflection cache
        private static object[] _doorInvokeArgs = Array.Empty<object>(); // process-scoped: scratch

        private static void EnsureDoorMethodsCached()
        {
            if (_doorOpenMethod != null && _doorCloseMethod != null)
                return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo[] methods = typeof(Door).GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == "open" && _doorOpenMethod == null)
                {
                    _doorOpenMethod = m;
                    _doorOpenPars = m.GetParameters() ?? Array.Empty<ParameterInfo>();
                }
                else if (m.Name == "close" && _doorCloseMethod == null)
                {
                    _doorCloseMethod = m;
                    _doorClosePars = m.GetParameters() ?? Array.Empty<ParameterInfo>();
                }
            }
        }

        /// <summary>
        /// Invokes Door open/close via cached MethodInfo (no GetMethods per apply).
        /// Falls back to collider/anim toggle if reflection fails.
        /// </summary>
        private static void InvokeDoorMethod(Door door, string methodName, Vector3 openerPos = default, float openForce = 0f)
        {
            try
            {
                bool opening = methodName == "open";
                bool invoked = false;

                try
                {
                    EnsureDoorMethodsCached();
                    MethodInfo m = opening ? _doorOpenMethod : _doorCloseMethod;
                    ParameterInfo[] pars = opening ? _doorOpenPars : _doorClosePars;
                    if (m != null)
                    {
                        if (_doorInvokeArgs.Length != pars.Length)
                            _doorInvokeArgs = new object[pars.Length];
                        for (int i = 0; i < pars.Length; i++)
                        {
                            Type pt = pars[i].ParameterType;
                            if (pt == typeof(Vector3)) _doorInvokeArgs[i] = opening ? openerPos : Vector3.zero;
                            else if (pt == typeof(Transform))
                            {
                                // Don't pass a transform so open() doesn't overwrite openerPos
                                // with the door's position when we already have a world opener.
                                if (opening && openerPos != default)
                                    _doorInvokeArgs[i] = null;
                                else
                                    _doorInvokeArgs[i] = door.transform;
                            }
                            else if (pt == typeof(float)) _doorInvokeArgs[i] = opening ? openForce : 0f;
                            else if (pt == typeof(bool)) _doorInvokeArgs[i] = opening;
                            else if (pt == typeof(int)) _doorInvokeArgs[i] = 0;
                            else _doorInvokeArgs[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                        }
                        m.Invoke(door, _doorInvokeArgs);
                        invoked = true;
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DoorReflect] failed for " + methodName + ": " + ex);
                }

                if (invoked)
                    return;

                foreach (Collider c in door.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null && !c.isTrigger)
                        c.enabled = !opening;
                }

                tk2dSpriteAnimator anim = door.GetComponentInChildren<tk2dSpriteAnimator>();
                if (anim != null)
                {
                    string clip = methodName;
                    if (anim.GetClipByName(clip) != null) anim.Play(clip);
                    else if (anim.GetClipByName(opening ? "Open" : "Close") != null)
                        anim.Play(opening ? "Open" : "Close");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DoorAnim] " + ex);
            }
        }
    }
}
