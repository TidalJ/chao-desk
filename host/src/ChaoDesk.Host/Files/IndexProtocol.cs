using System.IO;
using System.Runtime.InteropServices;

namespace ChaoDesk.Host.Files;

/// <summary>
/// The one-way stream from the elevated index helper to the app. The app is the pipe
/// server at medium integrity; the helper connects and only ever writes, so nothing flows to the elevated side.
/// </summary>
internal static class IndexProtocol
{
    public const int Version = 2;

    public const byte Hello = 1;        // int version
    public const byte VolumeBegin = 2;  // byte volume, long estimated entries
    public const byte Entry = 3;        // byte volume, long frn, long parent frn, uint attributes, ushort length, UTF-16 name
    public const byte Delete = 4;       // byte volume, long frn
    public const byte VolumeReady = 5;  // byte volume
    public const byte Ping = 6;
    public const byte Error = 7;        // string message
    public const byte Checkpoint = 8;   // byte volume, ulong journal id, long next USN — everything before it is applied
    public const byte VolumeResume = 9; // byte volume — continuing from the saved index; journal changes follow
    public const byte VolumeGone = 10;  // byte volume — the drive was removed

    public static string PipeName(int sessionId) => $"ChaoDesk.FileIndex.{sessionId}";
}

/// <summary>Helper side: buffered, thread-safe writes. Any I/O error means the app has gone.</summary>
internal sealed class IndexWriter
{
    private readonly BinaryWriter _w;
    private readonly object _gate = new();

    public IndexWriter(Stream pipe) => _w = new BinaryWriter(new BufferedStream(pipe, 1 << 20));

    public void Hello() { lock (_gate) { _w.Write(IndexProtocol.Hello); _w.Write(IndexProtocol.Version); } }
    public void Ping() { lock (_gate) _w.Write(IndexProtocol.Ping); }
    public void VolumeReady(int volume) { lock (_gate) { _w.Write(IndexProtocol.VolumeReady); _w.Write((byte)volume); } }
    public void Error(string message) { lock (_gate) { _w.Write(IndexProtocol.Error); _w.Write(message); } }
    public void VolumeResume(int volume) { lock (_gate) { _w.Write(IndexProtocol.VolumeResume); _w.Write((byte)volume); } }
    public void VolumeGone(int volume) { lock (_gate) { _w.Write(IndexProtocol.VolumeGone); _w.Write((byte)volume); } }

    public void Checkpoint(int volume, ulong journalId, long usn)
    {
        lock (_gate) { _w.Write(IndexProtocol.Checkpoint); _w.Write((byte)volume); _w.Write(journalId); _w.Write(usn); }
    }

    public void VolumeBegin(int volume, long estimate)
    {
        lock (_gate) { _w.Write(IndexProtocol.VolumeBegin); _w.Write((byte)volume); _w.Write(estimate); }
    }

    public void Entry(int volume, long frn, long parentFrn, uint attributes, ReadOnlySpan<char> name)
    {
        lock (_gate)
        {
            _w.Write(IndexProtocol.Entry);
            _w.Write((byte)volume);
            _w.Write(frn);
            _w.Write(parentFrn);
            _w.Write(attributes);
            _w.Write((ushort)name.Length);
            _w.Write(MemoryMarshal.AsBytes(name));
        }
    }

    public void Delete(int volume, long frn)
    {
        lock (_gate) { _w.Write(IndexProtocol.Delete); _w.Write((byte)volume); _w.Write(frn); }
    }

    public void Flush() { lock (_gate) _w.Flush(); }
}
