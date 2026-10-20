using Rotina.Core;
using Rotina.Service;

const int Port = 47800;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rotina");
Directory.CreateDirectory(dataDir);
var store = new Store(Path.Combine(dataDir, "rotina.db"));
var settings = store.Get<Settings>("settings", "settings");
if (settings is null) store.Put("settings", "settings", settings = new Settings());
var engine = Engine.For(settings);
var gate = new Lock();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Rotina");
// Localhost only until phase 5 adds the paired-device token for the LAN.
builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(Port));
// Blocks DNS rebinding: a web page whose domain resolves to 127.0.0.1 still sends its own Host and gets 400.
// Set in code, not appsettings.json, because the dev run's content root may not hold that file.
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(Api.Json.Converters[0]));
var app = builder.Build();
var api = app.MapGroup("/api/v1");

api.MapGet("/health", () => Results.Ok(new { ok = true }));

api.MapGet("/state", () => Guarded(() =>
{
    var now = DateTimeOffset.UtcNow;
    var cur = store.CurrentSession();
    var state = Engine.StateOf(cur, now);
    if (cur is null) return Results.Ok(new StateDto(state, null, 0, null, null, settings.Mode));

    var always = AlwaysList();
    List<string> With(List<string> own, Platform p) =>
        [.. own.Concat(always.Where(x => x.Platform == p).Select(x => x.Identifier)).Distinct()];
    var snap = cur.RulesSnapshot;
    var queued = state == RuleState.Locked
        ? engine.Pending(cur, store.All<ScheduleEntry>("schedule"), Activities(), id => store.Exists("session", id), now)
        : null;
    return Results.Ok(new StateDto(state, cur,
        (int)Math.Ceiling(Math.Max(0, (cur.MinEndsAt - now).TotalSeconds)),
        new AllowedLists(With(snap.AllowedAndroidPackages, Platform.Android),
            With(snap.AllowedWindowsExecutables, Platform.Windows),
            With(snap.AllowedSites, Platform.Web)),
        queued?.Activity.Id, settings.Mode));
}));

api.MapGet("/activities", () => Guarded(() =>
    Results.Ok(store.All<Activity>("activity").Where(a => !a.Deleted).OrderBy(a => a.Name))));

api.MapPost("/activities", (Activity a) => Guarded(() => SaveActivity(a with { Id = Guid.NewGuid() })));

api.MapPut("/activities/{id:guid}", (Guid id, Activity a) => Guarded(() =>
{
    if (store.Get<Activity>("activity", id) is not { Deleted: false }) return Results.NotFound();
    Engine.EnsureActivityEditable(store.CurrentSession(), id);
    return SaveActivity(a with { Id = id });
}));

api.MapDelete("/activities/{id:guid}", (Guid id) => Guarded(() =>
{
    if (store.Get<Activity>("activity", id) is not { Deleted: false } a) return Results.NotFound();
    Engine.EnsureActivityEditable(store.CurrentSession(), id);
    var now = DateTimeOffset.UtcNow;
    store.Atomically(() =>
    {
        store.Put("activity", id, a with { Deleted = true, UpdatedAt = now });
        foreach (var e in store.All<ScheduleEntry>("schedule").Where(e => e.ActivityId == id && !e.Deleted))
            store.Put("schedule", e.Id, e with { Deleted = true, UpdatedAt = now });
    });
    return Results.NoContent();
}));

api.MapGet("/schedule", () => Guarded(() =>
    Results.Ok(store.All<ScheduleEntry>("schedule").Where(e => !e.Deleted))));

api.MapPut("/schedule", (List<ScheduleEntry> incoming) => Guarded(() =>
{
    var acts = Activities();
    if (incoming.Any(e => !acts.TryGetValue(e.ActivityId, out var a) || a.Deleted || e.DaysOfWeek is not { Count: > 0 }))
        return Results.BadRequest(new { error = "Cada horário precisa de uma atividade existente e de pelo menos um dia." });
    incoming = [.. incoming.Select(e => e.Id == Guid.Empty ? e with { Id = Guid.NewGuid() } : e)];
    var now = DateTimeOffset.UtcNow;
    var existing = store.All<ScheduleEntry>("schedule").Where(e => !e.Deleted).ToDictionary(e => e.Id);
    store.Atomically(() =>
    {
        foreach (var e in incoming)
        {
            // Untouched rows keep their UpdatedAt, otherwise a queued start would be dropped (see Engine.Pending).
            if (existing.TryGetValue(e.Id, out var old) && Engine.SameSlot(old, e)) continue;
            store.Put("schedule", e.Id, e with { UpdatedAt = now, Deleted = false });
        }
        foreach (var old in existing.Values.Where(o => incoming.All(e => e.Id != o.Id)))
            store.Put("schedule", old.Id, old with { Deleted = true, UpdatedAt = now });
    });
    var saved = store.All<ScheduleEntry>("schedule").Where(e => !e.Deleted).ToList();
    return Results.Ok(new ScheduleSaveResult(saved, Engine.ScheduleWarnings(saved, acts)));
}));

api.MapPost("/sessions/start", (StartRequest req) => Guarded(() =>
{
    if (store.Get<Activity>("activity", req.ActivityId) is not { Deleted: false } a) return Results.NotFound();
    var started = engine.StartManual(store.CurrentSession(), a, DateTimeOffset.UtcNow, out var ended);
    Apply(ended, started);
    return Results.Ok(started);
}));

api.MapPost("/sessions/{id:guid}/end", (Guid id) => Guarded(() =>
{
    if (store.CurrentSession() is not { } cur || cur.Id != id) return Results.NotFound();
    var ended = Engine.End(cur, DateTimeOffset.UtcNow);
    Apply(ended, null);
    return Results.Ok(ended);
}));

api.MapGet("/always-allowed", () => Guarded(() => Results.Ok(AlwaysList())));

api.MapPut("/always-allowed", (List<AlwaysAllowed> list) => Guarded(() =>
{
    Engine.EnsureGlobalEditable(store.CurrentSession(), DateTimeOffset.UtcNow);
    list = [.. list.Where(x => !string.IsNullOrWhiteSpace(x.Identifier)).Distinct()];
    store.Put("alwaysAllowed", "all", list);
    return Results.Ok(list);
}));

app.Lifetime.ApplicationStarted.Register(() => _ = RunScheduler(app.Lifetime.ApplicationStopping));
app.Run();

async Task RunScheduler(CancellationToken stop)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    try
    {
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                lock (gate)
                {
                    var r = engine.Tick(store.CurrentSession(), store.All<ScheduleEntry>("schedule"), Activities(),
                        id => store.Exists("session", id), DateTimeOffset.UtcNow);
                    Apply(r.Ended, r.Started);
                }
            }
            catch (Exception e) { app.Logger.LogError(e, "Falha no ciclo da agenda"); }
        }
    }
    catch (OperationCanceledException) { }
}

IResult Guarded(Func<IResult> action)
{
    lock (gate)
    {
        try { return action(); }
        catch (RuleException e) { return Results.Conflict(new { error = e.Message }); }
    }
}

IResult SaveActivity(Activity a)
{
    if (string.IsNullOrWhiteSpace(a.Name) || a.MinDurationMinutes < 1)
        return Results.BadRequest(new { error = "Informe o nome e um tempo mínimo de pelo menos 1 minuto." });
    a = a with
    {
        Name = a.Name.Trim(),
        AllowedAndroidPackages = Clean(a.AllowedAndroidPackages),
        AllowedWindowsExecutables = Clean(a.AllowedWindowsExecutables),
        AllowedSites = Clean(a.AllowedSites?.Select(s => Sites.Normalize(s) ?? "").ToList()),
        UpdatedAt = DateTimeOffset.UtcNow,
        Deleted = false,
    };
    store.Put("activity", a.Id, a);
    return Results.Ok(a);
}

void Apply(Session? ended, Session? started)
{
    if (ended is null && started is null) return;
    store.Atomically(() =>
    {
        if (ended is not null) Record(ended, SessionEventType.End, ended.EndedAt!.Value);
        if (started is not null) Record(started, SessionEventType.Start, started.StartedAt);
    });
}

void Record(Session s, SessionEventType type, DateTimeOffset at)
{
    store.Put("session", s.Id, s);
    var ev = new SessionEvent { SessionId = s.Id, Type = type, At = at, DeviceId = settings.DeviceId };
    store.Put("sessionEvent", ev.Id, ev);
}

Dictionary<Guid, Activity> Activities() => store.All<Activity>("activity").ToDictionary(a => a.Id);
List<AlwaysAllowed> AlwaysList() => store.Get<List<AlwaysAllowed>>("alwaysAllowed", "all") ?? [];
static List<string> Clean(List<string>? list) => [.. (list ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct()];
