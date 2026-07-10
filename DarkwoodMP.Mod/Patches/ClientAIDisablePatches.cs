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

    [HarmonyPatch(typeof(Character), "Update")]
    public static class ClientCharacterUpdatePatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "canSeeEnemy")]
    public static class ClientAIDisableCanSeeEnemyPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "checkStuff")]
    public static class ClientAIDisableCheckStuffPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "checkForCharactersInViewRange")]
    public static class ClientAIDisableViewRangePatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "alertInArea")]
    public static class ClientAIDisableAlertInAreaPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "scareInArea")]
    public static class ClientAIDisableScareInAreaPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "heardSound")]
    public static class ClientAIDisableHeardSoundPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "alertCharactersInArea")]
    public static class ClientAIDisableAlertCharsPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "beAlerted")]
    public static class ClientAIDisableBeAlertedPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), "runAway")]
    public static class ClientAIDisableRunAwayPatch
    {
        private static bool Prefix(Character __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

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

    // -----------------------------------------------------------------------
    // AI audit v10 — remaining standalone AI components with their own Update
    // Each uses the aggressive Component overload of ShouldSkipAI to block
    // even when GetComponent<Character>() would return null.
    // -----------------------------------------------------------------------

    [HarmonyPatch(typeof(AILerp), "Update")]
    public static class ClientAILerpDisablePatch
    {
        private static bool Prefix(AILerp __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Flier), "Update")]
    public static class ClientFlierDisablePatch
    {
        private static bool Prefix(Flier __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Shooter), "Update")]
    public static class ClientShooterDisablePatch
    {
        private static bool Prefix(Shooter __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(InSightOfPlayer), "Update")]
    public static class ClientInSightOfPlayerDisablePatch
    {
        private static bool Prefix(InSightOfPlayer __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(RandomMovement), "Update")]
    public static class ClientRandomMovementDisablePatch
    {
        private static bool Prefix(RandomMovement __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Pathfinding.RVO.RVOController), "Update")]
    public static class ClientRVOControllerDisablePatch
    {
        private static bool Prefix(Pathfinding.RVO.RVOController __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(Pathfinding.RichAI), "Update")]
    public static class ClientRichAIDisablePatch
    {
        private static bool Prefix(Pathfinding.RichAI __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    // -----------------------------------------------------------------------
    // ShadowCreature — fully client-blocked. Shadows are driven entirely by
    // host state (ShadowSpawnMessage + ShadowStateUpdateMessage).
    // -----------------------------------------------------------------------

    [HarmonyPatch(typeof(ShadowCreature), "Start")]
    public static class ClientShadowStartPatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            // Let host shadows run; block on client (non-player)
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "OnEnable")]
    public static class ClientShadowOnEnablePatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "appear")]
    public static class ClientShadowAppearPatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "die")]
    public static class ClientShadowDiePatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }

    [HarmonyPatch(typeof(ShadowCreature), "Update")]
    public static class ClientShadowUpdatePatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }
    }
}
