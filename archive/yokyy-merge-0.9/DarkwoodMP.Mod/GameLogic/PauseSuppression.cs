using DarkwoodMP.Network;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Counted suppression for Core.pause / Core.unpause while co-op UI is open
/// (Horde PauseSuppression). FreezeTracker still owns intentional freezes.
/// </summary>
public static class PauseSuppression
{
    public static int SuppressPause;
    public static int SuppressUnpause;

    public static bool MultiplayerActive =>
        NetworkManager.Instance != null && NetworkManager.Instance.IsConnected;

    public static void Reset()
    {
        SuppressPause = 0;
        SuppressUnpause = 0;
    }

    public static void BeginNoPause()
    {
        if (MultiplayerActive) SuppressPause++;
    }

    public static void EndNoPause()
    {
        if (MultiplayerActive && SuppressPause > 0) SuppressPause--;
    }

    public static void BeginNoUnpause()
    {
        if (MultiplayerActive) SuppressUnpause++;
    }

    public static void EndNoUnpause()
    {
        if (MultiplayerActive && SuppressUnpause > 0) SuppressUnpause--;
    }
}
