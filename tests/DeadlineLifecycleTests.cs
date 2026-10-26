using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Block 1: C1 finished card in place, C2 deleting a mistaken deadline with its posts.</summary>
static class DeadlineLifecycleTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Handler(Func<HttpRequestMessage, Task<object>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await f(request)), Encoding.UTF8, "application/json") };
    }
    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var c = make("lifecycle.db");
        c.SummaryTopicId = 0; c.Ambient.Enabled = false;
        var port = 22000 + Random.Shared.Next(1000);
        c.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
        using var s = new BotStore(c);
        var calls = new List<(string Method, JsonElement Body)>();
        var undeletable = new HashSet<long>();
        var flaky = 0;
        var nextId = 500;
        var tg = new HttpClient(new Handler(async request =>
        {
            var method = request.RequestUri!.Segments[^1];
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            if (method == "deleteMessage" && flaky > 0) { flaky--; throw new HttpRequestException("network"); }
            lock (calls) calls.Add((method, body));
            if (method == "editMessageText" && body.Number("message_id") == 404)
                return new { ok = false, error_code = 400, description = "Bad Request: message to edit not found" };
            if (method == "deleteMessage" && undeletable.Contains(body.Number("message_id")))
                return new { ok = false, error_code = 400, description = "Bad Request: message can't be deleted" };
            if (method != "sendMessage") return new { ok = true, result = (object)true };
            return new { ok = true, result = new { message_id = Interlocked.Increment(ref nextId), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                chat = new { id = body.Number("chat_id"), type = "supergroup" }, message_thread_id = body.Number("message_thread_id"), text = body.Text("text") } };
        })) { BaseAddress = new Uri("https://telegram.test/") };
        var ai = new HttpClient(new Handler(_ => Task.FromResult<object>(new { choices = new[] { new { message = new { content = "ok" }, finish_reason = "stop" } } })))
            { BaseAddress = new Uri("https://openrouter.test/") };
        var worker = new BotWorker(c, s, new TelegramClient(tg, "t"), new OpenRouterClient(ai, c, "k"), NullLogger<BotWorker>.Instance);
        Task Publish() => (Task)typeof(BotWorker).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, [CancellationToken.None])!;
        string Info() => (string)typeof(BotWorker).GetMethod("Info", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, ["/deadlines", new[] { "/deadlines" }, false, false])!;
        Deadline D(int id) => s.AllDeadlines().Single(d => d.Id == id);
        Deadline Save(ParsedDeadline p, DateTimeOffset at, long actor = 1)
        {
            var draft = Deadlines.Resolve(c, p, null, s.AllDeadlines(), actor, "test", at);
            Assert(draft.Missing.Length == 0, string.Join("; ", draft.Missing));
            return s.ConfirmDirect(actor, draft with { Intro = "Гайс, сдаём" }, at);
        }
        var utc = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(c.LocalNow(utc));
        var past = today.AddDays(-7); while (past.DayOfWeek != DayOfWeek.Monday) past = past.AddDays(-1);
        var future = today.AddDays(14); while (future.DayOfWeek != DayOfWeek.Monday) future = future.AddDays(1);
        var then = c.Utc(past.ToDateTime(new TimeOnly(9, 0)));

        // C1. Cards posted by the old version for deadlines that have since finished.
        var live = Save(new(SubjectId: "siaod", Work: "Практика №1", Date: past.ToString("yyyy-MM-dd")), then);
        var manual = Save(new(SubjectId: "siaod", Work: "Практика №2", Date: past.ToString("yyyy-MM-dd")), then);
        var gone = Save(new(SubjectId: "siaod", Work: "Практика №3", Date: past.ToString("yyyy-MM-dd")), then);
        var lost = Save(new(SubjectId: "siaod", Work: "Практика №4", Date: past.ToString("yyyy-MM-dd")), then);
        s.SavePost("deadline:" + live.Id, 401, Deadlines.Card(c, live, then));
        s.SavePost("deadline:" + lost.Id, 404, Deadlines.Card(c, lost, then));
        s.SavePost("deadline:" + manual.Id, 402, "поправлено руками"); s.Set($"manual_msg:{c.GroupId}:402", "1");
        s.SavePost("deadline:" + gone.Id, 403, Deadlines.Card(c, gone, then)); s.Set($"deleted_msg:{c.GroupId}:403", "1");
        calls.Clear();
        await Publish();
        var edits = calls.Where(x => x.Method == "editMessageText" && x.Body.Number("message_id") != 404).ToList();
        Assert(edits.Count == 2 && edits.All(e => e.Body.Text("text")!.Contains("ДЕДЛАЙН ПРОШЕЛ") && e.Body.Text("text")!.Contains("<s>")), "Finished cards not restyled");
        Assert(edits.Select(e => e.Body.Number("message_id")).Order().SequenceEqual([401L, 402L]) && s.Get($"manual_msg:{c.GroupId}:402") is null, "Manual card kept or deleted card touched");
        Assert(calls.All(x => x.Method != "sendMessage") && s.Post("deadline:" + gone.Id)!.Text == Deadlines.Card(c, gone, then), "Finished deadline re-posted or deleted post changed");
        Assert(s.Post("deadline:" + lost.Id) is null, "Missing finished card not forgotten");
        Assert(s.Post("deadline:" + live.Id)!.Text == Deadlines.Card(c, live, DateTimeOffset.UtcNow) && Deadlines.Card(c, live, DateTimeOffset.UtcNow).StartsWith("Гайс, сдаём"), "Stored card or lead differs");
        calls.Clear();
        await Publish();
        Assert(calls.Count(x => x.Method is "editMessageText" or "sendMessage") == 0, "Second step changed finished cards again");
        Console.WriteLine("PASS lifecycle: finished cards are edited in place once, nothing new is sent, deleted posts stay untouched");

        // C2. A mistaken deadline with a card, sent change news, a pending news, a wake-up and a morning post.
        var basis0 = worker.Basis();
        const string Mark = "Ошибочная №77";
        var wrong = Save(new(SubjectId: "siaod", Work: Mark, Date: future.ToString("yyyy-MM-dd")), utc);
        var moved = future.AddDays(7).ToString("yyyy-MM-dd");
        var v2 = Save(new("update", wrong.Id, Date: moved), utc);
        await Publish();
        var news = s.Posts($"change:{wrong.Id}:").Single();
        var card = s.Post("deadline:" + wrong.Id)!;
        var v3 = Save(new("update", wrong.Id, Date: future.ToString("yyyy-MM-dd")), utc);
        s.AddWakeup(wrong.Id, utc.AddDays(3).ToUnixTimeSeconds(), "напомнить", "manual", 1);
        var conversation = new Conversation(c, s);
        var focus = El(new { message_id = 1, date = utc.ToUnixTimeSeconds(), chat = new { id = c.GroupId, type = "supergroup" }, message_thread_id = c.ChatTopicId,
            from = new { id = 3, first_name = "Студент" }, text = "что сдаём?" });
        string[] Views() => [Info(), conversation.AmbientContext("summary"), conversation.Context(focus, false), Json.Write(conversation.PlannerState(1, null, null))];
        Assert(Views().All(v => v.Contains(Mark)) && worker.Basis() != basis0, "Deadline not visible before deletion");
        Assert(s.PendingDeliveries().Any(d => d.Key.StartsWith($"change:{wrong.Id}:")) && s.PendingDeliveries().Any(d => d.Key == "morning:" + v3.Date), "Setup lacks pending news or morning post");
        try { s.DeleteDeadline(3, wrong.Id, v3.Version, utc); throw new Exception("Member deleted a deadline"); } catch (InvalidOperationException) { }
        try { s.DeleteDeadline(1, wrong.Id, v2.Version, utc); throw new Exception("Stale version deleted"); } catch (InvalidOperationException) { }

        using var cts = new CancellationTokenSource();
        var web = new AdminWeb(c, s, worker, NullLogger<AdminWeb>.Instance);
        await web.StartAsync(cts.Token);
        try
        {
            await Task.Delay(300);
            using var head = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            await head.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = s.CreateWebToken(2, "login", TimeSpan.FromMinutes(5)) }));
            head.DefaultRequestHeaders.Add("X-Vozduhan", "1");
            async Task<(HttpStatusCode Code, JsonElement Body)> Post(object body)
            {
                var r = await head.PostAsync("api/deadline/delete", new StringContent(Json.Write(body), Encoding.UTF8, "application/json"));
                return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
            }
            var (code, dry) = await Post(new { id = wrong.Id, expectedVersion = v3.Version, dry = true });
            Assert(code == HttpStatusCode.OK && dry.Flag("card") && dry.Number("news") == 1 && dry.Number("wakeups") == 1 && D(wrong.Id).Status == "active", "Dry run wrong or deleted");
            (code, _) = await Post(new { id = wrong.Id, expectedVersion = v2.Version });
            Assert(code == HttpStatusCode.Conflict && D(wrong.Id).Status == "active", "Stale web delete not rejected");
            (code, _) = await Post(new { id = wrong.Id, expectedVersion = v3.Version });
            Assert(code == HttpStatusCode.OK, "Head could not delete");
            var deleted = D(wrong.Id);
            Assert(deleted.Status == "deleted" && deleted.Version == v3.Version + 1 && deleted.Work == Mark, "Status or version wrong, row lost");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath))
            {
                db.Open();
                using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT actor,payload FROM audit WHERE action='delete'";
                using var r = cmd.ExecuteReader();
                Assert(r.Read() && r.GetInt64(0) == 2 && Json.Read<JsonElement>(r.GetString(1)).Field("before").Number("version") == v3.Version && !r.Read(), "Audit row missing");
            }
            Assert(!s.PendingDeliveries().Any(d => d.Key.StartsWith($"change:{wrong.Id}:") || d.Key == "morning:" + v3.Date), "Pending news or morning post survived");
            Assert(s.Wakeups(wrong.Id).All(w => w.Status != "pending"), "Pending wake-up survived");
            Assert(Views().All(v => !v.Contains(Mark)) && worker.Basis() == basis0, "Deleted deadline still in a view or Basis");
            var list = await head.GetStringAsync("api/deadlines");
            var all = await head.GetStringAsync("api/deadlines?deleted=1");
            Assert(!list.Contains(Mark) && all.Contains(Mark) && all.Contains("\"deleted\""), "Web list filter wrong");
            Assert((await Post(new { id = wrong.Id, expectedVersion = deleted.Version })).Code == HttpStatusCode.NotFound, "Deleted twice");
        }
        finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        try { Save(new("update", wrong.Id, Work: "воскрес"), utc); throw new Exception("Deleted deadline updated"); } catch (InvalidOperationException) { }
        Console.WriteLine("PASS lifecycle: delete checks roles and version, audits, cancels news, morning and wake-ups, hides from every view");

        calls.Clear();
        flaky = 1;
        await Publish();
        Assert(calls.Count(x => x.Method == "deleteMessage") == 1 && s.Posts($"change:{wrong.Id}:").Count + (s.Post("deadline:" + wrong.Id) is null ? 0 : 1) == 1, "Network failure lost a post");
        await Publish();
        var removed = calls.Where(x => x.Method == "deleteMessage").Select(x => x.Body.Number("message_id")).ToHashSet();
        Assert(removed.SetEquals([card.MessageId, news.MessageId]) && s.Post("deadline:" + wrong.Id) is null && s.Posts($"change:{wrong.Id}:").Count == 0, "Posts not deleted or not forgotten");
        Assert(calls.All(x => x.Method is "deleteMessage" || x.Body.Number("chat_id") == c.OwnerId), "Deletion sent or edited something in the group");

        var old = Save(new(SubjectId: "java", Work: "Старая ошибка", Date: future.ToString("yyyy-MM-dd")), utc);
        await Publish();
        var oldCard = s.Post("deadline:" + old.Id)!;
        s.Pinned(oldCard.Key, true);
        undeletable.Add(oldCard.MessageId);
        s.DeleteDeadline(1, old.Id, old.Version, utc);
        calls.Clear();
        await Publish();
        Assert(calls.Any(x => x.Method == "editMessageText" && x.Body.Number("message_id") == oldCard.MessageId && x.Body.Text("text") == "🗑 Запись удалена как ошибочная.") &&
            calls.Any(x => x.Method == "unpinChatMessage" && x.Body.Number("message_id") == oldCard.MessageId) && s.Post(oldCard.Key) is null, "400 fallback missing");
        calls.Clear();
        await Publish();
        Assert(calls.Count == 0, "Removed posts touched again");

        var again = Save(new(SubjectId: "siaod", Work: Mark, Date: future.ToString("yyyy-MM-dd")), utc);
        Assert(again.Id != wrong.Id && again.Status == "active", "Same deadline cannot be created again");
        Console.WriteLine("PASS lifecycle: posts of a deleted deadline are removed with retry, 400 edits and unpins, the same deadline can be re-created");
        return 3;
    }
}
