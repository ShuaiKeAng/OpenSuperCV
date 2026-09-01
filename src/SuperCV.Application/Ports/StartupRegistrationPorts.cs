namespace SuperCV.Application.Ports;

/// <summary>
/// Manages registration of the current application for the current user's sign-in.
/// </summary>
public interface IStartupRegistrationService
{
    /// <summary>
    /// Returns whether the current executable is registered with the expected command.
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// Enables or disables registration for the current executable.
    /// </summary>
    void SetEnabled(bool enabled);
}
