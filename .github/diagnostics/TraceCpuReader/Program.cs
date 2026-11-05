using System.Globalization;
using System.Runtime.InteropServices;

namespace TraceCpuReader;

// These exit codes are part of the private wrapper protocol.
internal enum ReaderStatus
{
    Success = 0,
    InvalidInput = 10,
    UnsupportedOrMissingData = 20,
    ParseFailure = 30,
    LostData = 31,
    AmbiguousIdentity = 32,
    NoTargetObservations = 33
}
internal sealed class ReaderFailure(ReaderStatus status, int? sourceHResult = null) : Exception
{
    internal ReaderStatus Status { get; } = status;
    internal int? SourceHResult { get; } = sourceHResult;
}

internal static class Program
{
    private const long MaximumBytes = 256L * 1024 * 1024;

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            if (!TryParseArguments(args, out uint processId, out uint[] threadIds))
                return Finish(ReaderStatus.InvalidInput);
            using FileStream input = OpenInput(args[0]);
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return Finish(ReaderStatus.UnsupportedOrMissingData);
            TraceAnalysis.Read(input.Name, processId, threadIds);
            return Finish(ReaderStatus.Success);
        }
        catch (ReaderFailure error)
        {
            if (error.SourceHResult.HasValue)
                Console.WriteLine($"NATIVE_TYPED_CPU hresult={error.SourceHResult.Value}");
            return Finish(error.Status);
        }
        catch (Exception error)
        {
            Console.WriteLine($"NATIVE_TYPED_CPU hresult={error.HResult}");
            return Finish(ClassifyFailure(error));
        }
    }

    private static ReaderStatus ClassifyFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                or FileNotFoundException or FileLoadException or NotSupportedException
                || current.HResult is unchecked((int)0x8007007E) or unchecked((int)0x8007007F)
                    or unchecked((int)0x800700C1))
                return ReaderStatus.UnsupportedOrMissingData;
        }
        return ReaderStatus.ParseFailure;
    }

    private static int Finish(ReaderStatus status)
    {
        Console.WriteLine($"NATIVE_TYPED_CPU status={(int)status}");
        return (int)status;
    }

    private static bool TryParseArguments(string[] args, out uint processId, out uint[] threadIds)
    {
        processId = 0;
        threadIds = [];
        if (args.Length != 3 || string.IsNullOrWhiteSpace(args[0])
            || !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            || processId == 0 || args[2].Length is < 1 or > 703)
            return false;
        Span<Range> parts = stackalloc Range[65];
        ReadOnlySpan<char> text = args[2];
        int count = text.Split(parts, ',');
        if (count is < 1 or > 64)
            return false;
        threadIds = new uint[count];
        for (int i = 0; i < count; i++)
        {
            if (!uint.TryParse(text[parts[i]], NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
                || id == 0 || Array.IndexOf(threadIds, id, 0, i) >= 0)
                return false;
            threadIds[i] = id;
        }
        return true;
    }

    private static FileStream OpenInput(string path)
    {
        FileStream? input = null;
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".etl", StringComparison.OrdinalIgnoreCase))
                throw new ReaderFailure(ReaderStatus.InvalidInput);
            // Hold a read-only handle without write sharing throughout SDK consumption.
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaximumBytes)
                throw new ReaderFailure(ReaderStatus.InvalidInput);
            return input;
        }
        catch
        {
            input?.Dispose();
            throw new ReaderFailure(ReaderStatus.InvalidInput);
        }
    }
}