using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SuperCV;

public sealed class CvActionButtonsVisibilityState : INotifyPropertyChanged
{
    private bool _areVisible = true;

    private CvActionButtonsVisibilityState()
    {
    }

    public static CvActionButtonsVisibilityState Current { get; } = new();

    public bool AreVisible
    {
        get => _areVisible;
        set
        {
            if (_areVisible == value)
            {
                return;
            }

            _areVisible = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
