using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rotina.Core;

public enum Platform { Android, Windows, Web }
public enum SessionOrigin { Schedule, Manual }
public enum SessionEventType { Start, End, Queued }
public enum RuleState { Free, Locked, Completable }
public enum AppMode { Development, Production }

public sealed record Activity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Color { get; init; } = "#4A90D9";
    public int MinDurationMinutes { get; init; }
    public List<string> AllowedAndroidPackages { get; init; } = [];
    public List<string> AllowedWindowsExecutables { get; init; } = [];
    public List<string> AllowedSites { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
    public bool Deleted { get; init; }
}

public sealed record ScheduleEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ActivityId { get; init; }
    public List<DayOfWeek> DaysOfWeek { get; init; } = [];
    public TimeOnly StartTime { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset UpdatedAt { get; init; }
    public bool Deleted { get; init; }
}

public sealed record Session
{
    public Guid Id { get; init; }
    public Guid ActivityId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset MinEndsAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public SessionOrigin Origin { get; init; }
    public string DeviceId { get; init; } = "";
    // Copy of the activity at start, so later edits never change a running session.
    public Activity RulesSnapshot { get; init; } = new();
    /// <summary>Ended early with the recovery key (the only way to end before the minimum).</summary>
    public bool Recovered { get; init; }
}

public sealed record SessionEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public SessionEventType Type { get; init; }
    public DateTimeOffset At { get; init; }
    public string DeviceId { get; init; } = "";
}

public sealed record AlwaysAllowed(Platform Platform, string Identifier);

public static class Sites
{
    /// <summary>"https://www.YouTube.com/watch?v=1" becomes "youtube.com"; null when it is not a domain.</summary>
    public static string? Normalize(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (!Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.') ? host : null;
    }
}

public static class Api
{
    public const string BaseUrl = "http://127.0.0.1:47800/api/v1/";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// API shapes shared by the service and the desktop app.
public sealed record AllowedLists(List<string> Android, List<string> Windows, List<string> Web);
public sealed record StateDto(RuleState State, Session? Session, int SecondsUntilCompletable,
    AllowedLists? Allowed, Guid? QueuedActivityId, AppMode Mode, int? ExtensionSeenSecondsAgo = null);
public sealed record ScheduleSaveResult(List<ScheduleEntry> Entries, List<string> Warnings);
public sealed record StartRequest(Guid ActivityId);

// Phone pairing and sync (phase 5).
/// <summary>The paired phone. Only a hash of its token is stored.</summary>
public sealed record Device(string Id, string Name, string TokenHash, DateTimeOffset? LastSeenAt);
public sealed record PairingCode(string Host, int Port, string Token, string Code);
public sealed record PairingStatus(bool Paired, DateTimeOffset? LastSeenAt);
public sealed record CatalogItem(string Identifier, string Label);
public sealed record SyncPull(long Rev, AppMode Mode, List<Activity> Activities, List<ScheduleEntry> Schedule,
    List<AlwaysAllowed>? AlwaysAllowed, List<Session> Sessions, string? RecoveryKeyHash,
    string? TimeZone = null); // IANA id ("America/Sao_Paulo"): the phone reads the schedule in the PC's zone

// Recovery key and production mode (phase 6).
public sealed record RecoveryKey(string Key);
public sealed record RecoveryRequest(string Key);
public sealed record ModeRequest(AppMode Mode, string? Key);
public sealed record SettingsDto(AppMode Mode, bool RecoveryKeySet, bool RecoveryConfirmed);
public sealed record SyncPush(List<Session> Sessions);
public sealed record SyncPushResult(List<Guid> Accepted, List<Guid> Rejected);
public sealed record MergeResult(bool Accepted, List<Session> Writes);

public sealed record Settings
{
    public AppMode Mode { get; init; } = AppMode.Development;
    // Fixed at first run so changing the system time zone cannot shift the schedule.
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public string DeviceId { get; init; } = Guid.NewGuid().ToString();
    public string? RecoveryKeyHash { get; init; }
    /// <summary>The user said the key is printed and with a trusted person; production mode needs this.</summary>
    public bool RecoveryConfirmed { get; init; }
}
