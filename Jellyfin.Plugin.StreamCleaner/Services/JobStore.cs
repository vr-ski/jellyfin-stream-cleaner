using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Models;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>Atomic JSON persistence for a single job. Single-writer safe via lock.</summary>
public sealed class JobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JobStore(string dataFolderPath)
    {
        Directory.CreateDirectory(dataFolderPath);
        _path = Path.Combine(dataFolderPath, "job.json");
    }

    public async Task<JobState?> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JobState>(json, JsonOptions);
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(JobState state, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _lock.Release(); }
    }
}
