using DWMPHorde;
using DWMPHorde.Sync;
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

                Examinable best = FindExaminable(pos, msg.ObjectName);
                if (best == null)
                {
                    ModRuntime.Log?.LogWarning($"[ExamineSync] host: no Examinable near {pos} name={msg.ObjectName}");
                    return;
                }

                // Run full host examine (triggers → GameEvents → 4.2).
                // Postfix will Broadcast ActionState.
                best.examine();
                return;
            }

            if (msg.Action == ExamineObjectMessage.ActionState)
            {
                // Host already applied locally.
                if (_net.Role == NetworkRole.Host) return;

                Examinable best = FindExaminable(pos, msg.ObjectName);
                if (best == null)
                {
                    ModRuntime.Log?.LogWarning($"[ExamineSync] client: no Examinable near {pos}");
                    return;
                }

                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    best.examined = msg.Examined;
                    best.displayedDescriptionPool = msg.DisplayedDescriptionPool;
                }
                finally
                {
                    LanNetworkManager.IsApplyingRemoteState = false;
                }
                ModRuntime.LegacyInfo(
                    $"[ExamineSync] client applied state {best.name} examined={msg.Examined}");
            }
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
