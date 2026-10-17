using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional Files wire contract; method and capability values are stable. */
public static class FilesProtocol
{
    public const string Capability = "fs.transfer";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodFileRead, MethodFileWrite, MethodFileList, MethodFsStat, MethodFsMkdir, MethodFsRename, MethodFsDelete,
            MethodFsUploadBegin, MethodFsUploadChunk, MethodFsUploadCommit, MethodFsUploadAbort, MethodFsDownload]);

    public const string MethodFileRead = "host.file.read";
    public const string MethodFileWrite = "host.file.write";
    public const string MethodFileList = "host.file.list";
    public const string MethodFsStat = "host.fs.stat";
    public const string MethodFsMkdir = "host.fs.mkdir";
    public const string MethodFsRename = "host.fs.rename";
    public const string MethodFsDelete = "host.fs.delete";
    public const string MethodFsUploadBegin = "host.fs.upload.begin";
    public const string MethodFsUploadChunk = "host.fs.upload.chunk";
    public const string MethodFsUploadCommit = "host.fs.upload.commit";
    public const string MethodFsUploadAbort = "host.fs.upload.abort";
    public const string MethodFsDownload = "host.fs.download";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteFilePath))]
[JsonSerializable(typeof(RemoteFileContent))]
[JsonSerializable(typeof(RemoteDirectoryEntry))]
[JsonSerializable(typeof(RemoteDirectoryListing))]
[JsonSerializable(typeof(IReadOnlyList<RemoteDirectoryEntry>))]
[JsonSerializable(typeof(RemoteFileStat))]
[JsonSerializable(typeof(RemoteFsRename))]
[JsonSerializable(typeof(RemoteFsDelete))]
[JsonSerializable(typeof(RemoteUploadBegin))]
[JsonSerializable(typeof(RemoteUploadRef))]
[JsonSerializable(typeof(RemoteUploadChunk))]
[JsonSerializable(typeof(RemoteUploadCommit))]
[JsonSerializable(typeof(RemoteDownloadRequest))]
[JsonSerializable(typeof(RemoteFileChunk))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
public partial class FilesProtocolJsonContext : JsonSerializerContext;
