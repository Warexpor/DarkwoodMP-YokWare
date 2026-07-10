namespace DWMPHorde.Networking
{
    public enum ContainerAction : byte
    {
        TakeItem = 0,
        PlaceItem = 1,
        RemoveItem = 2,
        Searched = 3
    }

    public struct ContainerItemMessage
    {
        public float PosX, PosY, PosZ;
        public ContainerAction Action;
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;
        public bool IsPlayerPlaced;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put((byte)Action);
            w.Put(SlotIndex);
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(Durability);
            w.Put(Ammo);
            w.Put(IsPlayerPlaced);
        }

        public static ContainerItemMessage Deserialize(NetReader r) => new ContainerItemMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Action = (ContainerAction)r.GetByte(),
            SlotIndex = r.GetByte(),
            ItemType = r.GetString(),
            Amount = r.GetInt(),
            Durability = r.GetFloat(),
            Ammo = r.GetInt(),
            IsPlayerPlaced = r.GetBool()
        };
    }

    public struct ContainerStateRequestMessage
    {
        public float PosX, PosY, PosZ;
        public int TargetEntityHash;

        public void Serialize(NetWriter w) { w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(TargetEntityHash); }
        public static ContainerStateRequestMessage Deserialize(NetReader r) => new ContainerStateRequestMessage
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            TargetEntityHash = r.GetInt()
        };
    }

    public struct SlotStateEntry
    {
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;
    }

    public struct ContainerStateSyncMessage
    {
        public float PosX, PosY, PosZ;
        public int EntityHash;
        public int SlotCount;
        public SlotStateEntry[] Slots;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(EntityHash);
            w.Put(SlotCount);
            for (int i = 0; i < SlotCount; i++)
            {
                w.Put(Slots[i].SlotIndex);
                w.Put(Slots[i].ItemType ?? "");
                w.Put(Slots[i].Amount);
                w.Put(Slots[i].Durability);
                w.Put(Slots[i].Ammo);
            }
        }

        public static ContainerStateSyncMessage Deserialize(NetReader r)
        {
            var msg = new ContainerStateSyncMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                EntityHash = r.GetInt(),
                SlotCount = r.GetInt()
            };
            if (msg.SlotCount < 0 || msg.SlotCount > 4096) msg.SlotCount = 0;
            msg.Slots = new SlotStateEntry[msg.SlotCount];
            for (int i = 0; i < msg.SlotCount; i++)
            {
                msg.Slots[i] = new SlotStateEntry
                {
                    SlotIndex = r.GetByte(),
                    ItemType = r.GetString(),
                    Amount = r.GetInt(),
                    Durability = r.GetFloat(),
                    Ammo = r.GetInt()
                };
            }
            return msg;
        }
    }
}
