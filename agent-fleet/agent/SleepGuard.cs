using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>Keeps the computer from going to sleep while something important runs.</summary>
internal interface ISleepGuard
{
    /// <summary>Holds the computer awake until the returned handle is disposed.</summary>
    IDisposable Hold(string reason);
}

internal sealed class NoSleepGuard : ISleepGuard
{
    public static NoSleepGuard Instance { get; } = new();

    public IDisposable Hold(string reason) => new Released();

    private sealed class Released : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Stops the hub from sleeping while a plan runs, so a plan left going overnight is not frozen by the power settings
/// (a laptop on battery can sleep after a few minutes). Windows: SetThreadExecutionState from a thread of its own,
/// which works from a service too. macOS: caffeinate. Linux: systemd-inhibit. It does not stop someone closing a
/// laptop's lid or choosing Sleep, and it does nothing for the other machines: their own settings apply (the worker
/// setup's -KeepAwake).
/// </summary>
internal sealed class SystemSleepGuard(ILogger logger) : ISleepGuard
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly object _gate = new();
    private int _holders;
    private Thread? _windowsThread;
    private ManualResetEventSlim? _release;
    private Process? _inhibitor;

#pragma warning disable SYSLIB1054 // LibraryImport would need unsafe code enabled for the whole project.
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
#pragma warning restore SYSLIB1054

    public IDisposable Hold(string reason)
    {
        lock (_gate)
        {
            if (_holders++ == 0)
            {
                Start(reason);
            }
        }

        return new Handle(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holders == 0)
            {
                Stop();
            }
        }
    }

    private void Start(string reason)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The request belongs to the thread that made it, so it gets a thread that lives until released.
                var release = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    uint previous = SetThreadExecutionState(EsContinuous | EsSystemRequired);
                    if (previous == 0)
                    {
                        logger.LogWarning("Windows refused the request to stay awake while a plan runs.");
                    }

                    release.Wait();
                    SetThreadExecutionState(EsContinuous);
                })
                {
                    IsBackground = true,
                    Name = "Agent Fleet stay awake"
                };
                thread.Start();
                _windowsThread = thread;
                _release = release;
            }
            else
            {
                string program = OperatingSystem.IsMacOS() ? "caffeinate" : "systemd-inhibit";
                string[] arguments = OperatingSystem.IsMacOS()
                    ? ["-i", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                    : ["--what=idle:sleep", "--who=Agent Fleet", $"--why={reason}", "--mode=block", "sleep", "infinity"];
                if (QuickProcess.FindOnPath(program) is null)
                {
                    logger.LogInformation("{Program} is not available, so the computer's own sleep settings apply while the plan runs.", program);
                    return;
                }

                var startInfo = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (string argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                _inhibitor = Process.Start(startInfo);
            }

            logger.LogInformation("Keeping this computer awake: {Reason}.", reason);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            logger.LogWarning(exception, "Could not keep the computer awake while the plan runs.");
        }
    }

    private void Stop()
    {
        _release?.Set();
        _windowsThread?.Join(TimeSpan.FromSeconds(5));
        _release?.Dispose();
        _release = null;
        _windowsThread = null;

        if (_inhibitor is { } inhibitor)
        {
            try
            {
                if (!inhibitor.HasExited)
                {
                    inhibitor.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            inhibitor.Dispose();
            _inhibitor = null;
        }

        logger.LogInformation("No plan is running: the computer's own sleep settings apply again.");
    }

    private sealed class Handle(SystemSleepGuard owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
