using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Serialize concurrent talks on the same NPC.
    /// initiateDialogue is the single entry for player/NPC conversation open.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "initiateDialogue")]
    public static class NpcDialogueLockAcquirePatch
    {
        // Vanilla signature: initiateDialogue(NPC _npc). Harmony binds by parameter name.
        private static bool Prefix(NPC _npc)
        {
            if (_npc == null || string.IsNullOrEmpty(_npc.name))
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;

            int localId = net.LocalPlayerId;
            string npcName = _npc.name;
            // This peer's view of the NPC's world; the host decides under it.
            bool dream = NpcDialogueLock.IsDreamWorldNpc(_npc);

            if (net.Role == NetworkRole.Host)
            {
                if (!NpcDialogueLock.HostTryGrant(net, npcName, localId, dream))
                {
                    ModRuntime.LegacyInfo($"[DialogLock] host blocked talk with {npcName}");
                    try
                    {
                        if (Player.Instance != null)
                        {
                            PersonalFlavorHud.BeginBypass();
                            try { Player.Instance.displayMessage("Someone is already talking to them…"); }
                            finally { PersonalFlavorHud.EndBypass(); }
                        }
                    }
                    catch { /* ignore */ }
                    return false;
                }
                DialogHostApplyGuard.ClearChainedDisplayBlock();
                try { PeerItemPresence.SendFullLocalInventory(); }
                catch { /* ignore */ }
                NpcDialogueLock.BeginLeaseRenewal(_npc);
                return true;
            }

            // Client: optimistic local check + request host grant.
            if (NpcDialogueLock.IsLockedByOther(npcName, localId, dream))
            {
                try
                {
                    if (Player.Instance != null)
                    {
                        PersonalFlavorHud.BeginBypass();
                        try { Player.Instance.displayMessage("Someone is already talking to them…"); }
                        finally { PersonalFlavorHud.EndBypass(); }
                    }
                }
                catch { /* ignore */ }
                return false;
            }

            net.Send(NetMessageType.DialogNpcLock,
                w => new DialogNpcLockMessage
                {
                    NpcName = npcName,
                    OwnerPlayerId = localId,
                    Granted = false,
                    Release = false,
                    IsRequest = true,
                    Dream = dream
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Local optimistic acquire so UI opens; host deny will close if racing.
            NpcDialogueLock.TryAcquire(npcName, localId, dream);
            try { PeerItemPresence.SendFullLocalInventory(); }
            catch { /* ignore */ }
            NpcDialogueLock.BeginLeaseRenewal(_npc);
            return true;
        }
    }

    /// <summary>
    /// Releases the lock only when vanilla close() actually closed the conversation. The release
    /// used to run in the Prefix, ahead of vanilla's early returns (tweening, exit dialogue shown
    /// instead of closing), so a second close() released again and the host replayed
    /// onCloseDialogue twice. Prefix captures the NPC; Postfix runs only if the original ran and
    /// left dw.npc cleared.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "close")]
    public static class NpcDialogueLockReleasePatch
    {
        private static void Prefix(DialogueWindow __instance, ref NPC __state)
        {
            __state = __instance != null ? __instance.npc : null;
        }

        private static void Postfix(DialogueWindow __instance, NPC __state, bool __runOriginal)
        {
            // A prefix (DialogHostSilentClosePatch) skipped vanilla: not a real close.
            if (!__runOriginal) return;
            if (__state == null || string.IsNullOrEmpty(__state.name)) return;
            // close() returned early and left npc bound: the conversation is still open.
            if (__instance != null && __instance.npc != null) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            // World-only DialogOutcome SilentClose must not release the speaker's lease.
            // Without this guard HostRelease(localId) can drop the host's real talk lock
            // mid-conversation when world-only close runs on the same NPC name.
            if (DialogHostApplyGuard.DialogueApplyActive)
                return;

            string npcName = __state.name;
            int localId = net.LocalPlayerId;
            bool preferDream = NpcDialogueLock.IsDreamWorldNpc(__state);

            if (net.Role == NetworkRole.Host)
            {
                NpcDialogueLock.HostRelease(net, npcName, localId, preferDream);
            }
            else
            {
                // The world of the hold actually released (it may predate a dream start / end).
                bool dream = NpcDialogueLock.Release(npcName, localId, preferDream);
                net.Send(NetMessageType.DialogNpcLock,
                    w => new DialogNpcLockMessage
                    {
                        NpcName = npcName,
                        OwnerPlayerId = localId,
                        Granted = true,
                        Release = true,
                        IsRequest = false,
                        Dream = dream
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
