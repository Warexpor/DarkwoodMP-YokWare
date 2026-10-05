using System;
using System.Collections.Generic;
using DWMPHorde.Audio;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        /// <summary>
        /// Everything this service remembers about the current session. <see cref="ResetCore"/>
        /// replaces the object, so a field added here can never be missed by a reset list.
        /// </summary>
        private static SessionState _s = new SessionState(new ThrownLightState()); // reset-in: ResetCore

        /// <summary>Thrown flares/molotovs in flight or burning; survives a host promotion.</summary>
        private sealed class ThrownLightState
        {
            /// <summary>Thrown flares burning in this world (each on its own FlareClock); for joiners.</summary>
            public readonly List<GameObject> ThrownFlares = new List<GameObject>(16);
            public readonly List<ThrownLightFade> ThrownLightFades = new List<ThrownLightFade>(8);
        }

        /// <summary>Per-session physics, door/trap/generator, push-sound, pickup and light state.</summary>
        private sealed class SessionState
        {
            public readonly ThrownLightState Thrown;

            public SessionState(ThrownLightState thrown) => Thrown = thrown;

            public float NextDreamPropColliderBroadcast;
            public Item[] DreamPropItemsCache;
            public int DreamPropItemsRootId;
            /// <summary>
            /// LightState that arrived before the Item existed (unloaded location grid).
            /// Flushed by <see cref="TryFlushPendingLights"/> once the world is ready.
            /// </summary>
            public readonly List<LightStateMessage> PendingLights = new List<LightStateMessage>(32);
            public float NextPendingLightFlushTime;
            public float PendingLightQueuedAt = -1f;
            public readonly Dictionary<int, Vector3> LastPos = new Dictionary<int, Vector3>();
            public readonly Dictionary<int, float> LastMoveTime = new Dictionary<int, float>();
            public readonly Dictionary<int, float> LastClientUpdateTime = new Dictionary<int, float>();
            public uint NextSnapshotSequence;
            public int ClientUpdateCleanupCounter;
            public readonly Dictionary<int, (Rigidbody rb, float releaseTime, string objName)> ClientKinematic = new Dictionary<int, (Rigidbody rb, float releaseTime, string objName)>();
            public readonly Dictionary<int, float> BodyPushSoundTimer = new Dictionary<int, float>();
            public readonly Dictionary<int, float> LastPushSoundTime = new Dictionary<int, float>();
            public readonly Dictionary<int, AudioSource> PushSoundSource = new Dictionary<int, AudioSource>();
            public readonly Dictionary<int, (float startVol, float endTime)> PushSoundFade = new Dictionary<int, (float, float)>();
            public readonly Dictionary<int, int> PushStationaryCount = new Dictionary<int, int>();
            public readonly Dictionary<string, int> PushNameToGid = new Dictionary<string, int>();
            public readonly Dictionary<int, string> PushGidToName = new Dictionary<int, string>();
            public readonly Dictionary<int, float> ClientKinematicGate = new Dictionary<int, float>();
            public readonly Dictionary<int, AudioObject> PushSoundAO = new Dictionary<int, AudioObject>();
            /// <summary>Object names with an active body-push scrape (start once, stop once).</summary>
            public readonly HashSet<string> BodyPushSoundActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<PosNameKey, float> DestroyDebounce = new Dictionary<PosNameKey, float>();
            public readonly Dictionary<PosNameKey, float> OutboundRemoveDebounce = new Dictionary<PosNameKey, float>();
            /// <summary>
            /// Session consume set for non-GUID world pickups (isDroppedItem without
            /// DroppedItemIdentifier). Mirrors ConsumedDropGuids so the second peer
            /// cannot grant after the first remove is claimed locally or on the wire.
            /// </summary>
            public readonly HashSet<PosNameKey> ConsumedWorldPickups = new HashSet<PosNameKey>();
            public readonly Dictionary<Vector3, bool> LastDoorOpen = new Dictionary<Vector3, bool>();
            public readonly Dictionary<Vector3, bool> LastTrapTriggered = new Dictionary<Vector3, bool>();
            public float FullResyncTimer;
            public float LastFullRbScanTime = -999f;
            /// <summary>Last successful FindOrSpawn hit by object name (distance-gated; non-unique names OK).</summary>
            public readonly Dictionary<string, GameObject> LastResolvedByName = new Dictionary<string, GameObject>(128);
            public readonly Dictionary<int, GameObject> KnownTraps = new Dictionary<int, GameObject>();
            /// <summary>Trap classification per collider root instance id, with last time the scan saw it.</summary>
            public readonly Dictionary<int, TrapClassification> TrapResultCache = new Dictionary<int, TrapClassification>();
            public readonly Dictionary<Vector3, StateKeyAge> DoorKeyAge = new Dictionary<Vector3, StateKeyAge>();
            public readonly Dictionary<Vector3, StateKeyAge> TrapKeyAge = new Dictionary<Vector3, StateKeyAge>();
            public readonly Dictionary<Vector3, StateKeyAge> GeneratorKeyAge = new Dictionary<Vector3, StateKeyAge>();
            public readonly Dictionary<Vector3, bool> LastGeneratorOn = new Dictionary<Vector3, bool>();
            public readonly Dictionary<Vector3, float> LastGeneratorFuel = new Dictionary<Vector3, float>();
            public readonly List<Vector3> ScanCenters = new List<Vector3>(8);
            public readonly HashSet<int> ScannedObjectIds = new HashSet<int>();
            public readonly Dictionary<int, ObjectInterpState> ObjectInterp = new Dictionary<int, ObjectInterpState>();
        }
    }
}
