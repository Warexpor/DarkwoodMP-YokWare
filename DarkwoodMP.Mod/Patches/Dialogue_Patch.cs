using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// World NPC dialogue story hook (v0.7). When a player finishes a conversation
/// with an NPC, its dialogue tree has progressed: nodes marked alreadyShown,
/// gossip consumed (disabled/alreadyShown), special options unlocked, portrait
/// changed. All of that lives on the shared CharacterDialogue owned by the Flags
/// singleton (see DialogueSync). Most story OUTCOMES (worldFlag, journal, dreams)
/// already replicate through their own channels, but the dialogue's own consumed
/// state did not - so the partner would still be offered gossip/options the first
/// player already exhausted, and a later save would diverge.
///
/// DialogueWindow.close is the right beat: by the time it fully closes, every
/// board/outcome for the conversation has run. close() early-returns (without
/// nulling npc) when it still has an exit dialogue to display - we detect the
/// REAL close by npc having been nulled at the end of the method, and only then
/// snapshot &amp; broadcast the just-talked NPC's dialogue.
/// </summary>
public sealed class Dialogue_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(Dialogue_Patch).GetMethod(nameof(ClosePrefix), statics)!),
            postfix: new HarmonyMethod(typeof(Dialogue_Patch).GetMethod(nameof(ClosePostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("DialogueWindow", "close");
    }

    // Capture the NPC before close() runs; close() sets npc = null on a real
    // close, so the postfix cannot read it afterwards. The captured reference
    // stays valid - only the window's field is cleared.
    public static void ClosePrefix(DialogueWindow __instance, ref NPC __state)
    {
        __state = __instance != null ? __instance.npc : null;
    }

    public static void ClosePostfix(DialogueWindow __instance, NPC __state)
    {
        try
        {
            if (__state == null) return;
            // Real close only: an exit-dialogue early return leaves npc set.
            if (__instance != null && __instance.npc != null) return;

            // Releasing the NPC lock is independent of the RemoteApply guard: a
            // force-close (tie-break loss) still needs to drop any lock we held.
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<InteractionLock>()
                ?.ReleaseIfKind(InteractionLock.Kind.Npc);

            if (RemoteApply.Active) return;
            var dialogueSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<DialogueSync>();
            dialogueSync?.Broadcast(__state.characterDialogue, __state);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Dialogue_Patch] {ex.Message}");
        }
    }
}
