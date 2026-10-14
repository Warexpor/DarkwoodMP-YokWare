using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using Unity.Collections;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Client: a creature copy the host drives does not shove furniture here. Its pose is written
    /// every frame, and where it stands against a free body the physics pushed that body out of
    /// it, on top of the host's own push, which arrives as the body's streamed pose. The client's
    /// wardrobe crept 12 to 20 units from the host's while a redneck stood at it ("DESYNC
    /// Containers Wardrobe_low_01 ... client's nearest"), and nothing sent it back, since on the
    /// host it lay still. Contacts between a driven copy and a piece of furniture the host streams
    /// (a free Item body: not a pickup, a drop or a thrown bottle) are dropped (Unity's contact
    /// modification), so furniture moves by the host's push alone. Everything else keeps its
    /// contacts: walls, players, other creatures, and a thrown molotov still breaks on the copy.
    /// </summary>
    public static partial class ClientEntityInterpolationService
    {
        private const float DrivenContactsRefresh = 0.25f;

        // Read on the physics thread: whole sets are swapped in, never changed after.
        private static volatile HashSet<int> _drivenBodyIds = new HashSet<int>(); // process-scoped: copy-on-write
        private static volatile HashSet<int> _furnitureBodyIds = new HashSet<int>(); // process-scoped: copy-on-write
        private const float FurnitureRefresh = 2f;
        private static float _nextFurnitureRefresh; // process-scoped: throttle
        private static readonly HashSet<int> _modifiableSet = new HashSet<int>(); // process-scoped: colliders already flagged
        private static float _nextDrivenContactsRefresh; // process-scoped: throttle
        private static bool _contactHookInstalled; // process-scoped: one subscription

        private static void TickDrivenContacts(float now)
        {
            if (now < _nextDrivenContactsRefresh)
                return;
            _nextDrivenContactsRefresh = now + DrivenContactsRefresh;

            LanNetworkManager net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client || _states.Count == 0)
            {
                if (_drivenBodyIds.Count > 0)
                    _drivenBodyIds = new HashSet<int>();
                return;
            }

            if (!_contactHookInstalled)
            {
                Physics.ContactModifyEvent += OnContactModify;
                _contactHookInstalled = true;
            }

            var driven = new HashSet<int>();
            foreach (KeyValuePair<short, EntityInterpState> kv in _states)
            {
                Rigidbody rb = kv.Value.CachedRb;
                if (rb == null)
                    continue;
                driven.Add(rb.GetInstanceID());
                if (_modifiableSet.Add(rb.GetInstanceID()))
                {
                    foreach (Collider col in rb.GetComponentsInChildren<Collider>(true))
                        if (col != null && !col.isTrigger)
                            col.hasModifiableContacts = true;
                }
            }

            if (now >= _nextFurnitureRefresh)
            {
                _nextFurnitureRefresh = now + FurnitureRefresh;
                var furniture = new HashSet<int>();
                Item[] items = WorldQueryHelper.GetCachedSceneComponents<Item>();
                for (int i = 0; i < items.Length; i++)
                {
                    Item it = items[i];
                    if (it == null || it.isDroppedItem)
                        continue;
                    Rigidbody rb = it.GetComponent<Rigidbody>();
                    // Kinematic too: it is one while it follows the host's stream, and free after.
                    if (rb == null)
                        continue;
                    GameObject go = rb.gameObject;
                    if (go.GetComponent<Character>() != null || go.GetComponent<ThrownItem>() != null
                        || go.GetComponent<DroppedItemIdentifier>() != null || go.GetComponent<DeathDrop>() != null)
                        continue;
                    furniture.Add(rb.GetInstanceID());
                }
                _furnitureBodyIds = furniture;
            }
            _drivenBodyIds = driven;
        }

        /// <summary>Physics thread: drop a driven copy's contacts with a piece of furniture.</summary>
        private static void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            HashSet<int> driven = _drivenBodyIds;
            if (driven.Count == 0)
                return;
            HashSet<int> furniture = _furnitureBodyIds;
            for (int i = 0; i < pairs.Length; i++)
            {
                ModifiableContactPair pair = pairs[i];
                int a = pair.bodyInstanceID, b = pair.otherBodyInstanceID;
                bool drop = (driven.Contains(a) && furniture.Contains(b))
                    || (driven.Contains(b) && furniture.Contains(a));
                if (!drop)
                    continue;
                for (int k = 0; k < pair.contactCount; k++)
                    pair.IgnoreContact(k);
            }
        }
    }
}
