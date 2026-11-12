using System.IO;
using System.IO.Compression;
using Microsoft.VisualBasic.FileIO;

namespace HandheldExplorer.Services;

public readonly record struct TransferProgress(double Fraction, string Name);

/// <summary>Copy, move, delete and zip helpers. Everything runs on background threads.</summary>
public static class FileOps
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    /// <summary>Returns a path in <paramref name="dir"/> that doesn't exist yet: "name", "name (2)", ...</summary>
    public static string UniquePath(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    public static long SizeOf(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;

            long total = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                total += file.Length;
            }
            return total;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }

    /// <summary>Copies or moves items into <paramref name="destDir"/>. Name clashes get a numbered name; nothing is overwritten.</summary>
    public static Task TransferAsync(IReadOnlyList<string> sources, string destDir, bool move,
        IProgress<TransferProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        string destFull = Path.GetFullPath(destDir).TrimEnd(Sep) + Sep;

        foreach (var source in sources)
        {
            if (Directory.Exists(source) &&
                destFull.StartsWith(Path.GetFullPath(source).TrimEnd(Sep) + Sep, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A folder can't be copied or moved into itself.");
        }

        long total = 0;
        foreach (var source in sources) total += SizeOf(source, ct);

        long done = 0, lastReport = 0;
        void Report(string name, long delta)
        {
            done += delta;
            long now = Environment.TickCount64;
            if (now - lastReport < 60) return;
            lastReport = now;
            progress?.Report(new TransferProgress(total > 0 ? Math.Min(1.0, (double)done / total) : 0, name));
        }

        foreach (var raw in sources)
        {
            ct.ThrowIfCancellationRequested();
            string source = raw.TrimEnd(Sep);
            string name = Path.GetFileName(source);
            string? parent = Path.GetDirectoryName(source);
            bool sameDir = parent != null &&
                           string.Equals(parent.TrimEnd(Sep) + Sep, destFull, StringComparison.OrdinalIgnoreCase);
            if (move && sameDir) continue;

            bool isDir = Directory.Exists(source);
            if (!isDir && !File.Exists(source)) continue;

            string target = UniquePath(destDir, name);

            bool sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destFull), StringComparison.OrdinalIgnoreCase);
            if (move && sameVolume)
            {
                if (isDir) Directory.Move(source, target); else File.Move(source, target);
                Report(name, 0);
                continue;
            }

            if (isDir) CopyDirectory(source, target, Report, ct);
            else CopyFile(source, target, Report, ct);

            if (move)
            {
                if (isDir) Directory.Delete(source, true); else File.Delete(source);
            }
        }
    }, ct);

    private static void CopyDirectory(string source, string target, Action<string, long> report, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            CopyFile(file, Path.Combine(target, Path.GetFileName(file)), report, ct);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), report, ct);
        }
    }

    private static void CopyFile(string source, string target, Action<string, long> report, CancellationToken ct)
    {
        string name = Path.GetFileName(source);
        var buffer = new byte[1024 * 1024];
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    report(name, read);
                }
            }
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(target); } catch { /* leave the partial file if it can't be removed */ }
            throw;
        }
    }

    /// <summary>Sends items to the Recycle Bin (drives without one delete permanently).</summary>
    public static Task DeleteAsync(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    });

    public static Task ZipAsync(IReadOnlyList<string> sources, string zipPath, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var raw in sources)
            {
                string source = raw.TrimEnd(Sep);
                if (Directory.Exists(source))
                {
                    string parent = Path.GetDirectoryName(source) ?? source;
                    foreach (var file in Directory.EnumerateFiles(source, "*", System.IO.SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(file, Path.GetRelativePath(parent, file).Replace('\\', '/'));
                    }
                }
                else if (File.Exists(source))
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(source, Path.GetFileName(source));
                }
            }
        }
        catch
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
            throw;
        }
    }, ct);

    public static Task UnzipAsync(string zipPath, string destDir) =>
        Task.Run(() => ZipFile.ExtractToDirectory(zipPath, destDir));
}
