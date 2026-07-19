using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Path B port of Yokyy DialogueSync: replicate CharacterDialogue consumed-node
    /// state (alreadyShown / disabled / specialOptions / portrait) + NPC wantsToTalk/rep
    /// so peers share the same dialogue tree after a conversation.
    /// </summary>
    public static class DialogTreeSync
    {
        public static void TryBroadcastFromNpc(NPC npc)
        {
            if (npc == null) return;
            CharacterDialogue cd = npc.characterDialogue;
            if (cd == null) return;
            TryBroadcast(cd, npc);
        }

        public static void TryBroadcast(CharacterDialogue cd, NPC npc = null)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (cd == null || string.IsNullOrEmpty(cd.name)) return;

            string payload = EncodeFromGame(cd, npc);
            if (string.IsNullOrEmpty(payload)) return;

            var msg = new DialogTreeStateMessage { Payload = payload };
            if (net.Role == NetworkRole.Host)
            {
                net.Broadcast(NetMessageType.DialogTreeState, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
            else if (net.Role == NetworkRole.Client)
            {
                net.Send(NetMessageType.DialogTreeState, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }

            ModLog.Event(LogCat.Session,
                $"[DialogTree] sent '{cd.name}' npc={(npc != null ? npc.name : "")}");
        }

        public static void ApplyPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            if (!DialogTreeWireCodec.TryDecode(
                    payload,
                    out string name,
                    out int[] nodeFlags,
                    out int portrait,
                    out string npcName,
                    out char wants,
                    out string rep,
                    out List<(string name, string type)> specials))
                return;

            using (new NetworkApplyGuard())
            {
                var flags = Singleton<Flags>.Instance;
                CharacterDialogue cd = flags != null ? flags.getDialogue(name) : null;
                if (cd != null)
                    ApplyToDialogue(cd, nodeFlags, portrait, specials);
                else
                    ModLog.Warn(LogCat.Session, "[DialogTree] no Flags dialogue '" + name + "'");

                // Scene-local copies not yet rebound to Flags asset.
                // includeInactive: dialogue props (door_underground) often deactivate post-talk.
                CharacterDialogue[] sceneCds = Object.FindObjectsOfType<CharacterDialogue>(true);
                for (int i = 0; i < sceneCds.Length; i++)
                {
                    CharacterDialogue sceneCd = sceneCds[i];
                    if (sceneCd == null || sceneCd == cd) continue;
                    if (sceneCd.name != name) continue;
                    ApplyToDialogue(sceneCd, nodeFlags, portrait, specials);
                }

                NPC[] npcs = Object.FindObjectsOfType<NPC>(true);
                for (int i = 0; i < npcs.Length; i++)
                {
                    NPC n = npcs[i];
                    if (n == null || n.characterDialogue == null) continue;
                    if (n.characterDialogue.name != name) continue;
                    n.portraitType = (CharacterDialogue.PortraitType)portrait;
                }

                if (!string.IsNullOrEmpty(npcName))
                    ApplyNpcState(npcName, wants, rep);
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[DialogTree] applied '" + name + "'");
        }

        /// <summary>Host late-join: send every progressed dialogue tree to one peer.</summary>
        public static void SendBulkTo(LanNetworkManager net, int targetPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host || targetPlayerId <= 0)
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null || flags.dialogues == null) return;

            int sent = 0;
            for (int i = 0; i < flags.dialogues.Count; i++)
            {
                CharacterDialogue cd = flags.dialogues[i];
                if (cd == null || string.IsNullOrEmpty(cd.name)) continue;
                if (!HasProgressGame(cd)) continue;

                string payload = EncodeFromGame(cd, npc: null);
                if (string.IsNullOrEmpty(payload)) continue;

                var msg = new DialogTreeStateMessage { Payload = payload };
                net.SendToPlayer(targetPlayerId, NetMessageType.DialogTreeState,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }

            // Non-default NPC conversation state (wantsToTalk / rep) without full tree progress.
            if (flags.npcStates != null)
            {
                for (int i = 0; i < flags.npcStates.Count; i++)
                {
                    Flags.NPCState st = flags.npcStates[i];
                    if (st == null || string.IsNullOrEmpty(st.name)) continue;
                    if (st.wantsToTalk && st.reputation == 0) continue;

                    // Minimal payload: empty node flags, attach NPC state only.
                    // Use a synthetic dialogue name prefix so Apply still runs NPC state.
                    // Prefer attaching to real dialogue if NPC is live.
                    CharacterDialogue linked = null;
                    NPC live = FindNpc(st.name);
                    if (live != null && live.characterDialogue != null)
                        linked = live.characterDialogue;
                    if (linked == null)
                        continue; // needs a dialogue asset; reputation bulk covers rep alone

                    if (!HasProgressGame(linked))
                    {
                        string payload = EncodeFromGame(linked, live);
                        if (string.IsNullOrEmpty(payload)) continue;
                        var msg = new DialogTreeStateMessage { Payload = payload };
                        net.SendToPlayer(targetPlayerId, NetMessageType.DialogTreeState,
                            w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                        sent++;
                    }
                }
            }

            ModLog.Event(LogCat.Session,
                $"[DialogTree] bulk → p{targetPlayerId}: {sent} snapshot(s)");
        }

        private static string EncodeFromGame(CharacterDialogue cd, NPC npc)
        {
            if (cd == null || string.IsNullOrEmpty(cd.name)) return "";

            int[] nodeFlags = null;
            if (cd.dialogues != null)
            {
                nodeFlags = new int[cd.dialogues.Count];
                for (int i = 0; i < cd.dialogues.Count; i++)
                {
                    var node = cd.dialogues[i];
                    if (node == null)
                    {
                        nodeFlags[i] = 0;
                        continue;
                    }
                    nodeFlags[i] = DialogTreeWireCodec.PackNodeFlag(node.alreadyShown, node.disabled);
                }
            }

            var specials = new List<(string, string)>();
            if (cd.specialOptions != null)
            {
                for (int i = 0; i < cd.specialOptions.Count; i++)
                {
                    var opt = cd.specialOptions[i];
                    if (opt == null) continue;
                    specials.Add((opt.name ?? "", opt.type ?? ""));
                }
            }

            string npcName = "";
            char wants = '-';
            string rep = "-";
            if (npc != null)
            {
                npcName = npc.name ?? "";
                var state = Singleton<Flags>.Instance != null
                    ? Singleton<Flags>.Instance.getNPCState(npcName)
                    : null;
                if (state != null)
                {
                    wants = state.wantsToTalk ? '1' : '0';
                    rep = state.reputation.ToString();
                }
            }

            return DialogTreeWireCodec.Encode(
                cd.name,
                nodeFlags,
                (int)cd.portraitType,
                npcName,
                wants,
                rep,
                specials);
        }

        private static bool HasProgressGame(CharacterDialogue cd)
        {
            if (cd == null) return false;
            int[] nodeFlags = null;
            if (cd.dialogues != null)
            {
                nodeFlags = new int[cd.dialogues.Count];
                for (int i = 0; i < cd.dialogues.Count; i++)
                {
                    var node = cd.dialogues[i];
                    nodeFlags[i] = node != null
                        ? DialogTreeWireCodec.PackNodeFlag(node.alreadyShown, node.disabled)
                        : 0;
                }
            }
            int specials = cd.specialOptions != null ? cd.specialOptions.Count : 0;
            return DialogTreeWireCodec.HasProgress(
                nodeFlags,
                specials,
                (int)cd.portraitType,
                (int)cd.initialPortraitType);
        }

        private static void ApplyToDialogue(
            CharacterDialogue cd,
            int[] nodeFlags,
            int portrait,
            List<(string name, string type)> specials)
        {
            if (cd == null) return;

            if (cd.dialogues != null && nodeFlags != null)
            {
                int count = System.Math.Min(cd.dialogues.Count, nodeFlags.Length);
                for (int i = 0; i < count; i++)
                {
                    var node = cd.dialogues[i];
                    if (node == null) continue;
                    int v = nodeFlags[i];
                    node.alreadyShown = DialogTreeWireCodec.UnpackAlreadyShown(v);
                    node.disabled = DialogTreeWireCodec.UnpackDisabled(v);
                }
            }

            if (cd.specialOptions != null)
            {
                cd.specialOptions.Clear();
                if (specials != null)
                {
                    for (int i = 0; i < specials.Count; i++)
                    {
                        var (n, t) = specials[i];
                        cd.specialOptions.Add(new CharacterDialogue.SpecialOption(n, t));
                    }
                }
            }

            cd.portraitType = (CharacterDialogue.PortraitType)portrait;
        }

        private static void ApplyNpcState(string npcName, char wants, string rep)
        {
            var flags = Singleton<Flags>.Instance;
            if (flags == null || string.IsNullOrEmpty(npcName)) return;

            Flags.NPCState state = flags.getNPCState(npcName);
            if (state == null)
            {
                state = new Flags.NPCState { name = npcName, wantsToTalk = true };
                flags.npcStates.Add(state);
            }

            if (wants == '1') state.wantsToTalk = true;
            else if (wants == '0') state.wantsToTalk = false;

            if (rep != "-" && int.TryParse(rep, out int repValue))
                state.reputation = repValue;
        }

        private static NPC FindNpc(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            NPC[] all = Object.FindObjectsOfType<NPC>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name)
                    return all[i];
            }
            return null;
        }
    }
}
