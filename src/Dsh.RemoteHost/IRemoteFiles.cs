namespace Dsh.RemoteHost;

/** Optional remote file browsing and transfer capabilities. */
public interface IRemoteFiles
{
    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /** 列出远端某目录的子项(供“选择远端目录”浏览); Path 支持 `~` 前缀。 */
    Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /** host.fs.* (needs fs.transfer); a missing path stats as Exists = false. */
    Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default);

    Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default);

    /** Returns the number of bytes the host has received so far. */
    Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default);

    Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default);

    /** Streams Source to Path chunk by chunk and replaces Path atomically at the end; an error aborts the upload. */
    Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default);

    /** Copies Path into Destination chunk by chunk; returns the number of bytes copied. */
    Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default);
}
