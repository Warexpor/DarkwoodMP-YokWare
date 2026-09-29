using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host <see cref="Flags.NPCState.attackedID"/> / <see cref="Flags.NPCState.dead"/> /
    /// <see cref="Flags.NPCState.deadID"/> are written on the host only (getHit / die2).
    /// Clients need them for wolfman despawn-on-death, onlyOneInstance dedup, and
    /// EventTriggerRequirement.npcStateIsDead. UniqueIds are per-peer SaveManager
    /// counters, so clients remap host ids onto the local SaveableObject when present.
    /// Portrait / anim visual trailers live in the Visuals partial (0.8.96/97/115).
    /// </summary>
    internal static partial class NpcAttackedIdSync
    {
        internal static void ApplyAttackedId(Flags.NPCState state, string npcName, int hostAttackedId)
        {
            if (state == null) return;
            if (hostAttackedId == 0)
            {
                state.attackedID = 0;
                return;
            }

            state.attackedID = RemapHostUidToLocal(npcName, hostAttackedId);
        }

        internal static void ApplyDead(Flags.NPCState state, string npcName, bool dead, int hostDeadId)
        {
            if (state == null) return;
            state.dead = dead;
            if (!dead)
            {
                state.deadID = 0;
                return;
            }

            if (hostDeadId == 0)
            {
                state.deadID = 0;
                return;
            }

            state.deadID = RemapHostUidToLocal(npcName, hostDeadId);
        }

        /// <summary>
        /// Map a host SaveableObject.uniqueId onto the local NPC instance when loaded;
        /// otherwise keep the host id until Remap*IfNeeded on OnEnable.
        /// </summary>
        private static int RemapHostUidToLocal(string npcName, int hostUid)
        {
            NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(npcName);
            if (npc != null)
            {
                SaveableObject so = npc.GetComponent<SaveableObject>();
                if (so != null && so.uniqueId != 0)
                    return so.uniqueId;
            }

            return hostUid;
        }

        /// <summary>
        /// Before vanilla onlyOneInstance checks: if attackedID is a foreign host id,
        /// adopt this instance so the live body is kept and true duplicates still remove.
        /// </summary>
        internal static void RemapAttackedIdIfNeeded(NPC npc)
        {
            RemapUidFieldIfNeeded(npc, attacked: true);
        }

        /// <summary>
        /// Same remap for deadID so onlyOneInstance keeps the corpse that died and
        /// removes duplicate story NPC bodies.
        /// </summary>
        internal static void RemapDeadIdIfNeeded(NPC npc)
        {
            RemapUidFieldIfNeeded(npc, attacked: false);
        }

        private static void RemapUidFieldIfNeeded(NPC npc, bool attacked)
        {
            if (npc == null) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            Flags.NPCState state = flags.getNPCState(npc.name);
            if (state == null) return;

            int current = attacked ? state.attackedID : state.deadID;
            if (current == 0) return;
            // deadID only matters while dead; skip remap when still alive.
            if (!attacked && !state.dead) return;

            Core.addToSaveable(npc.gameObject, isDynamic: true, assignID: true);
            SaveableObject so = npc.GetComponent<SaveableObject>();
            if (so == null || so.uniqueId == 0) return;
            if (current == so.uniqueId) return;

            SaveManager sm = Singleton<SaveManager>.Instance;
            if (sm != null && sm.uniqueIdDict != null
                && sm.uniqueIdDict.ContainsKey(current))
                return; // another local instance already owns the remapped id

            if (attacked)
                state.attackedID = so.uniqueId;
            else
                state.deadID = so.uniqueId;
        }

        internal static void BroadcastFromHost(
            string npcName,
            int reputation,
            int attackedId,
            bool hasDead = false,
            bool dead = false,
            int deadId = 0,
            bool hasPortrait = false,
            int portraitType = 0,
            bool applyDialoguePortrait = false,
            bool hasAnimLibrary = false,
            string animLibraryName = null,
            float posX = 0f,
            float posY = 0f,
            float posZ = 0f)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            if (string.IsNullOrEmpty(npcName)) return;

            var msg = new ReputationSyncMessage
            {
                NpcName = npcName,
                Reputation = reputation,
                HasAttackedId = true,
                AttackedId = attackedId,
                HasDead = hasDead,
                Dead = dead,
                DeadId = deadId,
                HasPortrait = hasPortrait,
                PortraitType = portraitType,
                ApplyDialoguePortrait = applyDialoguePortrait,
                HasAnimLibrary = hasAnimLibrary,
                AnimLibraryName = animLibraryName ?? "",
                PosX = posX,
                PosY = posY,
                PosZ = posZ
            };
            net.Broadcast(NetMessageType.ReputationSync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// When host Character.getHit writes NPC attackedID (0→id), fan it on ReputationSync.
    /// </summary>
    [HarmonyPatch(typeof(Character), "getHit", new[]
    {
        typeof(float), typeof(Transform), typeof(bool), typeof(bool), typeof(bool),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool)
    })]
    public static class NpcAttackedIdHostFanPatch
    {
        private static int _attackedIdBefore;

        private static void Prefix(Character __instance)
        {
            _attackedIdBefore = 0;
            if (__instance == null || __instance.npc == null) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            Flags.NPCState st = flags.getNPCState(__instance.npc.name);
            if (st != null) _attackedIdBefore = st.attackedID;
        }

        private static void Postfix(Character __instance)
        {
            if (__instance == null || __instance.npc == null) return;
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            // Host apply of remote hits runs under IsApplyingRemoteState + DialogHostApplyGuard.
            // Still fan attackedID — peers need the mark even when GE fan-out is already handled.
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            string npcName = __instance.npc.name;
            Flags.NPCState st = flags.getNPCState(npcName);
            if (st == null || st.attackedID == 0 || st.attackedID == _attackedIdBefore)
                return;

            NpcAttackedIdSync.BroadcastFromHost(npcName, st.reputation, st.attackedID);
            ModRuntime.LegacyInfo(
                $"[NpcAttacked] host fan '{npcName}' attackedID={st.attackedID}");
        }
    }

    /// <summary>
    /// When host Character.die2 first sets NPCState.dead + deadID, fan on ReputationSync.
    /// Clients never run die/die2 for non-player characters (presentation only).
    /// </summary>
    [HarmonyPatch(typeof(Character), "die2")]
    public static class NpcDeadStateHostFanPatch
    {
        private static bool _deadBefore;
        private static int _deadIdBefore;

        private static void Prefix(Character __instance)
        {
            _deadBefore = false;
            _deadIdBefore = 0;
            if (__instance == null || __instance.npc == null) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            Flags.NPCState st = flags.getNPCState(__instance.npc.name);
            if (st == null) return;
            _deadBefore = st.dead;
            _deadIdBefore = st.deadID;
        }

        private static void Postfix(Character __instance)
        {
            if (__instance == null || __instance.npc == null) return;
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            // Same as attackedID: fan even under remote-apply so peers get world death marks.

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            string npcName = __instance.npc.name;
            Flags.NPCState st = flags.getNPCState(npcName);
            if (st == null || !st.dead) return;
            if (_deadBefore && st.deadID == _deadIdBefore) return;

            NpcAttackedIdSync.BroadcastFromHost(
                npcName, st.reputation, st.attackedID,
                hasDead: true, dead: true, deadId: st.deadID);
            ModRuntime.LegacyInfo(
                $"[NpcDead] host fan '{npcName}' deadID={st.deadID}");
        }
    }

    [HarmonyPatch(typeof(NPC), "OnEnable")]
    public static class NpcAttackedIdClientRemapPatch
    {
        private static void Prefix(NPC __instance)
        {
            NpcAttackedIdSync.RemapAttackedIdIfNeeded(__instance);
            NpcAttackedIdSync.RemapDeadIdIfNeeded(__instance);
            NpcAttackedIdSync.FlushPendingVisualsIfNeeded(__instance);
        }
    }
}
