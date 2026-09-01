namespace SuperCV.Application.Ports;

public interface IFocusService
{
    void RecordCurrentForegroundWindow();

    bool TryRestorePreviousForegroundWindow();
}
