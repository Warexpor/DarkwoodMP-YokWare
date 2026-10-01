using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Disables client-side AI and freezes world-entity AI on the host during dreams.
/// Character and component patches share ClientAIConditionalHelper so the
/// client and host rules stay in one place.
/// </summary>
namespace DWMPHorde.Patches
{
    /// <summary>Determines whether a character or component should skip its AI update.</summary>
    internal static class ClientAIConditionalHelper
    {
        // These prefixes run per frame for every AI component on a client: Object.name allocates a
        // new string on each read, so the "is this a remote player proxy" answer is cached per object.
        private static readonly Dictionary<int, bool> _remotePlayerByInstance = new Dictionary<int, bool>(256);

        internal static void Reset() => _remotePlayerByInstance.Clear();

        private static bool IsRemotePlayerObject(UnityEngine.Object o)
        {
            int id = o.GetInstanceID();
            if (_remotePlayerByInstance.TryGetValue(id, out bool v))
                return v;
            if (_remotePlayerByInstance.Count > 4096)
                _remotePlayerByInstance.Clear();
            v = o.name.Contains("RemotePlayer");
            _remotePlayerByInstance[id] = v;
            return v;
        }

        internal static bool ShouldSkipAI(Character c)
        {
            if (ModRuntime.Network == null)
                return false;

            // Freeze host world entities while the dream session is active.
            if (ModRuntime.Network.Role == NetworkRole.Host && DreamSyncManager.IsWorldFrozenForComponent(c))
                return true;

            if (ModRuntime.Network.Role != NetworkRole.Client)
                return false;
            if (c == null || IsRemotePlayerObject(c))
                return false;

            // The host broadcasts entity state for the client to present.
            return true;
        }

        // Component overload: also covers pathfinding and helper objects that
        // do not carry a Character component.
        internal static bool ShouldSkipAI(Component comp)
        {
            if (ModRuntime.Network == null)
                return false;

            // Apply the same dream freeze to component-owned AI.
            if (ModRuntime.Network.Role == NetworkRole.Host && DreamSyncManager.IsWorldFrozenForComponent(comp))
                return true;

            if (ModRuntime.Network.Role != NetworkRole.Client)
                return false;
            if (comp == null)
                return false;
            bool isRemotePlayer = IsRemotePlayerObject(comp);
            bool isLocalPlayer = Player.Instance != null
                && comp.gameObject == Player.Instance.gameObject;
            return AiSuppressionPolicy.ShouldSuppressClientComponent(
                isClient: true, isRemotePlayer: isRemotePlayer, isLocalPlayer: isLocalPlayer);
        }
    }

    // -----------------------------------------------------------------------
    // Character methods share the Character overload and prefix.
    // -----------------------------------------------------------------------

    // Stacked class-level [HarmonyPatch(typeof, name)] attributes merge into one target, so each
    // group below resolves its targets explicitly. Character.alertInArea / scareInArea are static
    // (no instance to gate on); their per-character effects run through the patched instance
    // methods (heardSound).
    [HarmonyPatch]
    public static class ClientAIDisableCharacterPatches
    {
        // An empty target list aborts PatchAll; skip the class instead (missing targets are logged).
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        // Prepare and the patcher both call TargetMethods; resolve (and log misses) once.
        private static List<MethodBase> _targets; // process-scoped: immutable reflection result

        private static IEnumerable<MethodBase> TargetMethods() =>
            _targets ??= new List<MethodBase>(ResolveTargets());

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            Type t = typeof(Character);
            return PatchTargets.Resolve(
                PatchTargets.Find(t, "Update", Type.EmptyTypes),
                PatchTargets.Find(t, "canSeeEnemy", Type.EmptyTypes),
                PatchTargets.Find(t, "checkStuff", Type.EmptyTypes),
                PatchTargets.Find(t, "checkForCharactersInViewRange", Type.EmptyTypes),
                PatchTargets.Find(t, "heardSound", new[] { typeof(Vector3), typeof(float), typeof(bool), typeof(float), typeof(bool) }),
                PatchTargets.Find(t, "alertCharactersInArea", new[] { typeof(float), typeof(bool) }),
                PatchTargets.Find(t, "beAlerted", new[] { typeof(Transform), typeof(bool) }),
                PatchTargets.Find(t, "runAway", new[] { typeof(Vector3) }));
        }

        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // -----------------------------------------------------------------------
    // Component methods use the Component overload so Character is optional.
    // -----------------------------------------------------------------------

    [HarmonyPatch]
    public static class ClientAIDisableComponentPatches
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
                PatchTargets.Find(typeof(AILerp), "Update", Type.EmptyTypes),
                PatchTargets.Find(typeof(Flier), "Update", Type.EmptyTypes),
                PatchTargets.Find(typeof(Shooter), "Update", Type.EmptyTypes),
                PatchTargets.Find(typeof(InSightOfPlayer), "Update", Type.EmptyTypes),
                PatchTargets.Find(typeof(RandomMovement), "Update", Type.EmptyTypes),
                PatchTargets.Find(typeof(Pathfinding.RVO.RVOController), "Update", Type.EmptyTypes),
                PatchTargets.Find(typeof(Pathfinding.RichAI), "Update", Type.EmptyTypes));
        }

        private static bool Prefix(Component __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // -----------------------------------------------------------------------
    // ShadowCreature is driven by host state on clients.
    // -----------------------------------------------------------------------

    [HarmonyPatch]
    public static class ClientShadowCreaturePatches
    {
        // An empty target list aborts PatchAll; skip the class instead (missing targets are logged).
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        // Prepare and the patcher both call TargetMethods; resolve (and log misses) once.
        private static List<MethodBase> _targets; // process-scoped: immutable reflection result

        private static IEnumerable<MethodBase> TargetMethods() =>
            _targets ??= new List<MethodBase>(ResolveTargets());

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            Type t = typeof(ShadowCreature);
            return PatchTargets.Resolve(
                PatchTargets.Find(t, "Start", Type.EmptyTypes),
                PatchTargets.Find(t, "OnEnable", Type.EmptyTypes),
                PatchTargets.Find(t, "appear", Type.EmptyTypes),
                PatchTargets.Find(t, "die", Type.EmptyTypes),
                PatchTargets.Find(t, "Update", Type.EmptyTypes));
        }

        private static bool Prefix(ShadowCreature __instance)
        {
            // Host shadows run normally; clients only present host state.
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // Sniffer / AIPath can live on helper objects without a Character component.
    // Use the component overload so the null-Character gap cannot run AI on clients.

    [HarmonyPatch(typeof(Sniffer), "Update")]
    public static class ClientSnifferDisablePatch
    {
        private static bool Prefix(Sniffer __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(AIPath), "Update")]
    public static class ClientAIPathDisablePatch
    {
        private static bool Prefix(AIPath __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }
}