using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Examinable request/state handlers composed for 0.8.</summary>
    internal sealed class ExaminableNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal ExaminableNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleExamineObject(ExamineObjectMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            if (msg.Action == ExamineObjectMessage.ActionRequest)
            {
                if (_net.Role != NetworkRole.Host) return;

                // The client's pool line leaves the host's deck (and the re-run draws it).
                DescriptionDeck.BeginRemoteExamine(msg);
                try
                {
                    Examinable best = FindExaminable(pos, msg.ObjectName);
                    if (best == null)
                    {
                        ModRuntime.Log?.LogWarning($"[ExamineSync] host: no Examinable near {pos} name={msg.ObjectName}");
                        BroadcastDrawOnly(msg);
                        return;
                    }

                    // Run full host examine (triggers → GameEvents). Suppress HUD so the
                    // host does not see the client's personal flavor text; client already
                    // displayed locally. World-only + actor stamp: bag/teleport land on
                    // the examiner, not the host. Postfix broadcasts ActionState flags and the line.
                    ExaminableExamineSync.SuppressHostExamineHud++;
                    try
                    {
                        DialogHostApplyGuard.RunHostWorldFanout(() => best.examine());
                    }
                    finally
                    {
                        ExaminableExamineSync.SuppressHostExamineHud--;
                    }
                }
                finally
                {
                    DescriptionDeck.EndRemoteExamine();
                }
                return;
            }

            if (_net.Role == NetworkRole.Host)
                return; // host already applied locally

            // Another player's pool line leaves this deck (the drawer has already taken it out).
            if (msg.HasDraw && msg.DrawnBy != _net.LocalPlayerId)
                DescriptionDeck.Apply(msg.DrawPool, msg.DrawLine, msg.DrawRefreshed);

            if (msg.Action == ExamineObjectMessage.ActionState)
            {
                Examinable best = FindExaminable(pos, msg.ObjectName);
                if (best == null)
                {
                    ModRuntime.Log?.LogWarning($"[ExamineSync] client: no Examinable near {pos}");
                    return;
                }

                bool prevApply1 = LanNetworkManager.GetExplicitApplyingRemoteState();
                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    best.examined = msg.Examined;
                    best.displayedDescriptionPool = msg.DisplayedDescriptionPool;
                }
                finally
                {
                    LanNetworkManager.SetExplicitApplyingRemoteState(prevApply1);
                }
                ModRuntime.LegacyInfo(
                    $"[ExamineSync] client applied state {best.name} examined={msg.Examined}");
            }
        }

        /// <summary>Host could not re-run the examine: the other machines still take its line out of their decks.</summary>
        private void BroadcastDrawOnly(ExamineObjectMessage request)
        {
            if (!request.HasDraw)
                return;
            var draw = new ExamineObjectMessage
            {
                Action = ExamineObjectMessage.ActionDraw,
                ObjectName = request.ObjectName,
                PosX = request.PosX,
                PosY = request.PosY,
                PosZ = request.PosZ,
                HasDraw = true,
                DrawPool = request.DrawPool,
                DrawLine = request.DrawLine,
                DrawRefreshed = request.DrawRefreshed,
                DrawnBy = _net.CurrentReceivePlayerId > 0 ? _net.CurrentReceivePlayerId : request.DrawnBy
            };
            _net.Broadcast(NetMessageType.ExamineObject, w => draw.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        private static Examinable FindExaminable(Vector3 pos, string name)
        {
            Examinable byName = null;
            if (!string.IsNullOrEmpty(name))
                byName = WorldQueryHelper.FindNearestByName<Examinable>(pos, name, 3f);
            if (byName != null) return byName;
            return WorldQueryHelper.FindNearest<Examinable>(pos, 2.5f);
        }
    }
}
