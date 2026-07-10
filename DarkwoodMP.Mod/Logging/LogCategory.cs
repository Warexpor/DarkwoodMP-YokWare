namespace DWMPHorde.Logging
{
    /// <summary>Stable log categories for public/support filtering.</summary>
    public enum LogCat
    {
        Core,
        Network,
        Session,
        Combat,
        Entity,
        Physics,
        Container,
        World,
        AI,
        Dream,
        Death,
        Audio,
        UI,
        Save
    }

    public enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Event = 2,
        Info = 3,
        Trace = 4
    }

    public enum LogPreset
    {
        Public,
        Support,
        Dev,
        Trace
    }
}
