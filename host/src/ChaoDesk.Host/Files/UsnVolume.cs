using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Files;

/// <summary>
/// Reads one NTFS volume for the elevated helper: every name from the MFT (FSCTL_ENUM_USN_DATA), then live changes
/// from the USN journal (FSCTL_READ_USN_JOURNAL, blocking until something happens). Needs administrator rights.
/// </summary>
internal sealed class UsnVolume
{
    private const uint ChangeReasons = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE | USN_REASON_RENAME_NEW_NAME |
                                       USN_REASON_RENAME_OLD_NAME | USN_REASON_BASIC_INFO_CHANGE;
    private const int BufferSize = 1 << 20;

    private readonly char _letter;
    private readonly int _volume;
    private readonly IndexWriter _out;
    private readonly IndexCheckpoints.Entry? _resumeFrom;

    /// <param name="resumeFrom">Where the app's saved index left this volume's journal, if it has one.</param>
    public UsnVolume(char letter, IndexWriter output, IndexCheckpoints.Entry? resumeFrom)
    {
        _letter = char.ToUpperInvariant(letter);
        _volume = _letter - 'A';
        _out = output;
        _resumeFrom = resumeFrom;
    }

    /// <summary>
    /// Runs until the drive goes away (then reports VolumeGone and returns) or the process exits. Continues from the
    /// saved checkpoint when the journal still covers it; re-reads the whole volume otherwise, or when the journal
    /// is reset or wraps.
    /// </summary>
    public void Run()
    {
        using var volume = CreateFile($@"\\.\{_letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (volume.IsInvalid)
        {
            Fail($"Could not open {_letter}: ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
            return;
        }
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            bool mayResume = true;
            while (true)
            {
                var journal = QueryJournal(volume);
                if (journal == null) return;
                long start;
                if (mayResume && _resumeFrom is { } saved && saved.JournalId == journal.Value.UsnJournalID &&
                    saved.Usn >= journal.Value.FirstUsn && saved.Usn <= journal.Value.NextUsn)
                {
                    // The saved index is still valid: replay only what changed since it was written.
                    start = saved.Usn;
                    _out.VolumeResume(_volume);
                    Log.Information("Resuming {Volume} from the saved index ({Bytes:N0} journal bytes behind)", $"{_letter}:", journal.Value.NextUsn - saved.Usn);
                }
                else
                {
                    var started = Environment.TickCount64;
                    long count = Enumerate(volume, journal.Value, buffer);
                    Log.Information("Indexed {Volume}: {Count:N0} entries in {Ms} ms", $"{_letter}:", count, Environment.TickCount64 - started);
                    start = journal.Value.NextUsn;
                    _out.Checkpoint(_volume, journal.Value.UsnJournalID, start);
                }
                mayResume = false;
                _out.VolumeReady(_volume);
                _out.Flush();
                if (!Follow(volume, journal.Value, start, buffer)) // false: the drive is gone
                {
                    _out.VolumeGone(_volume);
                    _out.Flush();
                    return;
                }
                Log.Warning("USN journal on {Volume} was reset; reading the volume again", $"{_letter}:");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private unsafe USN_JOURNAL_DATA_V0? QueryJournal(SafeFileHandle volume)
    {
        USN_JOURNAL_DATA_V0 data;
        if (DeviceIoControl(volume, FSCTL_QUERY_USN_JOURNAL, null, 0, &data, sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero)) return data;
        int error = Marshal.GetLastWin32Error();
        if (error is ERROR_JOURNAL_NOT_ACTIVE or ERROR_JOURNAL_DELETE_IN_PROGRESS)
        {
            // Windows keeps a journal on the system drive; other NTFS drives may have none. Create a modest one,
            // as Everything does (32 MB, grows 8 MB at a time).
            var create = new CREATE_USN_JOURNAL_DATA { MaximumSize = 32UL << 20, AllocationDelta = 8UL << 20 };
            if (DeviceIoControl(volume, FSCTL_CREATE_USN_JOURNAL, &create, sizeof(CREATE_USN_JOURNAL_DATA), null, 0, out _, IntPtr.Zero) &&
                DeviceIoControl(volume, FSCTL_QUERY_USN_JOURNAL, null, 0, &data, sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
            {
                Log.Information("Created a USN journal on {Volume}", $"{_letter}:");
                return data;
            }
            error = Marshal.GetLastWin32Error();
        }
        Fail($"No change journal on {_letter}: ({new Win32Exception(error).Message})");
        return null;
    }

    private unsafe long Enumerate(SafeFileHandle volume, USN_JOURNAL_DATA_V0 journal, IntPtr buffer)
    {
        _out.VolumeBegin(_volume, EstimateEntries(volume));
        var med = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = journal.NextUsn };
        long count = 0;
        while (DeviceIoControl(volume, FSCTL_ENUM_USN_DATA, &med, sizeof(MFT_ENUM_DATA_V0), (void*)buffer, BufferSize, out int returned, IntPtr.Zero))
        {
            if (returned <= 8) break;
            med.StartFileReferenceNumber = *(ulong*)buffer;
            count += EmitRecords(buffer, returned, deletes: false);
        }
        int error = Marshal.GetLastWin32Error();
        if (error != ERROR_HANDLE_EOF && error != 0) Log.Warning("Enumerating {Volume} stopped early ({Error})", $"{_letter}:", error);
        return count;
    }

    private unsafe long EstimateEntries(SafeFileHandle volume)
    {
        NTFS_VOLUME_DATA_BUFFER data;
        if (!DeviceIoControl(volume, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &data, sizeof(NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero) || data.BytesPerFileRecordSegment == 0)
            return 100_000;
        // MFT records in use is an upper bound (many are free); the index reserves room accordingly.
        return data.MftValidDataLength / data.BytesPerFileRecordSegment;
    }

    /// <summary>Follows the journal from <paramref name="start"/>; true when it must be re-read from scratch, false when the drive is gone.</summary>
    private unsafe bool Follow(SafeFileHandle volume, USN_JOURNAL_DATA_V0 journal, long start, IntPtr buffer)
    {
        var read = new READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = start,
            ReasonMask = ChangeReasons,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 1, // block until there is at least one record
            UsnJournalID = journal.UsnJournalID,
        };
        while (true)
        {
            if (!DeviceIoControl(volume, FSCTL_READ_USN_JOURNAL, &read, sizeof(READ_USN_JOURNAL_DATA_V0), (void*)buffer, BufferSize, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is ERROR_JOURNAL_ENTRY_DELETED or ERROR_JOURNAL_DELETE_IN_PROGRESS or ERROR_JOURNAL_NOT_ACTIVE) return true;
                if (!System.IO.Directory.Exists($@"{_letter}:\")) return false; // unplugged
                Log.Warning("Reading the USN journal on {Volume} failed ({Error}); retrying", $"{_letter}:", error);
                Thread.Sleep(5000);
                continue;
            }
            if (returned < 8) continue;
            read.StartUsn = *(long*)buffer;
            EmitRecords(buffer, returned, deletes: true);
            _out.Checkpoint(_volume, journal.UsnJournalID, read.StartUsn);
        }
    }

    /// <summary>Parses USN_RECORD_V2 entries after the 8-byte header and forwards them.</summary>
    private unsafe long EmitRecords(IntPtr buffer, int length, bool deletes)
    {
        long count = 0;
        byte* p = (byte*)buffer + 8;
        byte* end = (byte*)buffer + length;
        while (p + 60 <= end)
        {
            uint recordLength = *(uint*)p;
            if (recordLength == 0 || p + recordLength > end) break;
            ushort major = *(ushort*)(p + 4);
            if (major == 2)
            {
                long frn = *(long*)(p + 8);
                long parent = *(long*)(p + 16);
                uint reason = *(uint*)(p + 40);
                uint attributes = *(uint*)(p + 52);
                ushort nameBytes = *(ushort*)(p + 56);
                ushort nameOffset = *(ushort*)(p + 58);
                var name = new ReadOnlySpan<char>(p + nameOffset, nameBytes / 2);
                if (deletes && (reason & USN_REASON_FILE_DELETE) != 0) _out.Delete(_volume, frn);
                else if (!deletes || (reason & USN_REASON_RENAME_OLD_NAME) == 0 || (reason & USN_REASON_RENAME_NEW_NAME) != 0)
                    _out.Entry(_volume, frn, parent, attributes, name);
                count++;
            }
            p += recordLength;
        }
        return count;
    }

    private void Fail(string message)
    {
        Log.Warning("{Message}", message);
        _out.Error(message);
        _out.Flush();
    }
}
