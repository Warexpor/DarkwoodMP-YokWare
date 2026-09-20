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
        /// <summary>
        /// Valid prefix length when arrays are recycled/oversized. 0 = use
        /// <c>Array.Length</c> (ad-hoc single-entry messages).
        /// </summary>
        public int ObjectCount;
        public int DoorCount;
        public int TrapCount;
        public int GeneratorCount;

        public int EffectiveObjectCount =>
            ObjectCount > 0 ? ObjectCount : (Objects != null ? Objects.Length : 0);
        public int EffectiveDoorCount =>
            DoorCount > 0 ? DoorCount : (Doors != null ? Doors.Length : 0);
        public int EffectiveTrapCount =>
            TrapCount > 0 ? TrapCount : (Traps != null ? Traps.Length : 0);
        public int EffectiveGeneratorCount =>
            GeneratorCount > 0 ? GeneratorCount : (Generators != null ? Generators.Length : 0);

        /// <summary>Serializes the full message into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(Reliable);
            w.Put(Sequence);
            int oc = EffectiveObjectCount;
            w.Put(oc);
            for (int i = 0; i < oc; i++) Objects[i].Serialize(w);

            int dc = EffectiveDoorCount;
            w.Put(dc);
            for (int i = 0; i < dc; i++) Doors[i].Serialize(w);

            int tc = EffectiveTrapCount;
            w.Put(tc);
            for (int i = 0; i < tc; i++) Traps[i].Serialize(w);

            int gc = EffectiveGeneratorCount;
            w.Put(gc);
            for (int i = 0; i < gc; i++) Generators[i].Serialize(w);
        }

        private static WorldObjectState[] _deserObjects = Array.Empty<WorldObjectState>();
        private static DoorState[] _deserDoors = Array.Empty<DoorState>();
        private static TrapState[] _deserTraps = Array.Empty<TrapState>();
        private static GeneratorState[] _deserGenerators = Array.Empty<GeneratorState>();

        private static void EnsureDeserCapacity<T>(ref T[] buf, int n)
        {
            if (n <= 0) return;
            if (buf.Length < n)
                buf = new T[Math.Max(n, buf.Length == 0 ? n : buf.Length * 2)];
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
            EnsureDeserCapacity(ref _deserObjects, oc);
            for (int i = 0; i < oc; i++) _deserObjects[i] = WorldObjectState.Deserialize(r);

            int dc = r.GetInt();
            if (dc < 0 || dc > 4096)
                throw new InvalidDataException("Physics door count is out of range: " + dc);
            EnsureDeserCapacity(ref _deserDoors, dc);
            for (int i = 0; i < dc; i++) _deserDoors[i] = DoorState.Deserialize(r);

            int tc = r.GetInt();
            if (tc < 0 || tc > 4096)
                throw new InvalidDataException("Physics trap count is out of range: " + tc);
            EnsureDeserCapacity(ref _deserTraps, tc);
            for (int i = 0; i < tc; i++) _deserTraps[i] = TrapState.Deserialize(r);

            int gc = r.GetInt();
            if (gc < 0 || gc > 4096)
                throw new InvalidDataException("Physics generator count is out of range: " + gc);
            EnsureDeserCapacity(ref _deserGenerators, gc);
            for (int i = 0; i < gc; i++) _deserGenerators[i] = GeneratorState.Deserialize(r);

            return new PhysicsStateMessage
            {
                Sequence = sequence,
                Reliable = reliable,
                Objects = oc > 0 ? _deserObjects : null,
                Doors = dc > 0 ? _deserDoors : null,
                Traps = tc > 0 ? _deserTraps : null,
                Generators = gc > 0 ? _deserGenerators : null,
                ObjectCount = oc,
                DoorCount = dc,
                TrapCount = tc,
                GeneratorCount = gc
            };
        }
    }
}
