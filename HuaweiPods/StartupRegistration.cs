using System.IO;
using Microsoft.Win32;

namespace HuaweiPods;

internal enum StartupRegistrationStatus
{
    Disabled,
    CurrentExecutable,
    DifferentExecutable
}

internal sealed class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HuaweiPods";
    private readonly string _keyPath;
    private readonly string? _processPath;

    public StartupRegistration() : this(RunKeyPath, Environment.ProcessPath)
    {
    }

    internal StartupRegistration(string keyPath, string? processPath)
    {
        _keyPath = keyPath;
        _processPath = processPath;
    }

    public StartupRegistrationStatus Read()
    {
        string executable = GetExecutablePath();
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return Read(key, executable);
    }

    public void SetEnabled(bool enabled)
    {
        string executable = GetExecutablePath();
        if (enabled)
        {
            string command = Quote(executable);
            if (command.Length > 260)
                throw new InvalidOperationException("应用路径太长，无法注册到 Windows 启动项。");
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath)
                ?? throw new IOException("无法打开当前用户的 Windows 启动项。");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private string GetExecutablePath()
    {
        if (_processPath is not { } path
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请直接运行 HuaweiPods 的 EXE 以配置开机自启。");
        return path;
    }

    private static StartupRegistrationStatus Read(RegistryKey? key, string executable)
    {
        string? command = (key?.GetValue(ValueName) as string)?.Trim();
        if (string.IsNullOrEmpty(command))
            return StartupRegistrationStatus.Disabled;
        return string.Equals(command, Quote(executable), StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, executable, StringComparison.OrdinalIgnoreCase)
            ? StartupRegistrationStatus.CurrentExecutable : StartupRegistrationStatus.DifferentExecutable;
    }

    private static string Quote(string executable) => $"\"{executable}\"";
}
