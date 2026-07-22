namespace Palera1nWin.App.Services;

public enum HardwareOperationKind
{
    None = 0,
    Diagnostics,
    DriverRepair,
    WslProvision,
    RuntimeUpdate,
    Jailbreak,
    Downgrade,
    DowngradeRecovery,
    TetherBoot,
    CombinedBoot,
}

public sealed record HardwareOperationState(
    HardwareOperationKind Operation,
    string? Detail,
    DateTimeOffset? StartedAt)
{
    public static HardwareOperationState Idle { get; } = new(HardwareOperationKind.None, null, null);
    public bool IsBusy => Operation != HardwareOperationKind.None;
}

public sealed class HardwareOperationBusyException : InvalidOperationException
{
    public HardwareOperationBusyException(HardwareOperationKind requested, HardwareOperationState active)
        : base(active.IsBusy
            ? $"Cannot start {requested} while {active.Operation} is active. Cancel or finish the active operation first."
            : $"Cannot start {requested} because the operation lock is unavailable.")
    {
        Requested = requested;
        Active = active;
    }

    public HardwareOperationKind Requested { get; }
    public HardwareOperationState Active { get; }
}

public sealed class HardwareOperationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private HardwareOperationState _state = HardwareOperationState.Idle;
    private bool _disposed;

    public event EventHandler<HardwareOperationState>? StateChanged;

    public HardwareOperationState Current
    {
        get { lock (_sync) return _state; }
    }

    public async Task<HardwareOperationLease> AcquireAsync(
        HardwareOperationKind operation,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (operation == HardwareOperationKind.None) throw new ArgumentOutOfRangeException(nameof(operation));

        // Diagnostics may be needed to explain an incomplete package. Every action
        // that can mutate drivers, WSL, USB ownership, firmware, or boot state must
        // first prove that its elevated executable/toolchain files still match the
        // package manifest created by CI.
        if (operation != HardwareOperationKind.Diagnostics)
            await PackageIntegrityVerifier.EnsureValidAsync(cancellationToken).ConfigureAwait(false);

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new HardwareOperationBusyException(operation, Current);

        SetState(new HardwareOperationState(operation, detail, DateTimeOffset.UtcNow));
        return new HardwareOperationLease(this, operation);
    }

    public void UpdateDetail(HardwareOperationKind operation, string detail)
    {
        HardwareOperationState? next = null;
        lock (_sync)
        {
            if (_state.Operation != operation) return;
            _state = _state with { Detail = detail };
            next = _state;
        }
        RaiseStateChanged(next);
    }

    private void SetState(HardwareOperationState state)
    {
        lock (_sync) _state = state;
        RaiseStateChanged(state);
    }

    private void RaiseStateChanged(HardwareOperationState state)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (EventHandler<HardwareOperationState> handler in handlers.GetInvocationList())
        {
            try { handler(this, state); }
            catch { }
        }
    }

    internal void Release(HardwareOperationKind operation)
    {
        var release = false;
        lock (_sync)
        {
            if (_state.Operation == operation)
            {
                _state = HardwareOperationState.Idle;
                release = true;
            }
        }
        if (!release) return;
        _gate.Release();
        RaiseStateChanged(HardwareOperationState.Idle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

public sealed class HardwareOperationLease : IAsyncDisposable, IDisposable
{
    private HardwareOperationCoordinator? _owner;
    private readonly HardwareOperationKind _operation;

    internal HardwareOperationLease(HardwareOperationCoordinator owner, HardwareOperationKind operation)
    {
        _owner = owner;
        _operation = operation;
    }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_operation);
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
