using System.Text.Json;
using System.Text.Json.Serialization;
using Velox.Core.Models;

namespace Velox.Core.Services;

/// <summary>Persistência da lista de downloads (incluindo progresso por segmento).</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateStore(string path) => _path = path;

    public List<DownloadItem> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<DownloadItem>();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<DownloadItem>>(json, Options) ?? new List<DownloadItem>();
        }
        catch
        {
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
            return new List<DownloadItem>();
        }
    }

    public async Task SaveAsync(IEnumerable<DownloadItem> items)
    {
        var snapshot = items.Select(i => i.CloneForPersistence()).ToList();
        var json = JsonSerializer.Serialize(snapshot, Options);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // não derruba o app por falha de gravação de estado
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Save(IEnumerable<DownloadItem> items)
    {
        var snapshot = items.Select(i => i.CloneForPersistence()).ToList();
        var json = JsonSerializer.Serialize(snapshot, Options);

        _gate.Wait();
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>Mensagem do último erro de leitura (arquivo corrompido), se houver.</summary>
    public string? LoadError { get; private set; }

    public AppSettings Load()
    {
        LoadError = null;
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
                if (s != null)
                {
                    if (string.IsNullOrWhiteSpace(s.DownloadDirectory))
                        s.DownloadDirectory = AppSettings.GetDefaultDownloadsFolder();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch { }
    }
}
