using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HalerithAddonClient.Core;

public sealed class MediumConfidenceUpdateChoice : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SyncAction Action { get; init; } = new();
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Name => Action.ManifestMod?.Name
        ?? Action.PackManifestItem?.Name
        ?? Action.LocalFile?.Stem
        ?? Action.LocalFile?.FileName
        ?? "";

    public string ContentType => ContentTypes.ToDisplay(Action.ContentType);
    public string LocalFile => Action.LocalFile?.FileName ?? "";
    public string TargetFile => Action.ManifestMod?.DownloadFilename ?? Action.PackManifestItem?.DownloadFilename ?? "";
    public string TargetVersion => Action.TargetVersion;
    public string Reason => Action.Reason ?? "";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class SyncPlanConfirmation
{
    public static List<SyncAction> GetMediumConfidenceUpdates(SyncPlan plan) =>
        plan.Actions
            .Where(a => a.Kind == SyncActionKind.Replace &&
                        a.MatchConfidence == SyncMatchConfidence.Medium)
            .ToList();

    public static SyncPlan WithoutUnselectedMediumConfidenceUpdates(
        SyncPlan plan,
        IReadOnlyCollection<SyncAction> selectedMediumUpdates)
    {
        var selected = selectedMediumUpdates.ToHashSet(ReferenceEqualityComparer.Instance);
        return new SyncPlan
        {
            Actions = plan.Actions
                .Where(a => a.MatchConfidence != SyncMatchConfidence.Medium ||
                            a.Kind != SyncActionKind.Replace ||
                            selected.Contains(a))
                .ToList()
        };
    }
}
