using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen.Zhinu.Tests;

/// <summary>
/// REL-SQLITE-01 diagnostic-only first-failure capture.
/// Inert unless ZG_SQLITE_STRESS=1. It never changes workflow, persistence or
/// assertion semantics: it only copies files after the workers have exited and
/// records observations (hashes, headers, timestamps, loaded native module).
/// No SQLite connection is opened against the captured originals.
/// </summary>
internal static class RelSqlite01Diagnostics
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("ZG_SQLITE_STRESS") == "1";

    public static void Capture(
        string label,
        string databaseDirectory,
        Action<string>? log = null,
        Exception? failure = null,
        IReadOnlyDictionary<string, object?>? extras = null)
    {
        if (!Enabled)
            return;
        var notes = new List<string>();
        void Note(string message)
        {
            notes.Add(message);
            log?.Invoke(message);
        }

        try
        {
            var configured = Environment.GetEnvironmentVariable("ZG_DIAG_EVIDENCE");
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "..", "rel-sqlite-01-evidence"))
                : Path.GetFullPath(configured);
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, Sanitize(label) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);

            CopyAndDescribe(databaseDirectory, target, "run.db", Note);
            CopyAndDescribe(databaseDirectory, target, "workflow.db", Note);

            var files = new List<object>();
            foreach (var name in new[]
                     {
                         "run.db", "run.db-wal", "run.db-shm",
                         "workflow.db", "workflow.db-wal", "workflow.db-shm"
                     })
            {
                var source = Path.Combine(databaseDirectory, name);
                var copy = Path.Combine(target, name);
                files.Add(Describe(source, copy));
            }

            var listing = new List<object>();
            if (Directory.Exists(databaseDirectory))
            {
                foreach (var path in Directory.GetFiles(databaseDirectory).OrderBy(value => value, StringComparer.Ordinal))
                {
                    var info = new FileInfo(path);
                    listing.Add(new
                    {
                        name = info.Name,
                        size = info.Length,
                        lastWriteTimeUtc = info.LastWriteTimeUtc.ToString("o")
                    });
                }
            }

            string? nativePath = null;
            string? nativeSha256 = null;
            try
            {
                var module = Process.GetCurrentProcess().Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(candidate => candidate.ModuleName.Equals(
                        OperatingSystem.IsWindows() ? "e_sqlite3.dll" : "libe_sqlite3.so",
                        StringComparison.OrdinalIgnoreCase));
                if (module?.FileName is { } modulePath)
                {
                    nativePath = modulePath;
                    using var stream = File.OpenRead(modulePath);
                    nativeSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }
            }
            catch (Exception moduleError)
            {
                Note($"module-capture-failed {moduleError.GetType().Name}: {moduleError.Message}");
            }

            var manifest = new
            {
                label,
                capturedUtc = DateTimeOffset.UtcNow.ToString("o"),
                processId = Environment.ProcessId,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processorCount = Environment.ProcessorCount,
                databaseDirectory,
                files,
                listing,
                nativeModule = new { path = nativePath, sha256 = nativeSha256 },
                extras
            };
            File.WriteAllText(
                Path.Combine(target, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            if (failure is not null)
            {
                var text = new StringBuilder();
                for (Exception? current = failure; current is not null; current = current.InnerException)
                {
                    text.AppendLine(current.GetType().FullName);
                    text.AppendLine(current.Message);
                    text.AppendLine(current.StackTrace);
                    if (current is Microsoft.Data.Sqlite.SqliteException sqlite)
                        text.AppendLine($"sqlite primary={sqlite.SqliteErrorCode} extended={sqlite.SqliteExtendedErrorCode}");
                    text.AppendLine("----");
                }
                File.WriteAllText(Path.Combine(target, "exception.txt"), text.ToString());
            }

            Note($"evidence-captured stage={label} directory={target}");
            File.WriteAllLines(Path.Combine(target, "capture.log"), notes);
        }
        catch (Exception captureError)
        {
            Note($"evidence-capture-failed stage={label} {captureError.GetType().Name}: {captureError.Message}");
        }
    }

    private static void CopyAndDescribe(string directory, string target, string name, Action<string> note)
    {
        var source = Path.Combine(directory, name);
        if (!File.Exists(source))
            return;
        var copy = Path.Combine(target, name);
        CopyRaw(source, copy);
        var bytes = File.ReadAllBytes(copy);
        note(name switch
        {
            "run.db" or "workflow.db" => $"db-header {name} {DescribeDatabase(bytes)}",
            _ => $"{name}-header {DescribeSidecar(bytes)}"
        });
    }

    private static void CopyRaw(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static object Describe(string source, string copy)
    {
        if (!File.Exists(source))
            return new { name = Path.GetFileName(source), exists = false };
        var info = new FileInfo(source);
        var copiedSize = File.Exists(copy) ? new FileInfo(copy).Length : 0;
        var bytes = File.Exists(copy) ? File.ReadAllBytes(copy) : Array.Empty<byte>();
        return new
        {
            name = info.Name,
            exists = true,
            size = info.Length,
            copiedSize,
            sha256 = bytes.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            creationTimeUtc = info.CreationTimeUtc.ToString("o"),
            lastWriteTimeUtc = info.LastWriteTimeUtc.ToString("o"),
            attributes = info.Attributes.ToString(),
            firstBytesHex = bytes.Length == 0 ? null : Convert.ToHexString(bytes.AsSpan(0, Math.Min(128, bytes.Length)))
        };
    }

    private static string DescribeDatabase(byte[] bytes)
    {
        if (bytes.Length < 100)
            return $"short bytes={bytes.Length}";
        var pageSize = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16, 2));
        var writeVersion = bytes[18];
        var readVersion = bytes[19];
        var changeCounter = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(24, 4));
        var pageCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28, 4));
        var validFor = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(92, 4));
        var magic = Encoding.ASCII.GetString(bytes, 0, 16);
        return $"magic='{magic.TrimEnd('\0')}' pageSizeField={pageSize} writeVersion={writeVersion} readVersion={readVersion} changeCounter={changeCounter} pageCount={pageCount} validFor={validFor} bytes={bytes.Length}";
    }

    private static string DescribeSidecar(byte[] bytes)
    {
        if (bytes.Length < 32)
            return $"short bytes={bytes.Length}";
        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        if (magic is 0x377f0682 or 0x377f0683)
        {
            var version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
            var pageSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
            var checkpointSequence = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4));
            var salt1 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var salt2 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            return $"wal magic=0x{magic:x8} version={version} pageSize={pageSize} checkpointSequence={checkpointSequence} salt1=0x{salt1:x8} salt2=0x{salt2:x8} bytes={bytes.Length}";
        }
        var iVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var iChange = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var isInit = bytes[12];
        var bigEndCksum = bytes[13];
        var pageSizeShm = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2));
        var mxFrame = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        var nPage = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4));
        return $"shm iVersion={iVersion} iChange={iChange} isInit={isInit} bigEndCksum={bigEndCksum} pageSize={pageSizeShm} mxFrame={mxFrame} nPage={nPage} bytes={bytes.Length}";
    }

    private static string Sanitize(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
}
