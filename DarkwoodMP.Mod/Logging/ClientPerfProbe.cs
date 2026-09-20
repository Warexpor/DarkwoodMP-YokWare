using DWMPHorde.Networking;

namespace DWMPHorde.Logging
{
    /// <summary>Backward-compatible name for call sites / older docs.</summary>
    public static class ClientPerfProbe
    {
        public static bool IsActive => CoopPerfProbe.IsActive;
        public static void SetActive(bool active) => CoopPerfProbe.SetActive(active, NetworkRole.Client);
        public static void SetActive(bool active, NetworkRole role) => CoopPerfProbe.SetActive(active, role);
        public static void FrameBegin() => CoopPerfProbe.FrameBegin();
        public static void MarkPoll() => CoopPerfProbe.MarkPoll();
        public static void MarkUpdateRest() => CoopPerfProbe.MarkUpdateRest();
        public static void MarkPhysBuild() => CoopPerfProbe.MarkPhysBuild();
        public static void LateBegin() => CoopPerfProbe.LateBegin();
        public static void MarkObjInterp() => CoopPerfProbe.MarkObjInterp();
        public static void MarkEntityTick() => CoopPerfProbe.MarkEntityTick();
        public static void LateEnd() => CoopPerfProbe.LateEnd();
        public static void NoteEntityApply(int a, int s, double ms) => CoopPerfProbe.NoteEntityApply(a, s, ms);
        public static void NotePhysApply(int o, double ms) => CoopPerfProbe.NotePhysApply(o, ms);
        public static void NotePacketRx() => CoopPerfProbe.NotePacketRx();
        public static void NotePacketRx(NetMessageType type) => CoopPerfProbe.NotePacketRx(type);
        public static void NoteFullRbScan() => CoopPerfProbe.NoteFullRbScan();
        public static void NoteFindObjectsOfType() => CoopPerfProbe.NoteFindObjectsOfType();
        public static void NoteFindObjectsOfType(string typeName, double ms) =>
            CoopPerfProbe.NoteFindObjectsOfType(typeName, ms);
        public static void SetPendingCounts(int lure, int locks, int light, int trap, int feeder, int saw, int construct) =>
            CoopPerfProbe.SetPendingCounts(lure, locks, light, trap, feeder, saw, construct);
        public static void NoteEntityBroadcast(int entityCount) =>
            CoopPerfProbe.NoteEntityBroadcast(entityCount);
        public static void BeginUpdateSegment(string name) => CoopPerfProbe.BeginUpdateSegment(name);
        public static void EndUpdateSegment() => CoopPerfProbe.EndUpdateSegment();
    }
}
