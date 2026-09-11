using System.Buffers.Binary;
using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;

namespace NineP.TestSupport;

/// <summary>Generated file and directory data whose storage is independent of logical size.</summary>
public sealed class GeneratedFilesystem : IFilesystem
{
    public GeneratedFilesystem(ulong length = 0, int entries = 0, int nameBytes = 8)
    {
        File = new GeneratedFile("data", length);
        Root = new GeneratedDirectory(File, entries, nameBytes);
    }

    public GeneratedFile File { get; }
    public GeneratedDirectory Root { get; }

    public ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IDirectoryHandler>(Root);
}

/// <summary>Each eight-byte block encodes its full block index, making high-bit truncation visible.</summary>
public static class OffsetPattern
{
    public static void Fill(ulong offset, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            ulong position = offset + (ulong)i;
            int lane = (int)(position & 7);
            destination[i] = (byte)((position >> 3 >> (lane * 8)) ^ (ulong)(0xA5 + (lane * 17)));
        }
    }

    public static bool Matches(ulong offset, ReadOnlySpan<byte> data)
    {
        // Decode full blocks instead of calling the generator as the oracle.
        Span<byte> block = stackalloc byte[8];
        for (int start = 0; start < data.Length;)
        {
            ulong position = offset + (ulong)start;
            BinaryPrimitives.WriteUInt64LittleEndian(block, position / 8);
            int lane = (int)(position % 8);
            for (; lane < 8 && start < data.Length; lane++, start++)
            {
                if (data[start] != (byte)(block[lane] ^ (0xA5 + (lane * 17))))
                {
                    return false;
                }
            }
        }
        return true;
    }
}

public sealed class GeneratedFile(string name, ulong length)
    : MemoryNode(name, FileKind.File, Perms.P0666, 2), IFileHandler
{
    public ulong Length { get; set; } = length;
    public ulong? ReportedSize { get; set; }
    public override ulong Size => ReportedSize ?? Length;
    public int MaxChunk { get; set; } = int.MaxValue;
    public int ReadCalls { get; private set; }
    public int LargestRead { get; private set; }
    public int Opens { get; private set; }
    public ulong LastWriteOffset { get; private set; }
    public long Written { get; private set; }

    public ValueTask<IOpenFile> OpenAsync(OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Opens++;
        // The server owns and disposes the returned open instance.
#pragma warning disable CA2000
        return ValueTask.FromResult<IOpenFile>(new Open(this));
#pragma warning restore CA2000
    }

    private sealed class Open(GeneratedFile file) : IOpenFile
    {
        public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.ReadCalls++;
            file.LargestRead = Math.Max(file.LargestRead, buffer.Length);
            int count = offset >= file.Length ? 0 : (int)Math.Min((ulong)Math.Min(buffer.Length, file.MaxChunk), file.Length - offset);
            OffsetPattern.Fill(offset, buffer.Span[..count]);
            return ValueTask.FromResult(count);
        }

        public ValueTask<int> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OffsetPattern.Matches(offset, data.Span))
            {
                throw new NinePException(NinePError.FromErrno(Errno.EIO));
            }
            file.LastWriteOffset = offset;
            file.Written += data.Length;
            return ValueTask.FromResult(data.Length);
        }

        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(file.Length);
        public ValueTask DisposeAsync()
        {
            file.Opens--;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class GeneratedDirectory(GeneratedFile file, int count, int nameBytes)
    : MemoryNode("/", FileKind.Directory, Perms.P0777, 1), IDirectoryHandler
{
    public int Pages { get; private set; }
    public int LargestPage { get; private set; }
    public ulong CookieBase { get; set; }

    public string NameAt(int index) => "e" + index.ToString("D7", CultureInfo.InvariantCulture) + new string('x', Math.Max(0, nameBytes - 8));

    public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name == "data")
        {
            return ValueTask.FromResult<IHandler?>(file);
        }
        if (name.Length >= 8 && int.TryParse(name.AsSpan(1, 7), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index < count && string.Equals(name, NameAt(index), StringComparison.Ordinal))
        {
            return ValueTask.FromResult<IHandler?>(new MemoryFile(name, Perms.P0644, (ulong)index + 3));
        }
        return ValueTask.FromResult<IHandler?>(null);
    }

    public ValueTask<DirectoryListing> ReadDirAsync(ulong cursor, int max, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pages++;
        int start = cursor == 0 ? 0 : (int)Math.Min((ulong)count, cursor - CookieBase);
        int take = Math.Min(Math.Max(0, max), count - start);
        LargestPage = Math.Max(LargestPage, take);
        List<DirEntry> page = new(take);
        for (int i = start; i < start + take; i++)
        {
            page.Add(new DirEntry(NameAt(i), new Qid(QidType.QTFILE, 0, (ulong)i + 3), FileKind.File, CookieBase + (ulong)i + 1));
        }
        return ValueTask.FromResult(new DirectoryListing(page, page.Count == 0 ? cursor : page[^1].Cursor, start + take == count));
    }

    public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IHandler>(new NinePException(NinePError.FromErrno(Errno.EROFS)));
    public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NinePException(NinePError.FromErrno(Errno.EROFS)));
    public ValueTask RenameAsync(string oldName, IDirectoryHandler newParent, string newName, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NinePException(NinePError.FromErrno(Errno.EROFS)));
}
