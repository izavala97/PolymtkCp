namespace PolymtkCp.Services.Watcher;

/// <summary>
/// Tunables for the trader-activity watcher. Bind from the
/// <c>"Watcher"</c> section in appsettings.
/// </summary>
public sealed class WatcherOptions
{
    public const string SectionName = "Watcher";

    /// <summary>
    /// How often the watcher polls every trader's /activity feed.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Page size requested from Polymarket /activity for each trader.
    /// 50 covers a brisk trader for several minutes between polls.
    /// </summary>
    public int ActivityPageSize { get; set; } = 50;

    /// <summary>
    /// If false, the hosted service is not registered. Useful for tests
    /// or to disable the watcher in environments without a service-role key.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
