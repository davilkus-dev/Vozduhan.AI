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
                var actor = store.WebToken(request.QueryString["t"], "login");
                if (actor is null || !config.CanEdit(actor.Value)) { await Html(response, 401, LoginPage("Ссылка недействительна или уже использована. Получи новую командой /web в личке бота.")); return; }
                var session = store.CreateWebToken(actor.Value, "session", TimeSpan.FromDays(config.Web.SessionDays));
                response.Headers.Add("Set-Cookie", $"{Cookie}={session}; Path=/; HttpOnly; SameSite=Strict; Max-Age={config.Web.SessionDays * 86400}");
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
                    history = store.RegistryHistory(30).Select(h => h with { Summary = string.Join("\n", RegistryOps.ForViewer(h.Summary.Split('\n'), owner)) })
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
                if (draft.Missing.Length > 0) { await Send(response, 400, new { ok = false, error = string.Join("\n", draft.Missing), preview = Deadlines.Preview(config, draft) }); return; }
                draft = draft with { Intro = draft.Action == "create" ? "Гайс, держим в голове:" : "Гайс, по сдаче новости.", MorningIntro = "Гайс, сегодня у нас:" };
                if (body.Field("dry").ValueKind == JsonValueKind.True) { await Send(response, 200, new { ok = true, preview = Deadlines.Preview(config, draft) }); return; }
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
                string Status(string key) => store.Get(key) switch { "done" => "done", "skipped" => "skipped", _ => "planned" };
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
                            summary = config.SummaryTopicId > 0 ? new { time = a.SummaryTime, status = Status($"ambient:summary:{ds}") } : null,
                            morning = a.MorningTime.Length == 0 ? null : new { time = a.MorningTime, status = Status($"ambient:morning:{ds}") },
                            spontaneous = spont?.Select((t, n) => new { time = t, status = Status($"ambient:spont:{ds}:{n}") }),
                            eve = tomorrow.Count == 0 || !a.EveReminders ? null : new { time = a.EveReminderTime, status = Status($"ambient:eve:{ds}"),
                                about = tomorrow.Select(x => config.R.SubjectName(x.SubjectId) + " — " + x.Work) }
                        };
                    }),
                    wakeups = store.Wakeups().Where(w => owner || w.Deadline != 0).Where(w => w.Status == "pending" || w.Due > DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds()).Select(w =>
                    {
                        var d = deadlines.FirstOrDefault(x => x.Id == w.Deadline);
                        return new { w.Id, w.Deadline, w.Due, w.Reason, w.Source, w.Status,
                            deadlineTitle = d is null ? null : $"#{d.Id} {config.R.SubjectName(d.SubjectId)} — {d.Work} ({d.Date}, {d.Period} пара)" };
                    }),
                    deliveries = store.PendingDeliveries().Select(x => new { x.Key, x.Due, x.Text,
                        topic = x.TopicId == config.DeadlinesTopicId ? "Дедлайны" : x.TopicId == config.SummaryTopicId ? "Сводка" : x.TopicId == config.ChatTopicId ? "Воздуханство" : "тема " + x.TopicId })
                });
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
                await Send(response, 200, new { ok = store.SetWakeup(body.Field("id").GetInt64(), "deleted") });
                return;
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
            case "/api/chats" when !post:
                await Send(response, 200, new { ok = true, chats = store.LogChats().Where(c => owner || c.Chat != config.OwnerId) });
                return;
            case "/api/log" when !post:
                if (!owner && L("chat") == config.OwnerId) { await Send(response, 403, new { ok = false, error = "Личка разработчика скрыта" }); return; }
                await Send(response, 200, new { ok = true, rows = store.Log(L("chat"), (int?)L("topic"), L("sender"), q["q"], L("before"), (int)(L("limit") ?? 150), owner ? null : config.OwnerId) });
                return;
            case "/api/raw" when !post:
            {
                var row = store.LogRow(L("id") ?? 0);
                if (row is null || (!owner && row.Value.Chat == config.OwnerId)) { await Send(response, 404, new { ok = false, error = "Нет такой записи" }); return; }
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
                await Send(response, 200, new
                {
                    ok = true,
                    users = store.SeenUsers().Select(u => new { u.Id, u.Username, u.Name, u.FirstSeen, u.LastSeen, u.LastChat, u.Count, member = bound.GetValueOrDefault(u.Id) })
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

    private static string LoginPage(string message) => $$"""
        <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Vozduhan.AI — вход</title><style>
        body{font:16px system-ui,sans-serif;background:#0f1115;color:#e8e8ea;display:grid;place-items:center;min-height:100vh;margin:0;padding:16px}
        .card{max-width:420px;background:#181b22;border:1px solid #2a2f3a;border-radius:14px;padding:28px}
        h1{font-size:20px;margin:0 0 12px}p{color:#a9adb8;line-height:1.5;margin:0}code{color:#7cc4ff}
        </style></head><body><div class="card"><h1>Vozduhan.AI · админка</h1><p>{{WebUtility.HtmlEncode(message)}}</p>
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
