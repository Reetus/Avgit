namespace Avgit.App.Services;

public sealed class RebasePlanItem
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Action { get; set; } = "pick";
}
