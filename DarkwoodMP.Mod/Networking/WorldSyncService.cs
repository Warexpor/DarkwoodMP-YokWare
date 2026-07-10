using BepInEx.Logging;
using DWMPHorde.Logging;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host-authoritative world/session agreement (seed, save slot, chapter).
    /// Inventory persistence for clients is applied on load via <see cref="ClientSaveBridge"/>.
    /// </summary>
    public sealed class WorldSyncService
    {
        public static WorldSyncService Instance { get; private set; }

        private readonly ManualLogSource _log;
        private WorldSessionMessage _hostSession;
        private bool _clientReady;

        public bool HasHostSession => _hostSession.SaveSlotName != null;
        public WorldSessionMessage HostSession => _hostSession;
        public bool ClientReady => _clientReady;

        public WorldSyncService(ManualLogSource log)
        {
            _log = log;
            Instance = this;
        }

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
            _clientReady = true;

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
            _clientReady = false;
        }
    }
}
