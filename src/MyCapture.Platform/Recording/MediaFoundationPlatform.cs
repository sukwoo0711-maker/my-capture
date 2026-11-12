using System.Runtime.InteropServices;

namespace MyCapture.Platform.Recording;

/// <summary>
/// The one owner of this process's Media Foundation startup. The first encoder, reader or probe
/// starts the platform, it stays started while the application runs, and the application's exit
/// shuts it down once.
/// </summary>
/// <remarks>
/// <para>
/// Each encoder, reader and duration probe used to call <c>MFStartup</c> itself and
/// <c>MFShutdown</c> when it was released, so back-to-back recordings, edits and thumbnails
/// started the whole platform and tore it down again every time. Those cycles left kernel handles
/// behind (thread pools, I/O completion ports, wait packets, events): 100 encodes in one process
/// grew the handle count by 252–384 on a Windows 10 PC, and an 8-hour soak by 8,221 (#243).
/// </para>
/// <para>
/// Code that uses Media Foundation takes a lease (<see cref="Acquire"/>) before it creates any
/// Media Foundation object and disposes the lease after it has released all of them.
/// <see cref="Shutdown"/> marks the exit: the matching <c>MFShutdown</c> runs at once when no lease
/// is outstanding, or else when the last one is returned, so the platform never shuts down beneath
/// a recording, export or reader that is still finishing. A lease taken after exit has begun starts
/// the platform again and the last release shuts it down again, so every successful
/// <c>MFStartup</c> keeps exactly one matching <c>MFShutdown</c>.
/// </para>
/// </remarks>
public static class MediaFoundationPlatform
{
    private const uint MfVersion = 0x0002_0070; // MF_SDK_VERSION << 16 | MF_API_VERSION

    // MFSTARTUP_FULL also initialises sockets for network sources. The readers started the
    // platform this way; the encoder asked for MFSTARTUP_LITE, which a full startup includes.
    private const uint MfStartupFull = 0;

    /// <summary>The process-wide lifetime. Diagnostics and tests read its counters.</summary>
    internal static MediaFoundationLifetime Lifetime { get; } = new(
        static () => MFStartup(MfVersion, MfStartupFull),
        static () => MFShutdown());

    /// <summary>
    /// Starts the platform on first use and keeps it started at least until the returned lease is
    /// disposed. Dispose the lease after releasing every Media Foundation object created under it.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>MFStartup</c> failed; no lease is held.</exception>
    internal static MediaFoundationLifetime.Lease Acquire() => Lifetime.Acquire();

    /// <summary>
    /// Called once by the application on exit: balances the platform's startup now, or as soon as
    /// the last outstanding lease is returned.
    /// </summary>
    public static void Shutdown() => Lifetime.Shutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();
}

/// <summary>
/// Reference-counted ownership of one native platform startup. The native calls are injected so
/// the pairing rules can be tested without starting Media Foundation.
/// </summary>
/// <remarks>
/// Unlike a plain reference count, returning the last lease does not shut the platform down while
/// the application runs: sequential uses would otherwise start and stop it again each time, which
/// is exactly the cycle that leaked handles. Only <see cref="Shutdown"/> ends it.
/// </remarks>
internal sealed class MediaFoundationLifetime
{
    private readonly Func<int> _startup;
    private readonly Func<int> _shutdown;
    private readonly object _gate = new();
    private int _activeLeases;
    private bool _started;
    private bool _exiting;
    private int _startupCount;
    private int _shutdownCount;
    private int _lastShutdownResult;

    internal MediaFoundationLifetime(Func<int> startup, Func<int> shutdown)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    /// <summary>Successful native startups so far.</summary>
    internal int StartupCount
    {
        get { lock (_gate) return _startupCount; }
    }

    /// <summary>Native shutdowns so far; equal to <see cref="StartupCount"/> whenever the platform is down.</summary>
    internal int ShutdownCount
    {
        get { lock (_gate) return _shutdownCount; }
    }

    /// <summary>Leases handed out and not yet disposed.</summary>
    internal int ActiveLeases
    {
        get { lock (_gate) return _activeLeases; }
    }

    /// <summary>True while one native startup is held and not yet balanced.</summary>
    internal bool IsStarted
    {
        get { lock (_gate) return _started; }
    }

    /// <summary>The HRESULT of the most recent native shutdown (0 before the first one).</summary>
    internal int LastShutdownResult
    {
        get { lock (_gate) return _lastShutdownResult; }
    }

    internal Lease Acquire()
    {
        lock (_gate)
        {
            if (!_started)
            {
                int result = _startup();
                if (result < 0)
                {
                    throw new InvalidOperationException(
                        $"Media Foundation call 'MFStartup' failed with HRESULT 0x{result:X8}.");
                }

                _started = true;
                _startupCount++;
            }

            _activeLeases++;
            return new Lease(this);
        }
    }

    internal void Shutdown()
    {
        lock (_gate)
        {
            _exiting = true;
            if (_activeLeases == 0)
            {
                ShutdownLocked();
            }
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _activeLeases--;
            if (_activeLeases == 0 && _exiting)
            {
                ShutdownLocked();
            }
        }
    }

    private void ShutdownLocked()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _shutdownCount++;
        _lastShutdownResult = _shutdown();
    }

    /// <summary>Keeps the platform started. Disposing it more than once returns it once.</summary>
    internal sealed class Lease : IDisposable
    {
        private MediaFoundationLifetime? _owner;

        internal Lease(MediaFoundationLifetime owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
