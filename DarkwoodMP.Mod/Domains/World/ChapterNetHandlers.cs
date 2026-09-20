using DWMPHorde.Patches;

namespace DWMPHorde.Networking
{
    /// <summary>Chapter transition handler composed for 0.8.</summary>
    internal sealed class ChapterNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal ChapterNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleChapterTransition(ChapterTransitionMessage msg)
        {
            DWMPHorde.Patches.ChapterTransitionHelpers.HandleChapterTransition(msg);
        }
    
    }
}
