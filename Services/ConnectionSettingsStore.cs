using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FTX1Control.Services;

internal sealed record ConnectionSettings(string PortName, int BaudRate);

internal sealed class ConnectionSettingsStore
{
    private readonly string _path;

    public ConnectionSettingsStore(string path) => _path = path;

    public static ConnectionSettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FTX1Control",
        "connection.json"));

    public ConnectionSettings? Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<ConnectionSettings>(json);
        if (settings is null || string.IsNullOrWhiteSpace(settings.PortName)
            || !Regex.IsMatch(settings.PortName, @"\ACOM[0-9]+\z", RegexOptions.IgnoreCase)
            || settings.BaudRate <= 0)
        {
            throw new InvalidDataException("Invalid saved connection settings.");
        }

        return settings;
    }

    public void Save(ConnectionSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings));
    }
}
