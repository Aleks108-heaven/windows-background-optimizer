namespace BgOptimizer.Services;

/// <summary>
/// Decides when a load is worth telling the user about: above their limit for several samples in a row (a
/// momentary spike is normal), once per episode, and not again for a few minutes. Pure logic, so it is tested
/// without a clock or a window.
/// </summary>
internal sealed class LoadWatch
{
    /// <summary>Three samples in a row (about 15 s at the 5 s refresh) before it counts as sustained.</summary>
    public const int SustainedSamples = 3;

    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private sealed class State
    {
        public int Streak;
        public bool Fired;
        public DateTime? LastAlert;
    }

    private readonly Dictionary<string, State> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feeds one sample of every load; returns the loads that just became worth an alert. A null reading changes nothing.</summary>
    public IReadOnlyList<string> Evaluate(DateTime now, IReadOnlyDictionary<string, double?> loads, int limitPercent)
    {
        var due = new List<string>();
        foreach (var (name, value) in loads)
        {
            if (value is not { } v) continue;
            if (!_state.TryGetValue(name, out var s)) _state[name] = s = new State();

            if (v > limitPercent)
            {
                s.Streak++;
                if (s.Streak >= SustainedSamples && !s.Fired && (s.LastAlert is null || now - s.LastAlert >= Cooldown))
                {
                    s.Fired = true;
                    s.LastAlert = now;
                    due.Add(name);
                }
            }
            else
            {
                s.Streak = 0;
                s.Fired = false; // the episode is over; the next one may alert again once the cooldown has passed
            }
        }
        return due;
    }

    /// <summary>Forgets everything, e.g. when the user changes the limit or turns the alert off and on.</summary>
    public void Reset() => _state.Clear();
}
