namespace Dsh.RemoteHost;

/** host.fs.stat: Exists is false (and the rest empty) for a missing path; times are Unix milliseconds. */
public sealed record RemoteFileStat(
    string Path,
    bool Exists,
    bool IsDirectory = false,
    bool IsSymbolicLink = false,
    long Size = 0,
    long ModifiedAt = 0);

public sealed record RemoteFsRename(string From, string To, bool Overwrite = false);

/** Deleting a non-empty directory needs Recursive. */
public sealed record RemoteFsDelete(string Path, bool Recursive = false);

/** host.fs.upload.begin: bytes go to a temporary file next to Path and replace Path atomically on commit. */
public sealed record RemoteUploadBegin(string Path, bool Overwrite = true);

/** ChunkBytes is the largest chunk the host accepts. */
public sealed record RemoteUploadRef(string UploadId, int ChunkBytes = 0);

/** Offset must equal the number of bytes received so far, so a retried chunk is rejected instead of written twice. */
public sealed record RemoteUploadChunk(string UploadId, long Offset, string Base64);

/** Size, when given, must match the received byte count. */
public sealed record RemoteUploadCommit(string UploadId, long? Size = null);

/** host.fs.download: Length is capped at the host's chunk size and defaults to it. */
public sealed record RemoteDownloadRequest(string Path, long Offset, int? Length = null);

public sealed record RemoteFileChunk(string Path, long Offset, string Base64, long TotalSize, bool Eof);

public sealed record RemoteFilePath(string Path);

/** A file or directory entry; timestamps are Unix milliseconds. */
public sealed record RemoteDirectoryEntry(string Name, string Path, bool IsDirectory, long? Size = null, long? ModifiedAt = null);

/** One directory listing, including its parent when present. */
public sealed record RemoteDirectoryListing(string Path, string? Parent, IReadOnlyList<RemoteDirectoryEntry> Entries);

public sealed record RemoteFileContent(string Path, string Base64);
