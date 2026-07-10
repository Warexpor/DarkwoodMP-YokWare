using System;
using System.Collections.Generic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Bulk world-file transfer (Phase 2 of world-download). Moves the host's/
/// server's ~11MB generated save to a joining client so both play the EXACT
/// same world instead of relying on deterministic worldgen.
///
/// Transport notes: the reliability layer has a ~2048-seq window and is built
/// for sparse idempotent packets, so we chunk at 8KB (~1400 chunks for 11MB,
/// under the window) and DRAIN A FEW PER FRAME (same paced pattern as the join
/// snapshot). Delivery is guaranteed by SendReliable; completion is detected by
/// counting chunks against the offer manifest.
///
/// Receivers are state-gated: only a client that called RequestWorld() (i.e.
/// connected from the menu and is waiting) consumes Offer/Chunk packets. Peers
/// already in-game ignore them, so broadcasting a transfer never reloads them.
///
/// The game glue is deliberately left as hooks:
///  - WorldProvider (host, Phase 3): flush save + return files + profile meta.
///  - OnWorldReady (client, Phase 4): load the freshly written world.
/// </summary>
public class WorldTransfer
{
    public const int ChunkSize = 8192;
    private const int ChunksPerFrame = 16;

    private readonly NetworkLayer _network;

    // Sender
    private readonly Queue<Packet> _sendQueue = new();
    private int _nextTransferId = 1;

    // Receiver
    private bool _awaiting;
    private int _activeTransferId = -1;
    private WorldOfferPacket _offer;
    private byte[][] _fileBuffers;
    private bool[][] _have;
    private int _totalChunks, _gotChunks;

    /// <summary>Host (Phase 3): supply the current world to send, or null if none available.</summary>
    public Func<WorldPayload> WorldProvider;

    /// <summary>Client (Phase 4): a downloaded world has been written to disk and is ready to load.</summary>
    public Action<WorldOfferPacket> OnWorldReady;

    public bool IsDownloading => _awaiting || _activeTransferId >= 0;
    public bool IsSending => _sendQueue.Count > 0;

    public WorldTransfer(NetworkLayer network)
    {
        _network = network;
    }

    public sealed class WorldPayload
    {
        public int Chapter, Day, Difficulty, MajorVersion, MinorVersion, RCVersion;
        public List<(string name, byte[] data)> Files = new();
    }

    // ------------------------------------------------------------------
    // Client: ask for the world
    // ------------------------------------------------------------------

    public void RequestWorld()
    {
        ResetReceiver();
        _awaiting = true;
        _network.SendReliable(new WorldRequestPacket { RequesterId = Math.Max(_network.LocalClientId, 0) });
        ModLogger.Msg("[WorldTransfer] Requested world from host/server");
    }

    // ------------------------------------------------------------------
    // Paced send drain
    // ------------------------------------------------------------------

    public void OnUpdate()
    {
        for (var i = 0; i < ChunksPerFrame && _sendQueue.Count > 0; i++)
            _network.SendReliable(_sendQueue.Dequeue());
    }

    // ------------------------------------------------------------------
    // Host/server: a client asked for the world
    // ------------------------------------------------------------------

    public void HandleRequest(WorldRequestPacket req)
    {
        var payload = WorldProvider?.Invoke();
        if (payload == null || payload.Files.Count == 0)
        {
            // Host-from-menu: the host has no world YET (still picking a
            // profile). Remember the ask and serve as soon as one is loaded
            // (NetworkManager calls TryServePending once in-game) - dropping
            // it stranded the menu-joiner forever.
            _pendingServe = true;
            ModLogger.Msg("[WorldTransfer] World requested but none loaded yet - will serve once a world is loaded");
            return;
        }
        BeginSend(payload);
    }

    private bool _pendingServe;

    /// <summary>An earlier world request could not be served (no world loaded).</summary>
    public bool HasPendingRequest => _pendingServe;

    /// <summary>
    /// Retry a request that arrived before the host had a world. One transfer
    /// serves every waiting joiner (transfers broadcast; receivers are
    /// state-gated on having called RequestWorld).
    /// </summary>
    public void TryServePending()
    {
        if (!_pendingServe) return;
        var payload = WorldProvider?.Invoke();
        if (payload == null || payload.Files.Count == 0) return;
        _pendingServe = false;
        ModLogger.Msg("[WorldTransfer] Serving world request that was waiting for a world");
        BeginSend(payload);
    }

    /// <summary>
    /// Unsolicited upload: the elected authority pushes its freshly-saved world
    /// to the dedicated server so the server's canonical copy never goes stale.
    /// Sent packets go only to the server (it consumes world packets instead of
    /// relaying); in-game peers ignore transfers they didn't request anyway.
    /// </summary>
    public bool UploadNow()
    {
        if (IsSending) return false; // don't interleave two transfers
        var payload = WorldProvider?.Invoke();
        if (payload == null || payload.Files.Count == 0) return false;
        ModLogger.Msg("[WorldTransfer] Uploading canonical save to server");
        BeginSend(payload);
        return true;
    }

    private void BeginSend(WorldPayload payload)
    {
        var tid = _nextTransferId++;
        var entries = new WorldOfferPacket.FileEntry[payload.Files.Count];
        for (var i = 0; i < payload.Files.Count; i++)
        {
            var (name, data) = payload.Files[i];
            entries[i] = new WorldOfferPacket.FileEntry
            {
                Name = name,
                TotalSize = data.Length,
                ChunkCount = (data.Length + ChunkSize - 1) / ChunkSize
            };
        }

        _sendQueue.Enqueue(new WorldOfferPacket
        {
            TransferId = tid,
            Chapter = payload.Chapter,
            Day = payload.Day,
            Difficulty = payload.Difficulty,
            MajorVersion = payload.MajorVersion,
            MinorVersion = payload.MinorVersion,
            RCVersion = payload.RCVersion,
            Files = entries
        });

        long totalBytes = 0;
        for (var fi = 0; fi < payload.Files.Count; fi++)
        {
            var data = payload.Files[fi].data;
            totalBytes += data.Length;
            var chunkCount = entries[fi].ChunkCount;
            for (var ci = 0; ci < chunkCount; ci++)
            {
                var offset = ci * ChunkSize;
                var len = Math.Min(ChunkSize, data.Length - offset);
                var slice = new byte[len];
                Buffer.BlockCopy(data, offset, slice, 0, len);
                _sendQueue.Enqueue(new WorldChunkPacket
                {
                    TransferId = tid,
                    FileIndex = fi,
                    ChunkIndex = ci,
                    Data = slice
                });
            }
        }
        _sendQueue.Enqueue(new WorldEndPacket { TransferId = tid });

        ModLogger.Msg($"[WorldTransfer] Sending world: {payload.Files.Count} files, {totalBytes / 1024}KB, {_sendQueue.Count} packets queued");
    }

    // ------------------------------------------------------------------
    // Client: receive
    // ------------------------------------------------------------------

    public void HandleOffer(WorldOfferPacket offer)
    {
        if (!_awaiting) return;              // not downloading -> ignore
        if (_activeTransferId == offer.TransferId) return; // duplicate offer

        _activeTransferId = offer.TransferId;
        _offer = offer;
        _fileBuffers = new byte[offer.Files.Length][];
        _have = new bool[offer.Files.Length][];
        _totalChunks = 0;
        _gotChunks = 0;

        for (var i = 0; i < offer.Files.Length; i++)
        {
            var size = Math.Max(offer.Files[i].TotalSize, 0);
            _fileBuffers[i] = new byte[size];
            _have[i] = new bool[Math.Max(offer.Files[i].ChunkCount, 0)];
            _totalChunks += offer.Files[i].ChunkCount;
        }
        ModLogger.Msg($"[WorldTransfer] Incoming world: {offer.Files.Length} files, {_totalChunks} chunks (chapter {offer.Chapter}, day {offer.Day})");
    }

    public void HandleChunk(WorldChunkPacket c)
    {
        if (_activeTransferId < 0 || c.TransferId != _activeTransferId || _fileBuffers == null) return;
        if (c.FileIndex < 0 || c.FileIndex >= _fileBuffers.Length) return;
        var buffer = _fileBuffers[c.FileIndex];
        var have = _have[c.FileIndex];
        if (c.ChunkIndex < 0 || c.ChunkIndex >= have.Length) return;
        if (have[c.ChunkIndex]) return;      // duplicate

        var offset = (long)c.ChunkIndex * ChunkSize;
        if (offset < 0 || offset + c.Data.Length > buffer.Length) return; // corrupt

        Buffer.BlockCopy(c.Data, 0, buffer, (int)offset, c.Data.Length);
        have[c.ChunkIndex] = true;
        _gotChunks++;

        if (_gotChunks >= _totalChunks)
            Complete();
    }

    public void HandleEnd(WorldEndPacket end)
    {
        // With guaranteed delivery, completion is detected by count. End is a
        // fallback: if it arrives and we somehow already have everything, finish.
        if (end.TransferId == _activeTransferId && _gotChunks >= _totalChunks && _totalChunks > 0)
            Complete();
    }

    private void Complete()
    {
        var offer = _offer;
        var files = new List<(string, byte[])>(offer.Files.Length);
        for (var i = 0; i < offer.Files.Length; i++)
            files.Add((offer.Files[i].Name, _fileBuffers[i]));

        var ok = SaveFiles.WriteWorld(SaveFiles.MultiplayerProfileId, files);
        ResetReceiver();

        if (ok)
        {
            ModLogger.Msg("[WorldTransfer] World download complete - written to MP profile");
            OnWorldReady?.Invoke(offer);
        }
        else
        {
            ModLogger.Error("[WorldTransfer] World download complete but writing files failed");
        }
    }

    public void ResetReceiver()
    {
        _awaiting = false;
        _activeTransferId = -1;
        _fileBuffers = null;
        _have = null;
        _totalChunks = 0;
        _gotChunks = 0;
    }

    public void Reset()
    {
        _sendQueue.Clear();
        ResetReceiver();
    }
}
