using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host: traps placed and traps gone since the world was last saved. A joiner (or a player
    /// coming back) loads the world from the last save, and only traps it already had were ever
    /// corrected: a trap placed since then did not exist for it (unseen, not disarmable), and one
    /// disarmed or picked up since then came back armed, to be disarmed and picked up a second
    /// time. After the world loads, the joiner is sent both lists.
    /// </summary>
    internal static class TrapLedger
    {
        private static readonly Dictionary<long, ItemSpawnMessage> _placed = new Dictionary<long, ItemSpawnMessage>(); // reset-in: Reset
        private static readonly List<KeyValuePair<Vector3, string>> _removed = new List<KeyValuePair<Vector3, string>>(); // reset-in: Reset
        private static float _nextScan; // reset-in: Reset

        internal static void Reset()
        {
            _placed.Clear();
            _removed.Clear();
            _nextScan = 0f;
        }

        internal static bool Active
            => ModRuntime.Network != null && ModRuntime.Network.IsConnected
               && ModRuntime.Network.Role == NetworkRole.Host && !Core.loadingGame && !Core.mainMenu;

        private static long Key(Vector3 p)
            => ((long)Mathf.RoundToInt(p.x * 10f) << 32) ^ (uint)Mathf.RoundToInt(p.z * 10f);

        /// <summary>A player placed a trap (host's own or a client's ItemSpawn applied here).</summary>
        internal static void NotePlaced(ItemSpawnMessage msg, GameObject go)
        {
            if (!Active || go == null || !TrapNetworkId.IsWorldTrap(go))
                return;
            _placed[Key(go.transform.position)] = msg;
            Watch(go);
        }

        internal static void NoteGone(GameObject go)
        {
            if (!Active || go == null)
                return;
            Vector3 p = go.transform.position;
            if (_placed.Remove(Key(p)))
                return; // placed and gone since the save: the save never had it
            if (_removed.Count < 1024)
                _removed.Add(new KeyValuePair<Vector3, string>(p, go.name));
        }

        /// <summary>The world was saved: the save now holds every trap as it is.</summary>
        internal static void OnSaved()
        {
            _placed.Clear();
            _removed.Clear();
        }

        /// <summary>Host tick: watch every world trap (location traps appear as locations spawn).</summary>
        internal static void Tick()
        {
            if (!Active || Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + 10f;
            Trigger[] all = WorldQueryHelper.GetCachedSceneComponents<Trigger>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && TrapNetworkId.IsWorldTrap(all[i].gameObject))
                    Watch(all[i].gameObject);
            }
        }

        private static void Watch(GameObject go)
        {
            if (go.GetComponent<TrapLedgerWatch>() == null)
                go.AddComponent<TrapLedgerWatch>();
        }

        internal static void SendTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            foreach (var kv in _placed)
            {
                ItemSpawnMessage msg = kv.Value;
                net.SendToPlayer(playerId, NetMessageType.ItemSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            for (int i = 0; i < _removed.Count; i++)
            {
                Vector3 p = _removed[i].Key;
                var rm = new WorldObjectRemovedMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = _removed[i].Value,
                    Mode = WorldObjectRemovedMessage.ModeRemove
                };
                net.SendToPlayer(playerId, NetMessageType.WorldObjectRemoved, w => rm.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            ModRuntime.LegacyInfo($"[TrapLedger] → p{playerId}: {_placed.Count} placed, {_removed.Count} gone since the save");
        }
    }

    /// <summary>Reports a trap's destruction (disarmed, picked up, sprung and gone) to the ledger.</summary>
    internal sealed class TrapLedgerWatch : MonoBehaviour
    {
        private static bool _quitting; // process-scoped: set once when the application quits

        private void OnApplicationQuit() => _quitting = true;

        private void OnDestroy()
        {
            if (!_quitting)
                TrapLedger.NoteGone(gameObject);
        }
    }

    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class TrapLedgerSavePatch
    {
        private static void Postfix(bool __runOriginal)
        {
            if (__runOriginal && TrapLedger.Active)
                TrapLedger.OnSaved();
        }
    }
}
