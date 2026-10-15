using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.file.* and host.fs.* requests of one connection. */
internal static class HostFileMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(HostFileSystem files, HostUploads uploads, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case HostProtocol.MethodFileRead:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteFilePath, "read requires path");
                var bytes = await files.ReadAsync(request.Path, cancellationToken).ConfigureAwait(false);
                return Serialize(new RemoteFileContent(request.Path, Convert.ToBase64String(bytes)), HostProtocolJsonContext.Default.RemoteFileContent);
            }
            case HostProtocol.MethodFileWrite:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteFileContent, "write requires path and content");
                await files.WriteAsync(request.Path, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                return True;
            }
            case HostProtocol.MethodFileList:
                return Serialize(files.List(Require(message, HostProtocolJsonContext.Default.RemoteFilePath, "list requires path").Path), HostProtocolJsonContext.Default.RemoteDirectoryListing);
            case HostProtocol.MethodFsStat:
                return Serialize(files.Stat(Require(message, HostProtocolJsonContext.Default.RemoteFilePath, "stat requires path").Path), HostProtocolJsonContext.Default.RemoteFileStat);
            case HostProtocol.MethodFsMkdir:
                files.CreateDirectory(Require(message, HostProtocolJsonContext.Default.RemoteFilePath, "mkdir requires path").Path);
                return True;
            case HostProtocol.MethodFsRename:
                files.Rename(Require(message, HostProtocolJsonContext.Default.RemoteFsRename, "rename requires from and to"));
                return True;
            case HostProtocol.MethodFsDelete:
                files.Delete(Require(message, HostProtocolJsonContext.Default.RemoteFsDelete, "delete requires path"));
                return True;
            case HostProtocol.MethodFsDownload:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteDownloadRequest, "download requires path and offset");
                return Serialize(await files.DownloadAsync(request, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteFileChunk);
            }
            default:
                return await HandleUploadAsync(files, uploads, name, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<JsonElement?> HandleUploadAsync(HostFileSystem files, HostUploads uploads, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case HostProtocol.MethodFsUploadBegin:
            {
                var upload = files.BeginUpload(Require(message, HostProtocolJsonContext.Default.RemoteUploadBegin, "upload begin requires path"));
                uploads.Add(upload);
                return Serialize(new RemoteUploadRef(upload.Id, HostFileSystem.TransferChunkBytes), HostProtocolJsonContext.Default.RemoteUploadRef);
            }
            case HostProtocol.MethodFsUploadChunk:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteUploadChunk, "upload chunk requires uploadId, offset and content");
                var received = await uploads.Get(request.UploadId).AppendAsync(request.Offset, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                return Serialize(received, HostProtocolJsonContext.Default.Int64);
            }
            case HostProtocol.MethodFsUploadCommit:
            {
                var request = Require(message, HostProtocolJsonContext.Default.RemoteUploadCommit, "upload commit requires uploadId");
                await using var upload = uploads.Take(request.UploadId);
                return Serialize(await upload.CommitAsync(request.Size, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteFileStat);
            }
            case HostProtocol.MethodFsUploadAbort:
            {
                await using var upload = uploads.Take(Require(message, HostProtocolJsonContext.Default.RemoteUploadRef, "upload abort requires uploadId").UploadId);
                return True;
            }
            default:
                throw MethodNotFound(name);
        }
    }
}

/** Uploads in progress on one connection; whatever is left when the connection ends is discarded with its temporary file. */
internal sealed class HostUploads : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostUpload> _uploads = new(StringComparer.Ordinal);

    public void Add(HostUpload upload) => _uploads[upload.Id] = upload;

    public HostUpload Get(string id)
        => _uploads.TryGetValue(id, out var upload) ? upload : throw new JsonRpcException(InvalidParams, $"unknown upload: {id}");

    public HostUpload Take(string id)
        => _uploads.TryRemove(id, out var upload) ? upload : throw new JsonRpcException(InvalidParams, $"unknown upload: {id}");

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _uploads.Keys)
        {
            if (_uploads.TryRemove(id, out var upload))
                await upload.DisposeAsync().ConfigureAwait(false);
        }
    }
}
