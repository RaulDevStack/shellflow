using ShellFlow.Core.Profiles;
using ShellFlow.Core.Settings;

namespace ShellFlow.App.Services;

/// <summary>Detected shells merged with user-defined profiles. User profiles with a detected id override it.</summary>
public sealed class ProfileCatalog
{
    private readonly IShellEnvironment _environment;
    private List<ShellProfile> _detected;
    private List<ShellProfile> _all = [];
    private AppSettings _settings;

    public ProfileCatalog(IShellEnvironment environment, AppSettings settings)
    {
        _environment = environment;
        _settings = settings;
        _detected = [.. ProfileDetector.Detect(environment, includeWsl: false)];
        Rebuild();
        WslDetection = Task.Run(DetectWsl);
    }

    public event EventHandler? Changed;

    /// <summary>Completes once WSL distributions have been enumerated (they are slow to list).</summary>
    public Task WslDetection { get; }

    public IReadOnlyList<ShellProfile> All => _all;

    public IEnumerable<ShellProfile> Visible => _all.Where(p => !p.Hidden);

    public IShellEnvironment Environment => _environment;

    public ShellProfile? Find(string? id) =>
        id is null ? null : _all.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public ShellProfile Default
    {
        get
        {
            var preferred = Find(_settings.DefaultProfileId);
            if (preferred is not null)
            {
                return preferred;
            }

            foreach (var id in new[] { ProfileDetector.PowerShellCoreId, ProfileDetector.WindowsPowerShellId, ProfileDetector.CmdId })
            {
                if (Find(id) is { } profile)
                {
                    return profile;
                }
            }

            return _all.FirstOrDefault() ?? new ShellProfile
            {
                Id = ProfileDetector.CmdId,
                Name = "Command Prompt",
                Executable = "cmd.exe",
                Icon = ProfileIcons.Cmd,
                Source = ProfileSource.Detected,
            };
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        var merged = new List<ShellProfile>();
        var userIds = new HashSet<string>(_settings.Profiles.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        merged.AddRange(_detected.Where(p => !userIds.Contains(p.Id)));
        merged.AddRange(_settings.Profiles);
        _all = merged;
    }

    private void DetectWsl()
    {
        try
        {
            var withWsl = ProfileDetector.Detect(_environment, includeWsl: true);
            if (withWsl.Count != _detected.Count)
            {
                _detected = [.. withWsl];
                Rebuild();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"WSL detection failed: {ex.Message}");
        }
    }
}
