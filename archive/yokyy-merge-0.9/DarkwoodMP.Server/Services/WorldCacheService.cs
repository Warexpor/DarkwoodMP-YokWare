using System.Net;
using DarkwoodMP.Packets;
using Microsoft.Extensions.Logging;

namespace DarkwoodMP.Server.Services;

/// <summary>
/// Dedicated-server world store (Phase 6 of world-download). The server has no
/// game engine, so it cannot GENERATE a world - it can only STORE and SERVE one.
///
/// Per the chosen model ("both"): on startup it loads an admin-provided world
/// from &lt;base&gt;/world/ if present; otherwise the FIRST in-game client to ask for
/// a world is asked to upload its own (seeding the cache), which is then
/// persisted to &lt;base&gt;/world_cache/ and served to every later client.
///
/// The wire protocol is symmetric with the mod's WorldTransfer: WorldRequest /
/// WorldOffer / WorldChunk / WorldEnd. The server both serves (per-endpoint
/// paced send queue drained each tick) and receives one upload at a time.
/// </summary>
public class WorldCacheService
{
    private const int ChunkSize = 8192;
    private const int ChunksPerTick = 32;
    private static readonly string[] FileNames = { "sav.dat", "savs.dat", "savch.dat" };

    private readonly ILogger<WorldCacheService> _logger;
    private readonly string _adminDir;
    private readonly string _cacheDir;
    private readonly object _lock = new();

    // Cached world
    private bool _hasWorld;
    private WorldMeta _meta;
    private readonly Dictionary<string, byte[]> _files = new();

    // Serving: one paced send queue per downloading endpoint
    private readonly Dictionary<IPEndPoint, Queue<Packet>> _sendQueues = new();
    private int _nextTransferId = 1;

    // Uploads: at most one at a time. Two flows share this state:
    //  - seed: no world cached, the server ASKED _seeder to upload one
    //  - refresh: the elected authority pushes its latest save on its own
    //    (canonical-save model - the server's copy would otherwise go stale
    //    after the first seed and rejoiners would get an old world)
    private IPEndPoint? _seeder;
    private DateTime _seedAskedAt;
    private static readonly TimeSpan SeedTimeout = TimeSpan.FromSeconds(12);
    private IPEndPoint? _uploadFrom;
    private int _uploadTransferId = -1;
    private WorldOfferPacket? _uploadOffer;
    private byte[][]? _uploadBuffers;
    private bool[][]? _uploadHave;
    private int _uploadTotal, _uploadGot;

    /// <summary>Set by ServerHostService: reliably send a packet to one endpoint.</summary>
    public Action<IPEndPoint, Packet>? SendReliable;

    /// <summary>
    /// Set by ServerHostService: is this endpoint the elected simulation
    /// authority (lowest-id connected player)? Gates unsolicited refresh
    /// uploads so a random client can't overwrite the canonical world.
    /// </summary>
    public Func<IPEndPoint, bool>? IsAuthorityEndpoint;

    public bool HasWorld { get { lock (_lock) return _hasWorld; } }

    private struct WorldMeta
    {
        public int Chapter, Day, Difficulty, MajorVersion, MinorVersion, RCVersion;
    }

    public WorldCacheService(ILogger<WorldCacheService> logger)
    {
        _logger = logger;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _adminDir = Path.Combine(baseDir, "world");
        _cacheDir = Path.Combine(baseDir, "world_cache");
        LoadFromDisk();
    }

    // ------------------------------------------------------------------
    // Startup load: admin dir wins, else previously-seeded cache
    // ------------------------------------------------------------------

    private void LoadFromDisk()
    {
        if (TryLoadDir(_adminDir, "admin")) return;
        TryLoadDir(_cacheDir, "cache");
    }

    private bool TryLoadDir(string dir, string label)
    {
        try
        {
            var dyn = Path.Combine(dir, "sav.dat");
            var stat = Path.Combine(dir, "savs.dat");
            if (!File.Exists(dyn) || !File.Exists(stat)) return false;

            _files.Clear();
            foreach (var name in FileNames)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) _files[name] = File.ReadAllBytes(path);
            }
            _meta = ReadMeta(Path.Combine(dir, "world.meta"));
            _hasWorld = true;
            _logger.LogInformation("Loaded {Label} world ({Bytes} KB, chapter {Ch}, day {Day})",
                label, TotalBytes() / 1024, _meta.Chapter, _meta.Day);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {Label} world from {Dir}", label, dir);
            return false;
        }
    }

    private static WorldMeta ReadMeta(string path)
    {
        var m = new WorldMeta();
        try
        {
            if (!File.Exists(path)) return m;
            var parts = File.ReadAllText(path).Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6)
            {
                int.TryParse(parts[0], out m.Chapter);
                int.TryParse(parts[1], out m.Day);
                int.TryParse(parts[2], out m.Difficulty);
                int.TryParse(parts[3], out m.MajorVersion);
                int.TryParse(parts[4], out m.MinorVersion);
                int.TryParse(parts[5], out m.RCVersion);
            }
        }
        catch { /* defaults */ }
        return m;
    }

    private void PersistCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            foreach (var kvp in _files)
                File.WriteAllBytes(Path.Combine(_cacheDir, kvp.Key), kvp.Value);
            File.WriteAllText(Path.Combine(_cacheDir, "world.meta"),
                $"{_meta.Chapter} {_meta.Day} {_meta.Difficulty} {_meta.MajorVersion} {_meta.MinorVersion} {_meta.RCVersion}");
            _logger.LogInformation("Persisted seeded world to {Dir}", _cacheDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist seeded world");
        }
    }

    private long TotalBytes()
    {
        long n = 0;
        foreach (var b in _files.Values) n += b.Length;
        return n;
    }

    // ------------------------------------------------------------------
    // A client asked for the world
    // ------------------------------------------------------------------

    public void HandleRequest(IPEndPoint endpoint, WorldRequestPacket req)
    {
        lock (_lock)
        {
            if (_hasWorld)
            {
                BeginServe(endpoint);
                return;
            }

            // No world yet - ask this client to seed us, if nobody else is.
            if (_seeder == null)
            {
                _seeder = endpoint;
                _seedAskedAt = DateTime.UtcNow;
                _logger.LogInformation("No world cached - asking {Endpoint} to upload its world (seeding)", endpoint);
                SendReliable?.Invoke(endpoint, new WorldRequestPacket { RequesterId = -1 });
            }
            else
            {
                _logger.LogDebug("World requested by {Endpoint} but a seed is already in progress", endpoint);
            }
        }
    }

    private void BeginServe(IPEndPoint endpoint)
    {
        var tid = _nextTransferId++;
        var files = FileNames.Where(_files.ContainsKey).ToArray();
        var entries = new WorldOfferPacket.FileEntry[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            var data = _files[files[i]];
            entries[i] = new WorldOfferPacket.FileEntry
            {
                Name = files[i],
                TotalSize = data.Length,
                ChunkCount = (data.Length + ChunkSize - 1) / ChunkSize
            };
        }

        var queue = new Queue<Packet>();
        queue.Enqueue(new WorldOfferPacket
        {
            TransferId = tid,
            Chapter = _meta.Chapter, Day = _meta.Day, Difficulty = _meta.Difficulty,
            MajorVersion = _meta.MajorVersion, MinorVersion = _meta.MinorVersion, RCVersion = _meta.RCVersion,
            Files = entries
        });
        for (var fi = 0; fi < files.Length; fi++)
        {
            var data = _files[files[fi]];
            for (var ci = 0; ci < entries[fi].ChunkCount; ci++)
            {
                var offset = ci * ChunkSize;
                var len = Math.Min(ChunkSize, data.Length - offset);
                var slice = new byte[len];
                Buffer.BlockCopy(data, offset, slice, 0, len);
                queue.Enqueue(new WorldChunkPacket { TransferId = tid, FileIndex = fi, ChunkIndex = ci, Data = slice });
            }
        }
        queue.Enqueue(new WorldEndPacket { TransferId = tid });
        _sendQueues[endpoint] = queue;
        _logger.LogInformation("Serving world to {Endpoint}: {Count} packets", endpoint, queue.Count);
    }

    // ------------------------------------------------------------------
    // Receiving an upload (seed) from the designated client
    // ------------------------------------------------------------------

    public void HandleOffer(IPEndPoint endpoint, WorldOfferPacket offer)
    {
        lock (_lock)
        {
            // Accept from the designated seeder, or from the elected authority
            // (unsolicited canonical-save refresh); never two uploads at once.
            var isSeeder = _seeder != null && endpoint.Equals(_seeder);
            var isAuthority = IsAuthorityEndpoint?.Invoke(endpoint) ?? false;
            if (!isSeeder && !isAuthority) return;
            if (_uploadFrom != null && !endpoint.Equals(_uploadFrom)) return;

            _uploadFrom = endpoint;
            if (!isSeeder)
                _logger.LogInformation("Authority {Endpoint} is refreshing the canonical world", endpoint);
            _uploadTransferId = offer.TransferId;
            _uploadOffer = offer;
            _uploadBuffers = new byte[offer.Files.Length][];
            _uploadHave = new bool[offer.Files.Length][];
            _uploadTotal = 0;
            _uploadGot = 0;
            for (var i = 0; i < offer.Files.Length; i++)
            {
                _uploadBuffers[i] = new byte[Math.Max(offer.Files[i].TotalSize, 0)];
                _uploadHave[i] = new bool[Math.Max(offer.Files[i].ChunkCount, 0)];
                _uploadTotal += offer.Files[i].ChunkCount;
            }
            _logger.LogInformation("Receiving seed upload from {Endpoint}: {Chunks} chunks", endpoint, _uploadTotal);
        }
    }

    public void HandleChunk(IPEndPoint endpoint, WorldChunkPacket c)
    {
        lock (_lock)
        {
            if (_uploadFrom == null || !endpoint.Equals(_uploadFrom) || _uploadBuffers == null) return;
            if (c.TransferId != _uploadTransferId) return;
            if (c.FileIndex < 0 || c.FileIndex >= _uploadBuffers.Length) return;
            var buf = _uploadBuffers[c.FileIndex];
            var have = _uploadHave![c.FileIndex];
            if (c.ChunkIndex < 0 || c.ChunkIndex >= have.Length || have[c.ChunkIndex]) return;
            var offset = (long)c.ChunkIndex * ChunkSize;
            if (offset < 0 || offset + c.Data.Length > buf.Length) return;
            Buffer.BlockCopy(c.Data, 0, buf, (int)offset, c.Data.Length);
            have[c.ChunkIndex] = true;
            _uploadGot++;
            if (_uploadGot >= _uploadTotal) CompleteUpload();
        }
    }

    public void HandleEnd(IPEndPoint endpoint, WorldEndPacket end)
    {
        lock (_lock)
        {
            if (_uploadFrom != null && endpoint.Equals(_uploadFrom) && _uploadGot >= _uploadTotal && _uploadTotal > 0)
                CompleteUpload();
        }
    }

    private void CompleteUpload()
    {
        var offer = _uploadOffer!;
        _files.Clear();
        for (var i = 0; i < offer.Files.Length; i++)
            _files[offer.Files[i].Name] = _uploadBuffers![i];
        _meta = new WorldMeta
        {
            Chapter = offer.Chapter, Day = offer.Day, Difficulty = offer.Difficulty,
            MajorVersion = offer.MajorVersion, MinorVersion = offer.MinorVersion, RCVersion = offer.RCVersion
        };
        _hasWorld = true;
        _seeder = null;
        _uploadFrom = null;
        _uploadTransferId = -1;
        _uploadBuffers = null;
        _uploadHave = null;
        _uploadOffer = null;
        _logger.LogInformation("World upload complete - canonical world updated ({Bytes} KB, day {Day})",
            TotalBytes() / 1024, _meta.Day);
        PersistCache();
    }

    /// <summary>A client disconnected - abandon an in-flight seed/serve for it.</summary>
    public void OnEndpointGone(IPEndPoint endpoint)
    {
        lock (_lock)
        {
            _sendQueues.Remove(endpoint);
            if ((_seeder != null && endpoint.Equals(_seeder))
                || (_uploadFrom != null && endpoint.Equals(_uploadFrom)))
            {
                _seeder = null;
                _uploadFrom = null;
                _uploadBuffers = null;
                _uploadHave = null;
                _uploadOffer = null;
                _uploadTransferId = -1;
                _logger.LogWarning("Uploader {Endpoint} left before finishing upload", endpoint);
            }
        }
    }

    // ------------------------------------------------------------------
    // Paced send drain (called each server tick)
    // ------------------------------------------------------------------

    public void Tick()
    {
        if (SendReliable == null) return;
        List<(IPEndPoint ep, Packet pkt)> toSend = new();
        lock (_lock)
        {
            // A designated seeder that never started uploading (e.g. it connected
            // from the menu with no world of its own) must not pin seeding forever.
            if (_seeder != null && _uploadOffer == null && DateTime.UtcNow - _seedAskedAt > SeedTimeout)
            {
                _logger.LogInformation("Seed request to {Endpoint} timed out - freeing seeding for another client", _seeder);
                _seeder = null;
            }

            if (_sendQueues.Count == 0) return;
            List<IPEndPoint>? done = null;
            foreach (var kvp in _sendQueues)
            {
                var q = kvp.Value;
                for (var i = 0; i < ChunksPerTick && q.Count > 0; i++)
                    toSend.Add((kvp.Key, q.Dequeue()));
                if (q.Count == 0) (done ??= new List<IPEndPoint>()).Add(kvp.Key);
            }
            if (done != null) foreach (var ep in done) _sendQueues.Remove(ep);
        }
        foreach (var (ep, pkt) in toSend)
            SendReliable(ep, pkt);
    }
}
