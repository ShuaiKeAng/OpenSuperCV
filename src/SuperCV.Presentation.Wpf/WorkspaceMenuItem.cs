using System.ComponentModel;

namespace SuperCV;

public sealed class WorkspaceMenuItem : INotifyPropertyChanged
{
    public WorkspaceMenuItem(Guid id, string name, bool canDelete)
    {
        Id = id;
        Name = name ?? string.Empty;
        CanDelete = canDelete;
    }

    private WorkspaceMenuItem()
    {
        Id = Guid.Empty;
        Name = string.Empty;
        IsAddAction = true;
    }

    public Guid Id { get; }

    public string Name { get; }

    // Workspace names are persisted user data.  They never follow the display language.
    public string DisplayName => Name;

    public string AddActionText => LocalizationService.Current.IsEnglish
        ? LocalizationService.Current.T("新建")
        : "新建工作区";

    public bool CanDelete { get; }

    public bool IsAddAction { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static WorkspaceMenuItem CreateAddAction() => new();

    internal void RefreshLocalizedText()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddActionText)));
    }
}
