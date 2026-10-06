using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Players
{
    public class DroppedItemIdentifier : MonoBehaviour
    {
        public string Id;

        private static readonly Dictionary<string, DroppedItemIdentifier> _all = new Dictionary<string, DroppedItemIdentifier>();

        public static DroppedItemIdentifier FindById(string id)
        {
            _all.TryGetValue(id, out var di);
            return di;
        }

        /// <summary>Register after setting Id (OnEnable fires before Id is assigned).</summary>
        public static void Register(DroppedItemIdentifier ident)
        {
            if (ident != null && !string.IsNullOrEmpty(ident.Id))
                _all[ident.Id] = ident;
        }

        /// <summary>
        /// Network stop: prune entries whose object is gone. Live drops (including culled /
        /// inactive ones) stay registered — they are still real objects in the local world, and a
        /// re-host / late-join bulk or a Remove for them must still find them.
        /// </summary>
        public static void ClearRegistry()
        {
            List<string> dead = null;
            foreach (var kv in _all)
            {
                if (kv.Value == null)
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(kv.Key);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
                _all.Remove(dead[i]);
        }

        /// <summary>Snapshot of live networked drops (for late-join bulk sync).</summary>
        public static void CopyAll(System.Collections.Generic.List<DroppedItemIdentifier> into)
        {
            if (into == null) return;
            into.Clear();
            foreach (var kv in _all)
            {
                if (kv.Value != null)
                    into.Add(kv.Value);
            }
        }

        private void OnEnable()
        {
            Register(this);
        }

        // OnDestroy, not OnDisable: Cullable hides far drops with SetActive(false), and a culled
        // drop must stay findable for late-join bulk sync and for a Remove aimed at it.
        private void OnDestroy()
        {
            if (!string.IsNullOrEmpty(Id) && _all.TryGetValue(Id, out var di) && di == this)
                _all.Remove(Id);
        }
    }
}
