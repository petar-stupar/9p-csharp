using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// A page of directory entries (architecture §4). The handler decides how many it can produce; the
/// core decides how many fit the reply, and never splits one across replies (reference §6.7).
/// </summary>
/// <param name="Entries">The entries, in listing order, without "." or ".." (S-27).</param>
/// <param name="NextCursor">The cursor that continues after the last entry.</param>
/// <param name="EndOfDirectory">True when there is nothing after these entries.</param>
public readonly record struct DirectoryListing(
    IReadOnlyList<DirEntry> Entries, ulong NextCursor, bool EndOfDirectory);
