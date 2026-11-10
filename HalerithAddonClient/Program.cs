using Avalonia;
#if WINDOWS
using System.Security.Principal;
#endif

namespace HalerithAddonClient;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance is null)
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private sealed class SingleInstanceGuard : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        private SingleInstanceGuard(Mutex mutex)
        {
            _mutex = mutex;
            _ownsMutex = true;
        }

        public static SingleInstanceGuard? TryAcquire()
        {
            var prefix = OperatingSystem.IsWindows() ? @"Local\" : "";
            var mutexName = $"{prefix}HalerithAddonClient.{GetCurrentUserKey()}";
            var mutex = new Mutex(false, mutexName);

            try
            {
                if (!mutex.WaitOne(0, false))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous process exited without releasing; this process now owns it.
            }

            return new SingleInstanceGuard(mutex);
        }

        public void Dispose()
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }

            _mutex.Dispose();
        }

        private static string GetCurrentUserKey()
        {
#if WINDOWS
            try
            {
                var sid = WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrWhiteSpace(sid))
                    return sid;
            }
            catch
            {
                // Fall back to account text below.
            }
#endif

            return $"{Environment.UserDomainName}_{Environment.UserName}"
                .Replace('\\', '_')
                .Replace('/', '_');
        }
    }
}
