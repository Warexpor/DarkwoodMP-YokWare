namespace DWMPHorde.Networking
{
    /// <summary>
    /// Reads Darkwood save/world state from the active game and records what LAN clients should match.
    /// </summary>
    public static class ClientSaveBridge
    {
        private static WorldSessionMessage _pendingHostSession;
        private static string _lastClientSaveNote;

        /// <summary>
        /// Human-readable note describing what save the client should load before connecting.
        /// </summary>
        public static string LastClientSaveNote => _lastClientSaveNote;

        /// <summary>
        /// Returns the current profile's save slot name (e.g. "profile_0").
        /// </summary>
        public static string GetActiveSaveSlotName()
        {
            // Darkwood identifies save slots by "profile_" + profile id
            if (Core.currentProfile != null)
                return "profile_" + Core.currentProfile.id;

            return "unknown";
        }

        /// <summary>
        /// Returns a deterministic world seed based on the current chapter.
        /// </summary>
        public static int GetWorldSeed()
        {
            // Not a true procedural seed — composite identity for join mismatch checks:
            // chapter * 100000 + day (when available). Same-save is still required.
            int chapter = 0;
            int day = 0;
            if (Singleton<WorldGenerator>.Instance != null)
                chapter = Singleton<WorldGenerator>.Instance.chapterID;
            else if (Core.currentProfile != null)
                chapter = Core.currentProfile.chapter;
            if (Core.currentProfile != null)
                day = Core.currentProfile.day;
            return chapter * 100000 + day;
        }

        /// <summary>
        /// Returns the current chapter ID from the world generator or profile fallback.
        /// </summary>
        public static int GetChapterId()
        {
            if (Singleton<WorldGenerator>.Instance != null)
                return Singleton<WorldGenerator>.Instance.chapterID;

            if (Core.currentProfile != null)
                return Core.currentProfile.chapter;

            return 0;
        }

        /// <summary>
        /// Returns the current in-game day index from the player profile.
        /// </summary>
        public static int GetDayIndex()
        {
            if (Core.currentProfile != null)
                return Core.currentProfile.day;

            return 0;
        }

        /// <summary>
        /// Returns the name of the player's current big location, or empty if unavailable.
        /// </summary>
        public static string GetBigLocationName()
        {
            Player player = Player.Instance;
            if (player == null || player.whereAmI == null || player.whereAmI.bigLocation == null)
                return string.Empty;

            return player.whereAmI.bigLocation.name;
        }

        /// <summary>
        /// Stores a pending host session and builds a human-readable note so the client
        /// knows which save profile to load before connecting.
        /// </summary>
        public static void NoteClientShouldMatchHost(WorldSessionMessage session)
        {
            _pendingHostSession = session;
            _lastClientSaveNote =
                "Host session: "
                + session.SaveSlotName
                + ", chapter "
                + session.ChapterId
                + ", day "
                + session.DayIndex
                + ". New worlds are auto-shared when the host generates them while you are connected.";
        }

        /// <summary>
        /// Attempts to retrieve a previously stored pending host session.
        /// Returns true if a session with a valid save slot name is available.
        /// </summary>
        public static bool TryGetPendingHostSession(out WorldSessionMessage session)
        {
            session = _pendingHostSession;
            return session.SaveSlotName != null;
        }

        /// <summary>Clear pending host session note on disconnect.</summary>
        public static void Reset()
        {
            _pendingHostSession = default;
            _lastClientSaveNote = null;
        }
    }
}
