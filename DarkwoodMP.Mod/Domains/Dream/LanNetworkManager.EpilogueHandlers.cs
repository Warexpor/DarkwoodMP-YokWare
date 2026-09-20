namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin epilogue-message façade: delegates to <see cref="EpilogueNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal EpilogueNetHandlers EpilogueHandlers { get; private set; }

        private void HandleSceneLoad(SceneLoadMessage msg)
        {
            EpilogueHandlers.HandleSceneLoad(msg);
        }

        /// <summary>
        /// Coordinated scene load (credits). Fades peers out, stops network, loads scene.
        /// Idempotent; the first call wins.
        /// </summary>
        internal static void ApplySceneLoad(string sceneName, float delaySeconds = 0.5f)
        {
            EpilogueNetHandlers.ApplySceneLoad(sceneName, delaySeconds);
        }

        /// <summary>Reset pending flag on network stop so a new session can load scenes again.</summary>
        internal static void ResetSceneLoadState()
        {
            EpilogueNetHandlers.ResetSceneLoadState();
        }
    }
}
