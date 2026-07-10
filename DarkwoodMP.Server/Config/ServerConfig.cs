namespace DarkwoodMP.Server.Config;

public class ServerConfig
{
    public int Port { get; set; } = 7777;
    public int MaxPlayers { get; set; } = 8;
    public string Password { get; set; } = string.Empty;
    public int SaveInterval { get; set; } = 60;
    public int PositionSyncRate { get; set; } = 10;
    public int EnemySyncRate { get; set; } = 5;
    public int TickRate { get; set; } = 30;

    /// <summary>
    /// When false (default) the server is a PURE RELIABLE RELAY: it does not
    /// invent a game clock and does not re-broadcast accumulated world state.
    /// Game time and day/night come from the elected time-authority client
    /// (the lowest-id connected player - see the mod's NetworkManager
    /// .IsTimeAuthority) and are relayed like any other packet. This is the
    /// correct behaviour for co-op Darkwood, whose clock pauses in menus and
    /// jumps on sleep/death - a server-side real-time clock always drifts.
    /// Set true only for an experimental server-authoritative world sim.
    /// </summary>
    public bool AuthoritativeWorld { get; set; } = false;
}
