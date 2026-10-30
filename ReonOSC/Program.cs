namespace ReonOSC;

internal static class Program
{
    internal const string InstanceMutexName = @"Local\ReonOSC.Instance";
    internal const string QuitEventName = @"Local\ReonOSC.Quit";
    internal const string ShowEventName = @"Local\ReonOSC.Show";

    [STAThread]
    private static int Main(string[] args)
    {
        // `ReonOSC.exe --quit` asks a running instance to shut down cleanly
        // (socks turned off, BLE links closed) and waits until it has.
        // A hard kill skips all of that.
        if (args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase)))
            return QuitRunningInstance() ? 0 : 1;

        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Two instances would fight over the same socks. Bring the
            // running one to the front instead.
            try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); } catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        return 0;
    }

    /// <returns>true if no instance is running (anymore).</returns>
    private static bool QuitRunningInstance()
    {
        try
        {
            using var quit = EventWaitHandle.OpenExisting(QuitEventName);
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return true; }

        // The running instance holds the mutex until Main returns, which is
        // after the shutdown in TrayContext.ExitThreadCore has finished.
        try
        {
            using var m = Mutex.OpenExisting(InstanceMutexName);
            if (!m.WaitOne(TimeSpan.FromSeconds(20))) return false;
            m.ReleaseMutex();
        }
        catch (AbandonedMutexException) { }
        catch (WaitHandleCannotBeOpenedException) { }
        return true;
    }
}
