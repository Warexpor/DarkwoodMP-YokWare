using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Portrait / anim-library trailers for live ReputationSync and
    /// late-join ReputationBulkSync. Queues when the NPC body is missing.
    /// </summary>
    internal static partial class NpcAttackedIdSync
    {
        private struct PendingPortrait
        {
            public int PortraitType;
            public bool ApplyDialogue;
            public float PosX, PosY, PosZ;
        }

        private struct PendingAnim
        {
            public string LibraryName;
            public float PosX, PosY, PosZ;
        }

        private static readonly Dictionary<string, PendingPortrait> _pendingPortrait =
            new Dictionary<string, PendingPortrait>(32);
        private static readonly Dictionary<string, PendingAnim> _pendingAnim =
            new Dictionary<string, PendingAnim>(32);

        /// <summary>Session end: pending visuals belong to the host world that sent them.</summary>
        internal static void ResetPendingVisuals()
        {
            _pendingPortrait.Clear();
            _pendingAnim.Clear();
        }

        /// <summary>
        /// Host: fill sparse portrait / anim-library trailers for ReputationBulk from
        /// live NPC bodies (mirrors live ReputationSync).
        /// </summary>
        internal static void FillBulkVisualTrailers(ref ReputationBulkSyncMessage msg)
        {
            if (msg.NpcNames == null || msg.NpcCount <= 0) return;
            EnsureBulkVisualArrays(ref msg);

            bool dream = DreamSyncManager.IsDreamActive;
            for (int i = 0; i < msg.NpcCount && i < msg.NpcNames.Length; i++)
            {
                string npcName = msg.NpcNames[i];
                if (string.IsNullOrEmpty(npcName)) continue;
                NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(npcName, preferDreamPad: dream);
                if (npc == null) continue;
                Vector3 pos = npc.transform.position;

                CharacterDialogue cd = npc.characterDialogue;
                bool portraitChanged = cd == null
                    || npc.portraitType != cd.initialPortraitType
                    || cd.portraitType != cd.initialPortraitType;
                if (portraitChanged)
                {
                    msg.HasPortrait[i] = true;
                    msg.PortraitTypes[i] = (int)npc.portraitType;
                    msg.ApplyDialoguePortrait[i] = cd != null
                        && cd.portraitType == npc.portraitType;
                    msg.PortraitPosX[i] = pos.x;
                    msg.PortraitPosY[i] = pos.y;
                    msg.PortraitPosZ[i] = pos.z;
                }

                Character ch = npc.GetComponent<Character>();
                string lib = ch != null ? ch.animationLibraryOverride : null;
                if (!string.IsNullOrEmpty(lib))
                {
                    msg.HasAnimLibrary[i] = true;
                    msg.AnimLibraryNames[i] = lib;
                    msg.AnimPosX[i] = pos.x;
                    msg.AnimPosY[i] = pos.y;
                    msg.AnimPosZ[i] = pos.z;
                }
            }
        }

        private static void EnsureBulkVisualArrays(ref ReputationBulkSyncMessage msg)
        {
            int count = msg.NpcCount;
            if (msg.HasPortrait == null || msg.HasPortrait.Length < count)
                msg.HasPortrait = new bool[count];
            if (msg.PortraitTypes == null || msg.PortraitTypes.Length < count)
                msg.PortraitTypes = new int[count];
            if (msg.ApplyDialoguePortrait == null || msg.ApplyDialoguePortrait.Length < count)
                msg.ApplyDialoguePortrait = new bool[count];
            if (msg.PortraitPosX == null || msg.PortraitPosX.Length < count)
                msg.PortraitPosX = new float[count];
            if (msg.PortraitPosY == null || msg.PortraitPosY.Length < count)
                msg.PortraitPosY = new float[count];
            if (msg.PortraitPosZ == null || msg.PortraitPosZ.Length < count)
                msg.PortraitPosZ = new float[count];
            if (msg.HasAnimLibrary == null || msg.HasAnimLibrary.Length < count)
                msg.HasAnimLibrary = new bool[count];
            if (msg.AnimLibraryNames == null || msg.AnimLibraryNames.Length < count)
                msg.AnimLibraryNames = new string[count];
            if (msg.AnimPosX == null || msg.AnimPosX.Length < count)
                msg.AnimPosX = new float[count];
            if (msg.AnimPosY == null || msg.AnimPosY.Length < count)
                msg.AnimPosY = new float[count];
            if (msg.AnimPosZ == null || msg.AnimPosZ.Length < count)
                msg.AnimPosZ = new float[count];
        }

        /// <summary>
        /// Client: apply sparse portrait / anim trailers from ReputationBulk (queues if body missing).
        /// </summary>
        internal static int ApplyBulkVisualTrailers(ReputationBulkSyncMessage msg, int index, string npcName)
        {
            int applied = 0;
            if (msg.HasPortrait != null && index < msg.HasPortrait.Length && msg.HasPortrait[index])
            {
                ApplyPortrait(new ReputationSyncMessage
                {
                    NpcName = npcName,
                    HasPortrait = true,
                    PortraitType = msg.PortraitTypes != null && index < msg.PortraitTypes.Length
                        ? msg.PortraitTypes[index] : 0,
                    ApplyDialoguePortrait = msg.ApplyDialoguePortrait != null
                        && index < msg.ApplyDialoguePortrait.Length
                        && msg.ApplyDialoguePortrait[index],
                    PosX = msg.PortraitPosX != null && index < msg.PortraitPosX.Length
                        ? msg.PortraitPosX[index] : 0f,
                    PosY = msg.PortraitPosY != null && index < msg.PortraitPosY.Length
                        ? msg.PortraitPosY[index] : 0f,
                    PosZ = msg.PortraitPosZ != null && index < msg.PortraitPosZ.Length
                        ? msg.PortraitPosZ[index] : 0f
                });
                applied++;
            }
            if (msg.HasAnimLibrary != null && index < msg.HasAnimLibrary.Length && msg.HasAnimLibrary[index])
            {
                ApplyAnimLibrary(new ReputationSyncMessage
                {
                    NpcName = npcName,
                    HasAnimLibrary = true,
                    AnimLibraryName = msg.AnimLibraryNames != null && index < msg.AnimLibraryNames.Length
                        ? msg.AnimLibraryNames[index] : "",
                    PosX = msg.AnimPosX != null && index < msg.AnimPosX.Length
                        ? msg.AnimPosX[index] : 0f,
                    PosY = msg.AnimPosY != null && index < msg.AnimPosY.Length
                        ? msg.AnimPosY[index] : 0f,
                    PosZ = msg.AnimPosZ != null && index < msg.AnimPosZ.Length
                        ? msg.AnimPosZ[index] : 0f
                });
                applied++;
            }
            return applied;
        }

        /// <summary>
        /// Apply host GameEvent CharacterModify.portraitType onto the dream-pad NPC
        /// when a dream is active (not the overworld twin). Does not re-fire GameEvents.
        /// Queues when the body is not loaded (late join / other-room chunk wake).
        /// </summary>
        internal static void ApplyPortrait(ReputationSyncMessage msg)
        {
            if (!msg.HasPortrait || string.IsNullOrEmpty(msg.NpcName)) return;

            NPC npc = ResolveNpcNear(msg);
            if (npc == null)
            {
                _pendingPortrait[msg.NpcName] = new PendingPortrait
                {
                    PortraitType = msg.PortraitType,
                    ApplyDialogue = msg.ApplyDialoguePortrait,
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ
                };
                return;
            }

            ApplyPortraitToNpc(npc, msg.PortraitType, msg.ApplyDialoguePortrait);
            _pendingPortrait.Remove(msg.NpcName);
        }

        /// <summary>
        /// Apply host GameEvent CharacterModify.animationLibraryOverride via the vanilla
        /// Character setter (Resources.Load → animator.Library). Dream-pad aware.
        /// Queues when the body is not loaded.
        /// </summary>
        internal static void ApplyAnimLibrary(ReputationSyncMessage msg)
        {
            if (!msg.HasAnimLibrary || string.IsNullOrEmpty(msg.NpcName)) return;

            NPC npc = ResolveNpcNear(msg);
            if (npc == null)
            {
                _pendingAnim[msg.NpcName] = new PendingAnim
                {
                    LibraryName = msg.AnimLibraryName ?? "",
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ
                };
                return;
            }

            ApplyAnimLibraryToNpc(npc, msg.AnimLibraryName);
            _pendingAnim.Remove(msg.NpcName);
        }

        /// <summary>
        /// Flush queued portrait / anim when an NPC body wakes (chunk / late-join load).
        /// </summary>
        internal static void FlushPendingVisualsIfNeeded(NPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;

            string name = npc.name;
            if (_pendingPortrait.TryGetValue(name, out PendingPortrait portrait))
            {
                ApplyPortraitToNpc(npc, portrait.PortraitType, portrait.ApplyDialogue);
                _pendingPortrait.Remove(name);
            }
            if (_pendingAnim.TryGetValue(name, out PendingAnim anim))
            {
                ApplyAnimLibraryToNpc(npc, anim.LibraryName);
                _pendingAnim.Remove(name);
            }
        }

        private static void ApplyPortraitToNpc(NPC npc, int portraitType, bool applyDialogue)
        {
            var portrait = (CharacterDialogue.PortraitType)portraitType;
            npc.portraitType = portrait;
            if (applyDialogue && npc.characterDialogue != null)
                npc.characterDialogue.portraitType = portrait;

            ModRuntime.LegacyInfo(
                $"[RepSync] applied portrait '{npc.name}' → {portrait} dialogue={applyDialogue}");
        }

        private static void ApplyAnimLibraryToNpc(NPC npc, string libraryName)
        {
            Character ch = npc.GetComponent<Character>();
            if (ch == null) return;

            // Same setter vanilla GameEvent uses — loads tk2dSpriteAnimation into Library.
            ch.animationLibraryOverride = libraryName ?? "";

            ModRuntime.LegacyInfo(
                $"[RepSync] applied animLibrary '{npc.name}' → '{libraryName}'");
        }

        private static NPC ResolveNpcNear(ReputationSyncMessage msg)
        {
            Vector3 near = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool dream = DreamSyncManager.IsDreamActive;
            NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByNameNear(
                msg.NpcName, preferDreamPad: dream, near);
            if (npc == null)
                npc = DialogOutcomeCloseNetHandlers.FindNpcByName(msg.NpcName, preferDreamPad: dream);
            return npc;
        }
    }
}
