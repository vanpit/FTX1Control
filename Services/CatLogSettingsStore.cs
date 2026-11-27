using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace FTX1Control.Services;

internal sealed class CatLogSettingsStore
{
    private readonly string _path;

    public CatLogSettingsStore(string path) => _path = path;

    public static CatLogSettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FTX1Control",
        "cat-log-folder.txt"));

    public string? Load()
    {
        string folder;
        try
        {
            folder = File.ReadAllText(_path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (folder.Length == 0)
        {
            return null;
        }
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new InvalidDataException("Invalid saved CAT log folder.");
        }

        return folder;
    }

    public void Save(string? folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, folder ?? string.Empty);
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
                Trace.TraceError($"Unable to remove temporary CAT log settings: {exception}");
            }
        }
    }
}
