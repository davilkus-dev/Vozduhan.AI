using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vozduhan.AI;

/// <summary>FIX-3: honest wake-up statuses, error isolation, planning off the hot path, stale and time-shifted warm-ups.</summary>
static partial class WakeupTests
{
    static int count;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Check(string name, Func<Task> test) { await test(); count++; Console.WriteLine("PASS " + name); }
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-12-01T09:00:00Z");

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<object>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await f(request, ct)), Encoding.UTF8, "application/json") };
    }
    sealed class Logs : ILogger<BotWorker>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) { lock (Lines) Lines.Add(format(state, e)); }
    }

    /// <summary>A worker with fake Telegram and OpenRouter; AI answers are routed by the task text of each prompt.</summary>
    sealed class Env : IDisposable
    {
        public readonly BotConfig C; public readonly BotStore S; public BotWorker W; public readonly Logs Log = new();
        readonly TelegramClient tgClient; readonly OpenRouterClient aiClient;
        public readonly List<(string Method, JsonElement Body)> Sent = [];
        public readonly List<string> Ai = [];
        public Func<string, CancellationToken, Task<string>> Plan = (_, _) => Task.FromResult("{}");
        public Func<string, Task<string>> Text = body => Task.FromResult("Гайс, сдаём СиАОД, шевелитесь");
        public Env(Func<string, BotConfig> make, string file)
        {
            C = make(file);
            C.Ambient.SpontaneousPerDay = 0; C.Ambient.EveReminders = false; C.Ambient.MorningTime = ""; C.ModelRouting = new(); // one request per AI call
            // These checks are about delivery itself: no preview window unless a test asks for it.
            C.Features.PublicationModes = BotWorker.PublicationKinds.ToDictionary(k => k, _ => BotWorker.AutoMode);
            S = new BotStore(C);
            var tg = new HttpClient(new Handler(async (request, _) =>
            {
                var method = request.RequestUri!.Segments[^1];
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
                lock (Sent) Sent.Add((method, body));
                if (method != "sendMessage") return new { ok = true, result = (object)true };
                return new { ok = true, result = new { message_id = 1000 + Sent.Count, date = 1, chat = new { id = body.Number("chat_id"), type = "supergroup" },
                    message_thread_id = body.Number("message_thread_id"), text = body.Text("text") } };
            })) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = new HttpClient(new Handler(async (request, ct) =>
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (Ai) Ai.Add(body);
                var content = body.Contains("планируешь, когда тебе") ? await Plan(body, ct) : await Text(body);
                return new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } };
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            tgClient = new TelegramClient(tg, "t"); aiClient = new OpenRouterClient(ai, C, "k");
            W = new BotWorker(C, S, tgClient, aiClient, Log);
        }
        /// <summary>A new worker on the same database and fakes, as after a service restart.</summary>
        public void Restart() => W = new BotWorker(C, S, tgClient, aiClient, Log);
        public List<string> Posts => Sent.Where(x => x.Method == "sendMessage").Select(x => x.Body.Text("text")!).ToList();
        public int PlanCalls => Ai.Count(x => x.Contains("планируешь, когда тебе"));
        public Task Tick(DateTimeOffset utc) => Call("AmbientTick", utc, CancellationToken.None);
        public Task PlanTick(DateTimeOffset utc, CancellationToken? ct = null) => Call("PlanTick", utc, ct ?? CancellationToken.None);
        public async Task<Publication> Warm(string key) => await (Task<Publication>)Method("Warm").Invoke(W, [key, CancellationToken.None])!;
        public Task Admin(string key, string action, string? text = null) => Call("AdminPublication", key, action, text!, CancellationToken.None);
        public Publication Pub(string key) => S.Publication(key)!;
        Task Call(string name, params object[] args) => (Task)Method(name).Invoke(W, args)!;
        static MethodInfo Method(string name) => typeof(BotWorker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public Deadline Add(string date, string work = "Практика №1")
        {
            var d = Deadlines.Resolve(C, new("create", SubjectId: "siaod", Work: work, Date: date), null, S.AllDeadlines(), 1, "t", T);
            Assert(d.Missing.Length == 0, string.Join("; ", d.Missing));
            return S.ConfirmDirect(1, d, T);
        }
        public Deadline Change(Deadline d, ParsedDeadline p) => S.ConfirmDirect(1, Deadlines.Resolve(C, p with { Action = "update", TargetId = d.Id }, null, S.AllDeadlines(), 1, "t", T), T);
        public Wakeup Wake(long id) => S.Wakeups().Single(w => w.Id == id);
        public void Dispose() => S.Dispose();
    }
    static string Plan(params (string At, string Reason)[] points) => Json.Write(new { wakeups = points.Select(p => new { at = p.At, reason = p.Reason }) });
    static long Unix(string utc) => DateTimeOffset.Parse(utc).ToUnixTimeSeconds();
    /// <summary>The ambient context the model received (the user message of the request).</summary>
    static JsonElement Context(string body) => JsonDocument.Parse(JsonDocument.Parse(body).RootElement.Field("messages")[1].Text("content")!).RootElement.Clone();

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        await Check("schema: old wakeups table gains attempts, note and next_try; rows survive", () =>
        {
            var c = make("w-migrate.db");
            using (var old = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE wakeups(id INTEGER PRIMARY KEY AUTOINCREMENT, deadline INTEGER NOT NULL, due INTEGER NOT NULL,
                        reason TEXT NOT NULL, source TEXT NOT NULL, status TEXT NOT NULL, created INTEGER NOT NULL, actor INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO wakeups(deadline,due,reason,source,status,created) VALUES(0,1,'старый','post','fired',1);
                    PRAGMA user_version=2;
                    """;
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var s = new BotStore(c)) Assert(s.Wakeups().Single() is { Reason: "старый", Attempts: 0, Note: null, NextTry: 0 }, "Old row lost");
            using (var again = new BotStore(c)) Assert(again.Wakeups().Count == 1, "Second start failed");
            return Task.CompletedTask;
        });
        await Check("wake-ups: the hot path never plans; each PlanTick plans exactly one deadline, nearest first", async () =>
        {
            using var e = new Env(make, "w-hot.db");
            var ds = new[] { e.Add("2026-12-14"), e.Add("2026-12-07"), e.Add("2026-12-09") };
            var id = e.S.AddWakeup(ds[1].Id, T.ToUnixTimeSeconds(), "ручное", "manual");
            await e.Tick(T.AddMinutes(1));
            Assert(e.PlanCalls == 0 && e.Wake(id).Status == "fired" && e.Posts.Count == 1, "Hot path planned or did not fire");
            foreach (var (expected, n) in new[] { (ds[1], 1), (ds[2], 2), (ds[0], 3) })
            {
                await e.PlanTick(T.AddMinutes(2));
                Assert(e.PlanCalls == n && e.S.Get("wakeplan:" + expected.Id) is not null && ds.Count(d => e.S.Get("wakeplan:" + d.Id) is not null) == n, $"PlanTick {n} planned wrong");
            }
        });

        await Check("wake-ups: fallback plan is marked default, retried after 15 min, replaced by AI, at most 3 tries per version", async () =>
        {
            using var e = new Env(make, "w-fallback.db");
            var d = e.Add("2026-12-14");
            e.Plan = (_, _) => throw new HttpRequestException("down");
            await e.PlanTick(T);
            Assert(e.S.Wakeups(d.Id).Count > 0 && e.S.Wakeups(d.Id).All(w => w.Source == "default") && e.S.Get("wakeplan:" + d.Id) == $"{d.Version}:fallback", "Fallback not marked");
            await e.PlanTick(T.AddMinutes(5));
            Assert(e.PlanCalls == 1, "Retried before 15 minutes");
            e.Plan = (_, _) => Task.FromResult(Plan(("2026-12-12 12:00", "ИИ-точка")));
            await e.PlanTick(T.AddMinutes(16));
            var pending = e.S.Wakeups(d.Id).Where(w => w.Status == "pending").ToList();
            Assert(e.PlanCalls == 2 && pending.Count == 1 && pending[0].Source == "ai" && pending[0].Reason == "ИИ-точка" && e.S.Get("wakeplan:" + d.Id) == d.Version.ToString(), "AI plan did not replace fallback");
            var d2 = e.Add("2026-12-21", "Практика №2");
            e.Plan = (_, _) => throw new HttpRequestException("down");
            for (var i = 0; i < 5; i++) await e.PlanTick(T.AddMinutes(20 + 16 * i));
            Assert(e.PlanCalls == 5, "More than 3 AI tries for one version: " + e.PlanCalls);
            d2 = e.Change(d2, new(Work: "Практика №3"));
            await e.PlanTick(T.AddHours(3));
            Assert(e.PlanCalls == 6, "New version not re-planned");
        });

        await Check("wake-ups: cancellation during planning keeps the old AI plan", async () =>
        {
            using var e = new Env(make, "w-cancel.db");
            var d = e.Add("2026-12-14");
            e.Plan = (_, _) => Task.FromResult(Plan(("2026-12-11 12:00", "старая-1"), ("2026-12-13 12:00", "старая-2")));
            await e.PlanTick(T);
            d = e.Change(d, new(Work: "Практика №5"));
            using var cts = new CancellationTokenSource();
            e.Plan = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return "{}"; };
            var running = e.PlanTick(T.AddMinutes(1), cts.Token);
            await Task.Delay(200); cts.Cancel();
            try { await running; throw new Exception("Cancelled planning completed"); } catch (OperationCanceledException) { }
            var pending = e.S.Wakeups(d.Id).Where(w => w.Status == "pending").Select(w => w.Reason).ToList();
            Assert(pending.SequenceEqual(["старая-1", "старая-2"]), "Old plan lost: " + string.Join(",", pending));
        });

        await Check("wake-ups: obsolete, silent (SKIP and rejected text), skipped when late, fired once", async () =>
        {
            using var e = new Env(make, "w-status.db");
            var d = e.Add("2026-12-14");
            var obsolete = e.S.AddWakeup(d.Id, T.ToUnixTimeSeconds(), "к отменённому", "manual");
            e.S.ConfirmDirect(1, Deadlines.Resolve(e.C, new("cancel", d.Id), null, e.S.AllDeadlines(), 1, "c", T), T);
            await e.Tick(T.AddMinutes(1));
            Assert(e.Wake(obsolete).Status == "obsolete" && e.Wake(obsolete).Note == "дедлайн отменён или уже прошёл" && e.Posts.Count == 0, "Obsolete wake-up wrong");
            var live = e.Add("2026-12-21", "Практика №2");
            e.Text = _ => Task.FromResult("SKIP");
            var skip = e.S.AddWakeup(live.Id, T.AddMinutes(2).ToUnixTimeSeconds(), "молчун", "manual");
            await e.Tick(T.AddMinutes(3));
            Assert(e.Wake(skip).Status == "silent" && e.Wake(skip).Note == "модель решила промолчать" && e.Posts.Count == 0, "SKIP not silent");
            e.Text = _ => Task.FromResult("你好你好你好");
            var rejected = e.S.AddWakeup(live.Id, T.AddMinutes(4).ToUnixTimeSeconds(), "иероглифы", "manual");
            await e.Tick(T.AddMinutes(5));
            Assert(e.Wake(rejected).Status == "silent" && e.Wake(rejected).Note!.Contains("текст не прошёл проверку: иероглифы") && e.Posts.Count == 0, "Rejected text not silent");
            var late = e.S.AddWakeup(live.Id, T.AddHours(-3).ToUnixTimeSeconds(), "проспал", "manual");
            await e.Tick(T.AddMinutes(6));
            Assert(e.Wake(late).Status == "skipped" && e.Wake(late).Note!.StartsWith("опоздало на 186 мин"), "Late wake-up: " + e.Wake(late).Note);
            e.Text = _ => Task.FromResult("Гайс, СиАОД через неделю, шевелитесь");
            var fired = e.S.AddWakeup(live.Id, T.AddMinutes(7).ToUnixTimeSeconds(), "норм", "manual");
            await e.Tick(T.AddMinutes(8)); await e.Tick(T.AddMinutes(9));
            Assert(e.Wake(fired).Status == "fired" && e.Posts.Count == 1 && e.Posts[0].Contains("шевелитесь"), "Normal wake-up not fired exactly once");
        });

        await Check("wake-ups: one failing wake-up does not block others or jobs; retries with next_try, then failed", async () =>
        {
            using var e = new Env(make, "w-isolation.db");
            e.C.Ambient.MorningTime = "08:00";
            var at = DateTimeOffset.Parse("2026-12-02T05:00:00Z"); // 08:00 MSK
            e.C.SummaryTopicId = 30;
            e.S.Set("ambient:summary:2026-12-02", "done"); // finished before the upgrade: never sent again
            e.Text = body => body.Contains("A-сломан") ? throw new HttpRequestException("down")
                : Task.FromResult(body.Contains("ЗАДАЧА: утреннее") ? "Доброе утро, гайс" : "Пост Б для группы");
            var a = e.S.AddWakeup(0, at.ToUnixTimeSeconds(), "A-сломан", "post");
            var b = e.S.AddWakeup(0, at.ToUnixTimeSeconds(), "B-целый", "post");
            await e.Tick(at.AddMinutes(1));
            Assert(e.Posts.SequenceEqual(["Доброе утро, гайс"]) && e.Pub("ambient:summary:2026-12-02").Status == "sent" && e.Pub("ambient:morning:2026-12-02").Status == "sent", "Morning not sent first");
            await e.Tick(at.AddMinutes(16));
            var pa = e.Pub("wake:" + a);
            Assert(e.Wake(b).Status == "fired" && e.Posts.Contains("Пост Б для группы"), "Other work blocked");
            Assert(e.Wake(a).Status == "pending" && pa.Attempts == 1 && pa.NextTry > at.AddMinutes(16).ToUnixTimeSeconds() && pa.Note is { Length: > 0 } && !pa.Note.Contains("down"), "Failed wake-up state: " + Json.Write(pa));
            for (var i = 0; i < 20 && e.Pub("wake:" + a).Active; i++)
                await e.Tick(DateTimeOffset.FromUnixTimeSeconds(Math.Max(e.Pub("wake:" + a).NextTry, e.Pub("wake:" + a).Due) + 1));
            Assert(e.Wake(a).Status == "failed" && e.Pub("wake:" + a) is { Status: "failed", Attempts: 5 } && e.Posts.Count == 2, "Not failed after 5 attempts: " + Json.Write(e.Pub("wake:" + a)));
        });

        await Check("warm-up: a stale AI text is regenerated, an editor's text goes as is, an old prep without basis is regenerated", async () =>
        {
            using var e = new Env(make, "w-stale.db");
            var d = e.Add("2026-12-14");
            var due = DateTimeOffset.Parse("2026-12-10T07:00:00Z");
            var w1 = e.S.AddWakeup(d.Id, due.ToUnixTimeSeconds(), "первый", "manual");
            e.Text = _ => Task.FromResult("ТЕКСТ-СТАРЫЙ про А412");
            Assert((await e.Warm("wake:" + w1)).Basis == e.W.Basis(), "Warm-up has no basis");
            d = e.Change(d, new(Room: "Б777"));
            e.Text = _ => Task.FromResult("ТЕКСТ-НОВЫЙ про Б777");
            var calls = e.Ai.Count;
            await e.Tick(due.AddMinutes(1));
            Assert(e.Posts.SequenceEqual(["ТЕКСТ-НОВЫЙ про Б777"]) && e.Ai.Count == calls + 1 && e.Ai.Last().Contains("Б777") && e.Log.Lines.Any(x => x.Contains("wake:" + w1) && x.Contains("facts changed")), "Stale prep was sent");
            // Reminders of one deadline are 2 h apart here: closer ones are moved by the 90-minute cooldown (tested separately).
            var w2 = e.S.AddWakeup(d.Id, due.AddHours(2).ToUnixTimeSeconds(), "второй", "manual");
            await e.Admin("wake:" + w2, "save", "РУЧНОЙ ТЕКСТ");
            d = e.Change(d, new(Room: "В888"));
            await e.Tick(due.AddHours(2).AddMinutes(1));
            Assert(e.Posts.Last() == "РУЧНОЙ ТЕКСТ", "Editor's text not sent as is");
            var w3 = e.S.AddWakeup(d.Id, due.AddHours(4).ToUnixTimeSeconds(), "третий", "manual");
            e.S.Set("prep:wake:" + w3, Json.Write(new { text = "БЕЗ-БАЗЫ", at = 1, source = "ai" }));
            await e.Tick(due.AddHours(4).AddMinutes(1));
            Assert(e.Posts.Last() == "ТЕКСТ-НОВЫЙ про Б777", "Prep without basis was sent");
        });

        await Check("warm-up is written for the publication moment: morning, drop and evening reminder", async () =>
        {
            using var e = new Env(make, "w-moment.db");
            e.C.Ambient.MorningTime = "08:00"; e.C.Ambient.EveReminders = true;
            var day = DateOnly.FromDateTime(e.C.LocalNow(T)).AddDays(2);
            while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
            var date = day.ToString("yyyy-MM-dd");
            e.Add(date, "Практика-в-день-публикации");
            e.S.Set($"ambient:plan:{date}", Json.Write(new[] { "13:30" }));
            foreach (var (key, time, block) in new[] { ($"ambient:morning:{date}", $"{date} 08:00", "today_deadlines"), ($"ambient:spont:{date}:0", $"{date} 13:30", "today_deadlines"),
                ($"ambient:eve:{day.AddDays(-1):yyyy-MM-dd}", $"{day.AddDays(-1):yyyy-MM-dd} 20:00", "tomorrow_deadlines") })
            {
                await e.Warm(key);
                var context = Context(e.Ai.Last());
                Assert(context.Field("extra").Text("for_time") == time, $"{key}: for_time {context.Field("extra").Text("for_time")}");
                Assert(context.Field(block).EnumerateArray().Any(x => x.Text("work") == "Практика-в-день-публикации"), $"{key}: {block} not for the publication day");
            }
        });

        await Check("wake-up plan keeps 2 h from sent and manual reminders and shows them to the planner", async () =>
        {
            using var e = new Env(make, "w-spacing.db");
            var d = e.Add("2026-12-14");
            var now = DateTimeOffset.Parse("2026-12-12T10:00:00Z"); // 13:00 MSK
            var sent = e.S.AddWakeup(d.Id, now.AddHours(-1).ToUnixTimeSeconds(), "уже-было", "ai");
            e.S.SetWakeup(sent, "fired");
            e.S.AddWakeup(d.Id, Unix("2026-12-13T09:00:00Z"), "ручное-напоминание", "manual"); // 12:00 MSK
            e.Plan = (_, _) => Task.FromResult(Plan(("2026-12-12 13:30", "рядом с отправленным"), ("2026-12-13 13:00", "рядом с ручным"), ("2026-12-14 10:00", "нормальная")));
            await e.PlanTick(now);
            var ai = e.S.Wakeups(d.Id).Where(w => w.Source == "ai" && w.Status == "pending").Select(w => w.Reason).ToList();
            var request = e.Ai.Single(x => x.Contains("планируешь, когда тебе"));
            Assert(ai.SequenceEqual(["нормальная"]), "Spacing ignored existing reminders: " + string.Join(",", ai));
            Assert(request.Contains("sent_reminders") && request.Contains("уже-было") && request.Contains("manual_reminders") && request.Contains("ручное-напоминание"), "Planner context lacks reminders");
        });

        await Check("admin: wake-up status fields and stale warm-ups; head sees deadline wake-ups only; owner can preview as head", async () =>
        {
            using var e = new Env(make, "w-web.db");
            var port = 19000 + Random.Shared.Next(1000);
            e.C.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            e.S.CommitRegistry(1, "t", null, cur => RegistryOps.ApplyAll(cur, [JsonSerializer.SerializeToElement(new { op = "knowledge_set", text = "СЕКРЕТ-ЗНАНИЯ" }, Json.Options)], 1, true));
            var d = e.Add("2026-12-21");
            var future = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds();
            var post = e.S.AddWakeup(0, future, "пост-разработчика", "post", 1);
            var broken = e.S.AddWakeup(d.Id, future, "к дедлайну", "manual");
            var fresh = e.S.AddWakeup(d.Id, future + 3600, "свежий", "manual");
            e.S.EnsurePublication("wake:" + broken, "wakeup", future, 0, BotWorker.AutoPreview);
            e.S.RetryPublication("wake:" + broken, 2, future, "ошибка: таймаут");
            e.S.SetPublicationText("wake:" + broken, "старый текст", "ai", "old-basis");
            e.S.EnsurePublication("wake:" + fresh, "wakeup", future + 3600, 0, BotWorker.AutoPreview);
            e.S.SetPublicationText("wake:" + fresh, "свежий текст", "ai", e.W.Basis());
            using var cts = new CancellationTokenSource();
            var web = new AdminWeb(e.C, e.S, e.W, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminWeb>.Instance);
            await web.StartAsync(cts.Token);
            try
            {
                await Task.Delay(300);
                async Task<HttpClient> Login(long actor)
                {
                    var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                    await client.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = e.S.CreateWebToken(actor, "login", TimeSpan.FromMinutes(5)) }));
                    client.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                    return client;
                }
                async Task<JsonElement> Get(HttpClient h, string url) => JsonDocument.Parse(await h.GetStringAsync(url)).RootElement.Clone();
                static List<JsonElement> Wakes(JsonElement plan) => plan.Field("wakeups").EnumerateArray().ToList();
                StringContent Body(object o) => new(Json.Write(o), Encoding.UTF8, "application/json");
                using var owner = await Login(1);
                using var head = await Login(2);
                var ownerWakes = Wakes(await Get(owner, "api/plan"));
                var b = ownerWakes.Single(w => w.Field("id").GetInt64() == broken);
                Assert(ownerWakes.Any(w => w.Field("id").GetInt64() == post) && b.Text("note") == "ошибка: таймаут" && b.Field("attempts").GetInt32() == 2, "Owner plan lacks fields");
                Assert(b.Field("prep").Field("stale").GetBoolean() && !ownerWakes.Single(w => w.Field("id").GetInt64() == fresh).Field("prep").Field("stale").GetBoolean(), "Stale flag wrong");
                var headWakes = Wakes(await Get(head, "api/plan"));
                Assert(!headWakes.Any(w => w.Field("id").GetInt64() == post) && headWakes.Single(w => w.Field("id").GetInt64() == broken).Text("note") == "ошибка: таймаут" &&
                    headWakes.All(w => w.Field("prep").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined), "Head plan wrong");
                Assert((await head.PostAsync("api/view", Body(new { @as = "head" }))).StatusCode == HttpStatusCode.NotFound, "Head can switch views");
                Assert((await owner.PostAsync("api/view", Body(new { @as = "head" }))).IsSuccessStatusCode, "Owner cannot preview as head");
                var me = await Get(owner, "api/me");
                Assert(!me.Field("owner").GetBoolean() && me.Field("asHead").GetBoolean() && me.Field("realOwner").GetBoolean(), "Preview identity wrong: " + me);
                Assert(!(await owner.GetStringAsync("api/registry")).Contains("СЕКРЕТ") && !(await owner.GetStringAsync("")).Contains("VIEWS.knowledge") &&
                    (await owner.GetAsync("api/raw?id=1")).StatusCode == HttpStatusCode.Forbidden && !Wakes(await Get(owner, "api/plan")).Any(w => w.Field("id").GetInt64() == post), "Preview shows owner data");
                var reg = await Get(owner, "api/registry");
                var write = await owner.PostAsync("api/registry", Body(new { baseVersion = reg.Field("version").GetInt32(), registry = reg.Field("registry") }));
                var dry = await owner.PostAsync("api/registry?dry=1", Body(new { baseVersion = reg.Field("version").GetInt32(), registry = reg.Field("registry") }));
                Assert(write.StatusCode == HttpStatusCode.Forbidden && (await write.Content.ReadAsStringAsync()).Contains("Просмотр как староста") && dry.IsSuccessStatusCode, "Preview writes or dry run blocked");
                Assert((await owner.PostAsync("api/view", Body(new { @as = "owner" }))).IsSuccessStatusCode && (await Get(owner, "api/me")).Field("owner").GetBoolean() &&
                    (await owner.GetStringAsync("")).Contains("VIEWS.knowledge"), "Owner could not switch back");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        });
        await RunFix6(make);
        await RunPublications(make);
        return count;
    }
}
