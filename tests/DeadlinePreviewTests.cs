using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>FIX-4: the web admin shows exactly what a deadline save publishes; a new lead never overwrites a card silently.</summary>
static class DeadlinePreviewTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Handler(Func<HttpRequestMessage, Task<object>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await f(request)), Encoding.UTF8, "application/json") };
    }

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var c = make("dl-preview.db");
        c.ModelRouting = new(); c.Ambient.Enabled = false;
        var port = 20000 + Random.Shared.Next(1000);
        c.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
        using var s = new BotStore(c);
        var aiCalls = 0;
        Func<Task<string>> answer = () => Task.FromResult("Свежак: СиАОД сдаём, готовьтесь");
        var ai = new HttpClient(new Handler(async request =>
        {
            Interlocked.Increment(ref aiCalls);
            return new { choices = new[] { new { message = new { content = await answer() }, finish_reason = "stop" } } };
        })) { BaseAddress = new Uri("https://openrouter.test/") };
        var nextId = 500;
        var tg = new HttpClient(new Handler(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            if (request.RequestUri!.Segments[^1] != "sendMessage") return new { ok = true, result = (object)true };
            return new { ok = true, result = new { message_id = Interlocked.Increment(ref nextId), date = 1, chat = new { id = body.Number("chat_id"), type = "supergroup" },
                message_thread_id = body.Number("message_thread_id"), text = body.Text("text") } };
        })) { BaseAddress = new Uri("https://telegram.test/") };
        var worker = new BotWorker(c, s, new TelegramClient(tg, "t"), new OpenRouterClient(ai, c, "k"), NullLogger<BotWorker>.Instance);
        async Task Publish() => await (Task)typeof(BotWorker).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, [CancellationToken.None])!;
        List<(string Action, string Payload)> Audit()
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath);
            db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT action,payload FROM audit ORDER BY id";
            using var r = cmd.ExecuteReader(); var list = new List<(string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
        using var cts = new CancellationTokenSource();
        var web = new AdminWeb(c, s, worker, NullLogger<AdminWeb>.Instance);
        await web.StartAsync(cts.Token);
        try
        {
            await Task.Delay(300);
            async Task<(HttpClient Client, HttpStatusCode Login)> Login(long actor)
            {
                var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                var login = await client.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = s.CreateWebToken(actor, "login", TimeSpan.FromMinutes(5)) }));
                client.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                return (client, login.StatusCode);
            }
            var (owner, _) = await Login(1);
            var (head, _) = await Login(2);
            var (member, memberLogin) = await Login(3);
            async Task<(HttpStatusCode Code, JsonElement Body)> Post(HttpClient h, string url, object body)
            {
                var r = await h.PostAsync("api/" + url, new StringContent(Json.Write(body), Encoding.UTF8, "application/json"));
                return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
            }
            static bool Absent(JsonElement e) => e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            object Create(string work, object? extra = null) => Merge(new { action = "create", subjectId = "siaod", work, date = "2026-12-07" }, extra);

            // 1. Preview of a new deadline: AI lead in the card, no news, nothing saved, the editor's Telegram draft untouched.
            var tgDraft = Deadlines.Resolve(c, new("create", SubjectId: "java", Work: "Лаба №1", Date: "2026-12-07"), null, [], 1, "tg", DateTimeOffset.UtcNow);
            s.SaveDraft(tgDraft);
            var (code, p) = await Post(owner, "deadline", Create("Практика №7", new { preview = true }));
            Assert(code == HttpStatusCode.OK && p.Text("intro") == "Свежак: СиАОД сдаём, готовьтесь" && p.Text("introSource") == "ai" && aiCalls == 1, "Preview create: " + p);
            Assert(p.Text("card")!.Contains("Свежак: СиАОД сдаём") && p.Text("card")!.Contains("📌 <b>Дедлайн</b>") && Absent(p.Field("news")) && p.Text("facts")!.Contains("Практика №7"), "Preview card: " + p);
            Assert(s.AllDeadlines().Count == 0 && s.PendingDeliveries().Count == 0 && s.Posts("").Count == 0 && s.Draft(1)?.Token == tgDraft.Token, "Preview saved something");
            Console.WriteLine("PASS deadline preview: create shows the AI lead in the card and saves nothing");

            // 2. Preview with the editor's own lead: no AI, unsafe HTML removed by the sanitizer.
            (code, p) = await Post(owner, "deadline", Create("Практика №7", new { preview = true, intro = "Ручная <script>alert(1)</script> <a href=\"javascript:alert(1)\">жми</a> <b>жирно</b>" }));
            var card = p.Text("card")!;
            Assert(code == HttpStatusCode.OK && aiCalls == 1 && p.Text("introSource") == "manual" && card.Contains("Ручная") && card.Contains("<b>жирно</b>") &&
                !card.Contains("<script") && !card.Contains("javascript:") && !card.Contains("<a "), "Manual preview: " + card);
            Console.WriteLine("PASS deadline preview: an editor's lead needs no AI and is sanitized");

            // 3. Saving: the given lead exactly, empty = standard phrase, > 700 rejected, no key = server-side generation as before.
            (code, p) = await Post(owner, "deadline", Create("Практика №7", new { intro = "Мой текст\n\n\n\nвторой абзац  " }));
            var d7 = s.AllDeadlines().Single(d => d.Work == "Практика №7");
            Assert(code == HttpStatusCode.OK && d7.Intro == "Мой текст\n\nвторой абзац" && aiCalls == 1, "Saved lead differs: " + Json.Write(d7.Intro));
            (code, _) = await Post(owner, "deadline", Create("Практика №8", new { intro = "" }));
            var d8 = s.AllDeadlines().Single(d => d.Work == "Практика №8");
            Assert(code == HttpStatusCode.OK && d8.Intro == "" && Deadlines.Card(c, d8, DateTimeOffset.UtcNow).StartsWith("Гайс, держим в голове.") && aiCalls == 1, "Empty lead not standard");
            (code, p) = await Post(owner, "deadline", Create("Практика №9", new { intro = new string('ж', 701) }));
            Assert(code == HttpStatusCode.BadRequest && p.Text("error") == "Вводная длиннее 700 символов" && !s.AllDeadlines().Any(d => d.Work == "Практика №9"), "Long lead accepted");
            (code, _) = await Post(owner, "deadline", Create("Практика №9"));
            Assert(code == HttpStatusCode.OK && aiCalls == 2 && s.AllDeadlines().Single(d => d.Work == "Практика №9").Intro.StartsWith("Свежак"), "Legacy save without intro broken");
            Console.WriteLine("PASS deadline save: exact lead, standard phrase, 700 limit, legacy generation");

            // 4. Update and cancel: card and change news; the saved news carries exactly the previewed lead.
            (code, p) = await Post(owner, "deadline", new { action = "update", targetId = d7.Id, expectedVersion = d7.Version, date = "2026-12-14", preview = true, intro = "Перенесли, гайс" });
            Assert(code == HttpStatusCode.OK && p.Text("card")!.Contains("Перенесли, гайс") && p.Text("news")!.Contains("Было:") && p.Text("news")!.Contains("Перенесли, гайс"), "Update preview: " + p);
            await Post(owner, "deadline", new { action = "update", targetId = d7.Id, expectedVersion = d7.Version, date = "2026-12-14", intro = "Перенесли, гайс" });
            d7 = s.AllDeadlines().Single(d => d.Id == d7.Id);
            Assert(s.PendingDeliveries().Single(x => x.Key == $"change:{d7.Id}:{d7.Version}").Text.StartsWith("Перенесли, гайс"), "Update news differs from preview");
            (code, p) = await Post(owner, "deadline", new { action = "cancel", targetId = d8.Id, expectedVersion = d8.Version, preview = true, intro = "Отбой ручной" });
            Assert(code == HttpStatusCode.OK && p.Text("card")!.Contains("Сдача отменена") && p.Text("news")!.Contains("Отмена") && p.Text("news")!.Contains("Отбой ручной"), "Cancel preview: " + p);
            await Post(owner, "deadline", new { action = "cancel", targetId = d8.Id, expectedVersion = d8.Version, intro = "Отбой ручной" });
            d8 = s.AllDeadlines().Single(d => d.Id == d8.Id);
            Assert(d8.Status == "cancelled" && s.PendingDeliveries().Single(x => x.Key == $"change:{d8.Id}:{d8.Version}").Text.StartsWith("Отбой ручной"), "Cancel news differs from preview");
            Console.WriteLine("PASS deadline preview: update/cancel show card and news; what was shown is what goes out");

            // 5. Missing fields and stale versions are rejected before any AI call.
            var calls = aiCalls;
            (code, p) = await Post(owner, "deadline", new { action = "create", subjectId = "siaod", preview = true });
            Assert(code == HttpStatusCode.BadRequest && p.Text("error")!.Contains("Что именно сдаём") && p.Text("error")!.Contains("дату") && aiCalls == calls, "Missing fields: " + p);
            (code, _) = await Post(owner, "deadline", new { action = "update", targetId = d7.Id, expectedVersion = d7.Version - 1, date = "2026-12-21", preview = true });
            Assert(code == HttpStatusCode.Conflict && aiCalls == calls, "Stale preview accepted");

            // 6. The model fails or writes garbage twice: 502 in Russian, nothing saved; the manual path still works.
            var count = s.AllDeadlines().Count;
            answer = () => Task.FromResult("你好你好你好");
            (code, p) = await Post(owner, "deadline", Create("Практика №11", new { preview = true }));
            Assert(code == HttpStatusCode.BadGateway && p.Text("error")!.StartsWith("ИИ не ответил") && s.AllDeadlines().Count == count, "Garbage lead: " + p);
            answer = () => throw new HttpRequestException("down");
            (code, _) = await Post(owner, "deadline", Create("Практика №11", new { preview = true }));
            Assert(code == HttpStatusCode.BadGateway, "AI failure not 502");
            (code, p) = await Post(owner, "deadline", Create("Практика №11", new { preview = true, intro = "Руками" }));
            Assert(code == HttpStatusCode.OK && p.Text("card")!.Contains("Руками"), "Manual path broken without AI");
            answer = () => Task.FromResult("Новый вариант от ИИ");
            Console.WriteLine("PASS deadline preview: missing fields, stale version, AI failure and the manual fallback");

            // 7. Regen is only a preview; apply stores the lead without a new version, audits it, and the card is edited.
            await Post(owner, "deadline", Create("Практика №10", new { intro = "Старое интро" }));
            var d10 = s.AllDeadlines().Single(d => d.Work == "Практика №10");
            s.Set("wakeplan:" + d10.Id, d10.Version.ToString());
            var wake = s.AddWakeup(d10.Id, DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(), "ИИ-точка", "ai");
            await Publish();
            var before = s.Post("deadline:" + d10.Id)!.Text;
            var audit = Audit().Count;
            (code, p) = await Post(owner, "deadline/regen", new { id = d10.Id, expectedVersion = d10.Version, note = "Арапов злой" });
            Assert(code == HttpStatusCode.OK && p.Text("intro") == "Новый вариант от ИИ" && p.Text("card")!.Contains("Новый вариант от ИИ") && p.Field("version").GetInt32() == d10.Version &&
                !p.Field("manualEdited").GetBoolean(), "Regen: " + p);
            await Publish();
            Assert(s.AllDeadlines().Single(d => d.Id == d10.Id).Intro == "Старое интро" && s.Post("deadline:" + d10.Id)!.Text == before && Audit().Count == audit, "Regen changed the deadline");
            (code, _) = await Post(owner, "deadline/intro", new { id = d10.Id, expectedVersion = d10.Version + 1, intro = "Новое интро" });
            Assert(code == HttpStatusCode.Conflict, "Stale apply accepted");
            (code, p) = await Post(owner, "deadline/intro", new { id = d10.Id, expectedVersion = d10.Version, intro = "Новое интро" });
            var applied = s.AllDeadlines().Single(d => d.Id == d10.Id);
            var row = Audit().Last();
            Assert(code == HttpStatusCode.OK && p.Text("note")!.Contains("без нового уведомления") && applied.Intro == "Новое интро" && applied.Version == d10.Version, "Apply: " + p);
            Assert(row.Action == "intro" && row.Payload.Contains("\"before\":\"Старое интро\"") && row.Payload.Contains("\"after\":\"Новое интро\""), "Audit row: " + row.Payload);
            Assert(s.Get("wakeplan:" + d10.Id) == d10.Version.ToString() && s.Wakeups(d10.Id).Single(w => w.Id == wake).Status == "pending", "Wake-ups re-planned");
            await Publish();
            Assert(s.Post("deadline:" + d10.Id)!.Text.StartsWith("Новое интро"), "Published card not updated");
            Console.WriteLine("PASS deadline regen: preview only; apply audits, keeps the version and wake-ups, the card follows");

            // 8. A hand-edited card is never overwritten without force.
            var post = s.Post("deadline:" + d10.Id)!;
            var manualKey = $"manual_msg:{c.GroupId}:{post.MessageId}";
            s.Set(manualKey, "1");
            (code, p) = await Post(owner, "deadline/regen", new { id = d10.Id, expectedVersion = d10.Version });
            Assert(code == HttpStatusCode.OK && p.Field("manualEdited").GetBoolean(), "Regen does not flag manual edit");
            (code, p) = await Post(owner, "deadline", new { action = "update", targetId = d10.Id, expectedVersion = d10.Version, work = "Практика №12", preview = true, intro = "x" });
            Assert(code == HttpStatusCode.OK && p.Field("manualEdited").GetBoolean(), "Preview does not flag manual edit");
            (code, p) = await Post(owner, "deadline/intro", new { id = d10.Id, expectedVersion = d10.Version, intro = "Перезапись" });
            Assert(code == HttpStatusCode.Conflict && p.Text("code") == "manual_edit" && s.AllDeadlines().Single(d => d.Id == d10.Id).Intro == "Новое интро", "Manual edit overwritten");
            (code, _) = await Post(owner, "deadline/intro", new { id = d10.Id, expectedVersion = d10.Version, intro = "Перезапись", force = true });
            await Publish();
            Assert(code == HttpStatusCode.OK && s.Get(manualKey) is null && s.Post("deadline:" + d10.Id)!.Text.StartsWith("Перезапись"), "Forced apply failed");
            Console.WriteLine("PASS deadline regen: a hand-edited card needs explicit force");

            // 9. The head does the same; a non-editor is refused as before.
            (code, p) = await Post(head, "deadline", Create("Практика №13", new { preview = true }));
            var headRegen = await Post(head, "deadline/regen", new { id = d10.Id, expectedVersion = d10.Version });
            var headApply = await Post(head, "deadline/intro", new { id = d10.Id, expectedVersion = d10.Version, intro = "От старосты" });
            Assert(code == HttpStatusCode.OK && headRegen.Code == HttpStatusCode.OK && headApply.Code == HttpStatusCode.OK && s.AllDeadlines().Single(d => d.Id == d10.Id).Intro == "От старосты", "Head cannot preview/apply");
            Assert(memberLogin == HttpStatusCode.Unauthorized && (await member.PostAsync("api/deadline/intro", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Unauthorized,
                "Non-editor got access");
            Assert((await head.GetAsync("api/raw?id=1")).StatusCode == HttpStatusCode.Forbidden && (await head.GetAsync("api/ai")).StatusCode == HttpStatusCode.Forbidden, "Head restrictions changed");
            Console.WriteLine("PASS deadline preview: head has the same flow, non-editors are refused");
        }
        finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        return 8;
    }

    static Dictionary<string, object?> Merge(object a, object? b)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(Json.Write(a), Json.Options)!;
        if (b is not null) foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, object?>>(Json.Write(b), Json.Options)!) d[k] = v;
        return d;
    }
}
