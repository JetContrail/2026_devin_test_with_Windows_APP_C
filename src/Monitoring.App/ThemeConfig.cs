namespace Monitoring.App;

/// <summary>Per-user visual customization: theme accent color and optional logo
/// image path. Resolved from user_themes.json ("&lt;username&gt;" entry, "*" fallback).</summary>
public sealed record UserTheme(string AccentColor, string? LogoImage, string TitleSuffix);
