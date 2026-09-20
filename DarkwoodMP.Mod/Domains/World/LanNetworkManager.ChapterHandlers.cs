namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        internal ChapterNetHandlers ChapterHandlers { get; private set; }

        private void HandleChapterTransition(ChapterTransitionMessage msg)
        {
            ChapterHandlers.HandleChapterTransition(msg);
        }
    }
}
