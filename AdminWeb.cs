using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// Browser admin for the owner and the head. Plain HTTP on the VPN interface; access needs both an allowed source
/// network and a session obtained through a one-time link from the bot (/web) or the --web-link CLI.
/// </summary>
public sealed class AdminWeb(BotConfig config, BotStore store, BotWorker worker, ILogger<AdminWeb> log) : BackgroundService
{
    private const string Cookie = "vz_session";
    private readonly Cidr[] allowed = config.Web.AllowedNetworks.Select(n => Cidr.TryParse(n, out var c) ? c : null).OfType<Cidr>().ToArray();
    private static readonly Lazy<string> Page = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("admin.html")
            ?? throw new InvalidOperationException("admin.html resource is missing");
        return new StreamReader(stream).ReadToEnd();
    });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(config.Web.Listen);
        try { listener.Start(); }
        catch (Exception e)
        {
            log.LogError("Web admin could not listen on {Prefix}: {Error}. The bot keeps working without it.", config.Web.Listen, e.Message);
            return;
        }
        log.LogInformation("Web admin listening on {Prefix}, public URL {Url}", config.Web.Listen, config.Web.PublicUrl);
        using var stop = ct.Register(() => { try { listener.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning("Web admin accept failed: {Error}", e.Message); await Task.Delay(500, ct); continue; }
            _ = Task.Run(() => Handle(context, ct), ct);
        }
    }

    private async Task Handle(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request; var response = context.Response;
        try
        {
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
            var remote = request.RemoteEndPoint?.Address;
            if (remote is null || !allowed.Any(c => c.Contains(remote))) { await Text(response, 403, "Доступ только из VPN."); return; }
            var path = request.Url?.AbsolutePath ?? "/";
            if (path == "/login" && request.HttpMethod == "GET")
            {
                // Link previewers, antivirus scanners and browser prefetch only GET: they must not burn the one-time link.
                var t = request.QueryString["t"];
                var (actor, state) = store.PeekLogin(t);
                if (actor is null || !config.CanEdit(actor.Value)) { await Html(response, 401, LoginPage(LoginProblem(state))); return; }
                var name = actor == config.OwnerId ? "разработчик" : config.FindMember(actor.Value)?.FullName ?? "староста";
                await Html(response, 200, LoginPage($"Вход как: {name}.", $"""
                    <form method="post" action="/login" style="margin-top:16px"><input type="hidden" name="t" value="{WebUtility.HtmlEncode(t)}">
                    <button style="font:inherit;padding:10px 18px;border-radius:10px;border:0;background:#6f95ff;color:#0b0e14;font-weight:700;cursor:pointer">Войти в админку</button></form>
                    """));
                return;
            }
            if (path == "/login" && request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var form = System.Web.HttpUtility.ParseQueryString(await reader.ReadToEndAsync(ct));
                var t = form["t"];
                var state = store.PeekLogin(t).State;
                var actor = store.ConsumeLogin(t, request.RemoteEndPoint?.Address.ToString() ?? "?", request.UserAgent ?? "");
                if (actor is null || !config.CanEdit(actor.Value)) { await Html(response, 401, LoginPage(LoginProblem(state))); return; }
                var session = store.CreateWebToken(actor.Value, "session", TimeSpan.FromDays(config.Web.SessionDays));
                response.Headers.Add("Set-Cookie", $"{Cookie}={session}; Path=/; HttpOnly; SameSite=Lax; Max-Age={config.Web.SessionDays * 86400}");
                response.Redirect("/");
                response.Close();
                return;
            }
            var me = store.WebToken(request.Cookies[Cookie]?.Value, "session");
            if (me is not null && !config.CanEdit(me.Value)) me = null; // the head may have been replaced
            if (path == "/" && request.HttpMethod == "GET")
            {
                if (me is null) await Html(response, 200, LoginPage("Открой ссылку, которую пришлёт бот на команду /web в личке."));
                // The head gets a page without the owner-only parts at all, not just hidden ones.
                else await Html(response, 200, me == config.OwnerId
                    ? Page.Value.Replace("/*OWNER*/", "").Replace("/*END*/", "")
                    : System.Text.RegularExpressions.Regex.Replace(Page.Value, @"/\*OWNER\*/.*?/\*END\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline));
                return;
            }
            if (!path.StartsWith("/api/")) { await Text(response, 404, "Not found"); return; }
            if (me is null) { await Send(response, 401, new { ok = false, error = "Сессия истекла. Получи новую ссылку: /web в личке бота." }); return; }
            if (request.HttpMethod == "POST" && request.Headers["X-Vozduhan"] != "1") { await Send(response, 403, new { ok = false, error = "CSRF" }); return; }
            await Api(path, request, response, me.Value, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Web admin request failed: {Error}", e.GetType().Name + ": " + e.Message);
            try { await Send(response, 500, new { ok = false, error = "Внутренняя ошибка: " + e.Message }); } catch { }
        }
    }

    private async Task Api(string path, HttpListenerRequest request, HttpListenerResponse response, long actor, CancellationToken ct)
    {
        var q = request.QueryString;
        var post = request.HttpMethod == "POST";
        JsonElement body = default;
        if (post)
        {
            if (request.ContentLength64 > 4_000_000) { await Send(response, 413, new { ok = false, error = "Слишком большой запрос" }); return; }
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);
            body = JsonDocument.Parse(raw.Length == 0 ? "{}" : raw).RootElement.Clone();
        }
        long? L(string k) => long.TryParse(q[k], out var v) ? v : null;
        var owner = actor == config.OwnerId;
        bool Visible(long chat) => owner || chat == config.GroupId || chat == actor;
        switch (path)
        {
            case "/api/me" when !post:
                await Send(response, 200, new
                {
                    ok = true, actor, owner = actor == config.OwnerId, name = config.FindMember(actor)?.FullName ?? "Разработчик",
                    group = config.GroupName, groupId = config.GroupId, chatTopic = config.ChatTopicId, deadlinesTopic = config.DeadlinesTopicId,
                    today = DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow)).ToString("yyyy-MM-dd")
                });
                return;
            case "/api/logout" when post:
                store.DeleteWebToken(request.Cookies[Cookie]?.Value);
                response.Headers.Add("Set-Cookie", $"{Cookie}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0");
                await Send(response, 200, new { ok = true });
                return;
            case "/api/registry" when !post:
                await Send(response, 200, new
                {
                    ok = true, version = store.RegistryVersion(), registry = owner ? config.R : config.R.Redacted(),
                    history = store.RegistryHistory(30).Select(h => h with { Summary = string.Join("\n", owner ? h.Summary.Split('\n').ToList() : RegistryOps.ForViewer(RegistryOps.Diff(store.RegistryAt(h.Version - 1) ?? new Registry(), store.RegistryAt(h.Version)!), false)) })
                });
                return;
            case "/api/registry" when post:
            {
                var incoming = Json.Read<Registry>(body.Field("registry").GetRawText()).Normalize();
                if (!owner) incoming.WithSecretsFrom(config.R); // the head edits a redacted copy
                int? expected = body.Field("baseVersion").ValueKind == JsonValueKind.Number ? body.Field("baseVersion").GetInt32() : null;
                if (q["dry"] == "1")
                {
                    try
                    {
                        incoming.Validate(config.OwnerId);
                        RegistryOps.CheckPermissions(config.R, incoming, actor == config.OwnerId);
                        await Send(response, 200, new { ok = true, lines = RegistryOps.ForViewer(RegistryOps.Diff(config.R, incoming), owner) });
                    }
                    catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { await Send(response, 400, new { ok = false, error = e.Message }); }
                    return;
                }
                await Commit(response, actor, expected, "веб-админка", cur => (incoming, RegistryOps.Diff(cur, incoming)), ct);
                return;
            }
            case "/api/restore" when post:
            {
                var version = body.Field("version").GetInt32();
                var target = store.RegistryAt(version);
                if (target is null) { await Send(response, 404, new { ok = false, error = "Нет такой версии" }); return; }
                await Commit(response, actor, null, $"веб-админка: откат к версии {version}", cur => (target, RegistryOps.Diff(cur, target)), ct);
                return;
            }
            case "/api/version" when !post:
            {
                var version = (int)(L("v") ?? 0);
                var target = store.RegistryAt(version);
                var previous = store.RegistryAt(version - 1);
                List<string> lines = target is null ? [] : previous is null ? ["Первая версия"] : RegistryOps.Diff(previous, target);
                await Send(response, 200, new { ok = target is not null, lines = RegistryOps.ForViewer(lines, owner) });
                return;
            }
            case "/api/week" when !post:
            {
                var r = config.R;
                var day = DateOnly.TryParseExact(q["date"], "yyyy-MM-dd", out var d) ? d : DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow));
                var monday = day.AddDays(1 - Schedule.Dow(day));
                await Send(response, 200, new
                {
                    ok = true, monday = monday.ToString("yyyy-MM-dd"),
                    week = Schedule.Week(r, monday), parity = Schedule.ParityRu(Schedule.Parity(Schedule.Week(r, monday))), inTerm = Schedule.InTerm(r, monday),
                    days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).Select(x => new { date = x.ToString("yyyy-MM-dd"), weekday = Schedule.DayNames[Schedule.Dow(x)], slots = Schedule.Day(r, x) })
                });
                return;
            }
            case "/api/deadlines" when !post:
                await Send(response, 200, new
                {
                    ok = true,
                    deadlines = store.AllDeadlines().OrderByDescending(x => x.Date).Select(x => new
                    {
                        x.Id, x.Version, x.SubjectId, subject = config.R.SubjectName(x.SubjectId), x.Work, x.Date, x.Period, x.Start, x.End, x.Room, x.Status, x.Note,
                        past = Deadlines.Ends(config, x) < DateTimeOffset.UtcNow
                    })
                });
                return;
            case "/api/deadline" when post:
            {
                var parsed = Json.Read<ParsedDeadline>(body.GetRawText());
                var draft = Deadlines.Resolve(config, parsed, null, store.AllDeadlines(), actor, "веб-админка", DateTimeOffset.UtcNow);
                if (parsed.Action != "create" && parsed.ExpectedVersion != draft.ExpectedVersion)
                { await Send(response, 409, new { ok = false, error = "Дедлайн изменился. Обнови страницу и повтори." }); return; }
                if (draft.Missing.Length > 0) { await Send(response, 400, new { ok = false, error = string.Join("\n", draft.Missing), preview = Deadlines.Preview(config, draft) }); return; }
                if (body.Field("dry").ValueKind == JsonValueKind.True) { await Send(response, 200, new { ok = true, preview = Deadlines.Preview(config, draft) }); return; }
                draft = draft with { Intro = await worker.DeadlineIntro(draft, ct), MorningIntro = "Сегодня сдаём:" };
                try
                {
                    var saved = store.ConfirmDirect(actor, draft, DateTimeOffset.UtcNow);
                    await Send(response, 200, new { ok = true, preview = Deadlines.Facts(config, saved) });
                }
                catch (InvalidOperationException e) { await Send(response, 400, new { ok = false, error = e.Message }); }
                return;
            }
            case "/api/plan" when !post:
            {
                var a = config.Ambient;
                var today = DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow));
                var deadlines = store.AllDeadlines();
                string Status(string key) => store.Get(key) switch { "done" => "done", "skipped" => "skipped", "cancelled" => "cancelled", _ => "planned" };
                // Prepared ("warmed") texts are owner-only: they may contain roasts from people notes.
                object? P(string key) => !owner || worker.Prepared(key) is not { } p ? null
                    : new { p.Text, p.Source, p.At, preview = worker.Preview(key, p.Text) };
                await Send(response, 200, new
                {
                    ok = true, ambient = a,
                    days = Enumerable.Range(0, 7).Select(i =>
                    {
                        var d = today.AddDays(i);
                        var ds = d.ToString("yyyy-MM-dd");
                        var tomorrow = deadlines.Where(x => x.Status == "active" && x.Date == d.AddDays(1).ToString("yyyy-MM-dd")).ToList();
                        var spont = i == 0 ? worker.SpontaneousPlan(d) : store.Get($"ambient:plan:{ds}") is { } p ? Json.Read<List<string>>(p) : null;
                        return new
                        {
                            date = ds, weekday = Schedule.DayNames[Schedule.Dow(d)],
                            summary = config.SummaryTopicId > 0 ? new { key = $"ambient:summary:{ds}", time = a.SummaryTime, status = Status($"ambient:summary:{ds}"), prep = P($"ambient:summary:{ds}") } : null,
                            morning = a.MorningTime.Length == 0 ? null : new { key = $"ambient:morning:{ds}", time = a.MorningTime, status = Status($"ambient:morning:{ds}"), prep = P($"ambient:morning:{ds}") },
                            spontaneous = spont?.Select((t, n) => new { key = $"ambient:spont:{ds}:{n}", time = t, status = Status($"ambient:spont:{ds}:{n}"), prep = P($"ambient:spont:{ds}:{n}") }),
                            eve = tomorrow.Count == 0 || !a.EveReminders ? null : new { key = $"ambient:eve:{ds}", time = a.EveReminderTime, status = Status($"ambient:eve:{ds}"),
                                about = tomorrow.Select(x => config.R.SubjectName(x.SubjectId) + " — " + x.Work), prep = P($"ambient:eve:{ds}") }
                        };
                    }),
                    wakeups = store.Wakeups().Where(w => owner || w.Deadline != 0).Where(w => w.Status == "pending" || w.Due > DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds()).Select(w =>
                    {
                        var d = deadlines.FirstOrDefault(x => x.Id == w.Deadline);
                        return new { w.Id, w.Deadline, w.Due, w.Reason, w.Source, w.Status, key = "wake:" + w.Id, prep = P("wake:" + w.Id),
                            deadlineTitle = d is null ? null : $"#{d.Id} {config.R.SubjectName(d.SubjectId)} — {d.Work} ({d.Date}, {d.Period} пара)" };
                    }),
                    deliveries = store.PendingDeliveries().Select(x => new { x.Key, x.Due, x.Text,
                        topic = x.TopicId == config.DeadlinesTopicId ? "Дедлайны" : x.TopicId == config.SummaryTopicId ? "Сводка" : x.TopicId == config.ChatTopicId ? "Воздуханство" : "тема " + x.TopicId })
                });
                return;
            }
            case "/api/prep" when post && owner:
            {
                var key = body.Text("key") ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^(ambient:(summary|morning|eve):\d{4}-\d{2}-\d{2}|ambient:spont:\d{4}-\d{2}-\d{2}:\d+|wake:\d+)$"))
                { await Send(response, 400, new { ok = false, error = "Неизвестное событие" }); return; }
                switch (body.Text("action"))
                {
                    case "warm":
                        try
                        {
                            var p = await worker.Warm(key, ct);
                            await Send(response, 200, new { ok = true, text = p!.Text, preview = worker.Preview(key, p.Text) });
                        }
                        catch (InvalidOperationException e) { await Send(response, 400, new { ok = false, error = e.Message }); }
                        return;
                    case "save":
                        var text = body.Text("text")?.Trim();
                        if (string.IsNullOrEmpty(text)) { await Send(response, 400, new { ok = false, error = "Пустой текст. Чтобы бот промолчал, напиши SKIP." }); return; }
                        worker.SavePrep(key, Conversation.Limit(text, 3900), "manual");
                        await Send(response, 200, new { ok = true, preview = worker.Preview(key, text) });
                        return;
                    case "cancel" when key.StartsWith("ambient:"):
                        if (store.Get(key) is { } state && state != "cancelled") { await Send(response, 400, new { ok = false, error = "Уже отправлено или пропущено." }); return; }
                        store.Set(key, "cancelled"); // the scheduler sees the key and never runs the job
                        worker.ClearPrep(key);
                        await Send(response, 200, new { ok = true });
                        return;
                    case "restore" when key.StartsWith("ambient:"):
                        if (store.Get(key) == "cancelled") store.Delete(key);
                        await Send(response, 200, new { ok = true });
                        return;
                    default:
                        worker.ClearPrep(key);
                        await Send(response, 200, new { ok = true });
                        return;
                }
            }
            case "/api/ambient" when post && owner:
            {
                if (body.Field("reset").ValueKind == JsonValueKind.True)
                {
                    store.Delete("ambient_settings");
                    config.Ambient = config.FileAmbient ?? new AmbientSettings();
                }
                else
                {
                    var next = Json.Read<AmbientSettings>(body.Field("settings").GetRawText());
                    var probe = Json.Read<BotConfig>(Json.Write(config)); probe.Ambient = next; probe.Use(config.R);
                    try { probe.Validate(); }
                    catch (InvalidDataException e) { await Send(response, 400, new { ok = false, error = "Неверные настройки: " + e.Message }); return; }
                    var old = config.Ambient;
                    config.Ambient = next;
                    store.Set("ambient_settings", Json.Write(next));
                    // A new window or count re-rolls today's remaining drops (only if none happened yet).
                    var today = DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow)).ToString("yyyy-MM-dd");
                    if ((old.SpontaneousPerDay, old.SpontaneousChance, old.ActiveFrom, old.ActiveTo) != (next.SpontaneousPerDay, next.SpontaneousChance, next.ActiveFrom, next.ActiveTo) &&
                        store.Get($"ambient:spont:{today}:0") is null)
                        store.Delete($"ambient:plan:{today}");
                }
                await Send(response, 200, new { ok = true, ambient = config.Ambient });
                return;
            }
            case "/api/wakeup" when post:
            {
                var deadline = body.Field("deadline").ValueKind == JsonValueKind.Number ? body.Field("deadline").GetInt32() : 0;
                if (deadline == 0 && !owner) { await Send(response, 403, new { ok = false, error = "Свободные отложенные посты — только для разработчика" }); return; }
                if (!DateTime.TryParse(body.Text("at"), out var at)) { await Send(response, 400, new { ok = false, error = "Неверное время" }); return; }
                var utc = config.Utc(at);
                if (utc <= DateTimeOffset.UtcNow) { await Send(response, 400, new { ok = false, error = "Время уже прошло" }); return; }
                var reason = Conversation.Limit(body.Text("reason") ?? "", 1000);
                if (deadline == 0 && reason.Length == 0) { await Send(response, 400, new { ok = false, error = "О чём написать?" }); return; }
                await Send(response, 200, new { ok = true, id = store.AddWakeup(deadline, utc.ToUnixTimeSeconds(), reason.Length == 0 ? "ручное напоминание" : reason, deadline == 0 ? "post" : "manual", actor) });
                return;
            }
            case "/api/wakeup/delete" when post:
            {
                var id = body.Field("id").GetInt64();
                var wakeup = store.Wakeups().FirstOrDefault(w => w.Id == id);
                if (wakeup is null || (!owner && wakeup.Deadline == 0))
                { await Send(response, 403, new { ok = false, error = "Нет доступа к этому посту" }); return; }
                await Send(response, 200, new { ok = store.SetWakeup(id, "deleted") });
                return;
            }
            case "/api/wakeup/replan" when post:
            {
                var d = store.AllDeadlines().FirstOrDefault(x => x.Id == body.Field("deadline").GetInt32() && x.Status == "active");
                if (d is null) { await Send(response, 404, new { ok = false, error = "Нет активного дедлайна" }); return; }
                await Send(response, 200, new { ok = true, count = await worker.PlanWakeups(d, DateTimeOffset.UtcNow, ct) });
                return;
            }
            case "/api/post-now" when post && owner:
            {
                if (body.Field("send").ValueKind == JsonValueKind.True)
                {
                    var text = body.Text("text");
                    if (string.IsNullOrWhiteSpace(text)) { await Send(response, 400, new { ok = false, error = "Пустой текст" }); return; }
                    await worker.PostAmbient("manual", null, ct, text, body.Text("target") == "chat" ? config.ChatTopicId : null);
                    await Send(response, 200, new { ok = true });
                    return;
                }
                var generated = await worker.GenerateAmbient("manual", new { idea = body.Text("idea"), requested_at = config.LocalNow(DateTimeOffset.UtcNow).ToString("HH:mm") }, ct);
                await Send(response, generated is null ? 502 : 200, new { ok = generated is not null, text = generated, error = generated is null ? "Нейронка не ответила" : null });
                return;
            }
            case "/api/deadline/regen" when post:
            {
                var d = store.AllDeadlines().FirstOrDefault(x => x.Id == body.Field("id").GetInt32());
                if (d is null) { await Send(response, 404, new { ok = false, error = "Нет такого дедлайна" }); return; }
                var draft = new Draft("", actor, d.Id, d.Version, d.Status == "cancelled" ? "cancel" : "create", d.SubjectId, d.Work, d.Date, d.Period,
                    d.Start, d.End, d.Room, "", "", "", [], body.Text("note") ?? d.Note);
                var intro = await worker.DeadlineIntro(draft, ct);
                store.SetDeadlineIntro(d.Id, intro);
                await Send(response, 200, new { ok = true, intro });
                return;
            }
            case "/api/ai-settings" when !post && owner:
                await Send(response, 200, new { ok = true, current = config.AiSnapshot(), file = config.FileAi, saved = store.Get("ai_settings") is not null });
                return;
            case "/api/ai-settings" when post && owner:
            {
                if (body.Field("reset").ValueKind == JsonValueKind.True)
                {
                    store.Delete("ai_settings");
                    if (config.FileAi is { } f) { config.Model = f.Model; config.ModelRouting = new(f.ModelRouting); config.ReasoningEffort = new(f.ReasoningEffort); }
                    await Send(response, 200, new { ok = true, current = config.AiSnapshot() });
                    return;
                }
                var next = Json.Read<AiSettings>(body.Field("settings").GetRawText());
                next.ModelRouting = next.ModelRouting.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value.Trim());
                if (!AiSettings.ValidModel(next.Model = next.Model.Trim()) || next.ModelRouting.Values.Any(v => !AiSettings.ValidModel(v)))
                { await Send(response, 400, new { ok = false, error = "ID модели в формате OpenRouter: провайдер/модель, например google/gemini-3.8-flash" }); return; }
                if (next.ReasoningEffort.Values.Any(v => v is not ("minimal" or "low" or "medium" or "high")))
                { await Send(response, 400, new { ok = false, error = "Уровень мышления: minimal, low, medium или high" }); return; }
                // Routing replaces fully; efforts merge onto defaults.
                config.ModelRouting = new();
                config.ApplyAi(next);
                store.Set("ai_settings", Json.Write(config.AiSnapshot()));
                await Send(response, 200, new { ok = true, current = config.AiSnapshot() });
                return;
            }
            case "/api/ai-test" when post && owner:
            {
                var model = body.Text("model")?.Trim() ?? "";
                if (!AiSettings.ValidModel(model)) { await Send(response, 400, new { ok = false, error = "Неверный ID модели" }); return; }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var text = await worker.TestModel(model, ct);
                    await Send(response, 200, new { ok = true, ms = clock.ElapsedMilliseconds, text });
                }
                catch (Exception e) when (!ct.IsCancellationRequested) { await Send(response, 200, new { ok = false, ms = clock.ElapsedMilliseconds, error = e.Message }); }
                return;
            }
            case "/api/message" when !post && owner:
            {
                var row = store.LogEntry(L("id") ?? 0);
                if (row is null) { await Send(response, 404, new { ok = false, error = "Нет такого сообщения" }); return; }
                var r = row.Value;
                var editable = r.Direction is "out" or "out-edit";
                string html = r.Text;
                try
                {
                    var raw = JsonDocument.Parse(r.Raw).RootElement;
                    html = r.Direction == "out-edit" ? raw.Text("text") ?? r.Text
                        : TelegramHtml.FromEntities(raw.Text("text") ?? r.Text, raw.Field("entities"));
                }
                catch (JsonException) { }
                await Send(response, 200, new { ok = true, editable, chat = r.Chat, message = r.Message, html });
                return;
            }
            case "/api/message/edit" when post && owner:
            case "/api/message/delete" when post && owner:
            {
                var row = store.LogEntry(body.Field("id").GetInt64());
                if (row is null || row.Value.Direction is not ("out" or "out-edit")) { await Send(response, 400, new { ok = false, error = "Менять можно только сообщения бота" }); return; }
                try
                {
                    if (path.EndsWith("delete")) await worker.DeleteSent(row.Value.Chat, row.Value.Message, ct);
                    else await worker.EditSent(row.Value.Chat, row.Value.Message, body.Text("html") ?? "", ct);
                    await Send(response, 200, new { ok = true });
                }
                catch (TelegramError e) { await Send(response, 400, new { ok = false, error = "Telegram: " + e.Description }); }
                return;
            }
            case "/api/chats" when !post:
                await Send(response, 200, new { ok = true, chats = store.LogChats().Where(c => Visible(c.Chat)) });
                return;
            case "/api/log" when !post:
                if (L("chat") is { } chat && !Visible(chat)) { await Send(response, 403, new { ok = false, error = "Чужая личка скрыта" }); return; }
                await Send(response, 200, new { ok = true, rows = store.Log(L("chat"), (int?)L("topic"), L("sender"), q["q"], L("before"), (int)(L("limit") ?? 150), owner ? null : actor) });
                return;
            case "/api/raw" when !post && !owner:
                await Send(response, 403, new { ok = false, error = "Исходный JSON — только для разработчика" });
                return;
            case "/api/raw" when !post:
            {
                var row = store.LogRow(L("id") ?? 0);
                if (row is null) { await Send(response, 404, new { ok = false, error = "Нет такой записи" }); return; }
                await Send(response, 200, new { ok = true, raw = row.Value.Raw });
                return;
            }
            case "/api/ai" when !post && !owner:
                await Send(response, 403, new { ok = false, error = "ИИ-запросы содержат системный промпт — доступны только разработчику." });
                return;
            case "/api/ai" when !post:
                await Send(response, 200, new { ok = true, rows = store.AiCalls(q["tag"], L("id"), L("before"), (int)(L("limit") ?? 50), full: L("id") is not null || q["tag"] is not null) });
                return;
            case "/api/seen" when !post:
            {
                var bound = config.R.Members.Where(m => m.UserId is not null).ToDictionary(m => m.UserId!.Value, m => m.FullName);
                var seen = store.SeenUsers();
                await Send(response, 200, new
                {
                    ok = true,
                    users = owner ? seen.Select(u => (object)new { u.Id, u.Username, u.Name, u.FirstSeen, u.LastSeen, u.LastChat, u.Count, member = bound.GetValueOrDefault(u.Id) })
                        : seen.Where(u => !bound.ContainsKey(u.Id)).Select(u => (object)new { u.Id, u.Username, u.Name })
                });
                return;
            }
            case "/api/export" when !post:
            {
                var r = owner ? config.R : config.R.Redacted();
                response.Headers["Content-Disposition"] = "attachment; filename=\"registry-v" + store.RegistryVersion() + ".json\"";
                var options = new JsonSerializerOptions(Json.Options) { WriteIndented = true };
                await Bytes(response, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    registryVersion = config.RegistryVersion, members = r.Members, subjects = r.Subjects, periods = r.Periods, lessons = r.Lessons,
                    scheduleChanges = r.Changes, r.WeekOneMonday, r.TermEnd, r.DefaultCampus, r.Knowledge
                }, options)));
                return;
            }
        }
        await Send(response, 404, new { ok = false, error = "Неизвестный запрос" });
    }

    private async Task Commit(HttpListenerResponse response, long actor, int? expected, string source,
        Func<Registry, (Registry, List<string>)> change, CancellationToken ct)
    {
        try
        {
            var (version, lines) = store.CommitRegistry(actor, source, expected, change);
            await Send(response, 200, new { ok = true, version, lines = RegistryOps.ForViewer(lines, actor == config.OwnerId) });
            _ = worker.NotifyEditors(actor, version, lines, "веб-админка", ct);
        }
        catch (RegistryConflict e) { await Send(response, 409, new { ok = false, error = e.Message }); }
        catch (InvalidOperationException e) { await Send(response, 400, new { ok = false, error = e.Message }); }
    }

    private string LoginProblem(string state)
    {
        if (state == "expired") return "Ссылка истекла (живёт 30 минут). Получи новую командой /web в личке бота.";
        if (state.StartsWith('{'))
        {
            var used = JsonDocument.Parse(state).RootElement;
            var at = config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(used.Number("at"))).ToString("dd.MM HH:mm");
            return $"Ссылка уже использована {at} с адреса {used.Text("ip")} ({used.Text("agent")}). Если это был не ты — получи новую через /web.";
        }
        return "Такой ссылки нет в базе бота. Получи новую командой /web. Если повторится — проверь, что на порту админки работает именно текущий бот (README → «Админка не пускает»).";
    }

    private static string LoginPage(string message, string extra = "") => $$"""
        <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Vozduhan.AI — вход</title><style>
        body{font:16px system-ui,sans-serif;background:#0f1115;color:#e8e8ea;display:grid;place-items:center;min-height:100vh;margin:0;padding:16px}
        .card{max-width:420px;background:#181b22;border:1px solid #2a2f3a;border-radius:14px;padding:28px}
        h1{font-size:20px;margin:0 0 12px}p{color:#a9adb8;line-height:1.5;margin:0}code{color:#7cc4ff}
        </style></head><body><div class="card"><h1>Vozduhan.AI · админка</h1><p>{{WebUtility.HtmlEncode(message)}}</p>{{extra}}
        <p style="margin-top:12px">Если Telegram недоступен: на сервере <code>--web-link</code>.</p></div></body></html>
        """;

    private static Task Send(HttpListenerResponse r, int status, object value) =>
        Bytes(r, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Write(value)));
    private static Task Text(HttpListenerResponse r, int status, string text) => Bytes(r, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
    private static Task Html(HttpListenerResponse r, int status, string html) => Bytes(r, status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    private static async Task Bytes(HttpListenerResponse r, int status, string type, byte[] data)
    {
        r.StatusCode = status;
        r.ContentType = type;
        r.ContentLength64 = data.Length;
        await r.OutputStream.WriteAsync(data);
        r.Close();
    }
}
