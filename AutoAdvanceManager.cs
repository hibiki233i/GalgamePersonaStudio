namespace GalgamePersonaStudio;

/// <summary>
/// State machine for auto-advance through visual novel dialogue.
/// State fields (StuckCount, CurrentState) are managed by MainWindow directly
/// rather than through a facade method, for alignment with the capture timer.
/// </summary>
public class AutoAdvanceManager
{
    private int _postClickSkipCount;

    public enum State { Idle, Active, Stuck }
    public State CurrentState { get; set; } = State.Idle;

    public bool Enabled { get; set; }
    public int ClickX { get; set; }
    public int ClickY { get; set; }
    public int StuckCount { get; set; }
    public int StuckThreshold { get; set; } = 5;
    public int PostClickDelayMs { get; set; } = 1200;
    public int CaptureIntervalMs { get; set; } = 1200;
    public string ChoiceMode { get; set; } = "manual";
    public string ChoiceAutoRule { get; set; } = "first";

    public bool ShouldCapture()
    {
        if (!Enabled || CurrentState == State.Idle) return true;
        if (_postClickSkipCount > 0)
        {
            _postClickSkipCount--;
            return false;
        }
        return true;
    }

    public void OnClickDispatched()
    {
        if (!Enabled) return;
        var skipTicks = Math.Max(1, PostClickDelayMs / Math.Max(1, CaptureIntervalMs));
        _postClickSkipCount = skipTicks;
    }

    public void NotifyChoiceHandled()
    {
        StuckCount = 0;
        CurrentState = State.Active;
    }

    public void ResetStuck()
    {
        StuckCount = 0;
        CurrentState = State.Active;
    }

    public void Stop()
    {
        CurrentState = State.Idle;
        StuckCount = 0;
        _postClickSkipCount = 0;
    }
}
