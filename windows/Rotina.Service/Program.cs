using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rotina.Core;
using Rotina.Service;

const int Port = 47800;

// ROTINA_DATA_DIR: tests run against a throwaway database instead of the real one.
var dataDir = Environment.GetEnvironmentVariable("ROTINA_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rotina");
Directory.CreateDirectory(dataDir);
var store = new Store(Path.Combine(dataDir, "rotina.db"));
var settings = store.Get<Settings>("settings", "settings");
if (settings is null) store.Put("settings", "settings", settings = new Settings());
var engine = Engine.For(settings);
var gate = new Lock();
DateTimeOffset? extensionSeen = null; // last heartbeat from the Chrome extension (memory only)

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Rotina");
// The phone reaches the PC over the local network; see the access check below.
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(Port));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(Api.Json.Converters[0]));
var app = builder.Build();

// Access rules:
// - From this PC (the app, the Chrome extension): no token, but the Host must be localhost. That blocks DNS rebinding,
//   where a web page whose domain resolves to 127.0.0.1 still sends its own Host.
// - From the network (the phone): the pairing token is required, and pairing itself is never reachable.
app.Use(async (ctx, next) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    var fromThisPc = remote is null || IPAddress.IsLoopback(remote);
    var phone = TokenDevice(ctx.Request.Headers.Authorization.ToString());
    if (fromThisPc ? !IsLocalHostName(ctx.Request.Host.Host) && phone is null
                   : phone is null || LocalOnly(ctx.Request.Path))
    {
        ctx.Response.StatusCode = fromThisPc ? StatusCodes.Status400BadRequest : StatusCodes.Status401Unauthorized;
        return;
    }
    if (phone is not null) TouchDevice(phone);
    await next();
});

var api = app.MapGroup("/api/v1");

api.MapGet("/health", () => Results.Ok(new { ok = true }));

api.MapGet("/state", () => Guarded(() =>
{
    var now = DateTimeOffset.UtcNow;
    var cur = store.CurrentSession();
    var state = Engine.StateOf(cur, now);
    int? extensionAge = extensionSeen is { } seen ? (int)(now - seen).TotalSeconds : null;
    if (cur is null) return Results.Ok(new StateDto(state, null, 0, null, null, settings.Mode, extensionAge));

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
        queued?.Activity.Id, settings.Mode, extensionAge));
}));

// The Chrome extension calls this every few seconds; the desktop app closes Chrome if it stops during an activity.
api.MapPost("/heartbeat/extension", () =>
{
    extensionSeen = DateTimeOffset.UtcNow;
    return Results.NoContent();
});

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

// ---- Recovery key and mode ----

api.MapGet("/settings", () => Guarded(() =>
    Results.Ok(new SettingsDto(settings.Mode, settings.RecoveryKeyHash is not null, settings.RecoveryConfirmed))));

api.MapPost("/recovery/setup", (RecoveryRequest? current) => Guarded(() =>
{
    Engine.EnsureGlobalEditable(store.CurrentSession(), DateTimeOffset.UtcNow);
    // Replacing a key needs the current one, or a "spare" key could be made while free and kept for later.
    if (settings.RecoveryKeyHash is not null && !Recovery.Matches(current?.Key ?? "", settings.RecoveryKeyHash))
        return Results.Json(new { error = "Para gerar uma chave nova, digite a chave atual." }, statusCode: StatusCodes.Status403Forbidden);
    var key = Recovery.NewKey();
    SaveSettings(settings with { RecoveryKeyHash = Recovery.Hash(key), RecoveryConfirmed = false });
    return Results.Ok(new RecoveryKey(key));
}));

api.MapPost("/recovery/confirm", () => Guarded(() =>
{
    if (settings.RecoveryKeyHash is null) return Results.Conflict(new { error = "Gere a chave de recuperação primeiro." });
    SaveSettings(settings with { RecoveryConfirmed = true });
    return Results.NoContent();
}));

api.MapPut("/settings/mode", (ModeRequest req) => Guarded(() =>
{
    Engine.EnsureGlobalEditable(store.CurrentSession(), DateTimeOffset.UtcNow);
    if (req.Mode == AppMode.Production && !settings.RecoveryConfirmed)
        return Results.Conflict(new { error = "Antes do modo produção, gere a chave de recuperação e confirme que ela está guardada." });
    // Development reads minimums as seconds, so leaving production needs the key.
    if (req.Mode == AppMode.Development && settings.Mode == AppMode.Production && !Recovery.Matches(req.Key ?? "", settings.RecoveryKeyHash))
        return Results.Json(new { error = "Para voltar ao modo desenvolvimento, digite a chave de recuperação." }, statusCode: StatusCodes.Status403Forbidden);
    SaveSettings(settings with { Mode = req.Mode });
    return Results.NoContent();
}));

// From this PC or the paired phone: ends the running activity now, even before its minimum.
api.MapPost("/recovery", (RecoveryRequest req) => Guarded(() =>
{
    if (!Recovery.Matches(req.Key, settings.RecoveryKeyHash))
        return Results.Json(new { error = "Chave de recuperação incorreta." }, statusCode: StatusCodes.Status403Forbidden);
    if (store.CurrentSession() is { } cur) Apply(Engine.Recover(cur, DateTimeOffset.UtcNow), null);
    return Results.NoContent();
}));

// ---- Phone pairing and sync ----

api.MapPost("/pairing", () => Guarded(() =>
{
    // A new code replaces the old one: the previous token stops working right away.
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    store.Put("device", "phone", new Device("phone", "Celular", Hash(token), null));
    var host = LanAddress() ?? "127.0.0.1";
    return Results.Ok(new PairingCode(host, Port, token, $"rotina://pair?h={host}&p={Port}&t={token}"));
}));

api.MapGet("/pairing/status", () => Guarded(() =>
{
    var d = store.Get<Device>("device", "phone");
    return Results.Ok(new PairingStatus(d is not null, d?.LastSeenAt));
}));

api.MapGet("/sync", (long? since) => Guarded(() =>
{
    var rev = since ?? 0;
    return Results.Ok(new SyncPull(store.MaxRev(), settings.Mode,
        store.Since<Activity>("activity", rev), store.Since<ScheduleEntry>("schedule", rev),
        store.Since<List<AlwaysAllowed>>("alwaysAllowed", rev).FirstOrDefault(),
        store.Since<Session>("session", rev), settings.RecoveryKeyHash,
        // Same zone on both devices, or "08:00" would mean different moments and scheduled session ids would differ.
        TimeZoneInfo.TryConvertWindowsIdToIanaId(settings.TimeZoneId, out var iana) ? iana : settings.TimeZoneId));
}));

api.MapPost("/sync", (SyncPush push) => Guarded(() =>
{
    var accepted = new List<Guid>();
    var rejected = new List<Guid>();
    store.Atomically(() =>
    {
        // In time order, each against the PC's state after the previous one.
        foreach (var s in push.Sessions.OrderBy(s => s.StartedAt))
        {
            var r = Engine.MergeSession(s, store.Get<Session>("session", s.Id), store.CurrentSession());
            (r.Accepted ? accepted : rejected).Add(s.Id);
            foreach (var w in r.Writes)
                Record(w, w.EndedAt is null ? SessionEventType.Start : SessionEventType.End, w.EndedAt ?? w.StartedAt, s.DeviceId);
        }
    });
    return Results.Ok(new SyncPushResult(accepted, rejected));
}));

api.MapPost("/catalog/android", (List<CatalogItem> items) => Guarded(() =>
{
    store.Put("catalog", "android", items.Where(i => !string.IsNullOrWhiteSpace(i.Identifier)).Take(5000).ToList());
    return Results.NoContent();
}));

api.MapGet("/catalog/android", () => Guarded(() =>
    Results.Ok(store.Get<List<CatalogItem>>("catalog", "android") ?? [])));

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
                bool active;
                lock (gate)
                {
                    var r = engine.Tick(store.CurrentSession(), store.All<ScheduleEntry>("schedule"), Activities(),
                        id => store.Exists("session", id), DateTimeOffset.UtcNow);
                    Apply(r.Ended, r.Started);
                    active = store.CurrentSession() is not null;
                }
                // The app does the blocking; outside activities the service leaves the user alone.
                if (active) AgentLauncher.EnsureRunning(app.Logger);
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

void Record(Session s, SessionEventType type, DateTimeOffset at, string? deviceId = null)
{
    store.Put("session", s.Id, s);
    var ev = new SessionEvent { SessionId = s.Id, Type = type, At = at, DeviceId = deviceId ?? settings.DeviceId };
    store.Put("sessionEvent", ev.Id, ev);
}

static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

static bool IsLocalHostName(string host) => host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

// Pairing, the recovery key setup and the mode are changed only on this PC, never over the network.
static bool LocalOnly(PathString path) =>
    path.StartsWithSegments("/api/v1/pairing") || path.StartsWithSegments("/api/v1/settings")
    || path.StartsWithSegments("/api/v1/recovery/setup") || path.StartsWithSegments("/api/v1/recovery/confirm")
    || path.StartsWithSegments("/api/v1/heartbeat");

void SaveSettings(Settings s)
{
    settings = s;
    store.Put("settings", "settings", s);
    engine = Engine.For(s); // the mode decides whether minimums are seconds or minutes
}

/// <summary>The paired phone when the header carries its token, otherwise null.</summary>
Device? TokenDevice(string authorization)
{
    if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
    Device? d;
    lock (gate) d = store.Get<Device>("device", "phone");
    if (d is null) return null;
    var given = Encoding.ASCII.GetBytes(Hash(authorization[7..].Trim()));
    return CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(d.TokenHash)) ? d : null;
}

void TouchDevice(Device d)
{
    // At most every 20 s: every write bumps the sync revision.
    if (d.LastSeenAt is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(20)) return;
    lock (gate) store.Put("device", d.Id, d with { LastSeenAt = DateTimeOffset.UtcNow });
}

/// <summary>This PC's IPv4 on the network with a default gateway (the home Wi-Fi or cable), for the pairing code.</summary>
static string? LanAddress() => NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .Select(n => n.GetIPProperties())
    .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
    .SelectMany(p => p.UnicastAddresses)
    .Select(a => a.Address)
    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();

Dictionary<Guid, Activity> Activities() => store.All<Activity>("activity").ToDictionary(a => a.Id);
List<AlwaysAllowed> AlwaysList() => store.Get<List<AlwaysAllowed>>("alwaysAllowed", "all") ?? [];
static List<string> Clean(List<string>? list) => [.. (list ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct()];
