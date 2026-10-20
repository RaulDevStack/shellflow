using ShellFlow.Core.Settings;

namespace ShellFlow.Core.Persistence;

public sealed class SettingsStore(AppPaths paths)
{
    private bool _fromNewerVersion;

    public AppPaths Paths { get; } = paths;

    /// <summary>Notes for the user about recovered or corrected settings from the last load.</summary>
    public IReadOnlyList<string> LoadNotes { get; private set; } = [];

    public AppSettings Load()
    {
        var result = JsonFileStore.Load(Paths.SettingsFile, ShellFlowJsonContext.Default.AppSettings, AppSettings.CurrentSchemaVersion, () => new AppSettings());
        var notes = new List<string>();
        switch (result.Status)
        {
            case LoadStatus.Recovered:
                notes.Add(result.BackupPath is null
                    ? $"settings.json could not be read ({result.Error}). Default settings are in use."
                    : $"settings.json could not be read ({result.Error}). It was saved as {Path.GetFileName(result.BackupPath)} and default settings are in use.");
                break;
            case LoadStatus.NewerVersion:
                _fromNewerVersion = true;
                notes.Add("settings.json was written by a newer version of ShellFlow. Changes made here will not be saved, to avoid losing newer options.");
                break;
        }

        notes.AddRange(SettingsValidator.Normalize(result.Value));
        LoadNotes = notes;
        return result.Value;
    }

    /// <summary>Saves settings unless they came from a newer schema. Returns false when the save was skipped.</summary>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_fromNewerVersion)
        {
            return false;
        }

        SettingsValidator.Normalize(settings);
        JsonFileStore.Save(Paths.SettingsFile, settings, ShellFlowJsonContext.Default.AppSettings);
        return true;
    }
}
