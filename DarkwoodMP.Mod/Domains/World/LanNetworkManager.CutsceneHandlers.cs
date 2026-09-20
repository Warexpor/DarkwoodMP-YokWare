namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        internal CutsceneNetHandlers CutsceneHandlers { get; private set; }

        private void HandleCutsceneSync(CutsceneSyncMessage msg)
        {
            CutsceneHandlers.HandleCutsceneSync(msg);
        }
    }
}
