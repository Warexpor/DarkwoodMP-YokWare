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

        // Light protection (torch, lantern, LightArea)
        public bool HasLightProtection;

        // Dreams
        public bool IsDeadInDream;

        // Flare/Item lights
        public GameObject FlareLight;
        /// <summary>Held flare visual FX (particles/sprite), separate from Light2D root.</summary>
        public GameObject FlareFx;
        /// <summary>Last streamed flare item type (for FX prefab resolution).</summary>
        public string FlareItemType;
        public GameObject ItemLight;

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
            HasLightProtection = false;
            IsDeadInDream = false;
            FlareLight = null;
            FlareFx = null;
            FlareItemType = null;
            ItemLight = null;
            DragItemIds.Clear();
            DragItemNames.Clear();
        }
    }
}
