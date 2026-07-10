using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Replicates World NPC dialogue node/consumed state between players (v0.7).
///
/// Darkwood stores the persistent state of every conversation centrally in the
/// Flags singleton: Flags.dialogues is a List&lt;CharacterDialogue&gt; loaded from
/// Resources("Dialogue"), and NPC.init rebinds NPC.characterDialogue to the
/// matching entry via Flags.getDialogue(name) (verified in IL). That shared
/// CharacterDialogue is exactly what the save system persists through
/// Flags.SaveState.dialogueStates -&gt; CharacterDialogue.SaveState: per node,
/// alreadyShown + disabled, plus the NPC's specialOptions list and portraitType.
///
/// "Everything that talks" is an NPC component in Darkwood - traders, the oven,
/// wardrobe, shrine, doors - so the same snapshot covers them all. On top of the
/// node state, per-NPC conversation side effects live in Flags.NPCState:
/// wantsToTalk (the setDontWantToTalk outcome) and reputation (modifyReputation
/// outcome); both are carried in the snapshot too (v2) and in the join snapshot.
///
/// When one player finishes talking (Dialogue_Patch on DialogueWindow.close), the
/// NPC's dialogue is snapshotted and broadcast keyed by the dialogue's name. The
/// receiver applies it to the Flags-owned copy AND any loaded scene instance with
/// the same name (an NPC whose init hasn't rebound yet still holds its scene
/// component), so both players see the same available dialogue tree, and a later
/// save persists identical progression. Nodes match BY INDEX - the same order
/// the game's own CharacterDialogue.SaveState.loadValues uses, valid because
/// both machines load the identical "Dialogue" resource set.
/// </summary>
public class DialogueSync
{
    private readonly NetworkLayer _network;

    private const string Channel = "dlgstate:";
    private const string NpcStateChannel = "npcstate:";

    public DialogueSync(NetworkLayer network)
    {
        _network = network;
    }

    private int MyId => _network != null ? Math.Max(_network.LocalClientId, 0) : 0;

    // ------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------

    /// <summary>
    /// Broadcast the consumed state of an NPC's dialogue tree, including the
    /// NPC-side conversation effects (wantsToTalk, reputation) when the live
    /// NPC is known.
    /// </summary>
    public void Broadcast(CharacterDialogue cd, NPC npc = null)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (cd == null || string.IsNullOrEmpty(cd.name)) return;
            if (_network == null || !_network.IsConnected) return;

            string npcName = "";
            char wants = '-';
            string rep = "-";
            if (npc != null)
            {
                npcName = npc.name ?? "";
                var state = Singleton<Flags>.Instance?.getNPCState(npcName);
                if (state != null)
                {
                    wants = state.wantsToTalk ? '1' : '0';
                    rep = state.reputation.ToString();
                }
            }

            _network.SendReliable(new DialogStatePacket
            {
                PlayerId = MyId,
                Payload = Encode(cd, npcName, wants, rep)
            });

            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[DialogueSync] Broadcast state for '{cd.name}' ({cd.dialogues?.Count ?? 0} nodes, npc='{npcName}')");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DialogueSync] Broadcast failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Join snapshot: every dialogue with progression plus every non-default
    /// NPC state, so a newly connected player converges without having to
    /// re-hear each conversation.
    /// </summary>
    public void CollectSnapshot(List<Packet> into)
    {
        try
        {
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            foreach (var cd in flags.dialogues)
            {
                if (cd == null || string.IsNullOrEmpty(cd.name)) continue;
                if (!HasProgress(cd)) continue;
                into.Add(new DialogStatePacket
                {
                    PlayerId = MyId,
                    Payload = Encode(cd, "", '-', "-")
                });
            }

            foreach (var st in flags.npcStates)
            {
                if (st == null || string.IsNullOrEmpty(st.name)) continue;
                if (st.wantsToTalk && st.reputation == 0) continue; // default
                into.Add(new NpcConvStatePacket
                {
                    PlayerId = MyId,
                    WantsToTalk = st.wantsToTalk,
                    Reputation = st.reputation,
                    NpcName = st.name
                });
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DialogueSync] CollectSnapshot failed: {ex.Message}");
        }
    }

    private static bool HasProgress(CharacterDialogue cd)
    {
        if (cd.specialOptions != null && cd.specialOptions.Count > 0) return true;
        if (cd.portraitType != cd.initialPortraitType) return true;
        if (cd.dialogues != null)
        {
            foreach (var node in cd.dialogues)
                if (node != null && node.alreadyShown) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Receive
    // ------------------------------------------------------------------

    /// <summary>Apply a remote dialogue snapshot (the part after the channel prefix).</summary>
    public void ApplyRemote(string payload)
    {
        try
        {
            if (!Decode(payload, out var name, out var nodeFlags, out var portrait,
                    out var npcName, out var wants, out var rep, out var specials))
                return;

            var flags = Singleton<Flags>.Instance;
            var cd = flags != null ? flags.getDialogue(name) : null;

            RemoteApply.Active = true;
            try
            {
                if (cd != null)
                    ApplyToDialogue(cd, nodeFlags, portrait, specials);
                else if (NetworkLayer.VerboseLogging)
                    ModLogger.Warning($"[DialogueSync] No Flags dialogue '{name}'");

                // Scene-local copies: an NPC whose init hasn't rebound to the
                // Flags instance yet still shows its own component's state.
                foreach (var sceneCd in UnityEngine.Object.FindObjectsOfType<CharacterDialogue>())
                {
                    if (sceneCd == null || sceneCd == cd || sceneCd.name != name) continue;
                    ApplyToDialogue(sceneCd, nodeFlags, portrait, specials);
                }

                // Live NPCs cache portraitType from the dialogue - keep in step.
                foreach (var npc in UnityEngine.Object.FindObjectsOfType<NPC>())
                {
                    if (npc == null || npc.characterDialogue == null) continue;
                    if (npc.characterDialogue.name != name) continue;
                    npc.portraitType = (CharacterDialogue.PortraitType)portrait;
                }

                if (!string.IsNullOrEmpty(npcName))
                    ApplyNpcState(npcName, wants, rep);
            }
            finally
            {
                RemoteApply.Active = false;
            }

            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[DialogueSync] Applied remote state for '{name}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DialogueSync] ApplyRemote failed: {ex.Message}");
        }
    }

    /// <summary>Apply a standalone NPC-state update: "&lt;wtt&gt;:&lt;rep&gt;:&lt;name&gt;".</summary>
    public void ApplyRemoteNpcState(string payload)
    {
        try
        {
            var parts = payload.Split(new[] { ':' }, 3);
            if (parts.Length != 3 || string.IsNullOrEmpty(parts[2])) return;

            RemoteApply.Active = true;
            try
            {
                ApplyNpcState(parts[2],
                    parts[0] == "1" ? '1' : '0',
                    parts[1]);
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DialogueSync] ApplyRemoteNpcState failed: {ex.Message}");
        }
    }

    private static void ApplyToDialogue(CharacterDialogue cd, int[] nodeFlags, int portrait,
        List<(string, string)> specials)
    {
        // Node consumed-state, matched by index (same order on both machines).
        if (cd.dialogues != null)
        {
            var count = Math.Min(cd.dialogues.Count, nodeFlags.Length);
            for (int i = 0; i < count; i++)
            {
                var node = cd.dialogues[i];
                if (node == null) continue;
                var v = nodeFlags[i];
                node.alreadyShown = (v & 1) != 0;
                node.disabled = (v & 2) != 0;
            }
        }

        // Special options (added at runtime via addSpecialDialogueOption).
        cd.specialOptions.Clear();
        foreach (var (optName, optType) in specials)
            cd.specialOptions.Add(new CharacterDialogue.SpecialOption(optName, optType));

        // Portrait (changePortrait outcome mutates both NPC and dialogue).
        cd.portraitType = (CharacterDialogue.PortraitType)portrait;
    }

    private static void ApplyNpcState(string npcName, char wants, string rep)
    {
        var flags = Singleton<Flags>.Instance;
        if (flags == null) return;

        var state = flags.getNPCState(npcName);
        if (state == null)
        {
            state = new Flags.NPCState { name = npcName, wantsToTalk = true };
            flags.npcStates.Add(state);
        }
        if (wants == '1') state.wantsToTalk = true;
        else if (wants == '0') state.wantsToTalk = false;
        if (rep != "-" && int.TryParse(rep, out var repValue))
            state.reputation = repValue;
    }

    // ------------------------------------------------------------------
    // Wire format v2 (base64 of UTF-8 text, delimiter-safe over ':' channels)
    //   line 0: dialogue name
    //   line 1: node flags, one digit per node (bit0=alreadyShown, bit1=disabled)
    //   line 2: portraitType (int)
    //   line 3: NPC gameObject name ("" = none)
    //   line 4: wantsToTalk '1'/'0'/'-' (unknown)
    //   line 5: reputation int or '-' (unknown)
    //   line 6+: special options, "name|type" per line
    // ------------------------------------------------------------------

    private static string Encode(CharacterDialogue cd, string npcName, char wants, string rep)
    {
        var sb = new StringBuilder();
        sb.Append(cd.name).Append('\n');

        if (cd.dialogues != null)
        {
            foreach (var node in cd.dialogues)
            {
                var v = 0;
                if (node != null)
                {
                    if (node.alreadyShown) v |= 1;
                    if (node.disabled) v |= 2;
                }
                // 0..3 -> single char
                sb.Append((char)('0' + v));
            }
        }
        sb.Append('\n');

        sb.Append((int)cd.portraitType).Append('\n');
        sb.Append(npcName).Append('\n');
        sb.Append(wants).Append('\n');
        sb.Append(rep).Append('\n');

        if (cd.specialOptions != null)
        {
            foreach (var opt in cd.specialOptions)
            {
                if (opt == null) continue;
                sb.Append(opt.name).Append('|').Append(opt.type).Append('\n');
            }
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static bool Decode(string payload, out string name, out int[] nodeFlags,
        out int portrait, out string npcName, out char wants, out string rep,
        out List<(string, string)> specials)
    {
        name = null;
        nodeFlags = Array.Empty<int>();
        portrait = 0;
        npcName = "";
        wants = '-';
        rep = "-";
        specials = new List<(string, string)>();

        if (string.IsNullOrEmpty(payload)) return false;

        string text;
        try { text = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
        catch { return false; }

        var lines = text.Split('\n');
        if (lines.Length < 6) return false;

        name = lines[0];
        if (string.IsNullOrEmpty(name)) return false;

        var flagStr = lines[1];
        nodeFlags = new int[flagStr.Length];
        for (int i = 0; i < flagStr.Length; i++)
        {
            var c = flagStr[i];
            nodeFlags[i] = (c >= '0' && c <= '3') ? c - '0' : 0;
        }

        int.TryParse(lines[2], out portrait);
        npcName = lines[3];
        wants = lines[4].Length == 1 ? lines[4][0] : '-';
        rep = lines[5];

        for (int i = 6; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            var bar = line.IndexOf('|');
            if (bar < 0) continue;
            specials.Add((line.Substring(0, bar), line.Substring(bar + 1)));
        }

        return true;
    }
}
