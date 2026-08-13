namespace DWMPHorde.Sync
{
    /// <summary>
    /// While client applies DialogueWindow.displayNextBoard outcomes in co-op,
    /// suppress world/session mutations (flags, world events, local dream/transport).
    /// Host applies those once via DialogOutcomeSync (audit: double world apply).
    /// Personal bag give/remove still run on the speaking client.
    /// Journal identity applies locally (idempotent host fan-out) so the speaker can read notes.
    /// </summary>
    public static class DialogClientWorldDefer
    {
        private static int _depth;

        public static bool Active => _depth > 0;

        public static void Begin()
        {
            _depth++;
        }

        public static void End()
        {
            if (_depth > 0)
                _depth--;
        }

        public static void Reset()
        {
            _depth = 0;
        }
    }
}
