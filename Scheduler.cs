namespace KillSwitch;

/// <summary>
/// Edge-triggered schedule engine. Every tick it computes whether any enabled window is
/// currently active. It only acts on a TRANSITION (active↔inactive), so a manual toggle
/// made between scheduled edges is respected until the next edge.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _timer;
    private bool? _lastActive;

    /// <summary>Raised when the schedule wants to change state. Argument = should-be-blocked.</summary>
    public event Action<bool>? TransitionRequested;

    public Scheduler(AppSettings settings)
    {
        _settings = settings;
        _timer = new System.Windows.Forms.Timer { Interval = 15000 }; // check every 15s
        _timer.Tick += (_, _) => Evaluate();
    }

    public void Start()
    {
        _timer.Start();
        // Seed current state without firing a transition, then act if needed on the first real evaluation.
        _lastActive = null;
        Evaluate();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Is any enabled window active right now?</summary>
    public bool IsActiveNow(DateTime now) =>
        _settings.ScheduleEnabled && _settings.Schedule.Any(w => w.IsActiveAt(now));

    private void Evaluate()
    {
        if (!_settings.ScheduleEnabled)
        {
            _lastActive = null;
            return;
        }

        bool active = _settings.Schedule.Any(w => w.IsActiveAt(DateTime.Now));
        if (_lastActive is null)
        {
            // First evaluation after (re)start: align reality with the schedule.
            _lastActive = active;
            TransitionRequested?.Invoke(active);
            return;
        }

        if (active != _lastActive.Value)
        {
            _lastActive = active;
            TransitionRequested?.Invoke(active);
        }
    }

    /// <summary>Call after the schedule or its master toggle changes so the next tick re-aligns.</summary>
    public void Reset() => _lastActive = null;

    public void Dispose() => _timer.Dispose();
}
