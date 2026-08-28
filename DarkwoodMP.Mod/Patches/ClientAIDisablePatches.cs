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
        internal static bool ShouldSkipAI(Character c)
        {
            if (ModRuntime.Network == null)
                return false;

            // Freeze host world entities while the dream session is active.
            if (ModRuntime.Network.Role == NetworkRole.Host && DreamSyncManager.IsWorldFrozenForComponent(c))
                return true;

            if (ModRuntime.Network.Role != NetworkRole.Client)
                return false;
            if (c == null || c.name.Contains("RemotePlayer"))
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
            bool isRemotePlayer = comp.name.Contains("RemotePlayer");
            bool isLocalPlayer = Player.Instance != null
                && comp.gameObject == Player.Instance.gameObject;
            return AiSuppressionPolicy.ShouldSuppressClientComponent(
                isClient: true, isRemotePlayer: isRemotePlayer, isLocalPlayer: isLocalPlayer);
        }
    }

    // -----------------------------------------------------------------------
    // Character methods share the Character overload and prefix.
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
    // Component methods use the Component overload so Character is optional.
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
    // ShadowCreature is driven by host state on clients.
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