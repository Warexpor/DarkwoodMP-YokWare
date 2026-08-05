namespace DarkwoodMP.Server.Models;

public class ConnectionInfo
{
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Password { get; set; } = string.Empty;
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}
