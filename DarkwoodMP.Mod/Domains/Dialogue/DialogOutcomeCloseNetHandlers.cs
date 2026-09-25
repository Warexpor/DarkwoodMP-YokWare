using System;
using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Dialog close helpers: onCloseDialogue replay, leave-door GEs, NPC name resolve.
    /// </summary>
    internal sealed class DialogOutcomeCloseNetHandlers
    {
        private readonly DialogOutcomeApplyNetHandlers _apply;

        internal DialogOutcomeCloseNetHandlers(DialogOutcomeApplyNetHandlers apply)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        /// <summary>
        /// Replay vanilla DialogueWindow.close's onCloseDialogue on the host so one-shot
        /// GameEvents (onLeaveDoorDialogue_dream_*) run and Door.open can fan out.
        /// Prefer dream-pad NPC; also force-fire leave-door GEs and open the metal door
        /// when EventTrigger requirements block the vanilla path.
        /// </summary>
        internal void HostFireNpcCloseDialogue(string npcName)
        {
            // Still draining lookKeyhole boards; closing early would miss flags or GameEvent wiring.
            if (_apply.IsWorldDrainActive)
            {
                _apply.DeferCloseUntilDrainDone(npcName);
                ModRuntime.LegacyInfo(
                    "[DialogOutcome] defer onCloseDialogue until world-only drain finishes NPC="
                    + npcName);
                return;
            }

            NPC npc = FindNpcByName(npcName);
            if (npc == null || npc.gameObject == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[DialogOutcome] onCloseDialogue skip — NPC '" + npcName + "' not found");
                return;
            }

            Vector3 npcPos = npc.transform.position;
            if (DreamSyncManager.IsDreamActive)
            {
                Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
                bool onPad = dreamRoot != null
                    && (npc.transform.IsChildOf(dreamRoot)
                        || Vector3.Distance(npcPos, dreamRoot.position) <= 250f);
                if (!onPad)
                {
                    ModRuntime.LegacyInfo(
                        "[DialogOutcome] skip onCloseDialogue — dream pad NPC not ready for "
                        + npcName);
                    return;
                }
            }
            ModRuntime.LegacyInfo(
                "[DialogOutcome] host replaying onCloseDialogue for NPC=" + npcName
                + " at (" + npcPos.x.ToString("F0") + "," + npcPos.z.ToString("F0") + ")");

            // Pre-arm mute window before onCloseDialogue / leave-door GE so peer DoorOpen
            // applies without a second openSound (GE already played it).
            string npcLower = npcName ?? "";
            bool dialogueDoorNpc =
                npcLower.IndexOf("door_underground", System.StringComparison.OrdinalIgnoreCase) >= 0
                || (npcLower.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0
                    && npcLower.IndexOf("underground", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (dialogueDoorNpc)
                DWMPHorde.Patches.DialogueDoorAftermath.NoteLeaveDoorGameEvent();

            DialogHostApplyGuard.BeginWorldOnly();
            try
            {
                if (!npc.gameObject.activeInHierarchy)
                {
                    try { npc.gameObject.SetActive(true); }
                    catch { /* ignore */ }
                }

                Core.sendTriggerInfo(npc.gameObject, EventTrigger.Type.onCloseDialogue);

                // Vanilla sendTriggerInfo only checks the root GO. Dream props sometimes
                // EventTriggers can be attached to children; fire those too.
                EventTriggers[] ets = npc.GetComponentsInChildren<EventTriggers>(true);
                for (int i = 0; i < ets.Length; i++)
                {
                    EventTriggers et = ets[i];
                    if (et == null || et.gameObject == npc.gameObject) continue;
                    try { et.fireEventTrigger(EventTrigger.Type.onCloseDialogue); }
                    catch { /* ignore */ }
                }

                // Replay leave-door events under the dream pad when the normal
                // close trigger did not run.
                int leaveFired = HostFireDreamLeaveDoorGameEvents(npcPos);

                // If the leave-door event opened the door, skip force-open so
                // the client hears one open sound.
                if (leaveFired == 0)
                    DWMPHorde.Patches.DialogueDoorAftermath.HostEnsureDialogueDoorOpen(npcPos);
                else
                    DWMPHorde.Patches.DialogueDoorAftermath.NoteLeaveDoorGameEvent();
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DialogOutcome] onCloseDialogue: " + ex.Message);
            }
            finally
            {
                DialogHostApplyGuard.EndWorldOnly();
            }

            try { DWMPHorde.Patches.DialogueDoorAftermath.OnHostDialogWorldApplied(); }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogOutcome] door poll after close: " + ex.Message);
            }
        }

        private static int HostFireDreamLeaveDoorGameEvents(Vector3 nearPos)
        {
            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            // Dream is on but the pad is not loaded. Firing now hits the
            // overworld bunker, which uses the same leave-door events.
            if (DreamSyncManager.IsDreamActive && dreamRoot == null)
                return 0;

            GameEvents[] all = WorldQueryHelper.GetCachedSceneComponents<GameEvents>();
            int fired = 0;
            for (int i = 0; i < all.Length; i++)
            {
                GameEvents ge = all[i];
                if (ge == null) continue;
                string n = ge.name ?? "";
                if (n.IndexOf("onLeaveDoor", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("DoorDialogue", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("opening_door", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (dreamRoot != null
                    && !ge.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(ge.transform.position, dreamRoot.position) > 250f)
                    continue;
                if (dreamRoot == null
                    && Vector3.Distance(ge.transform.position, nearPos) > 80f)
                    continue;

                bool wasFired = ge.fired;
                try
                {
                    // Allow re-fire if a prior premature close marked it without opening.
                    if (wasFired && !ge.multipleFire)
                        ge.fired = false;
                    ge.fire();
                    fired++;
                    ModRuntime.LegacyInfo(
                        "[DialogOutcome] host force-fired leave-door GE '" + n
                        + "' wasFired=" + wasFired);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DialogOutcome] leave-door GE '" + n + "': " + ex.Message);
                }
            }
            if (fired == 0)
                ModRuntime.LegacyInfo(
                    "[DialogOutcome] no onLeaveDoor/DoorDialogue GameEvents under dream pad");
            return fired;
        }

        internal static NPC FindNpcByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string want = StripCloneSuffix(name);

            // Unity 2021.3 supports includeInactive. Dialogue door NPCs often deactivate after talk.
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            Transform dreamRoot = DreamSyncManager.IsDreamActive
                ? DreamSyncManager.GetDreamLocationTransform()
                : null;

            NPC bestDream = null;
            NPC bestActive = null;
            NPC bestAny = null;
            float bestDreamDist = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                NPC n = all[i];
                if (n == null) continue;
                if (!NpcNameMatches(n, want)) continue;
                bestAny = n;

                if (dreamRoot != null
                    && (n.transform.IsChildOf(dreamRoot)
                        || Vector3.Distance(n.transform.position, dreamRoot.position) <= 250f))
                {
                    float d = Vector3.Distance(n.transform.position, dreamRoot.position);
                    if (d < bestDreamDist)
                    {
                        bestDreamDist = d;
                        bestDream = n;
                    }
                    continue;
                }

                if (n.gameObject.activeInHierarchy && bestActive == null)
                    bestActive = n;
            }

            // Prefer the dream pad because the overworld bunker also has door_underground.
            NPC found = bestDream ?? bestActive ?? bestAny;
            if (found == null)
            {
                ModRuntime.Log?.LogWarning(
                    $"[DialogOutcome] FindNpc miss '{name}' (scanned {all.Length} NPCs, inactive incl.)");
            }
            else if (!found.gameObject.activeInHierarchy)
            {
                // Host world-apply needs a live target for displayDialogue / EventTriggers.
                try { found.gameObject.SetActive(true); }
                catch { /* ignore */ }
            }
            return found;
        }

        internal static string StripCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string clone = "(Clone)";
            if (name.EndsWith(clone, System.StringComparison.Ordinal))
                return name.Substring(0, name.Length - clone.Length).TrimEnd();
            return name;
        }

        private static bool NpcNameMatches(NPC n, string want)
        {
            if (n == null || string.IsNullOrEmpty(want)) return false;
            string go = StripCloneSuffix(n.name);
            if (string.Equals(go, want, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (n.characterDialogue != null)
            {
                string cd = StripCloneSuffix(n.characterDialogue.name ?? "");
                if (string.Equals(cd, want, System.StringComparison.OrdinalIgnoreCase))
                    return true;
                // door_underground vs door_underground_act1
                if (!string.IsNullOrEmpty(cd)
                    && (cd.StartsWith(want, System.StringComparison.OrdinalIgnoreCase)
                        || want.StartsWith(cd, System.StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }
    }
}
