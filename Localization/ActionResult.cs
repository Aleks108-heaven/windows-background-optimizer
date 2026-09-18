namespace BgOptimizer.Localization;

/// <summary>
/// Result of a service action, carrying a translation key + args instead of
/// a pre-formatted string so the activity log stays in the current language.
/// </summary>
public record ActionResult(bool Ok, string MessageKey, params object[] Args)
{
    public string Message => Loc.Instance.Format(MessageKey, Args);
}
