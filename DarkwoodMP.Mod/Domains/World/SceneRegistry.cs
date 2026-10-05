using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Harmony;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Live set of one scene component type, kept by the hooks in <see cref="SceneRegistries"/>.
    /// Holds the same objects a FindObjectsOfType&lt;T&gt;(includeInactive: true) would return,
    /// without the scan: a scene-wide scan costs 35-50 ms in this world and was behind almost
    /// every hitch above ~40 ms (late-join bulks, trap watch, net applies on a cache miss).
    /// </summary>
    internal static class SceneRegistry<T> where T : Component
    {
        private static readonly HashSet<T> _set = new HashSet<T>(); // process-scoped: scene objects outlive a session; emptied by Clear on each world seed
        private static readonly List<T> _list = new List<T>(64); // process-scoped: same, insertion order
        private static T[] _snapshot = Array.Empty<T>(); // process-scoped: rebuilt from _list only after it changed
        private static bool _dirty; // process-scoped: _list changed since _snapshot
        private static int _pruneAt = 256; // process-scoped: Add prunes destroyed members once the list reaches this

        /// <summary>Set once by <see cref="SceneRegistries"/> for every type it keeps.</summary>
        internal static bool Tracked; // process-scoped: fixed type table

        /// <summary>Called for each new member (TrapLedger watches world traps this way).</summary>
        internal static Action<T> Added; // process-scoped: fixed subscriber

        internal static void Add(T c)
        {
            if (c == null || !_set.Add(c))
                return;
            _list.Add(c);
            _dirty = true;
            if (_list.Count >= _pruneAt)
            {
                Prune();
                _pruneAt = Mathf.Max(256, _list.Count * 2);
            }
            Added?.Invoke(c);
        }

        /// <summary>
        /// The live members (destroyed ones dropped). The array is a snapshot: it is replaced, not
        /// changed, when the set changes, so a caller can keep iterating it while objects wake
        /// (and callers can key caches on its identity, as the GameEvents soft index does).
        /// </summary>
        internal static T[] Snapshot()
        {
            Prune();
            if (_dirty)
            {
                _snapshot = _list.Count > 0 ? _list.ToArray() : Array.Empty<T>();
                _dirty = false;
            }
            return _snapshot;
        }

        internal static int Count => _list.Count;

        /// <summary>World seed: drop everything (the seed scan re-adds what is in the world).</summary>
        internal static void Clear()
        {
            _set.Clear();
            _list.Clear();
            _snapshot = Array.Empty<T>();
            _dirty = false;
            _pruneAt = 256;
        }

        private static void Prune()
        {
            int write = 0;
            for (int read = 0; read < _list.Count; read++)
            {
                T c = _list[read];
                if (c == null)
                {
                    _set.Remove(c);
                    continue;
                }
                if (write != read)
                    _list[write] = c;
                write++;
            }
            if (write == _list.Count)
                return;
            _list.RemoveRange(write, _list.Count - write);
            _dirty = true;
        }
    }

    /// <summary>
    /// Registry setup for the scene types that network handlers and late-join bulks look up.
    /// Kept as follows:
    /// <list type="bullet">
    /// <item>World load (<c>WorldGenerator.onFinished</c>, new game and save load): one scan of every
    /// MonoBehaviour, dispatched by type. This catches objects that sit inactive from scene load and
    /// never ran Awake / Start (dialogue-door open shells, deactivated traders, closed pads).</item>
    /// <item>Additive scene loads after that (outside locations and dream pads, which vanilla loads as
    /// scenes and then deactivates): every root of the new scene, inactive children included.</item>
    /// <item>Objects created later: Awake (or OnEnable / Start where vanilla has no Awake) of each
    /// type, and the whole GameObject on Item / Door / NPC / Character / ThrownItem Awake, which also
    /// catches the types with no lifecycle method at all (Constructible, Locked, Examinable, Explodes,
    /// the journal / key / quest pickup references) and Start-only components that are disabled or
    /// have not started yet.</item>
    /// </list>
    /// Destroyed objects are dropped lazily (Unity fake-null). Until the first world seed (title,
    /// world generation, a client whose own worldgen is blocked) <see cref="Seeded"/> is false and
    /// WorldQueryHelper keeps its old short-TTL scan.
    /// </summary>
    internal static class SceneRegistries
    {
        private static readonly Dictionary<Type, Action<Component>> _adders = new Dictionary<Type, Action<Component>>(); // process-scoped: fixed type table
        private static readonly List<Action> _clears = new List<Action>(); // process-scoped: fixed type table
        private static readonly Dictionary<Type, Action<Component>> _resolved = new Dictionary<Type, Action<Component>>(); // process-scoped: runtime type -> combined adder (null when none) cache
        private static readonly List<MonoBehaviour> _scratch = new List<MonoBehaviour>(64); // process-scoped: scratch
        private static bool _scratchBusy; // process-scoped: call-scoped, cleared in DispatchAll's finally
        private static bool _hooked; // process-scoped: one-time sceneLoaded hook

        /// <summary>True once the current world was seeded; false from a single scene load until then.</summary>
        internal static bool Seeded { get; private set; }

        /// <summary>Bumped by each world seed (TrapLedger re-sweeps on a new value).</summary>
        internal static int Generation { get; private set; }

        static SceneRegistries()
        {
            Track<Item>();
            Track<Door>();
            Track<Window>();
            Track<NPC>();
            Track<GameEvents>();
            Track<Trigger>();
            Track<ChainParent>();
            Track<ExperienceMachine>();
            Track<Saw>();
            Track<Feeder>();
            Track<Lure>();
            Track<ShadowArmor>();
            Track<Burn>();
            Track<Padlock>();
            Track<InteractiveItem>();
            Track<Liquid>();
            Track<Infection>();
            Track<DeathDrop>();
            Track<CustomCursorAction>();
            Track<Generator>();
            Track<Inventory>();
            Track<CharacterDialogue>();
            Track<Constructible>();
            Track<Locked>();
            Track<Character>();
            Track<Location>();
            Track<UniqueObject>();
            Track<ItemSounds>();
            Track<CutsceneManager>();
            Track<Examinable>();
            Track<Explodes>();
            Track<JournalNoteReference>();
            Track<KeyReference>();
            Track<QuestItemReference>();
            // Host trap ledger: every world trap is watched as it is registered (no 10 s rescan).
            SceneRegistry<Trigger>.Added = TrapLedger.OnTriggerRegistered;
        }

        private static void Track<T>() where T : Component
        {
            SceneRegistry<T>.Tracked = true;
            _adders[typeof(T)] = c => SceneRegistry<T>.Add((T)c);
            _clears.Add(SceneRegistry<T>.Clear);
        }

        /// <summary>True when <typeparamref name="T"/> has a registry and the world is seeded.</summary>
        internal static bool Covers<T>() where T : Component => Seeded && SceneRegistry<T>.Tracked;

        /// <summary>Plugin start: watch additive scene loads (and single loads, which unseed).</summary>
        internal static void Install()
        {
            if (_hooked) return;
            _hooked = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
            {
                // A new world is loading; it is seeded when WorldGenerator finishes.
                Seeded = false;
                return;
            }
            if (!Seeded)
                return; // world generation: the seed at onFinished covers these
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                RegisterSubtree(roots[i]);
        }

        /// <summary>
        /// World load: one scan of every MonoBehaviour (inactive included) into the registries.
        /// Runs in the onFinished frame, which already unloads unused assets and collects garbage,
        /// with the loading screen still up for at least 2.1 s after (vanilla tweenLoading delay);
        /// the scan is about one per-type scan (Unity walks every behaviour for any script type)
        /// plus the type sort, logged below.
        /// </summary>
        internal static void SeedWorld()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < _clears.Count; i++)
                _clears[i]();
            MonoBehaviour[] all;
            // Seeded first: Dispatch drops wake-ups until a world is seeded (this pass re-adds them).
            Seeded = true;
            try
            {
                all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
                for (int i = 0; i < all.Length; i++)
                    Dispatch(all[i]);
            }
            catch
            {
                Seeded = false; // half-filled: keep WorldQueryHelper on its scan
                throw;
            }
            Generation++;
            sw.Stop();
            ModLog.Event(LogCat.World, $"[SceneRegistry] world seeded: {all.Length} behaviours scanned in {sw.Elapsed.TotalMilliseconds:F0} ms "
                + $"(items={SceneRegistry<Item>.Count} doors={SceneRegistry<Door>.Count} ge={SceneRegistry<GameEvents>.Count} "
                + $"npcs={SceneRegistry<NPC>.Count} triggers={SceneRegistry<Trigger>.Count})");
        }

        /// <summary>Every tracked component on this GameObject (disabled ones included).</summary>
        internal static void RegisterGameObject(GameObject go)
        {
            if (go == null) return;
            List<MonoBehaviour> buf = TakeScratch();
            go.GetComponents(buf);
            DispatchAll(buf);
        }

        /// <summary>Every tracked component under <paramref name="root"/>, inactive children included.</summary>
        internal static void RegisterSubtree(GameObject root)
        {
            if (root == null) return;
            List<MonoBehaviour> buf = TakeScratch();
            root.GetComponentsInChildren(true, buf);
            DispatchAll(buf);
        }

        // An Added subscriber can wake objects (AddComponent); a nested register gets its own list.
        private static List<MonoBehaviour> TakeScratch()
        {
            if (_scratchBusy)
                return new List<MonoBehaviour>();
            _scratchBusy = true;
            _scratch.Clear();
            return _scratch;
        }

        private static void DispatchAll(List<MonoBehaviour> buf)
        {
            try
            {
                for (int i = 0; i < buf.Count; i++)
                    Dispatch(buf[i]);
            }
            finally
            {
                if (ReferenceEquals(buf, _scratch))
                {
                    _scratch.Clear();
                    _scratchBusy = false;
                }
            }
        }

        internal static void Dispatch(Component c)
        {
            // Before the world seed (title, world generation, save load) the seed covers everything
            // and clears the registries first: registering each wake-up there is wasted load time.
            if (!Seeded || c == null) return;
            Type t = c.GetType();
            if (!_resolved.TryGetValue(t, out Action<Component> add))
            {
                // Tracked types are base classes too (FindObjectsOfType<T> returns subclasses).
                for (Type b = t; b != null && b != typeof(MonoBehaviour); b = b.BaseType)
                {
                    if (_adders.TryGetValue(b, out Action<Component> a))
                        add += a;
                }
                _resolved[t] = add;
            }
            add?.Invoke(c);
        }
    }

    /// <summary>World seed once vanilla has finished building or loading the world.</summary>
    [HarmonyPatch(typeof(WorldGenerator), "onFinished")]
    internal static class SceneRegistrySeedPatch
    {
        private static void Postfix(bool __runOriginal)
        {
            if (!__runOriginal)
                return;
            try
            {
                SceneRegistries.SeedWorld();
            }
            catch (Exception ex)
            {
                // Never break the world load; WorldQueryHelper stays on its scan for this world.
                ModLog.Error(LogCat.World, "[SceneRegistry] world seed failed", ex);
            }
        }
    }

    /// <summary>
    /// Objects that wake after the world seed. Awake where vanilla has one (it runs even for a
    /// disabled component); Start for the types that only have Start.
    /// </summary>
    [HarmonyPatch]
    internal static class SceneRegistryLifecyclePatch
    {
        // An empty target list aborts PatchAll; skip the class instead (missing targets are logged).
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        // Prepare and the patcher both call TargetMethods; resolve (and log misses) once.
        private static List<MethodBase> _targets; // process-scoped: immutable reflection result

        private static IEnumerable<MethodBase> TargetMethods() =>
            _targets ??= new List<MethodBase>(ResolveTargets());

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            return PatchTargets.Resolve(
                PatchTargets.Find(typeof(Window), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(GameEvents), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(Trigger), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(ChainParent), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(ExperienceMachine), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Saw), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Feeder), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Lure), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(ShadowArmor), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Burn), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Padlock), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(InteractiveItem), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Liquid), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Infection), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(DeathDrop), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(CustomCursorAction), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Generator), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Inventory), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(CharacterDialogue), "Start", Type.EmptyTypes),
                PatchTargets.Find(typeof(Location), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(UniqueObject), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(ItemSounds), "Awake", Type.EmptyTypes),
                // No Awake: OnEnable on activation, Start as well (OnEnable skips a disabled component).
                PatchTargets.Find(typeof(CutsceneManager), "OnEnable", Type.EmptyTypes),
                PatchTargets.Find(typeof(CutsceneManager), "Start", Type.EmptyTypes));
        }

        private static void Postfix(MonoBehaviour __instance) => SceneRegistries.Dispatch(__instance);
    }

    /// <summary>
    /// Item / Door / NPC / Character / ThrownItem wake: register the whole GameObject. Locks,
    /// constructibles, stations, inventories, examinables, explosives and journal / key / quest
    /// pickups live on these objects; Constructible, Locked, Examinable, Explodes and the three
    /// pickup references have no lifecycle method to hook, and a Start-only sibling is then known
    /// before its own Start (or while disabled).
    /// </summary>
    [HarmonyPatch]
    internal static class SceneRegistryHostObjectAwakePatch
    {
        // An empty target list aborts PatchAll; skip the class instead (missing targets are logged).
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        // Prepare and the patcher both call TargetMethods; resolve (and log misses) once.
        private static List<MethodBase> _targets; // process-scoped: immutable reflection result

        private static IEnumerable<MethodBase> TargetMethods() =>
            _targets ??= new List<MethodBase>(ResolveTargets());

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            return PatchTargets.Resolve(
                PatchTargets.Find(typeof(Item), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(Door), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(NPC), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(Character), "Awake", Type.EmptyTypes),
                PatchTargets.Find(typeof(ThrownItem), "Awake", Type.EmptyTypes));
        }

        private static void Postfix(MonoBehaviour __instance)
        {
            if (__instance != null)
                SceneRegistries.RegisterGameObject(__instance.gameObject);
        }
    }

    /// <summary>Door.lockMe adds a Locked at runtime when the door had none.</summary>
    [HarmonyPatch(typeof(Door), "lockMe")]
    internal static class SceneRegistryDoorLockPatch
    {
        private static void Postfix(Door __instance)
        {
            if (__instance != null)
                SceneRegistries.Dispatch(__instance.GetComponent<Locked>());
        }
    }
}
