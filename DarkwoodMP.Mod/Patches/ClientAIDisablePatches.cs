using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Disables AI on client (host-authoritative) and freezes world-entity AI on host during dreams.
/// Prefixes on 20+ Character/component Update/FixedUpdate methods all route through
/// ClientAIConditionalHelper.ShouldSkipAI() — single branching point for client-vs-host logic.
/// </summary>
namespace DWMPHorde.Patches
{
    /// <summary>Determines if AI should be skipped for a character. On client: all AI skipped (host-authoritative). On host: skips AI for frozen world characters during dreams.</summary>
    internal static class ClientAIConditionalHelper
    {
        internal static bool ShouldSkipAI(Character c)
        {
            if (ModRuntime.Network == null)
                return false;

            // Host dream freeze: block AI on all pre-dream host characters
            if (ModRuntime.Network.Role == NetworkRole.Host && DreamSyncManager.IsWorldFrozenForComponent(c))
                return true;

            if (ModRuntime.Network.Role != NetworkRole.Client)
                return false;
            if (c == null || c.name.Contains("RemotePlayer"))
                return false;

            // Host is authoritative for ALL AI.  The client must never run AI
            // independently — every entity near either player is broadcast by the
            // host at 3500f range, so the client only needs to render the
            // received state.
            return true;
        }

        // Aggressive overload: blocks ANY non-player component on the client,
        // regardless of whether a Character component exists on the GameObject.
        // This catches components on entities that lack a Character (e.g.,
        // RVOController, RichAI on pathfinding objects) and eliminates the
        // null-Character gap where GetComponent<Character>() might return null.
        internal static bool ShouldSkipAI(Component comp)
        {
            if (ModRuntime.Network == null)
                return false;

            // Host dream freeze: block AI on all pre-dream host characters
            if (ModRuntime.Network.Role == NetworkRole.Host && DreamSyncManager.IsWorldFrozenForComponent(comp))
                return true;

            if (ModRuntime.Network.Role != NetworkRole.Client)
                return false;
            if (comp == null)
                return false;
            if (comp.name.Contains("RemotePlayer"))
                return false;
            if (Player.Instance != null && comp.gameObject == Player.Instance.gameObject)
                return false;
            return true;
        }
    }

    // -----------------------------------------------------------------------
    // Character methods — all skip via the Character overload. One class with
    // multiple [HarmonyPatch] targets and a single shared prefix.
    // -----------------------------------------------------------------------

    [HarmonyPatch(typeof(Character), "Update")]
    [HarmonyPatch(typeof(Character), "canSeeEnemy")]
    [HarmonyPatch(typeof(Character), "checkStuff")]
    [HarmonyPatch(typeof(Character), "checkForCharactersInViewRange")]
    [HarmonyPatch(typeof(Character), "alertInArea")]
    [HarmonyPatch(typeof(Character), "scareInArea")]
    [HarmonyPatch(typeof(Character), "heardSound")]
    [HarmonyPatch(typeof(Character), "alertCharactersInArea")]
    [HarmonyPatch(typeof(Character), "beAlerted")]
    [HarmonyPatch(typeof(Character), "runAway")]
    public static class ClientAIDisableCharacterPatches
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // -----------------------------------------------------------------------
    // Component methods — aggressive Component overload (block even without a
    // Character component). One class, multiple targets, single shared prefix.
    // -----------------------------------------------------------------------

    [HarmonyPatch(typeof(AILerp), "Update")]
    [HarmonyPatch(typeof(Flier), "Update")]
    [HarmonyPatch(typeof(Shooter), "Update")]
    [HarmonyPatch(typeof(InSightOfPlayer), "Update")]
    [HarmonyPatch(typeof(RandomMovement), "Update")]
    [HarmonyPatch(typeof(Pathfinding.RVO.RVOController), "Update")]
    [HarmonyPatch(typeof(Pathfinding.RichAI), "Update")]
    public static class ClientAIDisableComponentPatches
    {
        private static bool Prefix(Component __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // -----------------------------------------------------------------------
    // ShadowCreature — fully client-blocked. Driven entirely by host state.
    // -----------------------------------------------------------------------

    [HarmonyPatch(typeof(ShadowCreature), "Start")]
    [HarmonyPatch(typeof(ShadowCreature), "OnEnable")]
    [HarmonyPatch(typeof(ShadowCreature), "appear")]
    [HarmonyPatch(typeof(ShadowCreature), "die")]
    [HarmonyPatch(typeof(ShadowCreature), "Update")]
    public static class ClientShadowCreaturePatches
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            // Let host shadows run; block on client (non-player)
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // Sniffer / AIPath resolve the Character component first, so they keep the
    // Character overload (null-Character stays unblocked — not the aggressive path).

    [HarmonyPatch(typeof(Sniffer), "Update")]
    public static class ClientSnifferDisablePatch
    {
        private static bool Prefix(Sniffer __instance)
        {
            Character c = __instance.GetComponent<Character>();
            return !ClientAIConditionalHelper.ShouldSkipAI(c);
        }
    }

    [HarmonyPatch(typeof(AIPath), "Update")]
    public static class ClientAIPathDisablePatch
    {
        private static bool Prefix(AIPath __instance)
        {
            Character c = __instance.GetComponent<Character>();
            return !ClientAIConditionalHelper.ShouldSkipAI(c);
        }
    }
}