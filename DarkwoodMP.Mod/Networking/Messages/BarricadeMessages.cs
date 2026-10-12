namespace DWMPHorde.Networking
{
    public enum BarricadeAction : byte
    {
        Built = 0,
        Destroyed = 1,
        Damaged = 2
    }

    public struct BarricadeEventMessage
    {
        /// <summary>
        /// MainHealth value for events that carry no door health (board-only removals, windows,
        /// items). A door's real health goes negative when it breaks (vanilla subtracts the full
        /// hit), so -1 cannot mean "unknown".
        /// </summary>
        public const int NoMainHealth = int.MinValue;

        public float PosX, PosY, PosZ;
        public byte IsWindow;
        public BarricadeAction Action;
        public int Health;
        public bool PlayerBarricade;
        public int MainHealth;
        public int DamageAmount;
        /// <summary>
        /// Destructible items (IsWindow 2) only: the object's name. A hit can push furniture, so
        /// the receiver's copy may not be at the sender's position yet.
        /// </summary>
        public string ItemName;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(IsWindow);
            w.Put((byte)Action);
            w.Put(Health);
            w.Put(PlayerBarricade);
            w.Put(MainHealth);
            w.Put(DamageAmount);
            w.Put(ItemName ?? "");
        }

        public static BarricadeEventMessage Deserialize(NetReader r) => new BarricadeEventMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsWindow = r.GetByte(),
            Action = (BarricadeAction)r.GetByte(),
            Health = r.GetInt(),
            PlayerBarricade = r.GetBool(),
            MainHealth = r.GetInt(),
            DamageAmount = r.GetInt(),
            ItemName = r.GetString()
        };
    }
}
