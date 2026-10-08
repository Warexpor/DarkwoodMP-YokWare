using System;
using DWMPHorde;
using DWMPHorde.Logging;
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
        internal void HostFireNpcCloseDialogue(NpcRef npcRef)
        {
            string npcName = npcRef.Name;
            // Still draining lookKeyhole boards; closing early would miss flags or GameEvent wiring.
            // Also while an outcome for this NPC is still queued: closing first would fire
            // onCloseDialogue before the outcome it depends on has been applied.
            if (_apply.IsWorldDrainActive || _apply.HasDeferredApplyFor(npcRef))
            {
                _apply.DeferCloseUntilDrainDone(npcRef);
                ModRuntime.LegacyInfo(
                    $"[DialogOutcome] defer onCloseDialogue until world-only drain finishes NPC={npcRef}");
                return;
            }

            NPC npc = ResolveNpc(npcRef);
            if (npc == null || npc.gameObject == null)
            {
                ModLog.WarnRate(LogCat.World, "dlg-close-npc-miss:" + npcName,
                    "[DialogOutcome] onCloseDialogue skip — NPC '" + npcRef + "' not found");
                return;
            }

            Vector3 npcPos = npc.transform.position;
            // A talk in the dream closes on the pad twin only. The sender's world bit says which;
            // without a spot (older peer) any active dream counts, as before.
            if (npcRef.HasPos ? npcRef.Dream : DreamSyncManager.IsDreamActive)
            {
                Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
                bool onPad = dreamRoot != null
                    && (npc.transform.IsChildOf(dreamRoot)
                        || Vector3.Distance(npcPos, dreamRoot.position) <= 250f);
                if (!onPad)
                {
                    ModRuntime.LegacyInfo(
                        $"[DialogOutcome] skip onCloseDialogue — dream pad NPC not ready for {npcName}");
                    return;
                }
            }
            ModRuntime.LegacyInfo(
                $"[DialogOutcome] host replaying onCloseDialogue for NPC={npcName} at ({npcPos.x.ToString("F0")},{npcPos.z.ToString("F0")})");

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

                // Replay leave-door events under the dream pad when the normal close trigger
                // did not run. Only for the bunker door NPC: any other NPC closing during a
                // dream must not re-fire the door's one-shot events or force a door open.
                if (dialogueDoorNpc)
                {
                    int leaveFired = HostFireDreamLeaveDoorGameEvents(npcPos);

                    // If the leave-door event opened the door, skip force-open so
                    // the client hears one open sound.
                    if (leaveFired == 0)
                        DWMPHorde.Patches.DialogueDoorAftermath.HostEnsureDialogueDoorOpen(npcPos);
                    else
                        DWMPHorde.Patches.DialogueDoorAftermath.NoteLeaveDoorGameEvent();
                }
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
                        $"[DialogOutcome] host force-fired leave-door GE '{n}' wasFired={wasFired}");
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
            return FindNpcByName(name, preferDreamPad: DreamSyncManager.IsDreamActive);
        }

        internal static NPC FindNpcByName(string name, bool preferDreamPad)
            => FindNpcByName(name, preferDreamPad, strictPad: false);

        /// <summary>
        /// <paramref name="strictPad"/>: with a dream active, return only an NPC on the dream pad
        /// (null when the pad twin is not loaded yet) and never fall back to the overworld twin.
        /// Callers that fire events or SetActive the result must use it (clone trap).
        /// <paramref name="lookupOnly"/>: a reader that only wants the NPC if it is there (a state
        /// snapshot): no miss warning, and an inactive one is not woken (a join woke the night
        /// trader by day).
        /// </summary>
        internal static NPC FindNpcByName(string name, bool preferDreamPad, bool strictPad, bool lookupOnly = false)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string want = StripCloneSuffix(name);

            // Unity 2021.3 supports includeInactive. Dialogue door NPCs often deactivate after talk.
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            Transform dreamRoot = preferDreamPad
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
                Transform pad = DreamSyncManager.GetDreamLocationTransform();
                bool onPad = pad != null && n.transform.IsChildOf(pad);
                if (!preferDreamPad && onPad)
                    continue;
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
            NPC found = strictPad && preferDreamPad ? bestDream : (bestDream ?? bestActive ?? bestAny);
            if (lookupOnly)
                return found;
            if (found == null)
            {
                ModLog.WarnRate(LogCat.World, "dlg-find-npc-miss:" + name,
                    $"[DialogOutcome] FindNpc miss '{name}' (scanned {all.Length} NPCs, inactive incl.)");
            }
            else if (!found.gameObject.activeInHierarchy && (dreamRoot == null || found == bestDream))
            {
                // Host world-apply needs a live target for displayDialogue / EventTriggers.
                // Never wake the overworld twin when a dream is active and only it was found.
                try { found.gameObject.SetActive(true); }
                catch { /* ignore */ }
            }
            return found;
        }

        /// <summary>
        /// The NPC a peer named on the wire: the one of that name at its spot, in its world.
        /// NPC.name is not unique (every hideout's oven is "oven"), so with a spot the body within
        /// <see cref="NpcDialogueLockPolicy.SameNpcRadius"/> is it. With none there, a name only one
        /// NPC of that world carries here is still that NPC (it walked, or its location sits in
        /// another pad slot on this machine); with several, taking one would bind another
        /// hideout's oven, so none is taken. Without a spot (an older peer) the name lookup
        /// decides, as before.
        /// </summary>
        internal static NPC ResolveNpc(NpcRef npc, bool strictPad = false)
        {
            if (!npc.IsValid) return null;
            if (!npc.HasPos)
                return strictPad
                    ? FindNpcByName(npc.Name, npc.Dream, strictPad: true)
                    : FindNpcByName(npc.Name);
            string want = StripCloneSuffix(npc.Name);
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            float r2 = NpcDialogueLockPolicy.SameNpcRadius * NpcDialogueLockPolicy.SameNpcRadius;
            NPC bestActive = null, bestAny = null, lone = null;
            float bestActiveD = float.MaxValue, bestAnyD = float.MaxValue;
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                NPC c = all[i];
                if (c == null || !NpcNameMatches(c, want)) continue;
                // The sender's world only: the pad twin and the overworld one share every name.
                bool onPad = pad != null && c.transform.IsChildOf(pad);
                if (onPad != npc.Dream) continue;
                count++;
                lone = c;
                Vector3 p = c.transform.position;
                float dx = p.x - npc.Pos.x;
                float dz = p.z - npc.Pos.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;
                // Twins at one spot (the shrine alive / dead): the one that is out.
                if (c.gameObject.activeInHierarchy && d2 < bestActiveD)
                {
                    bestActiveD = d2;
                    bestActive = c;
                }
                if (d2 < bestAnyD)
                {
                    bestAnyD = d2;
                    bestAny = c;
                }
            }

            NPC found = bestActive ?? bestAny ?? (count == 1 ? lone : null);
            if (found == null)
            {
                ModLog.WarnRate(LogCat.World, "dlg-find-npc-at-miss:" + npc.Name,
                    $"[DialogOutcome] no NPC '{npc}' (dream={npc.Dream}) within "
                    + NpcDialogueLockPolicy.SameNpcRadius.ToString("F0") + " of the sender's spot ("
                    + count + " of that name here)");
                return null;
            }
            if (!found.gameObject.activeInHierarchy)
            {
                // Host world-apply needs a live target for displayDialogue / EventTriggers.
                try { found.gameObject.SetActive(true); }
                catch { /* ignore */ }
            }
            return found;
        }

        /// <summary>
        /// Same world filter as <see cref="FindNpcByName(string, bool)"/>, then the closest body
        /// within <paramref name="maxDist"/> (null when none is that close).
        /// </summary>
        internal static NPC FindNpcByNameNear(string name, bool preferDreamPad, Vector3 near, float maxDist = float.MaxValue)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string want = StripCloneSuffix(name);
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            NPC best = null;
            float bestD = maxDist;
            for (int i = 0; i < all.Length; i++)
            {
                NPC n = all[i];
                if (n == null || !NpcNameMatches(n, want)) continue;
                bool onPad = pad != null && n.transform.IsChildOf(pad);
                if (preferDreamPad)
                {
                    if (!onPad) continue;
                }
                else if (onPad)
                {
                    continue;
                }
                float d = Vector3.Distance(n.transform.position, near);
                if (d < bestD)
                {
                    bestD = d;
                    best = n;
                }
            }
            if (best != null && !best.gameObject.activeInHierarchy)
            {
                try { best.gameObject.SetActive(true); }
                catch { /* ignore */ }
            }
            return best;
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
                // door_underground vs door_underground_act1. A short name must not
                // match every longer one ("Wolf" is not "Wolfman").
                if (IsNameSuffixVariant(cd, want) || IsNameSuffixVariant(want, cd))
                    return true;
            }
            return false;
        }

        private static bool IsNameSuffixVariant(string longer, string shorter)
        {
            if (string.IsNullOrEmpty(longer) || string.IsNullOrEmpty(shorter))
                return false;
            if (!longer.StartsWith(shorter, System.StringComparison.OrdinalIgnoreCase))
                return false;
            if (longer.Length == shorter.Length)
                return true;
            if (longer[shorter.Length] != '_')
                return false;
            string rest = longer.Substring(shorter.Length + 1);
            if (rest.Length < 4 || !rest.StartsWith("act", System.StringComparison.OrdinalIgnoreCase))
                return false;
            for (int i = 3; i < rest.Length; i++)
            {
                if (rest[i] < '0' || rest[i] > '9')
                    return false;
            }
            return true;
        }
    }
}
