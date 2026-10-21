using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rotina.Core;

public sealed class RuleException(string message) : Exception(message);

public sealed record TickResult(Session? Ended, Session? Started);

public sealed record PendingStart(ScheduleEntry Entry, Activity Activity, DateTimeOffset At);

/// <summary>Pure rules engine: no I/O, the caller passes the current state and the clock.</summary>
public sealed class Engine(TimeZoneInfo zone, TimeSpan minUnit, string deviceId)
{
    static readonly string[] DayNames = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];

    public static Engine For(Settings s) => new(
        TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId),
        // Development mode reads "minutes" as seconds so tests of the flow take seconds.
        s.Mode == AppMode.Development ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1),
        s.DeviceId);

    public TimeSpan MinDuration(Activity a) => minUnit * a.MinDurationMinutes;

    public static RuleState StateOf(Session? current, DateTimeOffset now) =>
        current is null || current.EndedAt is not null ? RuleState.Free
        : now < current.MinEndsAt ? RuleState.Locked
        : RuleState.Completable;

    public Session StartManual(Session? current, Activity activity, DateTimeOffset now, out Session? ended)
    {
        if (activity.Deleted) throw new RuleException("Atividade excluída.");
        if (StateOf(current, now) == RuleState.Locked)
            throw new RuleException("A atividade atual ainda não atingiu o tempo mínimo.");
        ended = current is { EndedAt: null } ? current with { EndedAt = now } : null;
        return NewSession(Guid.NewGuid(), activity, now, SessionOrigin.Manual);
    }

    /// <summary>Recovery key: ends the running session now, even before its minimum. The schedule resumes with the next start.</summary>
    public static Session Recover(Session current, DateTimeOffset now) => current.EndedAt is null
        ? current with { EndedAt = now, Recovered = true }
        : throw new RuleException("Não há atividade em andamento.");

    public static Session End(Session current, DateTimeOffset now) => StateOf(current, now) switch
    {
        RuleState.Completable => current with { EndedAt = now },
        RuleState.Locked => throw new RuleException("Ainda não atingiu o tempo mínimo."),
        _ => throw new RuleException("Sessão já encerrada."),
    };

    public static void EnsureActivityEditable(Session? current, Guid activityId)
    {
        if (current is { EndedAt: null } && current.ActivityId == activityId)
            throw new RuleException("A atividade em andamento não pode ser editada nem excluída.");
    }

    public static void EnsureGlobalEditable(Session? current, DateTimeOffset now)
    {
        if (StateOf(current, now) == RuleState.Locked)
            throw new RuleException("Sempre liberados e configurações ficam travados enquanto a atividade estiver travada.");
    }

    /// <summary>The scheduled start that should happen now, or that is queued behind a locked session.</summary>
    public PendingStart? Pending(Session? current, IEnumerable<ScheduleEntry> entries,
        IReadOnlyDictionary<Guid, Activity> activities, Func<Guid, bool> sessionExists, DateTimeOffset now)
    {
        var running = current is { EndedAt: null } ? current : null;
        PendingStart? best = null;
        foreach (var e in entries)
        {
            if (!e.Enabled || e.Deleted || !activities.TryGetValue(e.ActivityId, out var a) || a.Deleted) continue;
            // Occurrences before the entry was last edited are ignored, so saving a past time never starts it.
            if (LatestOccurrence(e, now) is not { } at || at <= e.UpdatedAt) continue;
            // Free: a missed start only counts while its minimum would still be running.
            // Running: only starts that arrived after the current session began.
            if (running is null ? at + MinDuration(a) <= now : at <= running.StartedAt) continue;
            if (sessionExists(ScheduledSessionId(e, at))) continue;
            // Latest wins; ties break by entry id so PC and phone pick the same one.
            if (best is null || at > best.At
                || (at == best.At && string.CompareOrdinal(e.Id.ToString(), best.Entry.Id.ToString()) < 0))
                best = new(e, a, at);
        }
        return best;
    }

    public TickResult Tick(Session? current, IEnumerable<ScheduleEntry> entries,
        IReadOnlyDictionary<Guid, Activity> activities, Func<Guid, bool> sessionExists, DateTimeOffset now)
    {
        if (Pending(current, entries, activities, sessionExists, now) is not { } p) return new(null, null);
        var id = ScheduledSessionId(p.Entry, p.At);
        if (current is not { EndedAt: null } running) return new(null, NewSession(id, p.Activity, p.At, SessionOrigin.Schedule));
        if (now < running.MinEndsAt) return new(null, null); // queued
        // Replaces a completable session at the scheduled time, or starts a queued one right when the minimum ended.
        var startAt = p.At > running.MinEndsAt ? p.At : running.MinEndsAt;
        return new(running with { EndedAt = startAt }, NewSession(id, p.Activity, startAt, SessionOrigin.Schedule));
    }

    public DateTimeOffset? LatestOccurrence(ScheduleEntry e, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var back = 0; back <= 7; back++)
        {
            var date = today.AddDays(-back);
            if (!e.DaysOfWeek.Contains(date.DayOfWeek)) continue;
            var at = ToUtc(date.ToDateTime(e.StartTime));
            if (at <= now) return at;
        }
        return null;
    }

    /// <summary>Same id on every device: first 16 bytes of SHA-256("schedule:{entryId}:{yyyy-MM-dd}").</summary>
    public Guid ScheduledSessionId(ScheduleEntry e, DateTimeOffset occurrence)
    {
        var date = TimeZoneInfo.ConvertTime(occurrence, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"schedule:{e.Id}:{date}"));
        return Guid.Parse(Convert.ToHexString(hash, 0, 16));
    }

    /// <summary>
    /// Applies a session the phone created or changed (maybe offline) to the PC's history. The PC is the source of truth:
    /// nothing ends before its minimum, and of two activities that overlap the later one only stands if the earlier
    /// one could already end when it began.
    /// </summary>
    /// <param name="sameId">The PC's copy of the same session, if any.</param>
    /// <param name="open">The PC's session in progress, if any.</param>
    public static MergeResult MergeSession(Session incoming, Session? sameId, Session? open)
    {
        if (sameId is not null)
        {
            // Known session (a manual start already synced, or a scheduled one both devices made): only an end can be new.
            if (incoming.EndedAt is not { } end || sameId.EndedAt is not null) return new(true, []);
            // shortcut: an early end is trusted when the phone marks it Recovered (it checked the key, maybe offline);
            // whoever holds the phone's token could fake it. Upgrade by sending the key when online.
            return end >= sameId.MinEndsAt || incoming.Recovered
                ? new(true, [sameId with { EndedAt = end, Recovered = incoming.Recovered }])
                : new(false, []);
        }
        if (incoming.EndedAt is { } e && e < incoming.MinEndsAt && !incoming.Recovered) return new(false, []);
        if (open is null) return new(true, [incoming]);
        if (incoming.StartedAt <= open.StartedAt)
        {
            // Began before the PC's current one: kept as history, closed at the latest when the PC's began.
            var closed = incoming.EndedAt is { } ie && ie < open.StartedAt ? ie : open.StartedAt;
            return new(true, [incoming with { EndedAt = closed }]);
        }
        // Began after the PC's current one: only valid if that one could already end.
        return incoming.StartedAt >= open.MinEndsAt
            ? new(true, [open with { EndedAt = incoming.StartedAt }, incoming])
            : new(false, []);
    }

    /// <summary>True when a saved row did not really change, so it keeps its UpdatedAt and a queued start survives.</summary>
    public static bool SameSlot(ScheduleEntry a, ScheduleEntry b) =>
        a.ActivityId == b.ActivityId && a.StartTime == b.StartTime && a.Enabled == b.Enabled
        && a.DaysOfWeek.Order().SequenceEqual(b.DaysOfWeek.Order());

    public static List<string> ScheduleWarnings(IEnumerable<ScheduleEntry> entries, IReadOnlyDictionary<Guid, Activity> activities)
    {
        const int week = 7 * 1440;
        var occ = (from e in entries
                   where e.Enabled && !e.Deleted && activities.ContainsKey(e.ActivityId)
                   from d in e.DaysOfWeek.Distinct()
                   select (Entry: e, Day: d, Start: (int)d * 1440 + e.StartTime.Hour * 60 + e.StartTime.Minute)).ToList();
        var warnings = new List<string>();
        foreach (var a in occ)
        foreach (var b in occ)
        {
            if (a.Entry.Id == b.Entry.Id) continue;
            var act = activities[a.Entry.ActivityId];
            var next = activities[b.Entry.ActivityId];
            var gap = ((b.Start - a.Start) % week + week) % week;
            if (gap == 0 && string.CompareOrdinal(a.Entry.Id.ToString(), b.Entry.Id.ToString()) < 0)
                warnings.Add($"{act.Name} e {next.Name} começam no mesmo horário ({DayNames[(int)a.Day]} {Hm(a.Entry)}).");
            else if (gap > 0 && gap < act.MinDurationMinutes)
                warnings.Add($"{act.Name} ({DayNames[(int)a.Day]} {Hm(a.Entry)}) dura no mínimo {act.MinDurationMinutes} min e invade o início de {next.Name} às {Hm(b.Entry)}.");
        }
        return warnings;
    }

    static string Hm(ScheduleEntry e) => e.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    Session NewSession(Guid id, Activity a, DateTimeOffset at, SessionOrigin origin) => new()
    {
        Id = id,
        ActivityId = a.Id,
        StartedAt = at,
        MinEndsAt = at + MinDuration(a),
        Origin = origin,
        DeviceId = deviceId,
        RulesSnapshot = a,
    };

    DateTimeOffset ToUtc(DateTime local)
    {
        // shortcut: a start inside a DST gap moves 1 h later, revisit if Brazil brings DST back.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
