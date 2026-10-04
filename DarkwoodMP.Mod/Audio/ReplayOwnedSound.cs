using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Harmony;
using HarmonyLib;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// One owner per world sound. A sound played inside a vanilla method that every peer re-runs
    /// from synced state (a door opening, a lamp switching, a generator starting, a barricade
    /// being hit, an object's scrape or idle loop, a game event) is owned by that replay: each
    /// peer plays it itself, on its own copy of the object, with the game's reverb, occlusion and
    /// loop stop. The generic world-sound forward skips it; forwarding it too played every such
    /// sound twice on peers, left forwarded loops running forever (nothing forwards a stop), and
    /// echoed delayed replays (game event coroutines) back to the sender.
    /// </summary>
    internal static class ReplayOwnedSound
    {
        private static int _depth; // process-scoped: call-scoped, unwound by each scope's Finalizer / finally

        internal static bool Active => _depth > 0;

        internal static void Enter() => _depth++;

        internal static void Exit()
        {
            if (_depth > 0)
                _depth--;
        }
    }

    /// <summary>The vanilla methods whose sounds every peer replays itself (see <see cref="ReplayOwnedSound"/>).</summary>
    [OptionalPatch]
    [HarmonyPatch]
    public static class ReplayOwnedSoundScopePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // A target missing from this game build is skipped, not fatal to the others.
            foreach (MethodBase m in Candidates())
            {
                if (m != null)
                    yield return m;
            }
        }

        private static IEnumerable<MethodBase> Candidates()
        {
            // DoorOpen / DoorState replay Door.open / Door.close; BarricadeEvent replays the
            // getHit / destroy sounds (BarricadeNetHandlers).
            yield return AccessTools.Method(typeof(Door), "open", new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.Transform), typeof(float) });
            yield return AccessTools.Method(typeof(Door), "close");
            yield return AccessTools.Method(typeof(Door), "getHit", new[] { typeof(int), typeof(UnityEngine.Transform), typeof(bool), typeof(bool) });
            yield return AccessTools.Method(typeof(Door), "destroyBarricade", new[] { typeof(bool) });
            yield return AccessTools.Method(typeof(Door), "destroyDoor", new[] { typeof(bool) });
            // ItemSounds is driven by the object's own state on every peer: switch / start / loop /
            // stop through LightState and GeneratorState, spawn loops by the object existing,
            // moving scrape by MovingObjectSoundService.
            yield return AccessTools.Method(typeof(ItemSounds), "playStart");
            yield return AccessTools.Method(typeof(ItemSounds), "playIdleLoop");
            yield return AccessTools.Method(typeof(ItemSounds), "forcePlayStart");
            yield return AccessTools.Method(typeof(ItemSounds), "playStop");
            yield return AccessTools.Method(typeof(ItemSounds), "playSwitch");
            yield return AccessTools.Method(typeof(ItemSounds), "OnEnable");
            yield return AccessTools.Method(typeof(ItemSounds), "Update");
        }

        private static void Prefix() => ReplayOwnedSound.Enter();

        private static void Finalizer() => ReplayOwnedSound.Exit();
        }

    /// <summary>
    /// Game event sound steps run in the GameEvent.fire coroutine body, after its delay. Every
    /// peer replays the event (GameEventsFired for one-shots, its own run for multipleFire), so
    /// those sounds are replay-owned; a host-only spirit FX event is not replayed and keeps the
    /// forward. A scope per MoveNext also covers the replay's delayed steps, which ran after the
    /// apply guard was gone and echoed back to the sender.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch]
    public static class GameEventSoundScopePatch
    {
        private static FieldInfo _thisGoField; // process-scoped: reflection cache

        private static bool Prepare() => TargetMethod() != null;

        private static MethodBase TargetMethod()
        {
            System.Type[] nested = typeof(GameEvent).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nested.Length; i++)
            {
                System.Type t = nested[i];
                if (t.Name.IndexOf("fire", System.StringComparison.Ordinal) < 0)
                    continue;
                if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t))
                    continue;
                MethodInfo m = AccessTools.Method(t, "MoveNext");
                if (m != null)
                    return m;
            }
            return null;
        }

        // __state: whether this call entered the scope, so the Finalizer exits exactly that.
        private static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (_thisGoField == null)
                _thisGoField = AccessTools.Field(__instance.GetType(), "thisGO");
            UnityEngine.GameObject go = _thisGoField != null
                ? _thisGoField.GetValue(__instance) as UnityEngine.GameObject
                : null;
            if (go != null && Patches.GameEventsFiredPatch.IsHostOnlyFx(go.name))
                return;
            ReplayOwnedSound.Enter();
            __state = true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                ReplayOwnedSound.Exit();
        }
    }
}
