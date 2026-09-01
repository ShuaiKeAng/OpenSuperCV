namespace SuperCV.Domain.Workspaces;

public sealed record WorkspaceState
{
    public WorkspaceState(Guid activeWorkspaceId, WorkspaceDefinition[] workspaces)
    {
        ActiveWorkspaceId = activeWorkspaceId;
        Workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    }

    public Guid ActiveWorkspaceId { get; init; }

    public WorkspaceDefinition[] Workspaces { get; init; }
}
