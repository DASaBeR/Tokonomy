namespace Taskrail.Runtime.Orchestration;

/// <summary>
/// Hard spend cap. Every metered call adds its cost; crossing the cap cancels <see cref="Token"/>, which kills the
/// running executor's process tree. Thread-safe: the metering proxy reports from request threads.
/// </summary>
public sealed class BudgetGuard : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private decimal _spent;

    public BudgetGuard(decimal limitUsd, decimal alreadySpent = 0)
    {
        if (limitUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitUsd), "Budget must be positive.");
        }

        Limit = limitUsd;
        Add(alreadySpent);
    }

    public decimal Limit { get; }

    public decimal Spent
    {
        get
        {
            lock (_gate)
            {
                return _spent;
            }
        }
    }

    public bool Exhausted => _cts.IsCancellationRequested;

    public CancellationToken Token => _cts.Token;

    public void Add(decimal usd)
    {
        bool cancel;
        lock (_gate)
        {
            _spent += Math.Max(0, usd);
            cancel = _spent >= Limit;
        }

        if (cancel && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    public void Dispose() => _cts.Dispose();
}
