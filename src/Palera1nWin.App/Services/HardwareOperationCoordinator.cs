namespace Palera1nWin.App.Services;

public enum HardwareOperationKind
{
    None = 0,
    Diagnostics,
    DriverRepair,
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
    public static HardwareOperationState Idle { get; } =
        new(HardwareOperationKind.None, null, null);

    public bool IsBusy => Operation != HardwareOperationKind.None;
}

public sealed class HardwareOperationBusyException : InvalidOperationException
{
    public HardwareOperationBusyException(HardwareOperationKind requested, HardwareOperationState active)
        : base(active.IsBusy
            ? $"Cannot start {requested} while {active.Operation} is active. Cancel or finish the active hardware operation first."
            : $"Cannot start {requested} because the hardware operation lock is unavailable.")
    {
        Requested = requested;
        Active = active;
    }

    public HardwareOperationKind Requested { get; }
    public HardwareOperationState Active { get; }
}

public sealed class HardwareOperationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private HardwareOperationState _state = HardwareOperationState.Idle;

    public event EventHandler<HardwareOperationState>? StateChanged;

    public HardwareOperationState Current
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public async Task<HardwareOperationLease> AcquireAsync(
        HardwareOperationKind operation,
        string? detail,
        CancellationToken cancellationToken)
    {
        if (operation == HardwareOperationKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new HardwareOperationBusyException(operation, Current);
        }

        var next = new HardwareOperationState(operation, detail, DateTimeOffset.UtcNow);
        SetState(next);
        return new HardwareOperationLease(this, operation);
    }

    public void UpdateDetail(HardwareOperationKind operation, string detail)
    {
        HardwareOperationState? next = null;
        lock (_sync)
        {
            if (_state.Operation != operation)
            {
                return;
            }

            _state = _state with { Detail = detail };
            next = _state;
        }

        StateChanged?.Invoke(this, next);
    }

    private void SetState(HardwareOperationState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }

    internal void Release(HardwareOperationKind operation)
    {
        lock (_sync)
        {
            if (_state.Operation != operation)
            {
                return;
            }

            _state = HardwareOperationState.Idle;
        }

        _gate.Release();
        StateChanged?.Invoke(this, HardwareOperationState.Idle);
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
