using System;
using System.IO;
using System.Text.Json;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary><see cref="ISettingsService"/> 的 JSON 实现（settings.json）。</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public SettingsService(AppPaths paths)
    {
        _path = paths.SettingsPath;
        Current = Load();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_path, json);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // settings.json 损坏时回退默认，不阻断启动。
        }

        return new AppSettings();
    }
}
