using System;
using System.IO;

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
        public void Put(uint value) => _inner.Put(value);
        public void Put(float value) => _inner.Put(value);
        public void Put(bool value) => _inner.Put(value);
        public void Put(string value) => _inner.Put(value ?? string.Empty);

        /// <summary>Largest blob / long string a reader will accept (see <see cref="NetReader"/>).</summary>
        public const int MaxBlobBytes = 256 * 1024;

        /// <summary>
        /// String that may exceed LiteNetLib's 65535-byte <c>Put(string)</c> limit (that overload writes a
        /// ushort length and silently corrupts the packet past it). Written as an int-prefixed UTF-8 blob.
        /// </summary>
        public void PutLongString(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaxBlobBytes)
                throw new InvalidDataException("Long string is " + bytes.Length + " bytes (max " + MaxBlobBytes + ").");
            Put(bytes);
        }

        /// <summary>Length-prefixed blob (explicit sized arrays).</summary>
        public void Put(byte[] value)
        {
            if (value == null) { _inner.Put(0); return; }
            _inner.Put(value.Length);
            _inner.Put(value);
        }

        /// <summary>Length-prefixed slice — hot voice path writes from a recycled capture buffer.</summary>
        public void Put(byte[] value, int offset, int length)
        {
            if (value == null || length <= 0) { _inner.Put(0); return; }
            if (offset < 0) offset = 0;
            if (offset + length > value.Length)
                length = value.Length - offset;
            if (length <= 0) { _inner.Put(0); return; }
            _inner.Put(length);
            _inner.Put(value, offset, length);
        }

        /// <summary>
        /// Raw bytes with no length prefix. Used when rebroadcasting an
        /// already-framed payload on the host forward path.
        /// </summary>
        public void PutRaw(byte[] value)
        {
            if (value == null || value.Length == 0) return;
            _inner.Put(value, 0, value.Length);
        }

        /// <summary>Raw slice of an already-serialized body (no length prefix) — chunked snapshot split.</summary>
        public void PutRaw(byte[] value, int offset, int length)
        {
            if (value == null || length <= 0) return;
            _inner.Put(value, offset, length);
        }

        /// <summary>Bytes written since the last <see cref="Reset"/>.</summary>
        public int Length => _inner.Length;

        /// <summary>The written bytes in place (the first <see cref="Length"/>); valid until the next write or reset.</summary>
        internal byte[] RawBuffer => _inner.Data;

        /// <summary>Bytes the writer holds on to (it grows, never shrinks).</summary>
        internal int Capacity => _inner.Capacity;

        public void Reset() => _inner.Reset();
        public byte[] CopyData() => _inner.CopyData();

        /// <summary>
        /// Copy payload into a recycled buffer (grows only when needed). Prefer this on
        /// hot 10 Hz broadcast paths instead of <see cref="CopyData"/> every tick.
        /// </summary>
        public void CopyDataInto(ref byte[] buffer, out int length)
        {
            length = _inner.Length;
            if (length <= 0)
            {
                length = 0;
                return;
            }
            if (buffer == null || buffer.Length < length)
                buffer = new byte[Math.Max(length, buffer != null && buffer.Length > 0 ? buffer.Length * 2 : 256)];
            Buffer.BlockCopy(_inner.Data, 0, buffer, 0, length);
        }
    }

    /// <summary>
    /// Thin wrapper so message structs do not reference LiteNetLib reader types directly.
    /// </summary>
    public sealed class NetReader
    {
        private readonly LiteNetLib.Utils.NetDataReader _inner;
        private const int MaxBlobBytes = NetWriter.MaxBlobBytes;

        public NetReader(byte[] data)
        {
            if (data == null)
                throw new InvalidDataException("Packet payload is null.");
            _inner = new LiteNetLib.Utils.NetDataReader(data);
        }

        public byte GetByte() { Require(1, "byte"); return _inner.GetByte(); }
        public short GetShort() { Require(2, "short"); return _inner.GetShort(); }
        public int GetInt() { Require(4, "int"); return _inner.GetInt(); }
        public uint GetUInt() { Require(4, "uint"); return _inner.GetUInt(); }
        public float GetFloat() { Require(4, "float"); return _inner.GetFloat(); }
        public bool GetBool() { Require(1, "bool"); return _inner.GetBool(); }
        public string GetString()
        {
            try
            {
                return _inner.GetString();
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Malformed or truncated string.", ex);
            }
        }
        /// <summary>
        /// <see cref="GetString"/> for a short name that repeats packet after packet (animation
        /// clips, item types): the same bytes give back the same string object instead of a new
        /// one per read. The bytes are read as <see cref="NetWriter.Put(string)"/> wrote them
        /// (LiteNetLib: a ushort of size + 1, 0 for empty, then UTF-8).
        /// </summary>
        public string GetClipString()
        {
            Require(2, "string");
            int size = _inner.GetUShort();
            if (size == 0)
                return string.Empty;
            int n = size - 1;
            if (n < 0 || _inner.AvailableBytes < n)
                throw new InvalidDataException("Malformed or truncated string.");
            byte[] data = _inner.RawData;
            int at = _inner.Position;
            if (n > MaxCachedStringBytes)
            {
                string longer = System.Text.Encoding.UTF8.GetString(data, at, n);
                _inner.SkipBytes(n);
                return longer;
            }
            uint hash = 2166136261;
            for (int i = 0; i < n; i++)
                hash = (hash ^ data[at + i]) * 16777619;
            int slot = (int)(hash & (StringSlots - 1));
            byte[][] slotBytes = _slotBytes ?? (_slotBytes = new byte[StringSlots][]);
            string[] slotStrings = _slotStrings ?? (_slotStrings = new string[StringSlots]);
            byte[] known = slotBytes[slot];
            if (known != null && known.Length == n)
            {
                int i = 0;
                while (i < n && known[i] == data[at + i])
                    i++;
                if (i == n)
                {
                    _inner.SkipBytes(n);
                    return slotStrings[slot];
                }
            }
            string s = System.Text.Encoding.UTF8.GetString(data, at, n);
            var copy = new byte[n];
            Buffer.BlockCopy(data, at, copy, 0, n);
            slotBytes[slot] = copy;
            slotStrings[slot] = s;
            _inner.SkipBytes(n);
            return s;
        }

        private const int MaxCachedStringBytes = 128;
        private const int StringSlots = 256;
        [ThreadStatic] private static byte[][] _slotBytes; // process-scoped: decoded-name cache per thread, by hash
        [ThreadStatic] private static string[] _slotStrings; // process-scoped: its strings

        /// <summary>Counterpart of <see cref="NetWriter.PutLongString"/>.</summary>
        public string GetLongString()
        {
            byte[] bytes = GetBytes();
            return bytes.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Remaining unread bytes (for optional trailing fields).</summary>
        public int AvailableBytes => _inner.AvailableBytes;
        public byte[] GetBytes()
        {
            int len = GetInt();
            if (len < 0)
                throw new InvalidDataException("Byte array length cannot be negative.");
            if (len == 0) return new byte[0];
            if (len > MaxBlobBytes)
                throw new InvalidDataException("Byte array exceeds " + MaxBlobBytes + " bytes.");
            Require(len, "byte array");
            byte[] result = new byte[len];
            try
            {
                _inner.GetBytes(result, 0, len);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Malformed or truncated byte array.", ex);
            }
            return result;
        }

        private void Require(int bytes, string field)
        {
            if (bytes < 0 || AvailableBytes < bytes)
                throw new InvalidDataException(
                    "Truncated packet while reading " + field
                    + " (need " + bytes + ", have " + AvailableBytes + ").");
        }
    }
}
