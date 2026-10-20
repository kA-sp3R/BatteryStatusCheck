using System.Text.Json;

namespace DeviceBatteryStatus.Core.Settings;

public sealed record SettingsLoadResult(AppSettings Settings, string? Error, string? CorruptBackupPath = null);

/// <summary>Stores only application preferences; device measurements and credentials are not persisted.</summary>
public sealed class FileSettingsStore(string filePath)
{
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
                var backupPath = $"{path}.corrupt-{Guid.NewGuid():N}";
                try
                {
                    File.Copy(path, backupPath, overwrite: false);
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
                    JsonSerializer.Serialize(stream, normalized, JsonOptions);
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
}
