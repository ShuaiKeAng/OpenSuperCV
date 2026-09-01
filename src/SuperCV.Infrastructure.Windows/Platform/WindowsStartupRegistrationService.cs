using Microsoft.Win32;
using SuperCV.Application.Ports;

namespace SuperCV.Infrastructure.Windows.Platform;

public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string DefaultValueName = "SuperCV.V2";

    private readonly ICurrentUserRunRegistry _registry;
    private readonly string _valueName;
    private readonly string _startupCommand;

    public WindowsStartupRegistrationService()
        : this(ResolveCurrentExecutablePath())
    {
    }

    public WindowsStartupRegistrationService(string executablePath)
        : this(
            new CurrentUserRunRegistry(),
            DefaultValueName,
            executablePath)
    {
    }

    internal WindowsStartupRegistrationService(
        ICurrentUserRunRegistry registry,
        string valueName,
        string executablePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (valueName.IndexOf('\0', StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("The registry value name cannot contain a null character.", nameof(valueName));
        }

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The executable path must be fully qualified.", nameof(executablePath));
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (fullPath.IndexOf('"', StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("The executable path cannot contain a quotation mark.", nameof(executablePath));
        }

        _registry = registry;
        _valueName = valueName;
        _startupCommand = $"\"{fullPath}\"";
    }

    public bool IsEnabled()
    {
        string? registeredCommand = _registry.Read(RunKeyPath, _valueName);
        return string.Equals(registeredCommand, _startupCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _registry.Write(RunKeyPath, _valueName, _startupCommand);
            return;
        }

        _registry.Delete(RunKeyPath, _valueName);
    }

    private static string ResolveCurrentExecutablePath() =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("The current executable path is unavailable.");
}

internal interface ICurrentUserRunRegistry
{
    string? Read(string subKeyPath, string valueName);

    void Write(string subKeyPath, string valueName, string value);

    void Delete(string subKeyPath, string valueName);
}

internal sealed class CurrentUserRunRegistry : ICurrentUserRunRegistry
{
    public string? Read(string subKeyPath, string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key?.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string subKeyPath, string valueName, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open HKCU\\{subKeyPath} for writing.");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void Delete(string subKeyPath, string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
