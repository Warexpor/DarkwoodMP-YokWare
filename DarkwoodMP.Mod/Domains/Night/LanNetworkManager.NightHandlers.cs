namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="NightNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal NightNetHandlers NightHandlers { get; private set; }

        private void HandleShadowEvent(ShadowEventMessage msg)
        {
            NightHandlers.HandleShadowEvent(msg);
        }

        private void HandleNightShadowSpawnRequest(NightShadowSpawnRequestMessage msg)
        {
            NightHandlers.HandleNightShadowSpawnRequest(msg);
        }

        private void HandleShadowSpawn(ShadowSpawnMessage msg)
        {
            NightHandlers.HandleShadowSpawn(msg);
        }

        private void HandleShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            NightHandlers.HandleShadowStateUpdate(msg);
        }

        private void HandleScenarioSync(ScenarioSyncMessage msg)
        {
            NightHandlers.HandleScenarioSync(msg);
        }

        private void TryFlushPendingScenario()
        {
            NightHandlers.TryFlushPendingScenario();
        }

        private void HandleScenarioEventFired(ScenarioEventFiredMessage msg)
        {
            NightHandlers.HandleScenarioEventFired(msg);
        }

        private void HandleSleepEndRequest(SleepEndRequestMessage msg)
        {
            NightHandlers.HandleSleepEndRequest(msg);
        }

        private void HandleAfterNightEndRequest(AfterNightEndRequestMessage msg)
        {
            NightHandlers.HandleAfterNightEndRequest(msg);
        }

        private void ApplyScenarioEventFired(ScenarioEventFiredMessage msg)
        {
            NightHandlers.ApplyScenarioEventFired(msg);
        }

        private void ApplyScenarioSync(ScenarioSyncMessage msg)
        {
            NightHandlers.ApplyScenarioSync(msg);
        }

        internal void ClearShadowLookups()
        {
            NightHandlers.ClearShadowLookups();
        }
    }
}
