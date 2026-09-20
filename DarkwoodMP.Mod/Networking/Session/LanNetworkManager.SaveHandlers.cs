namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="SaveNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal SaveNetHandlers SaveHandlers { get; private set; }

        /// <summary>Arm after local/remote death so vanilla Save spam does not re-Save the host.</summary>
        public void NoteDeathSaveSyncWindow()
        {
            SaveHandlers.NoteDeathSaveSyncWindow();
        }

        /// <summary>
        /// After a local Save: host debounces broadcast; client requests host fan-out only.
        /// </summary>
        public void SendSaveSync(bool hostAlreadySavedLocally = false)
        {
            SaveHandlers.SendSaveSync(hostAlreadySavedLocally);
        }

        private void TickSaveSyncBroadcast()
        {
            SaveHandlers.TickSaveSyncBroadcast();
        }

        /// <summary>
        /// Sends the client's inventory/skills/state backup to the host, and mirrors it
        /// to local self so RESTORE SELF / rejoin fallback stay current.
        /// </summary>
        public void SendClientStateBackup()
        {
            SaveHandlers.SendClientStateBackup();
        }

        /// <summary>
        /// Client disconnect / quit while in-world: write local self (+ host if still linked)
        /// so exit position is not only whatever the last Save captured.
        /// </summary>
        private void TrySnapshotClientBackupOnExit()
        {
            SaveHandlers.TrySnapshotClientBackupOnExit();
        }

        /// <summary>
        /// Host-broadcast SaveSync → clients run full local Save with vanilla Saving UI.
        /// Client-originated SaveSync is host-only (debounced fan-out). Flag blocks loops.
        /// </summary>
        private void HandleSaveSync()
        {
            SaveHandlers.HandleSaveSync();
        }

        /// <summary>
        /// Host→client: push last stored per-player backup after late-join settle so
        /// week-later rejoins restore inv/skills (host world sav has host character).
        /// </summary>
        private void SendStoredClientBackupTo(int playerId)
        {
            SaveHandlers.SendStoredClientBackupTo(playerId);
        }

        /// <summary>
        /// Client: after phase-3 reconnect, wait for host backup push then fall back to local self.
        /// </summary>
        private void BeginClientBackupRestoreWait()
        {
            SaveHandlers.BeginClientBackupRestoreWait();
        }

        /// <summary>
        /// Host stores client→host snapshots. Client applies host→client push (rejoin restore).
        /// </summary>
        private void HandleClientStateBackup(ClientStateBackupMessage msg)
        {
            SaveHandlers.HandleClientStateBackup(msg);
        }
    }
}
