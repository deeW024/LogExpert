namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>Resolves a discovery adapter hint to its logical-record parser.</summary>
public static class MinecraftSourceParserResolver
{
    private static readonly ILogEventParser MinecraftLatestLog = new MinecraftLatestLogParser();
    private static readonly ILogEventParser YeezusTextLog = new YeezusTextLogParser();
    private static readonly ILogEventParser ReCactusSessionText = new ReCactusSessionTextParser();
    private static readonly ILogEventParser CactusMonitorSessionJsonl = new CactusMonitorSessionJsonlParser();

    public static ILogEventParser Resolve (MinecraftSourceAdapterHint adapterHint) =>
        adapterHint switch
        {
            MinecraftSourceAdapterHint.MinecraftLatestLog => MinecraftLatestLog,
            MinecraftSourceAdapterHint.YeezusTextLog => YeezusTextLog,
            MinecraftSourceAdapterHint.ReCactusSessionText => ReCactusSessionText,
            MinecraftSourceAdapterHint.CactusMonitorSessionJsonl => CactusMonitorSessionJsonl,
            _ => throw new ArgumentOutOfRangeException(nameof(adapterHint), adapterHint, null)
        };
}
