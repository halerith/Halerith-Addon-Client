namespace HalerithAddonClient.Core;

public static class ModpackFilePolicies
{
    public const string Preserve = "preserve";
    public const string Review = "review";
    public const string Replace = "replace";
    public const string Ignore = "ignore";
    public static string Normalize(string? value) => value is Review or Replace or Ignore ? value : Preserve;
    public static readonly string[] Values = [Preserve, Review, Replace, Ignore];
    public static readonly string[] Labels = ["Update, preserving local changes", "Ask before applying", "Replace with pack files", "Do not apply pack files"];
}

public sealed class ModpackFileChoice : System.ComponentModel.INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Change { get; init; } = "";
    public string ProposedAction { get; init; } = "";
    private string _selectedAction = "";
    public string SelectedAction { get => _selectedAction; set { _selectedAction = value; PropertyChanged?.Invoke(this, new(nameof(SelectedAction))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string? FixedAction { get; set; }
    public IReadOnlyList<string> Actions => FixedAction is { } fixedAction ? [fixedAction] : ProposedAction == "No change" ? ["No change"] : [ProposedAction, "Keep"];
    public string? ObservedHash { get; init; }
    public ManifestModpackFile? File { get; init; }
    public bool RequiresReview { get; init; }
}

public sealed class ModpackFileReview
{
    public string Revision { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string Policy { get; init; } = "";
    public bool Forced { get; init; }
    public bool RevisionChanged { get; init; }
    public List<ModpackFileChoice> Files { get; init; } = [];
    public bool RequiresReview => Files.Any(f => f.RequiresReview);
    public bool HasWork => Policy != ModpackFilePolicies.Ignore && (RevisionChanged || Files.Any(f => f.SelectedAction is "Install" or "Replace" or "Remove" || f.RequiresReview));
    public bool HasLocalChoices => Files.Any(f => f.SelectedAction == "Keep" && f.ProposedAction != "No change");
}
