using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;

namespace FTX1Control.Services;

internal sealed class HfPowerWarningSettingsStore
{
    private readonly string _path;

    public HfPowerWarningSettingsStore(string path) => _path = path;

    public static HfPowerWarningSettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FTX1Control",
        "hf-power-warning.txt"));

    public int Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (FileNotFoundException)
        {
            return 25;
        }
        catch (DirectoryNotFoundException)
        {
            return 25;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var watts)
            || watts is < 5 or > 100)
        {
            throw new InvalidDataException("Invalid saved HF power warning limit.");
        }

        return watts;
    }

    public void Save(int watts)
    {
        if (watts is < 5 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, watts.ToString(CultureInfo.InvariantCulture));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                Trace.TraceError($"Unable to remove temporary HF power warning settings: {exception}");
            }
        }
    }
}
