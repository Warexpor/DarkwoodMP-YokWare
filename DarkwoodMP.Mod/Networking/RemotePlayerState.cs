using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Consolidates all per-remote-player state that was previously scattered
    /// across 8 separate dictionaries in LanNetworkManager.
    /// </summary>
    internal sealed class RemotePlayerState
    {
        public int PlayerId { get; set; }

        // Bear traps
        public bool InBearTrap;
        public Vector3 BearTrapPos;
        /// <summary>Stable trap instance this remote occupies (0 = none / unknown).</summary>
        public int TrapNetId;

        // Light protection (torch, lantern, LightArea)
        public bool HasLightProtection;

        /// <summary>Peer took the nightShadows degrade perk (streamed on PlayerState trailer).</summary>
        public bool HasNightShadows;

        // Dreams
        public bool IsDeadInDream;

        // Flare/Item lights
        public GameObject FlareLight;
        /// <summary>Held flare visual FX (particles/sprite), separate from Light2D root.</summary>
        public GameObject FlareFx;
        /// <summary>Last streamed flare item type (for FX prefab resolution).</summary>
        public string FlareItemType;
        public GameObject ItemLight;

        /// <summary>Last applied event-path PlayerLightState fingerprint (skip no-ops / re-spawns).</summary>
        public string AppliedLightItemType;
        public bool AppliedLightOn;
        public bool AppliedFlash;
        public bool AppliedEmitter;
        public bool AppliedItemLight;
        public bool AppliedAmbient;
        public float AppliedLightRadius;

        // Drag tracking: InstanceIDs of items being dragged by this remote player.
        // Used by PhysicsState to skip these items (prevents drag from fighting physics sync).
        public readonly HashSet<int> DragItemIds = new HashSet<int>();

        // Drag tracking: GameObject names of items being dragged by this remote player.
        // Used cross-peer (InstanceIDs don't match across processes).
        public readonly HashSet<string> DragItemNames = new HashSet<string>();

        public void Reset()
        {
            InBearTrap = false;
            BearTrapPos = Vector3.zero;
            TrapNetId = 0;
            HasLightProtection = false;
            HasNightShadows = false;
            IsDeadInDream = false;
            FlareLight = null;
            FlareFx = null;
            FlareItemType = null;
            ItemLight = null;
            AppliedLightItemType = null;
            AppliedLightOn = false;
            AppliedFlash = false;
            AppliedEmitter = false;
            AppliedItemLight = false;
            AppliedAmbient = false;
            AppliedLightRadius = 0f;
            DragItemIds.Clear();
            DragItemNames.Clear();
        }
    }
}
