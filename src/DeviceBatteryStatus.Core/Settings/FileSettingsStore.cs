using System.Text.Json;

namespace DeviceBatteryStatus.Core.Settings;

public sealed record SettingsLoadResult(AppSettings Settings, string? Error, string? CorruptBackupPath = null);

/// <summary>Stores only application preferences; device measurements and credentials are not persisted.</summary>
public sealed class FileSettingsStore(string filePath)
{
    private const long MaximumSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string path = Path.GetFullPath(filePath);
    private readonly object gate = new();

    public SettingsLoadResult Load()
    {
        lock (gate)
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > MaximumSettingsBytes)
                    return new(new AppSettings(), "Settings exceed the 1 MiB storage limit; defaults are in use. The oversized original file was retained and was not copied.");
                var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions)
                    ?? throw new JsonException("Settings must contain an object.");
                return new(settings.Normalize(), null);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(new AppSettings(), null);
            }
            catch (JsonException exception)
            {
                // Keep one exact corrupt copy rather than another full copy on every failed startup.
                var backupPath = path + ".corrupt";
                try
                {
                    BackupCorruptSettings(backupPath);
                    return new(new AppSettings(), $"Settings could not be read; defaults are in use. {exception.Message}", backupPath);
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                {
                    return new(new AppSettings(),
                        $"Settings could not be read; defaults are in use. The original file was kept, but a backup could not be created: {backupError.Message}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(new AppSettings(), $"Settings could not be loaded; defaults are in use. {exception.Message}");
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        lock (gate)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var bounded = new SizeLimitedWriteStream(stream);
                    JsonSerializer.Serialize(bounded, normalized, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally
            {
                // Delete only this operation's temporary file; an existing settings file is never removed first.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private void BackupCorruptSettings(string backupPath)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length > MaximumSettingsBytes) throw new IOException("The settings file exceeds the 1 MiB backup limit.");
                using var bounded = new SizeLimitedWriteStream(output);
                input.CopyTo(bounded);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, backupPath, overwrite: true);
            // Migrate only the exact GUID-suffixed files created by older versions.
            // User-created notes and similarly named files are left alone.
            var prefix = Path.GetFileName(path) + ".corrupt-";
            try
            {
                foreach (var previous in Directory.EnumerateFiles(directory, prefix + "*"))
                {
                    var name = Path.GetFileName(previous);
                    if (name.Length != prefix.Length + 32 || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) continue;
                    try { File.Delete(previous); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Enforces the disk bound while serializing; failed saves never replace either valid settings file.</summary>
    private sealed class SizeLimitedWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireSize(count);
            inner.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireSize(buffer.Length);
            inner.Write(buffer);
        }
        private void RequireSize(int count)
        {
            if (count > MaximumSettingsBytes - inner.Position)
                throw new IOException("Settings exceed the 1 MiB storage limit; existing settings were retained.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
