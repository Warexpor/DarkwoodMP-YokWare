using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Audit H5: serialize concurrent talks on the same NPC.
    /// initiateDialogue is the single entry for player/NPC conversation open.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "initiateDialogue")]
    public static class NpcDialogueLockAcquirePatch
    {
        // Vanilla signature: initiateDialogue(NPC _npc) — Harmony binds by param name.
        private static bool Prefix(NPC _npc)
        {
            if (_npc == null || string.IsNullOrEmpty(_npc.name))
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;

            int localId = net.LocalPlayerId;
            string npcName = _npc.name;

            if (net.Role == NetworkRole.Host)
            {
                if (!NpcDialogueLock.HostTryGrant(net, npcName, localId))
                {
                    ModRuntime.LegacyInfo($"[DialogLock] host blocked talk with {npcName}");
                    try
                    {
                        if (Player.Instance != null)
                            Player.Instance.displayMessage("Someone is already talking to them…");
                    }
                    catch { /* ignore */ }
                    return false;
                }
                return true;
            }

            // Client: optimistic local check + request host grant.
            if (NpcDialogueLock.IsLockedByOther(npcName, localId))
            {
                try
                {
                    if (Player.Instance != null)
                        Player.Instance.displayMessage("Someone is already talking to them…");
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
                    IsRequest = true
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Local optimistic acquire so UI opens; host deny will close if racing.
            NpcDialogueLock.TryAcquire(npcName, localId);
            return true;
        }
    }

    [HarmonyPatch(typeof(DialogueWindow), "close")]
    public static class NpcDialogueLockReleasePatch
    {
        private static void Prefix(DialogueWindow __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            NPC npc = __instance != null ? __instance.npc : null;
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;

            string npcName = npc.name;
            int localId = net.LocalPlayerId;

            if (net.Role == NetworkRole.Host)
            {
                NpcDialogueLock.HostRelease(net, npcName, localId);
            }
            else
            {
                NpcDialogueLock.Release(npcName, localId);
                net.Send(NetMessageType.DialogNpcLock,
                    w => new DialogNpcLockMessage
                    {
                        NpcName = npcName,
                        OwnerPlayerId = localId,
                        Granted = true,
                        Release = true,
                        IsRequest = false
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
