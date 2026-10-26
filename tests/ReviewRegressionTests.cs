using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

static class ReviewRegressionTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static JsonElement El(object value) => JsonSerializer.SerializeToElement(value, Json.Options);
    static Task Call(BotWorker w, string method, params object?[] args) =>
        (Task)typeof(BotWorker).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args)!;

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var c = make("review.db");
        using var store = new BotStore(c);
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(c.LocalNow(now)).AddDays(14);
        while (date.DayOfWeek != DayOfWeek.Monday) date = date.AddDays(1);
        Deadline Save(ParsedDeadline p, Draft? previous = null)
        {
            var draft = Deadlines.Resolve(c, p, previous, store.AllDeadlines(), 1, "test", now);
            Assert(draft.Missing.Length == 0, string.Join("; ", draft.Missing));
            return store.ConfirmDirect(1, draft, now);
        }
        var first = Save(new(SubjectId: "siaod", Work: "№1", Date: date.ToString("yyyy-MM-dd"), Room: "CUSTOM", Note: "BACKSTAGE"));
        var second = Save(new("create", first.Id, "java", "№2", date.ToString("yyyy-MM-dd")));
        Assert(second.Id != first.Id && store.AllDeadlines().Count == 2, "Create overwrote an existing deadline");
        var unrelated = Deadlines.Resolve(c, new(SubjectId: "java", Work: "WRONG", Date: date.AddDays(7).ToString("yyyy-MM-dd")), null, [], 1, "", now);
        var updated = Save(new("update", first.Id, Work: "№3"), unrelated);
        Assert(updated.SubjectId == first.SubjectId && updated.Date == first.Date && updated.Room == "CUSTOM" && updated.Note == "BACKSTAGE", "Unrelated draft contaminated edit, room or note lost");
        Console.WriteLine("PASS review: create isolation, unrelated drafts, custom room and note preservation");

        var lesson = c.R.Lessons.First(l => l.SubjectId == "java");
        var exceptionDate = date.AddDays(7).ToString("yyyy-MM-dd");
        var r = c.R.Clone();
        r.Changes = [new(exceptionDate, "cancel") { Id = "C1", LessonId = lesson.Id }];
        var split = RegistryOps.ApplyAll(r, [El(new { op = "lesson_update", lessonId = lesson.Id, from = date.ToString("yyyy-MM-dd"), room = "NEW" })], 1, true).Result;
        Assert(Schedule.Day(split, DateOnly.Parse(exceptionDate)).Single(s => s.SubjectId == "java").Status == "cancelled", "Split lost future cancellation");
        r.Changes = [new(date.ToString("yyyy-MM-dd"), "modify") { Id = "C9", LessonId = lesson.Id, Room = "OLD" }, new(date.ToString("yyyy-MM-dd"), "modify") { Id = "C10", LessonId = lesson.Id, Room = "NEW" }];
        Assert(Schedule.Day(r, date).Single(s => s.SubjectId == "java").Room == "NEW", "Overrides sorted lexically");
        var version = store.RegistryVersion();
        store.SaveRegistryDraft(new("stale", 1, version, "ops", [Json.Write(new { op = "period_set", number = 2, start = "10:45", end = "12:15" })], [], "test"));
        store.CommitRegistry(1, "newer", version, cur => RegistryOps.ApplyAll(cur, [El(new { op = "period_set", number = 2, start = "10:50", end = "12:20" })], 1, true));
        try { store.ConfirmRegistryDraft(1, "stale"); throw new Exception("Stale registry confirmed"); } catch (RegistryConflict) { }
        Console.WriteLine("PASS review: future exceptions, override order and stale registry confirmation");

        var card = "<blockquote><b>Subject</b> — " + new string('x', 287) + "\n📅 <b>26.10</b>\n🚪 <code>A101</code></blockquote>";
        var html = string.Join("\n", Enumerable.Repeat(card, 12));
        var chunks = BotWorker.SplitPost(html).ToArray();
        Assert(chunks.Length > 1 && chunks.All(p => p.Length <= 4096 && TelegramHtml.Sanitize(p) == p), "HTML split is unbalanced");
        Assert(string.Concat(chunks.Select(TelegramHtml.Plain)) == TelegramHtml.Plain(html), "Splitting dropped text");
        Console.WriteLine("PASS review: long HTML is balanced and lossless");

        var sent = new List<JsonElement>();
        var fail = true;
        using var http = new HttpClient(new Handler(async req =>
        {
            var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync()).RootElement.Clone();
            if (req.RequestUri!.AbsolutePath.EndsWith("sendMessage"))
            {
                if (body.Text("text") == "second" && fail) { fail = false; throw new HttpRequestException("temporary"); }
                sent.Add(body);
                return new { ok = true, result = new { message_id = sent.Count + 100, date = now.ToUnixTimeSeconds(), chat = new { id = body.Number("chat_id"), type = "supergroup" }, message_thread_id = body.Number("message_thread_id"), text = body.Text("text") } };
            }
            return new { ok = true, result = true };
        })) { BaseAddress = new Uri("https://telegram.test/") };
        var tg = new TelegramClient(http, "test");
        var worker = new BotWorker(c, store, tg, new OpenRouterClient(http, c, null), NullLogger<BotWorker>.Instance);
        var info = (string)typeof(BotWorker).GetMethod("Info", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, ["/deadlines", new[] { "/deadlines" }, false, false])!;
        Assert(!info.Contains("BACKSTAGE"), "Public deadlines leaked note");
        await Call(worker, "MorningSummary", date, CancellationToken.None, OpenRouterClient.Skip);
        await Call(worker, "EveReminder", date, CancellationToken.None, OpenRouterClient.Skip);
        Assert(sent.Count == 0, "SKIP posted content");
        c.Ambient.MorningTime = ""; c.Ambient.SpontaneousPerDay = 0; c.Ambient.EveReminders = false;
        foreach (var d in store.AllDeadlines()) store.Set("wakeplan:" + d.Id, d.Version.ToString());
        var id = store.AddWakeup(0, now.ToUnixTimeSeconds(), "manual", "post");
        worker.SavePrep("wake:" + id, "first\n||\nsecond", "manual");
        c.SummaryTopicId = 0;
        await Call(worker, "AmbientTick", now, CancellationToken.None);
        var failed = store.Wakeups().Single(w => w.Id == id);
        Assert(failed.Status == "pending" && failed.Attempts == 1 && failed.NextTry > now.ToUnixTimeSeconds() && failed.Note is { Length: > 0 } && worker.Prepared("wake:" + id) is not null, "Failed wakeup lost");
        await Call(worker, "AmbientTick", now, CancellationToken.None);
        Assert(sent.Count == 1, "Retried before next_try");
        await Call(worker, "AmbientTick", now.AddMinutes(3), CancellationToken.None);
        Assert(sent.Count == 2 && store.Wakeups().Single(w => w.Id == id).Status == "fired", "Retry duplicated a sent part");
        var job = "review-job";
        try { await Call(worker, "Job", job, date, "08:00", date.ToDateTime(new TimeOnly(8, 1)), (Func<string, Task>)(_ => throw new HttpRequestException())); } catch (HttpRequestException) { }
        Assert(store.Get(job) is null, "Failed job marked done");
        await Call(worker, "Job", job, date, "08:00", date.ToDateTime(new TimeOnly(8, 1)), (Func<string, Task>)(_ => Task.CompletedTask));
        Assert(store.Get(job) == "done", "Retried job not completed");
        Console.WriteLine("PASS review: public notes, SKIP, failed jobs and multipart wakeup retry");
        fail = true;
        store.Set("postdraft:1", Json.Write(new { token = "retry-post", text = "first\n||\nsecond" }));
        var cb = El(new { id = "cb", from = new { id = 1 }, message = new { message_id = 700, chat = new { id = 1 } } });
        var before = sent.Count;
        try { await Call(worker, "PostCallback", cb, "achat", "retry-post", CancellationToken.None); } catch (HttpRequestException) { }
        Assert(store.Get("postdraft:1")!.Contains("retry-post"), "Failed manual post discarded");
        await Call(worker, "PostCallback", cb, "achat", "retry-post", CancellationToken.None);
        Assert(sent.Count == before + 2 && store.Get("postdraft:1") == "{}", "Manual post retry duplicated content");
        before = sent.Count;
        var message = El(new { message_id = 20, chat = new { id = c.GroupId, type = "supergroup" }, message_thread_id = c.ChatTopicId });
        await Call(worker, "Respond", message, "long-command", new string('x', 9000), CancellationToken.None, null, false);
        Assert(sent.Count == before + 3 && sent.Skip(before).All(p => p.Text("text")!.Length <= 4096), "Long command failed to split");
        var oldMessage = El(new { message_id = 800, date = now.ToUnixTimeSeconds(), chat = new { id = c.GroupId, type = "supergroup" }, message_thread_id = c.ChatTopicId, text = "OLD" });
        store.Remember(c.GroupId, c.ChatTopicId, 800, now.ToUnixTimeSeconds(), oldMessage.GetRawText());
        var edited = El(new { message_id = 800, date = now.ToUnixTimeSeconds(), chat = new { id = c.GroupId, type = "supergroup" }, message_thread_id = c.ChatTopicId, text = "NEW" });
        var observer = typeof(BotWorker).GetMethod("LogOutgoing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        observer.Invoke(worker, ["editMessageText", new { chat_id = c.GroupId, message_id = 800, text = "NEW" }, edited]);
        Assert(store.Message(c.GroupId, 800)!.Contains("NEW"), "Edit left stale AI history");
        await Call(worker, "DeleteSent", c.GroupId, 800, CancellationToken.None);
        Assert(store.Message(c.GroupId, 800) is null && store.Get($"deleted_msg:{c.GroupId}:800") is not null, "Deletion left history or omitted tombstone");
        store.SavePost("deadline:" + first.Id, 800, Deadlines.Card(c, store.AllDeadlines().Single(d => d.Id == first.Id), now));
        await Call(worker, "Publish", CancellationToken.None);
        Assert(store.Post("deadline:" + first.Id)!.MessageId == 800, "Publisher resurrected manually deleted card");
        Console.WriteLine("PASS review: manual post retry, long commands, edited/deleted history and deletion persistence");
        return 5;
    }

    sealed class Handler(Func<HttpRequestMessage, Task<object>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await response(request)), Encoding.UTF8, "application/json") };
    }
}
