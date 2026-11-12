using HuaweiPods;
using Microsoft.Win32;

string keyPath = @"Software\HuaweiPods-StartupSmoke-" + Guid.NewGuid().ToString("N");
const string firstExecutable = @"C:\First Release\HuaweiPods.exe";
const string secondExecutable = @"C:\Second Release\HuaweiPods.exe";
var first = new StartupRegistration(keyPath, firstExecutable);
var second = new StartupRegistration(keyPath, secondExecutable);

try
{
    Equal(StartupRegistrationStatus.Disabled, first.Read(), "Default startup status");
    using (var absent = Registry.CurrentUser.OpenSubKey(keyPath))
        Equal<RegistryKey?>(null, absent, "Reading must not create the key");

    first.SetEnabled(true);
    using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
    {
        Equal($"\"{firstExecutable}\"", key.GetValue("HuaweiPods") as string, "Quote the executable path");
        key.SetValue("Unrelated", "preserved");
    }
    Equal(StartupRegistrationStatus.CurrentExecutable, first.Read(), "Registered current path");
    Equal(StartupRegistrationStatus.DifferentExecutable, second.Read(), "Recognize a moved release");
    second.SetEnabled(false);
    Equal(StartupRegistrationStatus.Disabled, first.Read(), "Disable an older registration");

    second.SetEnabled(true);
    Equal(StartupRegistrationStatus.CurrentExecutable, second.Read(), "Register the new path");
    second.SetEnabled(false);
    second.SetEnabled(false);
    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
        Equal("preserved", key.GetValue("Unrelated") as string, "Preserve unrelated registry values");

    var tooLong = new StartupRegistration(keyPath, @"C:\" + new string('x', 260) + @"\HuaweiPods.exe");
    ExpectRejected(() => tooLong.SetEnabled(true));
    var dotnet = new StartupRegistration(keyPath, @"C:\Program Files\dotnet\dotnet.exe");
    ExpectRejected(() => dotnet.SetEnabled(true));
    Equal(StartupRegistrationStatus.Disabled, first.Read(), "Failed registration must leave the setting disabled");
    Console.WriteLine("HuaweiPods startup registration checks passed.");
}
finally
{
    Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
}

static void ExpectRejected(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("Invalid registration unexpectedly succeeded.");
}
