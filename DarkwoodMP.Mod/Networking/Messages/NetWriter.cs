namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin wrapper so message structs do not reference LiteNetLib writer types directly.
    /// </summary>
    public sealed class NetWriter
    {
        private readonly LiteNetLib.Utils.NetDataWriter _inner = new LiteNetLib.Utils.NetDataWriter();

        public void Put(byte value) => _inner.Put(value);
        public void Put(short value) => _inner.Put(value);
        public void Put(int value) => _inner.Put(value);
        public void Put(float value) => _inner.Put(value);
        public void Put(bool value) => _inner.Put(value);
        public void Put(string value) => _inner.Put(value ?? string.Empty);

        public void Put(byte[] value)
        {
            if (value == null) { _inner.Put(0); return; }
            _inner.Put(value.Length);
            _inner.Put(value);
        }

        public void Reset() => _inner.Reset();
        public byte[] CopyData() => _inner.CopyData();
    }

    /// <summary>
    /// Thin wrapper so message structs do not reference LiteNetLib reader types directly.
    /// </summary>
    public sealed class NetReader
    {
        private readonly LiteNetLib.Utils.NetDataReader _inner;

        public NetReader(byte[] data)
        {
            _inner = new LiteNetLib.Utils.NetDataReader(data);
        }

        public byte GetByte() => _inner.GetByte();
        public short GetShort() => _inner.GetShort();
        public int GetInt() => _inner.GetInt();
        public float GetFloat() => _inner.GetFloat();
        public bool GetBool() => _inner.GetBool();
        public string GetString() => _inner.GetString();
        public byte[] GetBytes()
        {
            int len = _inner.GetInt();
            if (len <= 0) return new byte[0];
            // Chunked world-save payloads are 16KB; allow headroom without OOM risk.
            if (len > 256 * 1024) len = 256 * 1024;
            byte[] result = new byte[len];
            _inner.GetBytes(result, 0, len);
            return result;
        }
    }
}
