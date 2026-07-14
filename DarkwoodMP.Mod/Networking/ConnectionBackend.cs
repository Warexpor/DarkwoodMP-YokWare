namespace DWMPHorde.Networking
{
    /// <summary>
    /// How this session moves packets. LAN = LiteNetLib UDP. Steam = Steamworks P2P + lobby.
    /// Mutually exclusive per session — pick one when hosting/joining.
    /// </summary>
    public enum ConnectionBackend : byte
    {
        None = 0,
        Lan = 1,
        Steam = 2,
    }
}
