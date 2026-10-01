namespace DWMPHorde.Networking
{
    public enum ContainerAction : byte
    {
        TakeItem = 0,
        PlaceItem = 1,
        RemoveItem = 2,
        Searched = 3,
        /// <summary>Client closed a world container. Host replays onCloseContainer.</summary>
        CloseContainer = 4
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
        /// <summary>ItemType is recipeFor when true.</summary>
        public bool IsRecipe;
        /// <summary>Workbench ItemUpgrade names.</summary>
        public string[] Upgrades;
        /// <summary>Flashlight / toggle on.</summary>
        public bool ShouldBeActive;

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
            w.Put(IsRecipe);
            DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades);
            w.Put(ShouldBeActive);
        }

        public static ContainerItemMessage Deserialize(NetReader r)
        {
            var msg = new ContainerItemMessage
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
                IsPlayerPlaced = r.GetBool(),
                IsRecipe = r.GetBool()
            };
            msg.Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.Read(r);
            msg.ShouldBeActive = r.GetBool();
            return msg;
        }
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

    /// <summary>
    /// Host→client deny for container take OR place races.
    /// Mode 0: take/remove lost — remove optimistic loot from player.
    /// Mode 1: place lost (slot type clash / bad amount) — add item back to player;
    /// host also pushes ContainerStateSync so the container slot is corrected.
    /// Durability/Ammo/Upgrades/ShouldBeActive describe the item for the place refund.
    /// </summary>
    public struct ContainerTakeDeniedMessage
    {
        public const byte ModeTakeRefund = 0;
        public const byte ModePlaceRefund = 1;

        public float PosX, PosY, PosZ;
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        /// <summary>0 = take refund (remove from player); 1 = place refund (add to player).</summary>
        public byte Mode;
        public float Durability;
        public int Ammo;
        /// <summary>Place-refund workbench upgrades.</summary>
        public string[] Upgrades;
        /// <summary>Place-refund shouldBeActive.</summary>
        public bool ShouldBeActive;

        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(SlotIndex);
            w.Put(ItemType ?? "");
            w.Put(Amount);
            w.Put(Mode);
            w.Put(Durability);
            w.Put(Ammo);
            DWMPHorde.Sync.InvItemUpgradeWire.Write(w, Upgrades);
            w.Put(ShouldBeActive);
        }

        public static ContainerTakeDeniedMessage Deserialize(NetReader r)
        {
            var msg = new ContainerTakeDeniedMessage
            {
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                SlotIndex = r.GetByte(),
                ItemType = r.GetString(),
                Amount = r.GetInt(),
                Mode = r.GetByte(),
                Durability = r.GetFloat(),
                Ammo = r.GetInt()
            };
            msg.Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.Read(r);
            msg.ShouldBeActive = r.GetBool();
            return msg;
        }
    }

    public struct SlotStateEntry
    {
        public byte SlotIndex;
        public string ItemType;
        public int Amount;
        public float Durability;
        public int Ammo;
        /// <summary>Vanilla InvItemClass.isRecipe. ItemType is recipeFor when set.</summary>
        public bool IsRecipe;
        /// <summary>Workbench ItemUpgrade names.</summary>
        public string[] Upgrades;
        /// <summary>shouldBeActive (flashlight on).</summary>
        public bool ShouldBeActive;
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
            for (int i = 0; i < SlotCount; i++)
                w.Put(Slots[i].IsRecipe);
            for (int i = 0; i < SlotCount; i++)
                DWMPHorde.Sync.InvItemUpgradeWire.Write(w, i < Slots.Length ? Slots[i].Upgrades : null);
            for (int i = 0; i < SlotCount; i++)
                w.Put(i < Slots.Length && Slots[i].ShouldBeActive);
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
            if (msg.SlotCount < 0 || msg.SlotCount > 4096)
                throw new System.IO.InvalidDataException("ContainerStateSync slot count " + msg.SlotCount);
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
            for (int i = 0; i < msg.SlotCount; i++)
                msg.Slots[i].IsRecipe = r.GetBool();
            for (int i = 0; i < msg.SlotCount; i++)
                msg.Slots[i].Upgrades = DWMPHorde.Sync.InvItemUpgradeWire.Read(r);
            for (int i = 0; i < msg.SlotCount; i++)
                msg.Slots[i].ShouldBeActive = r.GetBool();
            return msg;
        }
    }
}
