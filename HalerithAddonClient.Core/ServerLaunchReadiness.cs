namespace HalerithAddonClient.Core;

/// <summary>Content conditions that require the full Client before a managed server can launch.</summary>
public static class ServerLaunchReadiness
{
    public static bool HasRequiredManualFiles(SyncPlan plan) =>
        plan.Actions.Any(action => action.Kind == SyncActionKind.Missing && ServerPackManualImport.IsEligible(action));

    public static bool RequiresModpackReview(SyncPlan plan) =>
        plan.Actions.Any(action => action.Kind == SyncActionKind.Download && action.Modpack is not null
            && action.RequiresModpackReview);

    public static bool HasPendingActions(SyncPlan plan) =>
        plan.Actions.Any(action => action.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);

    public static bool CanLaunchAfterSync(SyncPlan plan, SyncExecutionResult? result) =>
        (result is null || result.Failed == 0) && !HasRequiredManualFiles(plan)
        && !RequiresModpackReview(plan) && !HasPendingActions(plan);
}
