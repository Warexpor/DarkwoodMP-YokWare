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
        public float PosX, PosY, PosZ;
        public byte IsWindow;
        public BarricadeAction Action;
        public int Health;
        public bool PlayerBarricade;
        public int MainHealth;
        public int DamageAmount;
        public bool HasAttackerPos;
        public float AttackerPosX, AttackerPosY, AttackerPosZ;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(IsWindow);
            w.Put((byte)Action);
            w.Put(Health);
            w.Put(PlayerBarricade);
            w.Put(MainHealth);
            w.Put(DamageAmount);
            w.Put(HasAttackerPos);
            w.Put(AttackerPosX); w.Put(AttackerPosY); w.Put(AttackerPosZ);
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
            HasAttackerPos = r.GetBool(),
            AttackerPosX = r.GetFloat(),
            AttackerPosY = r.GetFloat(),
            AttackerPosZ = r.GetFloat()
        };
    }
}
