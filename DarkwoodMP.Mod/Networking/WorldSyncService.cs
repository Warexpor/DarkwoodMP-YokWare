using DWMPHorde.Logging;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host-authoritative world/session agreement (seed, save slot, chapter).
    /// Inventory persistence for clients is applied on load via <see cref="ClientSaveBridge"/>.
    /// </summary>
    public sealed class WorldSyncService
    {
        private WorldSessionMessage _hostSession;

        public WorldSessionMessage BuildHostSession()
        {
            var session = new WorldSessionMessage
            {
                SaveSlotName = ClientSaveBridge.GetActiveSaveSlotName(),
                WorldSeed = ClientSaveBridge.GetWorldSeed(),
                ChapterId = ClientSaveBridge.GetChapterId(),
                DayIndex = ClientSaveBridge.GetDayIndex(),
                BigLocationName = ClientSaveBridge.GetBigLocationName()
            };

            _hostSession = session;
            return session;
        }

        public void ApplyHostSession(WorldSessionMessage session, bool asClient)
        {
            _hostSession = session;

            ModLog.Event(LogCat.Session,
                "World session "
                + (asClient ? "received" : "published")
                + ": slot="
                + session.SaveSlotName
                + " seed="
                + session.WorldSeed
                + " chapter="
                + session.ChapterId
                + " day="
                + session.DayIndex
                + " location="
                + session.BigLocationName);

            if (asClient)
                ClientSaveBridge.NoteClientShouldMatchHost(session);
        }

        public void Reset()
        {
            _hostSession = default;
        }
    }
}
