using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rotina.Core;

namespace Rotina.App;

public sealed record Swatch(string Hex, string Name);
public sealed record TodayItem(string Time, string Name, string Color);

public partial class MainWindow : Window
{
    static readonly Swatch[] Palette =
    [
        new("#0F766E", "Verde-azulado"), new("#1D4ED8", "Azul"), new("#6D28D9", "Roxo"),
        new("#BE185D", "Rosa"), new("#B91C1C", "Vermelho"), new("#C2410C", "Laranja"),
        new("#A16207", "Mostarda"), new("#15803D", "Verde"), new("#334155", "Grafite"),
    ];

    readonly HttpClient http = new() { BaseAddress = new Uri(Api.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
    readonly ObservableCollection<SlotRow> slots = [];
    List<Activity> activities = [];
    Guid? editingId;
    StateDto? state;
    bool online, refreshing, busy;
    int ticks;
    List<ScheduleEntry> schedule = [];
    readonly HashSet<string> warned = [];

    /// <summary>Windows notification 5 minutes before a scheduled start, once per occurrence.</summary>
    void WarnBeforeStart()
    {
        var now = DateTime.Now;
        foreach (var e in schedule.Where(e => e.Enabled && !e.Deleted && e.DaysOfWeek.Contains(now.DayOfWeek)))
        {
            var left = now.Date + e.StartTime.ToTimeSpan() - now;
            if (left <= TimeSpan.Zero || left > TimeSpan.FromMinutes(5) || !warned.Add($"{e.Id}:{now:yyyy-MM-dd}")) continue;
            var name = activities.FirstOrDefault(a => a.Id == e.ActivityId)?.Name ?? "Uma atividade";
            tray.ShowBalloonTip(10_000, $"{name} começa em 5 minutos", "Salve o que estiver fazendo.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }
    DateTime todayShown;

    readonly System.Windows.Forms.NotifyIcon tray = new() { Visible = true };
    string? trayColor;
    bool exiting;

    public MainWindow()
    {
        InitializeComponent();
        ScheduleGrid.ItemsSource = slots;
        ShowActivity(null);
        _ = AppCatalog.All(); // warm the app list so the picker opens fast

        tray.Icon = TrayIcon.App();
        trayColor = "app";
        tray.Text = "Rotina";
        tray.DoubleClick += (_, _) => ShowFromTray();
        tray.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Abrir Rotina", null, (_, _) => ShowFromTray());
        tray.ContextMenuStrip.Items.Add("Sair", null, (_, _) => Exit());
        Application.Current.SessionEnding += (_, _) => Exit();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await RefreshState();
        timer.Start();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // Closing the window keeps Rotina running in the tray; blocking must not depend on the window being open.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!exiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    void Exit()
    {
        exiting = true;
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    void UpdateTray(StateDto? s)
    {
        var snap = s?.Session?.RulesSnapshot;
        var text = s?.State switch
        {
            null => "Rotina · serviço parado",
            RuleState.Free => "Rotina · Livre",
            RuleState.Locked => $"Rotina · {snap!.Name} · faltam {Clock(s.SecondsUntilCompletable)}",
            _ => $"Rotina · {snap!.Name} · pode encerrar",
        };
        tray.Text = text.Length > 127 ? text[..127] : text;
        // Free: the app's own icon. During an activity: a dot in the activity's color.
        var color = snap?.Color ?? "app";
        if (color == trayColor) return;
        trayColor = color;
        var old = tray.Icon;
        tray.Icon = snap is null ? TrayIcon.App() : TrayIcon.Create(color);
        old?.Dispose();
    }

    void EnforceBlocking(StateDto s)
    {
        if (s.State == RuleState.Free || s.Allowed is null || s.Session is null) return;
        var extensionMissing = Blocking.ExtensionMissing(s.ExtensionSeenSecondsAgo);
        foreach (var b in Blocker.Enforce(s.Allowed.Windows, extensionMissing))
        {
            var name = AppCatalog.Describe(b.Path).Name;
            var activity = s.Session.RulesSnapshot;
            var wait = s.State == RuleState.Locked
                ? $"Faltam {Clock(s.SecondsUntilCompletable)} para poder encerrar."
                : "Você já pode encerrar a atividade no Rotina.";
            // Chrome is allowed but was closed because its extension is off.
            var chromeOnly = extensionMissing && s.Allowed.Windows.Contains(b.Path, StringComparer.OrdinalIgnoreCase);
            BlockToast.Notify(
                b.Closed ? $"{name} foi fechado" : $"{name} não está liberado",
                chromeOnly ? $"A extensão do Rotina está desativada. Reative em chrome://extensions para usar o Chrome em {activity.Name}."
                : b.Closed ? $"Não está liberado em {activity.Name}. {wait}"
                : $"Não está liberado em {activity.Name}, mas roda como administrador e não pôde ser fechado.",
                ColorBrush(activity.Color));
        }
    }

    // ---- State (home screen) ----

    async Task RefreshState()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var (ok, s) = await Send<StateDto>(HttpMethod.Get, "state");
            if (!ok || s is null)
            {
                online = false;
                ShowState("SEM CONEXÃO", "O serviço Rotina não está rodando", null,
                    "Inicie o Rotina Service. Enquanto isso, nada pode ser editado.", null);
                EndButton.Visibility = Visibility.Collapsed;
                StartList.IsEnabled = false;
                UpdateTray(null);
                return;
            }
            EnforceBlocking(s); // first, so a blocked app gets no extra second while the UI updates
            if (!online) { online = true; await ReloadAll(); }
            state = s;
            UpdateTray(s);
            var snap = s.Session?.RulesSnapshot;
            var queued = activities.FirstOrDefault(a => a.Id == s.QueuedActivityId)?.Name;
            switch (s.State)
            {
                case RuleState.Free:
                    ShowState("AGORA", "Livre", null, "Nenhuma atividade em andamento. Tudo está liberado.", null);
                    break;
                case RuleState.Locked:
                    ShowState("EM ANDAMENTO", snap!.Name, Clock(s.SecondsUntilCompletable),
                        "para poder encerrar" + (queued is null ? "" : $" · depois começa {queued}"), snap.Color);
                    break;
                default:
                    ShowState("EM ANDAMENTO", snap!.Name, null,
                        "Tempo mínimo cumprido. Você já pode encerrar ou trocar de atividade.", snap.Color);
                    break;
            }
            EndButton.Visibility = s.State == RuleState.Completable ? Visibility.Visible : Visibility.Collapsed;
            StartList.IsEnabled = s.State != RuleState.Locked;
            StartHint.Text = s.State == RuleState.Locked
                ? "Disponível quando a atividade atual atingir o tempo mínimo."
                : activities.Count == 0 ? "Nenhuma atividade ainda. Crie uma na aba Atividades." : "Escolha uma atividade para começar agora.";
            DevNote.Visibility = s.Mode == AppMode.Development ? Visibility.Visible : Visibility.Collapsed;
            if (DateTime.Today != todayShown) ShowToday(); // app left open past midnight
            WarnBeforeStart();
            UpdateRunningLock();
            if (++ticks % 5 == 1) { await RefreshPairing(); await RefreshSecurity(); }
        }
        finally { refreshing = false; }
    }

    void ShowState(string label, string title, string? clock, string detail, string? color)
    {
        StateLabel.Text = label;
        StateTitle.Text = title;
        StateClock.Text = clock ?? "";
        StateClock.Visibility = clock is null ? Visibility.Collapsed : Visibility.Visible;
        StateDetail.Text = detail;
        StateAccent.Background = color is null ? (Brush)FindResource("LineBrush") : ColorBrush(color);
    }

    // ---- Phone ----

    // ---- Security: recovery key and mode ----

    async Task RefreshSecurity()
    {
        var (ok, s) = await Send<SettingsDto>(HttpMethod.Get, "settings");
        if (!ok || s is null) return;
        KeyStatus.Text = !s.RecoveryKeySet ? "Nenhuma chave gerada ainda. Gere uma antes de ligar o modo produção."
            : s.RecoveryConfirmed ? "Chave gerada e entregue."
            : "Chave gerada, mas você ainda não confirmou que a entregou.";
        GenerateKeyButton.Visibility = s.RecoveryKeySet ? Visibility.Collapsed : Visibility.Visible;
        var prod = s.Mode == AppMode.Production;
        ModeStatus.Text = prod
            ? "Produção: os tempos mínimos contam em minutos e as proteções estão ligadas."
            : "Desenvolvimento: os tempos mínimos contam em segundos. Ligue a produção quando terminar de testar.";
        ProductionButton.Visibility = prod ? Visibility.Collapsed : Visibility.Visible;
        ProductionButton.IsEnabled = s.RecoveryConfirmed;
        ProductionButton.ToolTip = s.RecoveryConfirmed ? null : "Gere a chave e confirme que ela foi entregue";
        DevModeButton.Visibility = prod ? Visibility.Visible : Visibility.Collapsed;
        ReplaceKeyButton.Visibility = s.RecoveryKeySet ? Visibility.Visible : Visibility.Collapsed;
    }

    async void GenerateKey_Click(object sender, RoutedEventArgs e) => await NewKey(GenerateKeyButton, "");

    async void ReplaceKey_Click(object sender, RoutedEventArgs e) => await NewKey(ReplaceKeyButton, UseKeyBox.Text);

    async Task NewKey(Button button, string current)
    {
        var (ok, k) = await Busy(button, () => Send<RecoveryKey>(HttpMethod.Post, "recovery/setup", new RecoveryRequest(current)));
        if (!ok || k is null) return;
        NewKeyBox.Text = k.Key;
        NewKeyPanel.Visibility = Visibility.Visible;
        UseKeyBox.Clear();
        await RefreshSecurity();
    }

    async void ConfirmKey_Click(object sender, RoutedEventArgs e)
    {
        if (!(await Busy(ConfirmKeyButton, () => Send<object>(HttpMethod.Post, "recovery/confirm", new { }))).Ok) return;
        NewKeyBox.Clear(); // the key leaves the screen once it is safe elsewhere
        NewKeyPanel.Visibility = Visibility.Collapsed;
        await RefreshSecurity();
    }

    async void Production_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "No modo produção os tempos mínimos passam a contar em minutos e só a chave de recuperação encerra uma atividade antes do tempo. Ligar agora?",
                "Rotina", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Busy(ProductionButton, () => Send<object>(HttpMethod.Put, "settings/mode", new ModeRequest(AppMode.Production, null)));
        await RefreshSecurity();
    }

    async void DevMode_Click(object sender, RoutedEventArgs e)
    {
        await Busy(DevModeButton, () => Send<object>(HttpMethod.Put, "settings/mode", new ModeRequest(AppMode.Development, UseKeyBox.Text)));
        UseKeyBox.Clear();
        await RefreshSecurity();
    }

    async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Encerrar a atividade atual agora, no PC e no celular?", "Rotina",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if ((await Busy(RecoverButton, () => Send<object>(HttpMethod.Post, "recovery", new RecoveryRequest(UseKeyBox.Text)))).Ok)
        {
            UseKeyBox.Clear();
            MessageBox.Show(this, "Atividade encerrada. A agenda volta a valer na próxima atividade.", "Rotina");
        }
        await RefreshState();
    }

    async Task RefreshPairing()
    {
        var (ok, p) = await Send<PairingStatus>(HttpMethod.Get, "pairing/status");
        if (!ok || p is null) return;
        PairStatus.Text = p.Paired ? "Celular pareado" : "Nenhum celular pareado";
        PairDetail.Text = !p.Paired ? "Gere um código e leia no celular, em Configuração."
            : p.LastSeenAt is { } seen ? $"Último contato: {seen.ToLocalTime():dd/MM HH:mm}"
            : "Ainda sem contato. Abra o Rotina no celular, no mesmo Wi-Fi.";
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var (ok, code) = await Busy(PairButton, () => Send<PairingCode>(HttpMethod.Post, "pairing"));
        if (!ok || code is null) return;
        var png = new QRCoder.PngByteQRCode(new QRCoder.QRCodeGenerator().CreateQrCode(code.Code, QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(10);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.StreamSource = new System.IO.MemoryStream(png);
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.EndInit();
        PairQr.Source = image;
        PairCode.Text = code.Code;
        PairPanel.Visibility = Visibility.Visible;
        await RefreshPairing();
    }

    async Task<List<WinApp>> LoadAndroidCatalog()
    {
        var items = (await Send<List<CatalogItem>>(HttpMethod.Get, "catalog/android")).Value ?? [];
        ChipEditor.AndroidLabels = items.GroupBy(i => i.Identifier).ToDictionary(g => g.Key, g => g.First().Label);
        return [.. items.OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase).Select(i => new WinApp(i.Label, i.Identifier, null))];
    }

    async Task ReloadAll()
    {
        ChipEditor.AndroidCatalog = LoadAndroidCatalog;
        await LoadAndroidCatalog(); // fills the labels the Android chips show
        activities = (await Send<List<Activity>>(HttpMethod.Get, "activities")).Value ?? [];
        ActivityList.ItemsSource = activities;
        StartList.ItemsSource = activities;
        ActivityColumn.ItemsSource = activities;
        ActivitiesEmpty.Visibility = activities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSlots((await Send<List<ScheduleEntry>>(HttpMethod.Get, "schedule")).Value ?? []);
        var always = (await Send<List<AlwaysAllowed>>(HttpMethod.Get, "always-allowed")).Value ?? [];
        AlwaysWinChips.SetValues(always.Where(x => x.Platform == Platform.Windows).Select(x => x.Identifier));
        AlwaysSiteChips.SetValues(always.Where(x => x.Platform == Platform.Web).Select(x => x.Identifier));
        AlwaysAndroidChips.SetValues(always.Where(x => x.Platform == Platform.Android).Select(x => x.Identifier));
        var current = activities.FirstOrDefault(a => a.Id == editingId);
        ActivityList.SelectedItem = current;
        ShowActivity(current);
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Activity a) return;
        if (state is { State: RuleState.Completable, Session: { } cur } &&
            MessageBox.Show(this, $"Isso encerra {cur.RulesSnapshot.Name} e inicia {a.Name}. Continuar?", "Rotina",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Busy((Button)sender, () => Send<Session>(HttpMethod.Post, "sessions/start", new StartRequest(a.Id)));
        await RefreshState();
    }

    async void End_Click(object sender, RoutedEventArgs e)
    {
        if (state?.Session is not { } s) return;
        await Busy(EndButton, () => Send<Session>(HttpMethod.Post, $"sessions/{s.Id}/end"));
        await RefreshState();
    }

    // ---- Activities ----

    void ActivityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityList.SelectedItem is Activity a) ShowActivity(a);
    }

    void NewActivity_Click(object sender, RoutedEventArgs e)
    {
        ActivityList.SelectedItem = null;
        ShowActivity(null);
        NameBox.Focus();
    }

    async void SaveActivity_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { Warn("Dê um nome para a atividade."); NameBox.Focus(); return; }
        if (!int.TryParse(MinBox.Text, out var min) || min < 1) { Warn("O tempo mínimo precisa ser um número de minutos, 1 ou mais."); MinBox.Focus(); return; }
        var a = new Activity
        {
            Name = NameBox.Text,
            Color = ColorList.SelectedValue as string ?? Palette[0].Hex,
            MinDurationMinutes = min,
            AllowedWindowsExecutables = WinChips.Values,
            AllowedAndroidPackages = AndroidChips.Values,
            AllowedSites = SiteChips.Values,
        };
        var (ok, saved) = await Busy(SaveActivityButton, () => editingId is { } id
            ? Send<Activity>(HttpMethod.Put, $"activities/{id}", a)
            : Send<Activity>(HttpMethod.Post, "activities", a));
        if (!ok) return;
        editingId = saved!.Id;
        await ReloadAll();
        ActivityStatus.Text = $"Salvo às {DateTime.Now:HH:mm}";
    }

    async void DeleteActivity_Click(object sender, RoutedEventArgs e)
    {
        if (editingId is not { } id) return;
        if (MessageBox.Show(this, $"Excluir a atividade {NameBox.Text}? Os horários dela na agenda também serão removidos.",
                "Rotina", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!(await Busy(DeleteButton, () => Send<object>(HttpMethod.Delete, $"activities/{id}"))).Ok) return;
        editingId = null;
        await ReloadAll();
    }

    void ShowActivity(Activity? a)
    {
        editingId = a?.Id;
        FormTitle.Text = a is null ? "Nova atividade" : "Editar atividade";
        NameBox.Text = a?.Name ?? "";
        var color = a?.Color ?? Palette[0].Hex;
        // Colors saved before the palette existed still show, as an extra swatch.
        ColorList.ItemsSource = Palette.Any(p => p.Hex.Equals(color, StringComparison.OrdinalIgnoreCase))
            ? Palette : [.. Palette, new Swatch(color, "Cor atual")];
        ColorList.SelectedValue = ((IEnumerable<Swatch>)ColorList.ItemsSource)
            .First(p => p.Hex.Equals(color, StringComparison.OrdinalIgnoreCase)).Hex;
        MinBox.Text = (a?.MinDurationMinutes ?? 30).ToString(CultureInfo.InvariantCulture);
        WinChips.SetValues(a?.AllowedWindowsExecutables ?? []);
        SiteChips.SetValues(a?.AllowedSites ?? []);
        AndroidChips.SetValues(a?.AllowedAndroidPackages ?? []);
        DeleteButton.Visibility = a is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateRunningLock();
        ActivityStatus.Text = "";
    }

    // The running activity can't be edited (the service refuses too); say so and disable saving.
    void UpdateRunningLock()
    {
        var running = editingId is { } id && state?.Session is { EndedAt: null } run && run.ActivityId == id;
        RunningNote.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        SaveActivityButton.IsEnabled = DeleteButton.IsEnabled = !running && !busy;
    }

    // ---- Schedule ----

    void AddSlot_Click(object sender, RoutedEventArgs e)
    {
        if (activities.Count == 0) { Warn("Crie uma atividade antes de montar a agenda."); return; }
        var row = new SlotRow { ActivityId = activities[0].Id };
        slots.Add(row);
        ScheduleGrid.SelectedItem = row;
        ScheduleGrid.ScrollIntoView(row);
    }

    void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleGrid.SelectedItem is SlotRow r) slots.Remove(r);
        else Warn("Selecione uma linha da agenda para remover.");
    }

    async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        ScheduleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var entries = new List<ScheduleEntry>();
        foreach (var r in slots)
        {
            if (!TimeOnly.TryParseExact(r.Time.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                Warn($"Hora inválida: \"{r.Time}\". Use o formato HH:mm, por exemplo 08:30.");
                return;
            }
            entries.Add(r.ToEntry(t));
        }
        var (ok, res) = await Busy(SaveScheduleButton, () => Send<ScheduleSaveResult>(HttpMethod.Put, "schedule", entries));
        if (!ok) return;
        ShowSlots(res!.Entries);
        WarningsText.Text = string.Join(Environment.NewLine, res.Warnings);
        WarningsBox.Visibility = res.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleStatus.Text = $"Salvo às {DateTime.Now:HH:mm}";
    }

    void ShowSlots(IEnumerable<ScheduleEntry> entries)
    {
        slots.Clear();
        foreach (var e in entries.OrderBy(e => e.StartTime)) slots.Add(SlotRow.From(e));
        schedule = [.. entries];
        ShowToday();
    }

    // Built from the saved schedule (not the grid), so unsaved grid edits never show here.
    void ShowToday()
    {
        todayShown = DateTime.Today;
        var today = todayShown.DayOfWeek;
        var items = (from e in schedule
                     where e.Enabled && e.DaysOfWeek.Contains(today)
                     let a = activities.FirstOrDefault(a => a.Id == e.ActivityId)
                     where a is not null
                     orderby e.StartTime
                     select new TodayItem(e.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture), a.Name, a.Color)).ToList();
        TodayList.ItemsSource = items;
        TodayEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Always allowed ----

    async void SaveAlways_Click(object sender, RoutedEventArgs e)
    {
        var list = AlwaysWinChips.Values.Select(x => new AlwaysAllowed(Platform.Windows, x))
            .Concat(AlwaysAndroidChips.Values.Select(x => new AlwaysAllowed(Platform.Android, x)))
            .Concat(AlwaysSiteChips.Values.Select(x => new AlwaysAllowed(Platform.Web, x)))
            .ToList();
        if ((await Busy(SaveAlwaysButton, () => Send<List<AlwaysAllowed>>(HttpMethod.Put, "always-allowed", list))).Ok)
            AlwaysStatus.Text = $"Salvo às {DateTime.Now:HH:mm}";
    }

    // ---- Helpers ----

    /// <summary>Disables the button while the request runs, so a double click never sends twice.</summary>
    async Task<T> Busy<T>(Button button, Func<Task<T>> action)
    {
        button.IsEnabled = false;
        busy = true; // keeps the 1 s refresh from re-enabling Save/Delete mid-request
        try { return await action(); }
        finally { busy = false; button.IsEnabled = true; }
    }

    /// <summary>GETs fail silently (the home card shows "offline"); writes show the service's error message.</summary>
    async Task<(bool Ok, T? Value)> Send<T>(HttpMethod method, string path, object? body = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = JsonContent.Create(body, options: Api.Json);
            using var res = await http.SendAsync(req);
            if (res.IsSuccessStatusCode)
                return (true, res.StatusCode == HttpStatusCode.NoContent ? default : await res.Content.ReadFromJsonAsync<T>(Api.Json));
            if (method != HttpMethod.Get) Warn(await ErrorText(res));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (method != HttpMethod.Get) Warn("O serviço Rotina não está rodando.");
        }
        return (false, default);
    }

    static async Task<string> ErrorText(HttpResponseMessage res)
    {
        if (res.StatusCode == HttpStatusCode.NotFound) return "Não encontrado. Atualize a tela e tente de novo.";
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var msg)) return msg.GetString() ?? "";
        }
        catch (JsonException) { }
        return $"Erro {(int)res.StatusCode} no serviço.";
    }

    void Warn(string text) => MessageBox.Show(this, text, "Rotina", MessageBoxButton.OK, MessageBoxImage.Warning);

    static string Clock(int seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    static Brush ColorBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return Brushes.Gray; }
    }
}

/// <summary>One editable row of the weekly schedule grid.</summary>
public sealed class SlotRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActivityId { get; set; }
    public string Time { get; set; } = "08:00";
    public bool Enabled { get; set; } = true;
    public bool Sun { get; set; }
    public bool Mon { get; set; }
    public bool Tue { get; set; }
    public bool Wed { get; set; }
    public bool Thu { get; set; }
    public bool Fri { get; set; }
    public bool Sat { get; set; }

    public static SlotRow From(ScheduleEntry e) => new()
    {
        Id = e.Id,
        ActivityId = e.ActivityId,
        Time = e.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
        Enabled = e.Enabled,
        Sun = e.DaysOfWeek.Contains(DayOfWeek.Sunday),
        Mon = e.DaysOfWeek.Contains(DayOfWeek.Monday),
        Tue = e.DaysOfWeek.Contains(DayOfWeek.Tuesday),
        Wed = e.DaysOfWeek.Contains(DayOfWeek.Wednesday),
        Thu = e.DaysOfWeek.Contains(DayOfWeek.Thursday),
        Fri = e.DaysOfWeek.Contains(DayOfWeek.Friday),
        Sat = e.DaysOfWeek.Contains(DayOfWeek.Saturday),
    };

    public ScheduleEntry ToEntry(TimeOnly start) => new()
    {
        Id = Id,
        ActivityId = ActivityId,
        StartTime = start,
        Enabled = Enabled,
        DaysOfWeek = [.. new[] { Sun, Mon, Tue, Wed, Thu, Fri, Sat }
            .Select((on, i) => (on, day: (DayOfWeek)i)).Where(x => x.on).Select(x => x.day)],
    };
}
