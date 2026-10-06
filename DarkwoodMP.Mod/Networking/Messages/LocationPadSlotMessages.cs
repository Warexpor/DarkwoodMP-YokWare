namespace DWMPHorde.Networking
{
    /// <summary>
    /// One host-assigned outside-location pad placement: which of vanilla
    /// <c>OutsideLocations.locationPositions</c> the pad occupies, and its Y rotation.
    /// Slot &lt; 0 means the host has no free slot left (peer falls back to vanilla).
    /// </summary>
    public struct LocationPadSlotEntry
    {
        public string LocationName;
        public int Slot;
        public int YawDeg;

        public void Serialize(NetWriter w)
        {
            w.Put(LocationName ?? string.Empty);
            w.Put(Slot);
            w.Put(YawDeg);
        }

        public static LocationPadSlotEntry Deserialize(NetReader r) => new LocationPadSlotEntry
        {
            LocationName = r.GetString(),
            Slot = r.GetInt(),
            YawDeg = r.GetInt()
        };
    }

    /// <summary>
    /// Client→host (<see cref="NetMessageType.LocationPadSlotRequest"/>): this peer is about
    /// to spawn a pad the host has not assigned it yet; reply with a slot.
    /// </summary>
    public struct LocationPadSlotRequestMessage
    {
        public string LocationName;

        public void Serialize(NetWriter w) => w.Put(LocationName ?? string.Empty);

        public static LocationPadSlotRequestMessage Deserialize(NetReader r) =>
            new LocationPadSlotRequestMessage { LocationName = r.GetString() };
    }

    /// <summary>
    /// Host→peers (<see cref="NetMessageType.LocationPadSlotSync"/>): pad slot assignments
    /// (reply to a request, new-allocation broadcast, or late-join snapshot) plus the host's
    /// next free slot so peers keep <c>actualSpawnedLocationsCount</c> aligned.
    /// </summary>
    public struct LocationPadSlotSyncMessage
    {
        public int NextSlot;
        public LocationPadSlotEntry[] Entries;

        public void Serialize(NetWriter w)
        {
            w.Put(NextSlot);
            int n = Entries != null ? Entries.Length : 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
                Entries[i].Serialize(w);
        }

        public static LocationPadSlotSyncMessage Deserialize(NetReader r)
        {
            int next = r.GetInt();
            int n = r.GetInt();
            if (n < 0 || n > 128)
                throw new System.IO.InvalidDataException("LocationPadSlotSync count " + n);
            var entries = new LocationPadSlotEntry[n];
            for (int i = 0; i < n; i++)
                entries[i] = LocationPadSlotEntry.Deserialize(r);
            return new LocationPadSlotSyncMessage { NextSlot = next, Entries = entries };
        }
    }
}
