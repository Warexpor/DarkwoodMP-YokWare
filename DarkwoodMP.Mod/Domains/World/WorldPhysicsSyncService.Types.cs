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

    /// <summary>Serializable snapshot of a physics object's transform (name, position, rotation).</summary>
    public struct WorldObjectState
    {
        /// <summary>Name of the GameObject (used as a lookup key on the receiving end).</summary>
        public string Name;
        /// <summary>World position X coordinate.</summary>
        public float PosX;
        /// <summary>World position Y coordinate.</summary>
        public float PosY;
        /// <summary>World position Z coordinate.</summary>
        public float PosZ;
        /// <summary>Euler rotation X angle.</summary>
        public float RotX;
        /// <summary>Euler rotation Y angle.</summary>
        public float RotY;
        /// <summary>Euler rotation Z angle.</summary>
        public float RotZ;
        /// <summary>
        /// Item type identifier (<see cref="Item.invItem.type"/>), used on the receiving end
        /// to spawn the object on-demand when it doesn't exist locally (e.g. unloaded world chunk).
        /// Empty for objects that have no <see cref="Item"/> component.
        /// </summary>
        public string ItemType;

        /// <summary>Serializes this state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(Name ?? ""); w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(RotX); w.Put(RotY); w.Put(RotZ);
            w.Put(ItemType ?? "");
        }
        /// <summary>Deserializes a state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static WorldObjectState Deserialize(NetReader r) => new WorldObjectState
        {
            Name = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            RotX = r.GetFloat(),
            RotY = r.GetFloat(),
            RotZ = r.GetFloat(),
            ItemType = r.GetString()
        };
    }

    /// <summary>Serializable snapshot of a door's open/close state, including swing rotation and opener info.</summary>
    public struct DoorState
    {
        /// <summary>Rounded world position X (serves as lookup key).</summary>
        public float PosX;
        /// <summary>Rounded world position Y.</summary>
        public float PosY;
        /// <summary>Rounded world position Z.</summary>
        public float PosZ;
        /// <summary>Whether the door is open.</summary>
        public bool Opened;
        /// <summary>World position X of the player who opened the door (used for knock-back).</summary>
        public float OpenerPosX;
        /// <summary>World position Y of the opener.</summary>
        public float OpenerPosY;
        /// <summary>World position Z of the opener.</summary>
        public float OpenerPosZ;
        /// <summary>Force magnitude applied when opening (from the original open call).</summary>
        public float OpenForce;
        /// <summary>Y-axis euler angle of the door's body, matching the sender's swing position.</summary>
        public float BodyRotY;
        /// <summary>X component of door body angular velocity (for swing continuity).</summary>
        public float AngVelX;
        /// <summary>Y component of door body angular velocity.</summary>
        public float AngVelY;
        /// <summary>Z component of door body angular velocity.</summary>
        public float AngVelZ;

        /// <summary>Serializes this door state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(Opened);
            w.Put(OpenerPosX); w.Put(OpenerPosY); w.Put(OpenerPosZ);
            w.Put(OpenForce); w.Put(BodyRotY);
            w.Put(AngVelX); w.Put(AngVelY); w.Put(AngVelZ);
        }
        /// <summary>Deserializes a door state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static DoorState Deserialize(NetReader r) => new DoorState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Opened = r.GetBool(),
            OpenerPosX = r.GetFloat(),
            OpenerPosY = r.GetFloat(),
            OpenerPosZ = r.GetFloat(),
            OpenForce = r.GetFloat(),
            BodyRotY = r.GetFloat(),
            AngVelX = r.GetFloat(),
            AngVelY = r.GetFloat(),
            AngVelZ = r.GetFloat()
        };
    }

    /// <summary>Serializable snapshot of a trap's triggered state.</summary>
    public struct TrapState
    {
        /// <summary>Rounded world position X (serves as lookup key).</summary>
        public float PosX;
        /// <summary>Rounded world position Y.</summary>
        public float PosY;
        /// <summary>Rounded world position Z.</summary>
        public float PosZ;
        /// <summary>Whether the trap has been triggered (sprung/snapped).</summary>
        public bool Triggered;
        /// <summary>Stable trap net id (0 = position-only legacy).</summary>
        public int TrapNetId;
        /// <summary>Player occupying this trap (0 = none). Special: <see cref="OccupantSilentDisarm"/>.</summary>
        public short OccupantPlayerId;

        /// <summary>
        /// OccupantPlayerId sentinel: successful harvest or disarm applies the triggered visual only,
        /// no explosion prefab/sound (vanilla staysAfterDisarming switchToTriggered).
        /// </summary>
        public const short OccupantSilentDisarm = -2;

        /// <summary>Serializes this trap state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(Triggered);
            w.Put(TrapNetId);
            w.Put(OccupantPlayerId);
        }
        /// <summary>Deserializes a trap state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static TrapState Deserialize(NetReader r) => new TrapState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Triggered = r.GetBool(),
            TrapNetId = r.GetInt(),
            OccupantPlayerId = r.GetShort()
        };
    }

    /// <summary>Serializable snapshot of a generator's on/off state and fuel level.</summary>
    public struct GeneratorState
    {
        /// <summary>Rounded world position X (serves as lookup key).</summary>
        public float PosX;
        /// <summary>Rounded world position Y.</summary>
        public float PosY;
        /// <summary>Rounded world position Z.</summary>
        public float PosZ;
        /// <summary>Whether the generator is running.</summary>
        public bool IsOn;
        /// <summary>Current fuel level.</summary>
        public float Fuel;
        /// <summary>Low-power state (lights flicker when fuel < 10%).</summary>
        public bool LowPower;
        /// <summary>
        /// Item type identifier (<see cref="Item.invItem.type"/>), used on the receiving end
        /// to spawn the generator on-demand when it doesn't exist locally.
        /// </summary>
        public string ItemType;

        /// <summary>Serializes this generator state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(IsOn); w.Put(Fuel);
            w.Put(LowPower); w.Put(ItemType ?? "");
        }
        /// <summary>Deserializes a generator state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static GeneratorState Deserialize(NetReader r) => new GeneratorState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsOn = r.GetBool(),
            Fuel = r.GetFloat(),
            LowPower = r.GetBool(),
            ItemType = r.GetString()
        };
    }

    /// <summary>Top-level network message containing arrays of object, door, trap, and generator states.</summary>
    public struct PhysicsStateMessage
    {
        /// <summary>Monotonic sequence within the selected physics stream.</summary>
        public uint Sequence;
        /// <summary>Reliable event stream marker; kept separate from unreliable ordering.</summary>
        public bool Reliable;
        /// <summary>All physics object transforms in this snapshot.</summary>
        public WorldObjectState[] Objects;
        /// <summary>All door state changes in this snapshot.</summary>
        public DoorState[] Doors;
        /// <summary>All trap state changes in this snapshot.</summary>
        public TrapState[] Traps;
        /// <summary>All generator state changes in this snapshot.</summary>
        public GeneratorState[] Generators;

        /// <summary>Serializes the full message into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(Reliable);
            w.Put(Sequence);
            int oc = Objects != null ? Objects.Length : 0;
            w.Put(oc);
            for (int i = 0; i < oc; i++) Objects[i].Serialize(w);

            int dc = Doors != null ? Doors.Length : 0;
            w.Put(dc);
            for (int i = 0; i < dc; i++) Doors[i].Serialize(w);

            int tc = Traps != null ? Traps.Length : 0;
            w.Put(tc);
            for (int i = 0; i < tc; i++) Traps[i].Serialize(w);

            int gc = Generators != null ? Generators.Length : 0;
            w.Put(gc);
            for (int i = 0; i < gc; i++) Generators[i].Serialize(w);
        }

        /// <summary>Deserializes a full message from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static PhysicsStateMessage Deserialize(NetReader r)
        {
            bool reliable = r.GetBool();
            uint sequence = r.GetUInt();
            int oc = r.GetInt();
            if (oc < 0 || oc > 4096)
                throw new InvalidDataException("Physics object count is out of range: " + oc);
            var objs = new WorldObjectState[oc];
            for (int i = 0; i < oc; i++) objs[i] = WorldObjectState.Deserialize(r);

            int dc = r.GetInt();
            if (dc < 0 || dc > 4096)
                throw new InvalidDataException("Physics door count is out of range: " + dc);
            var doors = new DoorState[dc];
            for (int i = 0; i < dc; i++) doors[i] = DoorState.Deserialize(r);

            int tc = r.GetInt();
            if (tc < 0 || tc > 4096)
                throw new InvalidDataException("Physics trap count is out of range: " + tc);
            var traps = new TrapState[tc];
            for (int i = 0; i < tc; i++) traps[i] = TrapState.Deserialize(r);

            int gc = r.GetInt();
            if (gc < 0 || gc > 4096)
                throw new InvalidDataException("Physics generator count is out of range: " + gc);
            var generators = new GeneratorState[gc];
            for (int i = 0; i < gc; i++) generators[i] = GeneratorState.Deserialize(r);

            return new PhysicsStateMessage
            {
                Sequence = sequence,
                Reliable = reliable,
                Objects = objs,
                Doors = doors,
                Traps = traps,
                Generators = generators
            };
        }
    }

    /// <summary>
    /// Provides reflection-based helpers for reading and writing private fields
    /// on Door, Trigger, and other game types via Harmony Traverse.
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
            InsideCharacterSounds = false;
            IsInsideLocalExplosion = false;
            IsInsidePlayerBulletCollision = false;
            IsInsideFastProjectileRaycast = false;
        }

        /// <summary>Reads the private "opened" field from a Door instance.</summary>
        /// <param name="door">The door instance.</param>
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
        /// <param name="door">The door instance.</param>
        /// <param name="opened">True to open, false to close.</param>
        /// <param name="openerPos">Position of the player interacting with the door (used as open origin).</param>
        /// <param name="openForce">Force magnitude from the original open call.</param>
        /// <param name="bodyRotY">Target Y euler angle for the door body to match the sender's swing.</param>
        /// <param name="angVelX">X component of sender's door body angular velocity.</param>
        /// <param name="angVelY">Y component of sender's door body angular velocity.</param>
        /// <param name="angVelZ">Z component of sender's door body angular velocity.</param>
        public static void SetDoorOpened(Door door, bool opened, Vector3 openerPos = default, float openForce = 0f, float bodyRotY = 0f, float angVelX = 0f, float angVelY = 0f, float angVelZ = 0f)
        {
            InvokeDoorMethod(door, opened ? "open" : "close", openerPos, openForce);

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

                    // Apply sender's angular velocity so the door continues its natural swing
                    Vector3 senderAngVel = new Vector3(angVelX, angVelY, angVelZ);
                    if (senderAngVel.sqrMagnitude > 0f && opened)
                    {
                        doorBodyRB.angularVelocity = senderAngVel;
                    }

                    // Note: the kick sound ("door_hit_run" at thumpForce=45000)
                    // is played by Door.open() inside InvokeDoorMethod above,
                    // which receives the original OpenForce value from the sender.
                }
            }
        }

        /// <summary>
        /// Invokes the public or non-public "open" or "close" method on the Door type via reflection,
        /// matching the method's parameter signature. Falls back to toggling colliders and playing
        /// animation clips if reflection fails.
        /// </summary>
        private static void InvokeDoorMethod(Door door, string methodName, Vector3 openerPos = default, float openForce = 0f)
        {
            try
            {
                bool opening = (methodName == "open");
                bool invoked = false;

                // Try calling the original method via reflection (handles colliders, animation, internal state)
                try
                {
                    var methods = typeof(Door).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.Name != methodName) continue;
                        var pars = m.GetParameters();
                        object[] args = new object[pars.Length];
                        for (int i = 0; i < pars.Length; i++)
                        {
                            Type pt = pars[i].ParameterType;
                            if (pt == typeof(Vector3)) args[i] = opening ? openerPos : Vector3.zero;
                            else if (pt == typeof(Transform))
                            {
                                // Don't pass a transform so open() doesn't overwrite openerPos with the door's position
                                if (opening && openerPos != default)
                                    args[i] = null;
                                else
                                    args[i] = door.transform;
                            }
                            else if (pt == typeof(float)) args[i] = opening ? openForce : 0f;
                            else if (pt == typeof(bool)) args[i] = opening;
                            else if (pt == typeof(int)) args[i] = 0;
                            else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                        }
                        m.Invoke(door, args);
                        invoked = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DoorReflect] failed for " + methodName + ": " + ex);
                }

                if (invoked)
                    return;

                // Fallback: toggle colliders and play animation
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
                    else if (anim.GetClipByName(opening ? "Open" : "Close") != null) anim.Play(opening ? "Open" : "Close");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DoorAnim] " + ex);
            }
        }
    }
}
