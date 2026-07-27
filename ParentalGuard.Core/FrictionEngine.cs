namespace ParentalGuard.Core;

public enum FrictionPhase
{
    Free,
    Casual,
    Escalated,
    FinalWarning,
    HardBlock,
}

public class NudgeRequestedEventArgs : EventArgs
{
    public FrictionPhase Phase { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Evaluates total daily browser time against the friction phase ladder and raises
/// escalating confrontational nudges, culminating in a hard-block event at 60 minutes.
/// </summary>
public class FrictionEngine
{
    public static readonly TimeSpan FreeUntil = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CasualUntil = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan EscalatedUntil = TimeSpan.FromMinutes(55);
    public static readonly TimeSpan FinalWarningUntil = TimeSpan.FromMinutes(60);

    private static readonly string[] CasualMessages =
    {
        "Come on bro, you've been on here a while.",
        "Bro. Put it down.",
        "Thirty minutes, man. Go do something else.",
        "Alright, that's enough for now.",
    };

    private static readonly string[] EscalatedMessages =
    {
        "45 minutes. Are you serious?",
        "This is getting excessive. Log off.",
        "You really should not still be on this.",
    };

    private static readonly string[] FinalWarningMessages =
    {
        "Five minutes to hard block. Walk away.",
        "Last chance. Close it now.",
        "This locks in a few minutes. Save your work.",
    };

    private readonly Random _random = new();
    private readonly TimeSpan _nudgeCooldown;
    private DateTime _nextNudgeAllowedUtc = DateTime.MinValue;
    private FrictionPhase _lastPhase = FrictionPhase.Free;

    public event EventHandler<NudgeRequestedEventArgs>? NudgeRequested;
    public event EventHandler? HardBlockTriggered;

    public FrictionEngine(TimeSpan? nudgeCooldown = null)
    {
        _nudgeCooldown = nudgeCooldown ?? TimeSpan.FromMinutes(3);
    }

    public FrictionPhase CurrentPhase { get; private set; } = FrictionPhase.Free;

    /// <summary>Evaluates the current phase given today's total browser seconds and raises events as needed.</summary>
    public FrictionPhase Evaluate(double totalBrowserSeconds)
    {
        var elapsed = TimeSpan.FromSeconds(totalBrowserSeconds);
        var phase = DeterminePhase(elapsed);
        CurrentPhase = phase;

        if (phase == FrictionPhase.HardBlock)
        {
            HardBlockTriggered?.Invoke(this, EventArgs.Empty);
        }
        else if (phase != FrictionPhase.Free)
        {
            MaybeNudge(phase);
        }

        _lastPhase = phase;
        return phase;
    }

    private static FrictionPhase DeterminePhase(TimeSpan elapsed)
    {
        if (elapsed < FreeUntil) return FrictionPhase.Free;
        if (elapsed < CasualUntil) return FrictionPhase.Casual;
        if (elapsed < EscalatedUntil) return FrictionPhase.Escalated;
        if (elapsed < FinalWarningUntil) return FrictionPhase.FinalWarning;
        return FrictionPhase.HardBlock;
    }

    private void MaybeNudge(FrictionPhase phase)
    {
        var now = DateTime.UtcNow;
        var justEnteredPhase = phase != _lastPhase;
        if (!justEnteredPhase && now < _nextNudgeAllowedUtc) return;

        var pool = phase switch
        {
            FrictionPhase.Casual => CasualMessages,
            FrictionPhase.Escalated => EscalatedMessages,
            FrictionPhase.FinalWarning => FinalWarningMessages,
            _ => Array.Empty<string>(),
        };
        if (pool.Length == 0) return;

        var message = pool[_random.Next(pool.Length)];
        _nextNudgeAllowedUtc = now + _nudgeCooldown;
        NudgeRequested?.Invoke(this, new NudgeRequestedEventArgs { Phase = phase, Message = message });
    }

    /// <summary>Call after a day rollover so a fresh day starts back in the Free phase.</summary>
    public void ResetForNewDay()
    {
        CurrentPhase = FrictionPhase.Free;
        _lastPhase = FrictionPhase.Free;
        _nextNudgeAllowedUtc = DateTime.MinValue;
    }
}
