using Rotina.Core;

namespace Rotina.Tests;

public class EngineTests
{
    static readonly DateTimeOffset Mon8 = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero); // a Monday
    readonly Engine engine = new(TimeZoneInfo.Utc, TimeSpan.FromMinutes(1), "pc");
    readonly Activity study = new() { Name = "Estudo", MinDurationMinutes = 30, UpdatedAt = Mon8.AddDays(-1) };
    readonly Activity gym = new() { Name = "Treino", MinDurationMinutes = 60, UpdatedAt = Mon8.AddDays(-1) };

    Dictionary<Guid, Activity> Acts => new() { [study.Id] = study, [gym.Id] = gym };

    static ScheduleEntry Entry(Activity a, int hour, int minute = 0) => new()
    {
        ActivityId = a.Id,
        DaysOfWeek = [DayOfWeek.Monday],
        StartTime = new TimeOnly(hour, minute),
        UpdatedAt = Mon8.AddDays(-1),
    };

    TickResult Tick(Session? current, DateTimeOffset now, params ScheduleEntry[] entries) =>
        engine.Tick(current, entries, Acts, _ => false, now);

    Session Manual(Activity a, DateTimeOffset at) => engine.StartManual(null, a, at, out _);

    [Fact]
    public void States_follow_the_minimum()
    {
        var s = Manual(study, Mon8);
        Assert.Equal(RuleState.Free, Engine.StateOf(null, Mon8));
        Assert.Equal(RuleState.Locked, Engine.StateOf(s, Mon8.AddMinutes(29)));
        Assert.Equal(RuleState.Completable, Engine.StateOf(s, Mon8.AddMinutes(30)));
        Assert.Equal(RuleState.Free, Engine.StateOf(s with { EndedAt = Mon8.AddMinutes(40) }, Mon8.AddMinutes(41)));
    }

    [Fact]
    public void End_is_refused_before_the_minimum()
    {
        var s = Manual(study, Mon8);
        Assert.Throws<RuleException>(() => Engine.End(s, Mon8.AddMinutes(29)));
        Assert.Equal(Mon8.AddMinutes(30), Engine.End(s, Mon8.AddMinutes(30)).EndedAt);
    }

    [Fact]
    public void Manual_start_is_refused_while_locked_and_replaces_when_completable()
    {
        var s = Manual(study, Mon8);
        Assert.Throws<RuleException>(() => engine.StartManual(s, gym, Mon8.AddMinutes(10), out _));
        var next = engine.StartManual(s, gym, Mon8.AddMinutes(31), out var ended);
        Assert.Equal(Mon8.AddMinutes(31), ended!.EndedAt);
        Assert.Equal(gym.Id, next.ActivityId);
    }

    [Fact]
    public void Scheduled_start_happens_at_the_time_with_the_same_id_on_every_device()
    {
        var e = Entry(study, 8);
        Assert.Null(Tick(null, Mon8.AddSeconds(-1), e).Started);
        var started = Tick(null, Mon8.AddSeconds(1), e).Started!;
        Assert.Equal(Mon8, started.StartedAt);
        Assert.Equal(Mon8.AddMinutes(30), started.MinEndsAt);
        var phone = new Engine(TimeZoneInfo.Utc, TimeSpan.FromMinutes(1), "phone");
        Assert.Equal(started.Id, phone.Tick(null, [e], Acts, _ => false, Mon8.AddMinutes(5)).Started!.Id);
    }

    [Fact]
    public void Scheduled_session_id_matches_the_android_app()
    {
        // Same vector as android EngineTest: both devices must create the same session id.
        var e = Entry(study, 8) with { Id = Guid.Parse("11111111-1111-1111-1111-111111111111") };
        Assert.Equal(Guid.Parse("13f2ffe2-e261-482c-2e69-254023c1016b"), engine.ScheduledSessionId(e, Mon8));
    }

    [Fact]
    public void Scheduled_start_does_not_repeat_once_the_session_exists()
    {
        var e = Entry(study, 8);
        var id = engine.ScheduledSessionId(e, Mon8);
        Assert.Null(engine.Tick(null, [e], Acts, x => x == id, Mon8.AddMinutes(5)).Started);
    }

    [Fact]
    public void Missed_start_only_counts_while_its_minimum_is_still_running()
    {
        var e = Entry(study, 8);
        Assert.Equal(Mon8, Tick(null, Mon8.AddMinutes(29), e).Started!.StartedAt);
        Assert.Null(Tick(null, Mon8.AddMinutes(30), e).Started);
    }

    [Fact]
    public void Entry_edited_after_its_time_does_not_start_today()
    {
        var e = Entry(study, 8) with { UpdatedAt = Mon8.AddMinutes(1) };
        Assert.Null(Tick(null, Mon8.AddMinutes(2), e).Started);
    }

    [Fact]
    public void Scheduled_start_queues_behind_a_locked_session()
    {
        var current = Manual(gym, Mon8.AddMinutes(-30)); // locked until 08:30
        var e = Entry(study, 8);
        Assert.Equal(Mon8, engine.Pending(current, [e], Acts, _ => false, Mon8.AddMinutes(10))!.At);
        Assert.Equal(new TickResult(null, null), Tick(current, Mon8.AddMinutes(10), e));

        var r = Tick(current, Mon8.AddMinutes(30), e);
        Assert.Equal(Mon8.AddMinutes(30), r.Ended!.EndedAt);
        Assert.Equal(Mon8.AddMinutes(30), r.Started!.StartedAt);
        Assert.Equal(Mon8.AddMinutes(60), r.Started.MinEndsAt);
    }

    [Fact]
    public void Scheduled_start_replaces_a_completable_session_at_its_time()
    {
        var current = Manual(study, Mon8.AddMinutes(-40)); // completable since 07:50
        var r = Tick(current, Mon8.AddSeconds(5), Entry(gym, 8));
        Assert.Equal(Mon8, r.Ended!.EndedAt);
        Assert.Equal(Mon8, r.Started!.StartedAt);
        Assert.Equal(SessionOrigin.Schedule, r.Started.Origin);
    }

    [Fact]
    public void When_several_starts_queue_the_latest_wins()
    {
        var current = Manual(gym, Mon8.AddMinutes(-10)); // locked until 08:50
        var late = Entry(study, 8, 20);
        var r = Tick(current, Mon8.AddMinutes(50), Entry(study, 8, 10), late);
        Assert.Equal(engine.ScheduledSessionId(late, Mon8.AddMinutes(20)), r.Started!.Id);
        Assert.Equal(Mon8.AddMinutes(50), r.Started.StartedAt);
    }

    [Fact]
    public void Schedule_is_read_in_the_fixed_time_zone()
    {
        var brasilia = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        var br = new Engine(brasilia, TimeSpan.FromMinutes(1), "pc");
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.Zero),
            br.LatestOccurrence(Entry(study, 8), new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Running_activity_and_global_lists_are_locked_for_editing()
    {
        var s = Manual(study, Mon8);
        Assert.Throws<RuleException>(() => Engine.EnsureActivityEditable(s, study.Id));
        Engine.EnsureActivityEditable(s, gym.Id);
        Assert.Throws<RuleException>(() => Engine.EnsureGlobalEditable(s, Mon8.AddMinutes(10)));
        Engine.EnsureGlobalEditable(s, Mon8.AddMinutes(30));
        Engine.EnsureActivityEditable(s with { EndedAt = Mon8.AddMinutes(30) }, study.Id);
    }

    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("  https://www.YouTube.com/watch?v=1 ", "youtube.com")]
    [InlineData("m.youtube.com/feed", "m.youtube.com")]
    [InlineData("localhost", null)]
    [InlineData("não é site", null)]
    [InlineData("", null)]
    public void Sites_are_normalized_to_a_domain(string input, string? expected) =>
        Assert.Equal(expected, Sites.Normalize(input));

    [Theory]
    [InlineData(@"C:\Games\Jogo.exe", true)]                                       // not allowed
    [InlineData(@"c:\program files\microsoft vs code\code.exe", false)]            // allowed, other case
    [InlineData(@"C:\Windows\explorer.exe", false)]                                // shell
    [InlineData(@"C:\Windows\SystemApps\StartMenuExperienceHost.exe", false)]      // system part
    [InlineData(@"C:\Windows\System32\Taskmgr.exe", true)]                         // tool that could undo the block
    [InlineData(@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", true)]     // Settings
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]                            // tool, but allowed
    [InlineData(@"C:\Program Files\Rotina\App\Rotina.App.exe", false)]             // Rotina itself
    public void Only_unlisted_programs_and_risky_windows_tools_are_blocked(string exe, bool blocked)
    {
        string[] allowed = [@"C:\Program Files\Microsoft VS Code\Code.exe", @"C:\WINDOWS\system32\cmd.exe"];
        Assert.Equal(blocked, Blocking.ShouldBlock(exe, allowed, @"C:\Windows\", @"C:\Program Files\Rotina\App\Rotina.App.exe"));
    }

    [Fact]
    public void Phone_session_merges_by_the_rules()
    {
        var pc = Manual(gym, Mon8); // PC: locked until 09:00
        // Phone started something while the PC was locked: rejected.
        Assert.False(Engine.MergeSession(Manual(study, Mon8.AddMinutes(10)), null, pc).Accepted);
        // Phone started after the PC's minimum: accepted, and the PC's one ends right then.
        var later = Manual(study, Mon8.AddMinutes(61));
        var r = Engine.MergeSession(later, null, pc);
        Assert.True(r.Accepted);
        Assert.Equal(Mon8.AddMinutes(61), r.Writes[0].EndedAt);
        Assert.Equal(later.Id, r.Writes[1].Id);
        // Phone started before the PC's one: kept as history, closed when the PC's began.
        var earlier = Manual(study, Mon8.AddMinutes(-40));
        Assert.Equal(Mon8, Engine.MergeSession(earlier, null, pc).Writes.Single().EndedAt);
        // Nothing open on the PC: taken as is.
        Assert.Equal(later, Engine.MergeSession(later, null, null).Writes.Single());
    }

    [Theory]
    [InlineData(null, true)]   // never heard from the extension
    [InlineData(5, false)]
    [InlineData(60, false)]
    [InlineData(61, true)]
    public void Chrome_closes_only_when_the_extension_went_quiet(int? seconds, bool missing) =>
        Assert.Equal(missing, Blocking.ExtensionMissing(seconds));

    [Fact]
    public void Recovery_key_is_checked_by_its_normalized_hash()
    {
        // Same vector as android RecoveryTest: both devices must accept the same key.
        Assert.Equal("DA4E7EA9782C8CB109255854F713C0F231925C812491D35BC39200CFA5C39D0D", Recovery.Hash("abcde-fghjk lmnpq-rstuv"));
        var key = Recovery.NewKey();
        Assert.Matches("^[A-HJ-NP-Z2-9]{5}(-[A-HJ-NP-Z2-9]{5}){3}$", key);
        Assert.True(Recovery.Matches(key.ToLowerInvariant().Replace("-", " "), Recovery.Hash(key)));
        Assert.False(Recovery.Matches("AAAAA-AAAAA-AAAAA-AAAAA", Recovery.Hash(key)));
        Assert.False(Recovery.Matches(key, null));
    }

    [Fact]
    public void Recovery_ends_before_the_minimum_and_the_pc_accepts_it()
    {
        var s = Manual(study, Mon8);
        var recovered = Engine.Recover(s, Mon8.AddMinutes(5));
        Assert.Equal(RuleState.Free, Engine.StateOf(recovered, Mon8.AddMinutes(5)));
        Assert.True(Engine.MergeSession(recovered, s, s).Accepted);
        Assert.Throws<RuleException>(() => Engine.Recover(recovered, Mon8.AddMinutes(6)));
    }

    [Fact]
    public void Phone_end_counts_only_after_the_minimum()
    {
        var s = Manual(study, Mon8);
        Assert.False(Engine.MergeSession(s with { EndedAt = Mon8.AddMinutes(10) }, s, s).Accepted);
        Assert.Equal(Mon8.AddMinutes(31), Engine.MergeSession(s with { EndedAt = Mon8.AddMinutes(31) }, s, s).Writes.Single().EndedAt);
        // Already ended on the PC, or nothing new: no writes.
        Assert.Empty(Engine.MergeSession(s, s, s).Writes);
    }

    [Fact]
    public void Unchanged_schedule_row_is_the_same_slot()
    {
        var e = Entry(study, 8) with { DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Friday] };
        Assert.True(Engine.SameSlot(e, e with { DaysOfWeek = [DayOfWeek.Friday, DayOfWeek.Monday], UpdatedAt = default }));
        Assert.False(Engine.SameSlot(e, e with { StartTime = new TimeOnly(9, 0) }));
        Assert.False(Engine.SameSlot(e, e with { DaysOfWeek = [DayOfWeek.Monday] }));
        Assert.False(Engine.SameSlot(e, e with { Enabled = false }));
    }

    [Fact]
    public void Schedule_warns_when_a_minimum_invades_the_next_start()
    {
        Assert.Single(Engine.ScheduleWarnings([Entry(gym, 8), Entry(study, 8, 30)], Acts));
        Assert.Empty(Engine.ScheduleWarnings([Entry(gym, 8), Entry(study, 9)], Acts));
        Assert.Single(Engine.ScheduleWarnings([Entry(gym, 8), Entry(study, 8)], Acts));
    }
}
