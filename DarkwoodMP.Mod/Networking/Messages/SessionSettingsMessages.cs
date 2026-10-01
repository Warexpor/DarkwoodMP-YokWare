namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host→client (<see cref="NetMessageType.SessionSettings"/>): the gameplay settings every peer
    /// must agree on. <see cref="LootShare"/> carries the <c>LootShareMode</c> value as a byte
    /// (0 = Off, 1 = ScaleWithPlayers) so this struct stays free of the config types.
    /// <see cref="PartyMultiplier"/> is the host's party size (host plus every other peer).
    /// </summary>
    public struct SessionSettingsMessage
    {
        public bool FriendlyFireEnabled;
        public byte LootShare;
        public bool DoubleItemsEnabled;
        public int PartyMultiplier;

        public void Serialize(NetWriter w)
        {
            w.Put(FriendlyFireEnabled);
            w.Put(LootShare);
            w.Put(DoubleItemsEnabled);
            w.Put(PartyMultiplier);
        }

        public static SessionSettingsMessage Deserialize(NetReader r) => new SessionSettingsMessage
        {
            FriendlyFireEnabled = r.GetBool(),
            LootShare = r.GetByte(),
            DoubleItemsEnabled = r.GetBool(),
            PartyMultiplier = r.GetInt()
        };
    }
}
