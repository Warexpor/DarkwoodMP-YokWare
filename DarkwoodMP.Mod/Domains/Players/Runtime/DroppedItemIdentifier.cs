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

        /// <summary>Drop dictionary on network stop (objects may already be destroyed).</summary>
        public static void ClearRegistry() => _all.Clear();

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

        private void OnDisable()
        {
            if (!string.IsNullOrEmpty(Id) && _all.TryGetValue(Id, out var di) && di == this)
                _all.Remove(Id);
        }
    }
}
