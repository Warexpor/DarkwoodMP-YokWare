namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin examinable façade: delegates to <see cref="ExaminableNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal ExaminableNetHandlers ExaminableHandlers { get; private set; }

        private void HandleExamineObject(ExamineObjectMessage msg)
        {
            ExaminableHandlers.HandleExamineObject(msg);
        }
    }
}
