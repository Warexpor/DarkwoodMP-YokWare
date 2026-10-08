using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// World objects a scripted GameEvent spawns (gameObject / spawn of a prefab the save keeps,
    /// such as the night mushroom of the hideout's mushroom event). The step usually picks its
    /// spot at random (a random waypoint), so a client replaying it got its own object somewhere
    /// else, or none (a client replay skips item spawns as the host's), while the host's real one
    /// never reached it: the client saw no mushroom, and harvests and stage events on the host's
    /// missed it. The host now sends each one it spawns (prefab path from the save's own prefab
    /// table, exact spot) on EntitySpawn; a client replay skips the step.
    /// </summary>
    internal static class ScriptedSpawnSync
    {
        private static GameEvent _current; // process-scoped: call-scoped, set and restored around one MoveNext
        private static List<int> _currentScenePeers; // process-scoped: call-scoped, set and restored around one MoveNext

        /// <summary>
        /// EntitySpawn path prefix for a prefab loaded from the Resources root (vanilla
        /// <c>Core.AddPrefab(string)</c> reads under <c>Prefabs/</c> only).
        /// </summary>
        internal const string ResourcesRootMark = "res:";

        private const string SubeventFolder = "events/subevents/";

        // Resolved once per prefab name: the subevent's Resources path, or null.
        private static readonly System.Collections.Generic.Dictionary<string, string> _subeventPaths = new System.Collections.Generic.Dictionary<string, string>(); // process-scoped: immutable asset lookup

        /// <summary>Paths CoreAddPrefabPhysicsSyncPatch sends; anything else on EntitySpawn is a scripted spawn.</summary>
        private static bool IsGenericPath(string path)
            => path.StartsWith("Items/") || path.StartsWith("Traps/") || path.StartsWith("Objects/") || path.StartsWith("FX/");

        /// <summary>The save's prefab path for this prefab name (the table the save respawns objects from), or null.</summary>
        internal static string PathOf(string prefabName)
        {
            SaveManager sm = Singleton<SaveManager>.Instance;
            if (sm == null || string.IsNullOrEmpty(prefabName))
                return null;
            if (sm.prefabDictionary != null && sm.prefabDictionary.TryGetValue(prefabName, out string p))
                return p;
            if (sm.prefabNames == null || sm.prefabPaths == null)
                return null;
            int n = Mathf.Min(sm.prefabNames.Count, sm.prefabPaths.Count);
            for (int i = 0; i < n; i++)
                if (sm.prefabNames[i] == prefabName)
                    return sm.prefabPaths[i];
            return null;
        }

        /// <summary>A world object the save keeps (not a creature: those go by entity snapshots).</summary>
        internal static bool IsWorldObject(Transform prefab)
            => prefab != null && prefab.GetComponent<Character>() == null && PathOf(prefab.name) != null;

        internal static bool IsWorldObjectSpawn(GameEvent ge)
            => ge != null && ge.type == GameEvent.Type.gameObject
               && ge.gameObjectModifyType == GameEvent.GameObjectModify.spawn
               && IsWorldObject(ge.targetTransform);

        /// <summary>
        /// A creature's own scripted step spawning an event object (the banshee's attack spawns
        /// Banshee_attack_event_01: its sound, a run-away order, more spawns). A client's copy of the
        /// creature runs none of its own events, so the object was never there: the host's fire of
        /// it found nothing ("no GameEvents near ... dropped") and the client missed its sound.
        /// </summary>
        internal static bool IsCreatureSubeventSpawn(GameEvent ge, GameObject owner)
            => ge != null && owner != null && ge.type == GameEvent.Type.gameObject
               && ge.gameObjectModifyType == GameEvent.GameObjectModify.spawn
               && ge.targetTransform != null && ge.targetTransform.GetComponent<GameEvents>() != null
               && owner.GetComponentInParent<Character>() != null
               && SubeventPath(ge.targetTransform.gameObject) != null;

        /// <summary>The Resources path of a subevent prefab, or null.</summary>
        internal static string SubeventPath(GameObject prefab)
        {
            if (prefab == null)
                return null;
            if (_subeventPaths.TryGetValue(prefab.name, out string known))
                return known;
            string path = SubeventFolder + prefab.name;
            Object loaded = Resources.Load(path);
            string result = loaded == prefab ? path : null;
            _subeventPaths[prefab.name] = result;
            return result;
        }

        /// <summary>Host: run this spawn step with itself as the current one (its AddPrefab is sent).</summary>
        internal static IEnumerator WrapHost(GameEvent ge, IEnumerator inner, GameObject owner)
        {
            if (inner == null || !NetGuard.ConnectedHost(out _))
                return inner;
            // A night scene's own spawn (the knocking visitor at a door): the piece belongs to the
            // peers who have the scene (NightEventAnchor.ScenePeersOf), wherever it is parented.
            List<int> scenePeers = ge != null && owner != null && ge.type == GameEvent.Type.gameObject
                && ge.gameObjectModifyType == GameEvent.GameObjectModify.spawn
                ? NightEventAnchor.ScenePeersOf(owner.transform) : null;
            if (scenePeers == null && !IsWorldObjectSpawn(ge) && !IsCreatureSubeventSpawn(ge, owner))
                return inner;
            return new Scoped(inner, ge, scenePeers);
        }

        /// <summary>Core.AddPrefab(Object) postfix: the host's spawn step just placed its object.</summary>
        internal static void OnAddPrefab(GameObject result, Object prefab)
        {
            GameEvent ge = _current;
            if (ge == null || result == null || prefab == null || ge.targetTransform == null
                || prefab != ge.targetTransform.gameObject)
                return;
            // A scene piece's own later fires go to the scene's peers. A world object a scene spawns
            // (the night mushroom) is still sent below: it is the world's, and a replay skips it.
            if (_currentScenePeers != null)
                NightEventAnchor.NoteScenePiece(result.transform, _currentScenePeers);
            if (!IsWorldObject(ge.targetTransform) && !(prefab is GameObject g && SubeventPath(g) != null && _currentScenePeers == null))
                return;
            if (TraverseHack.ApplyingFromNetwork || Core.loadingGame || !Core.worldGenFinished())
                return;
            if (PersonalPrologue.IsOnProloguePad(result.transform))
                return;
            if (!NetGuard.ConnectedHost(out LanNetworkManager net))
                return;
            string path = PathOf(prefab.name);
            if (string.IsNullOrEmpty(path))
            {
                string sub = SubeventPath(prefab as GameObject);
                if (sub == null)
                    return;
                path = ResourcesRootMark + sub;
            }
            Vector3 pos = result.transform.position;
            Vector3 rot = result.transform.rotation.eulerAngles;
            var msg = new EntitySpawnMessage
            {
                PrefabPath = path,
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                RotX = rot.x, RotY = rot.y, RotZ = rot.z
            };
            net.Broadcast(NetMessageType.EntitySpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[ScriptedSpawn] sent {path} at {pos} (event {ge.customName})");
        }

        /// <summary>
        /// Client: a scripted spawn landed. Vanilla's step files it under the location it stands
        /// in (parent and object list); EntitySpawn placed it at the scene root.
        /// </summary>
        internal static void OnClientSpawned(GameObject go, string path)
        {
            if (go == null || string.IsNullOrEmpty(path) || IsGenericPath(path))
                return;
            Location loc = Location.getAtPos(go.transform.position);
            if (loc == null)
                return;
            go.transform.SetParent(loc.transform, true);
            loc.addToObjects(go);
        }

        private sealed class Scoped : IEnumerator
        {
            private readonly IEnumerator _inner;
            private readonly GameEvent _ge;
            private readonly List<int> _scenePeers;

            internal Scoped(IEnumerator inner, GameEvent ge, List<int> scenePeers)
            {
                _inner = inner;
                _ge = ge;
                _scenePeers = scenePeers;
            }

            public object Current => _inner.Current;

            public void Reset() => _inner.Reset();

            public bool MoveNext()
            {
                GameEvent prev = _current;
                List<int> prevPeers = _currentScenePeers;
                _current = _ge;
                _currentScenePeers = _scenePeers;
                try { return _inner.MoveNext(); }
                finally
                {
                    _current = prev;
                    _currentScenePeers = prevPeers;
                }
            }
        }
    }
}
