using System.IO;
using System.Text.Json;
using HuaweiPods.Protocol;

namespace HuaweiPods;

internal sealed record RememberedHeadset(string Model, DeviceIdentity? Identity = null);

internal sealed record DevicePreference(string? Model = null, bool? LowLatency = null,
    string? LowLatencyModel = null, bool RestoreLowLatency = false,
    string? ArtworkModelId = null, string? ArtworkSubModelId = null,
    RememberedHeadset? LastConnection = null);

internal sealed class DevicePreferences
{
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuaweiPods", "devices.json");
    private readonly Dictionary<string, DevicePreference> _devices = new(StringComparer.OrdinalIgnoreCase);

    public DevicePreferences()
    {
        try
        {
            if (File.Exists(_file)
                && JsonSerializer.Deserialize<Dictionary<string, DevicePreference>>(File.ReadAllText(_file)) is { } saved)
            {
                foreach (var (key, value) in saved)
                    _devices[key] = value;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    public DevicePreference Get(string key) => _devices.GetValueOrDefault(key) ?? new();

    public void Set(string key, DevicePreference value)
    {
        _devices[key] = value;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

        string temporaryFile = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(_devices));
            File.Move(temporaryFile, _file, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }
}
