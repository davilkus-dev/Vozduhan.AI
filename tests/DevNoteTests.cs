using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>FIX-5: the developer's private deadline note reaches the model for posts and never the head.</summary>
static class DevNoteTests
{
    const string Mark = "DEVSECRET-4821";
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Handler(Func<HttpRequestMessage, Task<object>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await f(request)), Encoding.UTF8, "application/json") };
    }
    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    /// <summary>The model request carries the mark in a dev_note field (the context is a JSON string inside the request JSON).</summary>
    static bool HasNote(string request) => System.Text.RegularExpressions.Regex.IsMatch(request, @"dev_note\\?""\s*:\s*\\?""" + Mark);

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        // 1. Migration from a deadlines table without dev_note.
        {
            var mc = make("devnote-migrate.db");
            var old = new Deadline(1, 3, "siaod", "Старая практика", "2026-12-07", 6, "18:00", "19:30", "А412", "active", 1, "t", "Интро", "Сегодня сдаём:", "Комментарий");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + mc.DataPath))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE deadlines(id INTEGER PRIMARY KEY AUTOINCREMENT, version INTEGER NOT NULL, date TEXT NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL);" +
                    "INSERT INTO deadlines VALUES(1,3,'2026-12-07','active',$p); PRAGMA user_version=2;";
                cmd.Parameters.AddWithValue("$p", Json.Write(old));
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var ms = new BotStore(mc);
            Assert(ms.AllDeadlines().Single() == old && ms.DevNote(1) is null, "Migration lost the deadline or invented a note");
            using var check = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + mc.DataPath);
            check.Open();
            using var info = check.CreateCommand(); info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('deadlines') WHERE name='dev_note'";
            Assert(Convert.ToInt32(info.ExecuteScalar()) == 1, "dev_note column missing");
            Console.WriteLine("PASS devnote: migration adds dev_note, data intact");
        }

        var c = make("devnote.db");
        c.ModelRouting = new(); c.SummaryTopicId = 30;
        c.Ambient.SpontaneousPerDay = 0; c.Ambient.EveReminders = false; c.Ambient.MorningTime = "08:00";
        c.Features.PublicationModes = BotWorker.PublicationKinds.ToDictionary(k => k, _ => BotWorker.AutoMode);
        var port = 21000 + Random.Shared.Next(1000);
        c.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
        using var s = new BotStore(c);
        var aiRequests = new List<string>();
        var plan = "{\"intent\":\"chat\"}";
        var ai = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            lock (aiRequests) aiRequests.Add(body);
            var content = body.Contains("разбираешь личное сообщение редактора") ? plan : body.Contains("планируешь, когда тебе") ? "{}" : "Гайс, сдаём, готовьтесь";
            return new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } };
        })) { BaseAddress = new Uri("https://openrouter.test/") };
        var sent = new List<(string Method, JsonElement Body)>();
        var nextId = 700;
        var tg = new HttpClient(new Handler(async request =>
        {
            var method = request.RequestUri!.Segments[^1];
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            lock (sent) sent.Add((method, body));
            if (method != "sendMessage") return new { ok = true, result = (object)true };
            return new { ok = true, result = new { message_id = Interlocked.Increment(ref nextId), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                chat = new { id = body.Number("chat_id"), type = body.Number("chat_id") > 0 ? "private" : "supergroup" }, message_thread_id = body.Number("message_thread_id"), text = body.Text("text") } };
        })) { BaseAddress = new Uri("https://telegram.test/") };
        var worker = new BotWorker(c, s, new TelegramClient(tg, "t"), new OpenRouterClient(ai, c, "k"), NullLogger<BotWorker>.Instance);
        typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, 99L);
        typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, "vozduhanAIbot");
        Task Call(string name, params object[] args) => (Task)typeof(BotWorker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, args)!;
        var updateId = 0;
        Task Update(object u) => Call("Process", El(u), CancellationToken.None);
        object Dm(long from, string text) => new { message = new { message_id = ++updateId + 100, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = from, type = "private" },
            from = new { id = from, first_name = "Имя", username = "user" + from }, text } };
        int Audits(string action)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM audit WHERE action=$a"; cmd.Parameters.AddWithValue("$a", action);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        string LastAudit()
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM audit WHERE action='devnote' ORDER BY id DESC LIMIT 1";
            return (string)cmd.ExecuteScalar()!;
        }
        string LastAi(string task) => aiRequests.Last(x => x.Contains(task));
        const string IntroTask = "ЗАДАЧА: живой текст поста", WakeTask = "ЗАДАЧА: ты проснулся";

        using var cts = new CancellationTokenSource();
        var web = new AdminWeb(c, s, worker, NullLogger<AdminWeb>.Instance);
        await web.StartAsync(cts.Token);
        try
        {
            await Task.Delay(300);
            async Task<HttpClient> Login(long actor)
            {
                var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                await client.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = s.CreateWebToken(actor, "login", TimeSpan.FromMinutes(5)) }));
                client.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                return client;
            }
            using var owner = await Login(1);
            using var head = await Login(2);
            async Task<(HttpStatusCode Code, string Body)> Post(HttpClient h, string url, object body)
            {
                var r = await h.PostAsync("api/" + url, new StringContent(Json.Write(body), Encoding.UTF8, "application/json"));
                return (r.StatusCode, await r.Content.ReadAsStringAsync());
            }
            Deadline D(int id) => s.AllDeadlines().Single(d => d.Id == id);

            // 2. The developer creates a deadline with a note: the model gets it, the owner sees it, the head's answer has no trace.
            var (code, _) = await Post(owner, "deadline", new { action = "create", subjectId = "siaod", work = "Практика №4", date = "2026-12-07", devNote = Mark + " препод валит" });
            var d1 = s.AllDeadlines().Single(d => d.Work == "Практика №4");
            var intro = LastAi(IntroTask);
            Assert(code == HttpStatusCode.OK && s.DevNote(d1.Id) == Mark + " препод валит" && HasNote(intro), "New deadline lead lacks dev_note");
            Assert(intro.Contains("dev_note — закрытая заметка разработчика") && LastAi(IntroTask).Contains("не говори, что она существует"), "Deadline prompt lacks the dev_note rule");
            Assert((await owner.GetStringAsync("api/deadlines")).Contains("\"devNote\":\"" + Mark), "Owner does not see the note");
            var headList = await head.GetStringAsync("api/deadlines");
            Assert(!headList.Contains("devNote") && !headList.Contains(Mark) && headList.Contains("Практика №4"), "Head list leaks the note: " + headList);
            Console.WriteLine("PASS devnote: owner creates with a note; the model gets it; the head's list has no field and no text");

            // 3. The head moves it: the note stays, the lead still uses it; the head can neither send nor edit a note.
            var calls = aiRequests.Count;
            (code, var body) = await Post(head, "deadline", new { action = "update", targetId = d1.Id, expectedVersion = d1.Version, date = "2026-12-14", preview = true });
            Assert(code == HttpStatusCode.OK && aiRequests.Count > calls && LastAi(IntroTask).Contains(Mark) && !body.Contains(Mark), "Head move preview without the saved note");
            (code, _) = await Post(head, "deadline", new { action = "update", targetId = d1.Id, expectedVersion = d1.Version, date = "2026-12-14", intro = "Перенесли, гайс" });
            Assert(code == HttpStatusCode.OK && D(d1.Id).Version == d1.Version + 1 && s.DevNote(d1.Id) == Mark + " препод валит", "Head save touched the note");
            d1 = D(d1.Id);
            (code, body) = await Post(head, "deadline", new { action = "update", targetId = d1.Id, expectedVersion = d1.Version, date = "2026-12-21", intro = "x", devNote = "чужое" });
            var (code2, _) = await Post(head, "deadline/devnote", new { id = d1.Id, text = "чужое" });
            Assert(code == HttpStatusCode.Forbidden && body.Contains("Заметка разработчика недоступна") && code2 == HttpStatusCode.Forbidden &&
                D(d1.Id) == d1 && s.DevNote(d1.Id) == Mark + " препод валит", "Head changed the note or the deadline");
            Console.WriteLine("PASS devnote: head edits keep the note; head requests with a note are refused");

            // 4. The developer edits the note: audited; deadline, card, deliveries and wake-ups stay as they are.
            var wake = s.AddWakeup(d1.Id, DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(), "ручное", "manual");
            await Call("Publish", CancellationToken.None);
            var snapshot = Json.Write(new { d = D(d1.Id), post = s.Post("deadline:" + d1.Id), deliveries = s.PendingDeliveries(), wakeups = s.Wakeups(d1.Id) });
            var audits = Audits("devnote");
            (code, _) = await Post(owner, "deadline/devnote", new { id = d1.Id, text = "  " + Mark + " новая  " });
            Assert(code == HttpStatusCode.OK && s.DevNote(d1.Id) == Mark + " новая" && Audits("devnote") == audits + 1 &&
                LastAudit().Contains("\"before\":\"" + Mark + " препод валит\"") && LastAudit().Contains("\"after\":\"" + Mark + " новая\""), "Note edit not audited");
            await Call("Publish", CancellationToken.None);
            Assert(Json.Write(new { d = D(d1.Id), post = s.Post("deadline:" + d1.Id), deliveries = s.PendingDeliveries(), wakeups = s.Wakeups(d1.Id) }) == snapshot, "Note edit changed published state");
            (code, _) = await Post(owner, "deadline/devnote", new { id = d1.Id, text = "" });
            Assert(code == HttpStatusCode.OK && s.DevNote(d1.Id) is null, "Empty text does not clear");
            (code, _) = await Post(owner, "deadline/devnote", new { id = d1.Id, text = new string('ж', 601) });
            var (missing, _) = await Post(owner, "deadline/devnote", new { id = 99999, text = "x" });
            Assert(code == HttpStatusCode.BadRequest && missing == HttpStatusCode.NotFound && s.DevNote(d1.Id) is null, "Limits or 404 wrong");
            await Post(owner, "deadline/devnote", new { id = d1.Id, text = Mark + " препод валит" });
            Console.WriteLine("PASS devnote: owner edit is audited and leaves deadline, card, deliveries, wake-ups untouched");

            // 5. Model requests: only the lead and the wake-up text carry the note.
            s.SetWakeup(wake, "deleted");
            var due = s.AddWakeup(d1.Id, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), "напомнить", "manual");
            await Call("AmbientTick", DateTimeOffset.UtcNow, CancellationToken.None);
            Assert(s.Wakeups(d1.Id).Single(w => w.Id == due).Status == "fired" && HasNote(LastAi(WakeTask)), "Wake-up text lacks the note");
            Assert(LastAi(WakeTask).Contains("wakeup.dev_note — закрытая заметка разработчика"), "Wake-up prompt lacks the dev_note rule");
            s.Delete("wakeplan:" + d1.Id);
            await Call("PlanTick", DateTimeOffset.UtcNow, CancellationToken.None);
            await Update(Dm(1, "как дела?"));
            await Update(Dm(2, "что завтра?"));
            await Update(new { message = new { message_id = 990, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = c.GroupId, type = "supergroup" }, message_thread_id = c.ChatTopicId,
                from = new { id = 3, first_name = "Студент" }, text = "@vozduhanAIbot что сдаём?", entities = new[] { new { type = "mention", offset = 0, length = 14 } } } });
            // The head's Telegram deadline dialog: a pending draft, then the planner sees it on the next message.
            plan = Json.Write(new { intent = "deadline", deadline = new { action = "update", targetId = d1.Id, date = "2026-12-21" } });
            await Update(Dm(2, $"перенеси #{d1.Id} на 21 декабря"));
            var headDraft = Json.Write(s.Draft(2));
            plan = "{\"intent\":\"chat\"}";
            await Update(Dm(2, "а что с дедлайном?"));
            Assert(s.Draft(2) is not null && !headDraft.Contains(Mark) && !headDraft.Contains("devNote") && !headDraft.Contains("dev_note") &&
                aiRequests.Last(x => x.Contains("разбираешь личное сообщение редактора")).Contains("pending_deadline_draft"), "Head draft or planner state carries the note");
            c.Ambient.EveReminders = true;
            foreach (var key in new[] { "ambient:summary:2026-12-14", "ambient:morning:2026-12-14", "ambient:eve:2026-12-13" })
                await (Task<Publication>)typeof(BotWorker).GetMethod("Warm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, [key, CancellationToken.None])!;
            s.Set("ambient:plan:2026-12-14", Json.Write(new[] { "13:30" }));
            await (Task<Publication>)typeof(BotWorker).GetMethod("Warm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, ["ambient:spont:2026-12-14:0", CancellationToken.None])!;
            (code, _) = await Post(owner, "post-now", new { idea = "сходка" });
            var kinds = new[] { "планируешь, когда тебе", "разбираешь личное сообщение редактора", "утреннее сообщение группе в теме «Сводка»", "ЗАДАЧА: утреннее сообщение в Воздуханство",
                "ЗАДАЧА: одна короткая вводная строка", "ЗАДАЧА: ты сам решил", "ЗАДАЧА: разработчик попросил" };
            foreach (var k in kinds) Assert(aiRequests.Any(x => x.Contains(k)), "Scenario did not reach: " + k);
            Assert(aiRequests.Count(x => x.Contains("одногруппник в чате учебной группы") && !x.Contains("ЗАДАЧА:")) >= 3, "Chat contexts (two DMs and the group) not reached");
            var leaking = aiRequests.Where(x => x.Contains(Mark) && !x.Contains(IntroTask) && !x.Contains(WakeTask)).ToList();
            Assert(code == HttpStatusCode.OK && leaking.Count == 0, "The note reached other model contexts: " + string.Join("\n---\n", leaking.Select(x => x[..Math.Min(400, x.Length)])));
            Console.WriteLine("PASS devnote: model gets the note only for deadline leads and wake-ups; planners, chat and ambient contexts do not");

            // 6. Full sweep of what the head can reach after the head moves, regenerates and cancels, plus editor notifications.
            var reg = JsonDocument.Parse(await owner.GetStringAsync("api/registry")).RootElement;
            var registry = Json.Read<Registry>(reg.Field("registry").GetRawText());
            registry.Lessons = registry.Lessons.Select(l => l.SubjectId == "java" ? l with { Room = "Г-321" } : l).ToArray();
            await Post(owner, "registry", new { baseVersion = reg.Field("version").GetInt32(), registry });
            d1 = D(d1.Id);
            (code, body) = await Post(head, "deadline/regen", new { id = d1.Id, expectedVersion = d1.Version });
            var (applied, _) = await Post(head, "deadline/intro", new { id = d1.Id, expectedVersion = d1.Version, intro = "Новый текст от старосты" });
            var (cancelPreview, cancelBody) = await Post(head, "deadline", new { action = "cancel", targetId = d1.Id, expectedVersion = d1.Version, preview = true });
            var (cancelled, _) = await Post(head, "deadline", new { action = "cancel", targetId = d1.Id, expectedVersion = d1.Version, intro = "Отбой" });
            Assert(code == HttpStatusCode.OK && applied == HttpStatusCode.OK && cancelPreview == HttpStatusCode.OK && cancelled == HttpStatusCode.OK && LastAi(IntroTask).Contains(Mark), "Head flow failed");
            await Call("Publish", CancellationToken.None);
            await Task.Delay(300); // NotifyEditors runs in the background
            var headSees = new List<string> { body, cancelBody };
            foreach (var url in new[] { "", "api/me", "api/deadlines", "api/plan", "api/log", "api/log?chat=2", $"api/log?chat={c.GroupId}", "api/chats", "api/seen", "api/week?date=2026-12-14", "api/registry", "api/export" })
                headSees.Add(await head.GetStringAsync(url));
            headSees.AddRange(sent.Where(x => x.Body.Number("chat_id") == 2).Select(x => x.Body.GetRawText()));
            Assert(sent.Any(x => x.Body.Number("chat_id") == 2 && (x.Body.Text("text") ?? "").Contains("изменил реестр")), "NotifyEditors did not reach the head");
            Assert(headSees.All(x => !x.Contains(Mark) && !x.Contains("devNote") && !x.Contains("dev_note")), "Something the head sees carries the note");
            Assert((await head.GetAsync("api/ai")).StatusCode == HttpStatusCode.Forbidden && (await head.GetAsync("api/audit")).StatusCode == HttpStatusCode.NotFound, "Head reaches AI log or audit");
            Assert(s.DevNote(d1.Id) == Mark + " препод валит", "Head flow changed the note");
            Console.WriteLine("PASS devnote: nothing the head can open, and no Telegram message to the head, contains the note");

            // 7. Page: the head never receives the note UI.
            var headPage = await head.GetStringAsync("");
            var ownerPage = await owner.GetStringAsync("");
            Assert(!headPage.Contains("deadline/devnote") && !headPage.Contains("Заметка разработчика") && !headPage.Contains("data-dn") &&
                ownerPage.Contains("deadline/devnote") && ownerPage.Contains("Заметка разработчика") && ownerPage.Contains("data-dn"), "Note UI in the head's page or missing for the owner");

            // 8. The developer's Telegram draft flow works as before; records gained no note fields.
            plan = Json.Write(new { intent = "deadline", deadline = new { action = "create", subjectId = "siaod", work = "Практика №6", date = "2026-12-21" } });
            await Update(Dm(1, "добавь сдачу практики 6 на 21 декабря"));
            var ownerDraft = s.Draft(1)!;
            Assert(!Json.Write(ownerDraft).Contains("dev", StringComparison.OrdinalIgnoreCase), "Draft serialization has a note field");
            await Update(new { callback_query = new { id = "cb", from = new { id = 1 }, data = "save:" + ownerDraft.Token, message = new { message_id = 9001, chat = new { id = 1 } } } });
            var d6 = s.AllDeadlines().Single(d => d.Work == "Практика №6");
            Assert(s.DevNote(d6.Id) is null && !Json.Write(d6).Contains("dev", StringComparison.OrdinalIgnoreCase), "Telegram save broken or carries a note");
            Assert(typeof(Draft).GetProperties().Concat(typeof(Deadline).GetProperties()).All(p => !p.Name.Contains("Dev")), "Draft/Deadline gained a note field");
            Console.WriteLine("PASS devnote: the page, the developer's Telegram flow and the records stay clean");
        }
        finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        return 7;
    }
}
