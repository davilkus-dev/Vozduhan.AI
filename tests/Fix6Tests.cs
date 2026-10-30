using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

static partial class WakeupTests
{
    static async Task RunFix6(Func<string, BotConfig> make)
    {
        using (var e = new Env(make, "fix6-web.db"))
        {
            var port = 24000 + Random.Shared.Next(1000);
            e.C.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            var now = e.C.LocalNow(DateTimeOffset.UtcNow);
            Deadline Add(DateTime start, string work) => e.S.ConfirmDirect(1,
                new Draft(Guid.NewGuid().ToString("N"), 1, null, 0, "create", "siaod", work, start.ToString("yyyy-MM-dd"), 6,
                    start.ToString("HH:mm"), start.AddHours(1).ToString("HH:mm"), "А412", "test", "вводная", "", []), e.C.Utc(start.AddDays(-1)));
            var future = now.Date.AddDays(7).AddHours(18);
            var d = Add(future, "Будущая сдача");
            var cancelled = Add(future, "Отменённая сдача");
            cancelled = e.S.ConfirmDirect(1, Deadlines.Resolve(e.C, new("cancel", cancelled.Id), null, e.S.AllDeadlines(), 1, "test", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            var past = Add(now.Date.AddDays(-1).AddHours(18), "Прошедшая сдача");
            var started = Add(now.AddMinutes(-5), "Начавшаяся сдача");
            var web = new AdminWeb(e.C, e.S, e.W, NullLogger<AdminWeb>.Instance);
            using var cts = new CancellationTokenSource();
            await web.StartAsync(cts.Token);
            try
            {
                await Task.Delay(300);
                async Task<HttpClient> Login(long actor)
                {
                    var h = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false }) { BaseAddress = new Uri(e.C.Web.Listen) };
                    await h.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = e.S.CreateWebToken(actor, "login", TimeSpan.FromMinutes(5)) }));
                    h.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                    return h;
                }
                async Task<(HttpStatusCode Code, JsonElement Body)> Post(HttpClient h, string path, object body)
                {
                    using var r = await h.PostAsync("api/" + path, new StringContent(Json.Write(body), Encoding.UTF8, "application/json"));
                    return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
                }
                using var owner = await Login(1);
                using var head = await Login(2);

                await Check("FIX-6.1: HTTP 500 hides exception details", async () =>
                {
                    using var r = await owner.PostAsync("api/wakeup", new StringContent("{SECRET-BROKEN-JSON", Encoding.UTF8, "application/json"));
                    var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                    Assert(r.StatusCode == HttpStatusCode.InternalServerError && !b.Field("ok").GetBoolean() &&
                        b.Text("error") == "Внутренняя ошибка. Подробности в журнале сервиса.", "500 leaked details: " + b);
                });

                await Check("FIX-6.3: wake-up validation, actor permissions and delete controls over HTTP", async () =>
                {
                    foreach (var h in new[] { owner, head })
                    {
                        var before = e.S.Wakeups().Count;
                        foreach (var id in new[] { int.MaxValue, cancelled.Id, past.Id, started.Id })
                            Assert((await Post(h, "wakeup", new { deadline = id, at = future.AddDays(-1), reason = "напомнить" })).Code == HttpStatusCode.BadRequest, "Invalid deadline accepted: " + id);
                        foreach (var at in new[] { now.AddMinutes(-1), future, future.AddMinutes(1) })
                            Assert((await Post(h, "wakeup", new { deadline = d.Id, at, reason = "напомнить" })).Code == HttpStatusCode.BadRequest, "Invalid wake-up time accepted");
                        Assert((await Post(h, "wakeup", new { deadline = d.Id, at = future.AddDays(-1), reason = new string('я', 301) })).Code == HttpStatusCode.BadRequest &&
                            e.S.Wakeups().Count == before, "Long reason truncated or invalid wake-up saved");
                    }
                    var own = await Post(owner, "wakeup", new { deadline = d.Id, at = future.AddDays(-1), reason = new string('я', 300) });
                    var mine = await Post(head, "wakeup", new { deadline = d.Id, at = future.AddDays(-1), reason = "мой" });
                    var ownId = own.Body.Number("id"); var mineId = mine.Body.Number("id");
                    Assert(own.Code == HttpStatusCode.OK && mine.Code == HttpStatusCode.OK && e.Wake(ownId).Actor == 1 && e.Wake(mineId).Actor == 2, "Actor or valid wake-up lost");
                    var denied = await Post(head, "wakeup/delete", new { id = ownId });
                    Assert(denied.Code == HttpStatusCode.Forbidden && denied.Body.Text("error") == "Этот будильник поставил разработчик" && e.Wake(ownId).Status == "pending", "Owner wake-up deleted by head");
                    foreach (var id in new[] { mineId, e.S.AddWakeup(d.Id, 1, "ai", "ai"), e.S.AddWakeup(d.Id, 1, "default", "default") })
                        Assert((await Post(head, "wakeup/delete", new { id })).Body.Field("ok").GetBoolean(), "Head lost delete access");
                    var hp = JsonDocument.Parse(await head.GetStringAsync("api/plan")).RootElement;
                    var op = JsonDocument.Parse(await owner.GetStringAsync("api/plan")).RootElement;
                    Assert(!hp.Field("wakeups").EnumerateArray().Single(w => w.Number("id") == ownId).Field("canDelete").GetBoolean() &&
                        op.Field("wakeups").EnumerateArray().Single(w => w.Number("id") == ownId).Field("canDelete").GetBoolean() &&
                        (await head.GetStringAsync("")).Contains("(w.status===\"pending\"||w.status===\"ready\")&&w.canDelete"), "Delete control ignores actor");
                    Assert((await Post(owner, "wakeup/delete", new { id = ownId })).Body.Field("ok").GetBoolean(), "Owner cannot delete own wake-up");
                    Assert((await Post(owner, "wakeup", new { deadline = 0, at = future, reason = new string('я', 1000) })).Code == HttpStatusCode.OK &&
                        (await Post(owner, "wakeup", new { deadline = 0, at = future, reason = new string('я', 1001) })).Code == HttpStatusCode.BadRequest &&
                        (await Post(head, "wakeup", new { deadline = 0, at = future, reason = "пост" })).Code == HttpStatusCode.Forbidden, "Standalone post limits changed");
                });

                await Check("FIX-6.4: inactive or ended deadline intro/regen rejected before AI over HTTP", async () =>
                {
                    var calls = e.Ai.Count;
                    foreach (var h in new[] { owner, head })
                        foreach (var item in new[] { cancelled, past })
                            foreach (var path in new[] { "deadline/regen", "deadline/intro" })
                            {
                                var r = await Post(h, path, new { id = item.Id, expectedVersion = item.Version, intro = path.EndsWith("intro") ? "новая вводная" : null });
                                Assert(r.Code == HttpStatusCode.BadRequest && r.Body.Text("error") == "Дедлайн отменён или уже прошёл — текст не меняется", "Inactive intro changed: " + r);
                                Assert(e.S.AllDeadlines().Single(x => x.Id == item.Id).Intro == item.Intro, "Intro saved on rejection");
                            }
                    Assert(e.Ai.Count == calls, "AI called for inactive deadline");
                    Assert((await Post(head, "deadline/regen", new { id = d.Id, expectedVersion = d.Version })).Code == HttpStatusCode.OK && e.Ai.Count == calls + 1, "Active regeneration blocked");
                    Assert((await Post(head, "deadline/intro", new { id = d.Id, expectedVersion = d.Version, intro = "новая вводная" })).Code == HttpStatusCode.OK, "Active intro blocked");
                });

                await Check("FIX-6.6: warm rejects bad text explicitly for every kind; genuine spontaneous SKIP persists", async () =>
                {
                    e.C.Ambient.MorningTime = "08:00"; e.C.Ambient.EveReminders = true; e.C.SummaryTopicId = 30;
                    var day = DateOnly.FromDateTime(future);
                    e.S.Set($"ambient:plan:{day:yyyy-MM-dd}", Json.Write(new[] { "13:30" }));
                    var manual = e.S.AddWakeup(0, e.C.Utc(future.AddDays(-1)).ToUnixTimeSeconds(), "пост", "post", 1);
                    var wake = e.S.AddWakeup(d.Id, e.C.Utc(future.AddDays(-1)).ToUnixTimeSeconds(), "напомнить", "manual", 2);
                    var keys = new[] { $"ambient:summary:{day:yyyy-MM-dd}", $"ambient:morning:{day:yyyy-MM-dd}", $"ambient:eve:{day.AddDays(-1):yyyy-MM-dd}",
                        $"ambient:spont:{day:yyyy-MM-dd}:0", "wake:" + manual, "wake:" + wake };
                    e.Text = _ => Task.FromResult("Гайс, 集合 в 15:00");
                    foreach (var key in keys)
                    {
                        var r = await Post(owner, "prep", new { key, action = "warm" });
                        Assert(r.Code == HttpStatusCode.BadRequest && r.Body.Text("error") == "Текст не прошёл проверку: иероглифы в тексте. Нажми ещё раз" && e.S.Publication(key)?.Text is null, "Bad warm-up became SKIP: " + r);
                    }
                    e.Text = _ => Task.FromResult("SKIP");
                    Assert((await Post(owner, "prep", new { key = keys[3], action = "warm" })).Code == HttpStatusCode.OK && e.S.Publication(keys[3])?.Text == "SKIP", "Genuine SKIP lost");
                });

                await Check("FIX-6.12: spending page has Russian kinds, raw titles and discard reasons", async () =>
                {
                    var page = await owner.GetStringAsync("");
                    foreach (var label in new[] { "ответ в чате", "планировщик редактора", "разбор дедлайна", "план будильников", "проверка модели", "вводная поста дедлайна",
                        "напоминание", "утренняя сводка", "утро в чате", "вечернее напоминание", "вброс", "ручной пост" })
                        Assert(page.Contains(label), "Missing kind label: " + label);
                    Assert(page.Contains("<span title=\"${esc(k)}\">${esc(KUSE[k]||k)}</span>") && page.Contains("wasteReason(x.discard)") &&
                        !page.Contains("num(x.retries)") && !page.Contains("num(x.cut)"), "Kind title/fallback or discard view wrong");
                });

                await Check("FIX-6.2: logout expires session and view cookies; fresh login restores owner", async () =>
                {
                    await Post(owner, "view", new { @as = "head" });
                    using var r = await owner.PostAsync("api/logout", new StringContent("{}", Encoding.UTF8, "application/json"));
                    var cookies = r.Headers.GetValues("Set-Cookie").ToArray();
                    Assert(r.StatusCode == HttpStatusCode.OK && new[] { "vz_session=;", "vz_view=;" }.All(name => cookies.Any(x => x.Contains(name) && x.Contains("Max-Age=0"))), "Logout missed a cookie");
                    Assert((await owner.GetAsync("api/me")).StatusCode == HttpStatusCode.Unauthorized, "Session survived logout");
                    await owner.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = e.S.CreateWebToken(1, "login", TimeSpan.FromMinutes(5)) }));
                    var me = JsonDocument.Parse(await owner.GetStringAsync("api/me")).RootElement;
                    Assert(me.Field("owner").GetBoolean() && !me.Field("asHead").GetBoolean(), "View cookie survived logout");
                });
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        }

        await Check("FIX-6.7: Windows and lone CR newlines pass TextGuard without regeneration", async () =>
        {
            using var e = new Env(make, "fix6-newlines.db");
            e.C.SummaryTopicId = 30;
            e.Text = _ => Task.FromResult("Первая строка\r\nВторая строка\rТретья строка");
            var key = "ambient:summary:2026-12-01";
            var p = await e.Warm(key);
            Assert(p.Text == "Первая строка\nВторая строка\nТретья строка" && e.Ai.Count == 1 && TextGuard.Check(p.Text, "", 3900) is null, "CR text rejected or regenerated");
        });

        await Check("FIX-6.8: partial delivery resumes the saved text despite changed basis", async () =>
        {
            using var e = new Env(make, "fix6-parts.db");
            e.C.SplitReplies = true;
            e.C.Ambient.MorningTime = e.C.LocalNow(T).ToString("HH:mm");
            var key = $"ambient:morning:{e.C.LocalNow(T):yyyy-MM-dd}";
            e.S.EnsurePublication(key, "morning", T.ToUnixTimeSeconds(), e.C.ChatTopicId, BotWorker.AutoMode);
            e.S.SetPublicationText(key, "Первая часть\n||\nВторая часть", "ai", "old-basis");
            e.S.SavePost("ambient_part:" + key + ":1", 444, "");
            await e.Tick(T.AddMinutes(1));
            Assert(e.Posts.SequenceEqual(["Первая часть"]) && e.Ai.Count == 0 && e.Pub(key) is { Status: "sent", Text: "Первая часть\n||\nВторая часть" }, "Partial post regenerated or duplicated");
        });

        await Check("FIX-6.9: retries wait 2/5/10/15 minutes, persist, end with failed after 5; a late retry is skipped", async () =>
        {
            foreach (var kind in new[] { "summary", "morning", "spont", "eve" })
            {
                Env Make(string suffix)
                {
                    var e = new Env(make, $"fix6-retry-{kind}{suffix}.db");
                    var day = DateOnly.FromDateTime(e.C.LocalNow(T));
                    var time = e.C.LocalNow(T).ToString("HH:mm");
                    e.C.SummaryTopicId = kind == "summary" ? 30 : 0;
                    e.C.Ambient.SummaryTime = time;
                    e.C.Ambient.MorningTime = kind == "morning" ? time : "";
                    e.C.Ambient.EveReminders = kind == "eve";
                    e.C.Ambient.EveReminderTime = time;
                    e.S.Set($"ambient:plan:{day:yyyy-MM-dd}", Json.Write(kind == "spont" ? new[] { time } : Array.Empty<string>()));
                    if (kind == "eve") e.Add(day.AddDays(1).ToString("yyyy-MM-dd"));
                    e.Text = _ => throw new HttpRequestException("down");
                    return e;
                }
                var key = $"ambient:{kind}:{DateOnly.FromDateTime(new BotConfig().LocalNow(T)):yyyy-MM-dd}" + (kind == "spont" ? ":0" : "");
                using (var e = Make(""))
                {
                    for (var seconds = 0; seconds < 120; seconds += 30) await e.Tick(T.AddSeconds(seconds));
                    await e.Tick(T.AddSeconds(119));
                    Assert(e.Ai.Count == 1 && e.Pub(key) is { Attempts: 1 } p1 && p1.NextTry == T.AddMinutes(2).ToUnixTimeSeconds(), kind + ": retried before 2 minutes or not persisted");
                    await e.Tick(T.AddMinutes(2));
                    for (var seconds = 150; seconds < 420; seconds += 30) await e.Tick(T.AddSeconds(seconds));
                    await e.Tick(T.AddSeconds(419));
                    Assert(e.Ai.Count == 2, kind + ": retried before 7 minutes");
                    await e.Tick(T.AddMinutes(7));
                    Assert(e.Ai.Count == 3, kind + ": third attempt missing");
                    e.Text = _ => Task.FromResult("Гайс, пора сдавать работу");
                    await e.Tick(T.AddMinutes(17).AddSeconds(-1));
                    Assert(e.Ai.Count == 3 && e.Pub(key).Status == "planned", "Retried before 17 minutes");
                    await e.Tick(T.AddMinutes(17));
                    Assert(e.Ai.Count == 4 && e.Pub(key).Status == "sent", "Success not recorded");
                }
                using (var e = Make("-fail"))
                {
                    foreach (var minute in new[] { 0, 2, 7, 17, 32 }) await e.Tick(T.AddMinutes(minute));
                    Assert(e.Pub(key) is { Status: "failed", Attempts: 5 }, "Not failed after 5 attempts: " + Json.Write(e.Pub(key)));
                }
                using (var e = Make("-late"))
                {
                    await e.Tick(T);
                    e.S.RetryPublication(key, 1, T.AddHours(3).ToUnixTimeSeconds(), "ошибка");
                    var calls = e.Ai.Count;
                    await e.Tick(T.AddHours(2).AddSeconds(1));
                    Assert(e.Ai.Count == calls && e.Pub(key).Status == "skipped", "Late retry not skipped");
                }
            }
        });

        await Check("FIX-6.10: wake-up planner prompt explains sent and manual reminders", async () =>
        {
            using var e = new Env(make, "fix6-prompt.db");
            e.Add("2026-12-14");
            await e.PlanTick(T);
            var prompt = JsonDocument.Parse(e.Ai.Single()).RootElement.Field("messages")[0].Text("content")!;
            Assert(prompt.Contains("sent_reminders — уже отправленные напоминания: не повторяй их поводы и время.") &&
                prompt.Contains("manual_reminders — поставленные вручную, держись от них не ближе 2 часов."), "Planner instructions missing");
        });

        await Check("FIX-6.12: ambient, deadline, parse and wakeplan callers supply stable tags", async () =>
        {
            using var e = new Env(make, "fix6-tags.db");
            e.C.Ambient.MorningTime = "08:00"; e.C.Ambient.EveReminders = true; e.C.SummaryTopicId = 30;
            var d = e.Add("2026-12-14");
            var wake = e.S.AddWakeup(d.Id, T.AddDays(1).ToUnixTimeSeconds(), "напомнить", "manual", 2);
            var post = e.S.AddWakeup(0, T.AddDays(1).ToUnixTimeSeconds(), "пост", "post", 1);
            e.S.Set("ambient:plan:2026-12-14", Json.Write(new[] { "13:30" }));
            foreach (var key in new[] { "ambient:summary:2026-12-14", "ambient:morning:2026-12-14", "ambient:eve:2026-12-13", "ambient:spont:2026-12-14:0", "wake:" + wake, "wake:" + post })
            {
                await e.Warm(key);
                Assert(e.S.AiCalls().Any(x => x.Tag == key), "Warm tag missing: " + key);
            }
            Task Call(string method, params object?[] args) => (Task)typeof(BotWorker).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e.W, args)!;
            await Call("PostAmbient", "manual", null, CancellationToken.None, null, null, null, false);
            await Call("PostAmbient", "morning", null, CancellationToken.None, null, e.C.ChatTopicId, "ambient:morning:2026-12-15", false);
            await Call("MorningSummary", new DateOnly(2026, 12, 15), CancellationToken.None, null);
            await Call("EveReminder", new DateOnly(2026, 12, 14), CancellationToken.None, null);
            var draft = Deadlines.Resolve(e.C, new("create", SubjectId: "siaod", Work: "Практика новая", Date: d.Date), null, e.S.AllDeadlines(), 1, "test", T);
            await Call("TryDeadlineIntro", draft, CancellationToken.None, null);
            await Call("TryDeadlineIntro", draft with { TargetId = d.Id }, CancellationToken.None, null);
            await e.PlanTick(T);
            e.Text = _ => Task.FromResult("{\"question\":\"Какой предмет?\"}");
            var message = JsonSerializer.SerializeToElement(new { message_id = 77, date = T.ToUnixTimeSeconds(), chat = new { id = 2, type = "private" },
                from = new { id = 2, first_name = "Староста" }, text = "нужна сдача" }, Json.Options);
            await Call("ManageDeadline", message, "test-parse", CancellationToken.None, null);
            var calls = e.S.AiCalls();
            foreach (var (kind, tag) in new[] { ("ambient-manual", "manual"), ("ambient-morning", "ambient:morning:2026-12-15"),
                ("ambient-summary", "ambient:summary:2026-12-15"), ("ambient-eve", "ambient:eve:2026-12-13"),
                ("ambient-deadline", "deadline:new"), ("ambient-deadline", "deadline:" + d.Id), ("wakeplan", "wakeplan:" + d.Id), ("deadline", "chat:2:77") })
                Assert(calls.Any(x => x.Kind == kind && x.Tag == tag), "Call tag missing: " + kind + " " + tag);
        });
    }
}
