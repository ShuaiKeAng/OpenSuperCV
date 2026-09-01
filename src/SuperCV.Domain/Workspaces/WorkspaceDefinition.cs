namespace SuperCV.Domain.Workspaces;

public sealed record WorkspaceDefinition
{
    public static readonly Guid DefaultWorkspaceId =
        new("bb7f7a5d-cc9e-4b6a-8bb7-e2f1ff8a6d75");

    public WorkspaceDefinition(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id;
        Name = name;
    }

    public Guid Id { get; init; }

    public string Name { get; init; }

    public static WorkspaceDefinition Create(string name) => new(Guid.NewGuid(), name);
}
