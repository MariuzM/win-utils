namespace WinUtils.Services;

public enum CheckState
{
    Compliant,
    NeedsChange,
    NotApplicable,
    Error,
}

public sealed class RemediationCheck
{
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public CheckState State { get; init; }
}
