using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Client dialogue choices → time-authority apply (Horde DialogOutcomeSync slim).
/// Live tree still flushes on close via DialogueSync; this covers host-side
/// story side-effects when the authority is not in the same UI.
/// </summary>
public sealed class DialogOutcome_Patch : IPatch
{
    private static int _nextIndex;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.Name)
        {
            case "addDecision":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(DialogOutcome_Patch).GetMethod(nameof(AddDecisionPostfix), statics)!));
                break;
            case "displayNextBoard":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(DialogOutcome_Patch).GetMethod(nameof(NextBoardPrefix), statics)!));
                break;
            case "onPress":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(DialogOutcome_Patch).GetMethod(nameof(OnPressPostfix), statics)!));
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("DialogueWindow", "addDecision");
        yield return ("DialogueWindow", "displayNextBoard");
        yield return ("DialogueButton", "onPress");
    }

    public static void NextBoardPrefix() => _nextIndex = 0;

    public static void AddDecisionPostfix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var menuField = __instance.GetType().GetField("menuOptions", flags);
            if (menuField?.GetValue(__instance) is not System.Collections.IList list || list.Count == 0)
                return;
            var btn = list[list.Count - 1] as Component;
            if (btn == null) return;
            var dci = btn.GetComponent<DialogChoiceIndex>();
            if (dci == null)
                dci = btn.gameObject.AddComponent<DialogChoiceIndex>();
            dci.Index = _nextIndex++;
        }
        catch { /* best-effort */ }
    }

    public static void OnPressPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            // Only non-authority → authority (host choices apply locally)
            if (manager.IsTimeAuthority) return;

            var btn = __instance as Component;
            if (btn == null) return;
            var dci = btn.GetComponent<DialogChoiceIndex>();
            int index = dci != null ? dci.Index : -1;

            string target = "";
            try
            {
                var dest = btn.GetType().GetField("destDialogueName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                target = dest?.GetValue(btn) as string ?? "";
            }
            catch { }

            string npcName = "";
            string dialogueName = "";
            try
            {
                var ui = Singleton<global::UI>.Instance;
                var dw = ui != null ? ui.dialogueWindow : null;
                if (dw?.npc != null) npcName = dw.npc.name ?? "";
                if (dw?.currentDialogue != null) dialogueName = dw.currentDialogue.fullName ?? "";
            }
            catch { }

            if (string.IsNullOrEmpty(target) && index < 0) return;

            // Pipe-safe fields
            npcName = (npcName ?? "").Replace("|", "/");
            dialogueName = (dialogueName ?? "").Replace("|", "/");
            target = (target ?? "").Replace("|", "/");

            network.SendReliable(new DialogOutcomePacket
            {
                PlayerId = manager.LocalPlayerId,
                Payload = $"{npcName}|{index}|{dialogueName}|{target}"
            });
            ModLogger.Msg($"[DialogOutcome] Client → auth: npc={npcName} idx={index} target={target}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DialogOutcome] {ex.Message}");
        }
    }

    /// <summary>Authority: re-run the chosen dialogue node for story side-effects.</summary>
    public static void ApplyRemoteOutcome(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        try
        {
            var parts = payload.Split('|');
            if (parts.Length < 4) return;
            var target = parts[3];
            if (string.IsNullOrEmpty(target)) return;

            var ui = Singleton<global::UI>.Instance;
            var dw = ui != null ? ui.dialogueWindow : null;
            if (dw == null) return;

            RemoteApply.Active = true;
            try
            {
                dw.displayDialogue(target);
                // Don't leave host stuck in dialogue UI if they weren't talking
                try { dw.close(); } catch { }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[DialogOutcome] Auth applied target dialogue '{target}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DialogOutcome] Apply: {ex.Message}");
        }
    }
}

/// <summary>Tags dialogue option buttons with a stable board-local index.</summary>
public sealed class DialogChoiceIndex : MonoBehaviour
{
    public int Index;
}
