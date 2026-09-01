namespace SuperCV.Domain.Instructions;

public sealed record CustomInstruction
{
    public CustomInstruction(Guid id, string label, string prompt, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An instruction id cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(prompt);

        Id = id;
        Label = label.Trim();
        Prompt = prompt;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid Id { get; init; }

    public string Label { get; init; }

    public string Prompt { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
}
