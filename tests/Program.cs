using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

Json.Options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
var count = 0;
var temp = Path.Combine(Path.GetTempPath(), "vozduhan-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
BotConfig Config(string file = "state.db") => new()
{
    OwnerId = 1, GroupId = -100123, ChatTopicId = 10, DeadlinesTopicId = 20,
    DataPath = Path.Combine(temp, file), WeekOneMonday = "2026-09-07", ReplyDelayScale = 0, ReactionChance = 1,
    Web = new() { Enabled = false },
    Members = [new(1, "Разработчик Тестовый", "Михаил", "developer", 1), new(2, "Староста Тестовый", "Никита", "head", 2, "head"),
        new(3, "Студент Тестовый", "Студент", "member", 3)],
    Subjects = [new("siaod", "СиАОД", ["сиад", "сиаод"]), new("java", "Java", ["джава"])],
    Periods = [new(2, "10:40", "12:10"), new(6, "18:00", "19:30")],
    Lessons = [new("siaod", 1, 6) { Room = "А412" }, new("siaod", 3, 2) { Room = "А412" }, new("java", 1, 2) { Room = "Б201" }]
};
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Check(string name, Action test) { test(); count++; Console.WriteLine("PASS " + name); }
void Throws(Action f) { try { f(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
JsonElement Element(object obj) => JsonSerializer.SerializeToElement(obj, Json.Options);
JsonElement Message(long chat, int topic, int id, long actor, string text, object? reply = null, object? quote = null, object[]? entities = null) =>
    Element(new { message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        chat = new { id = chat, type = chat > 0 ? "private" : "supergroup" }, message_thread_id = topic,
        from = new { id = actor, first_name = "Telegram alias", username = "alias", is_bot = actor == 99 },
        text, reply_to_message = reply, quote, entities });
Draft Make(BotStore s, string work, string date = "2026-11-30", long actor = 1) =>
    Deadlines.Resolve(s.Config, new("create", SubjectId: "siaod", Work: work, Date: date), null, s.AllDeadlines(), actor, "test source", now);
Deadline Save(BotStore s, Draft d) { Assert(d.Missing.Length == 0, string.Join("; ", d.Missing)); s.SaveDraft(d); return s.Confirm(d.ActorId, d.Token, now); }

try
{
    Check("configuration and numeric role checks", () =>
    {
        var c = Config(); c.Validate();
        Assert(c.CanEdit(1) && c.CanEdit(2) && !c.CanEdit(3) && !c.AllowedUser(999), "Role isolation failed");
        var invalid = Config(); invalid.GroupId = 0;
        try { invalid.Validate(); throw new Exception("Unconfigured bot must fail closed"); } catch (InvalidDataException) { }
    });
    Check("unbound roster members retain their number without granting account access", () =>
    {
        var c = Config();
        c.Members = [.. c.Members, new(null, "Монастырев Андрей Николаевич", "Андрей", ListNumber: 23),
            new(null, "Другой Участник", "Участник", ListNumber: 24)];
        c.Validate();
        Assert(c.Members.Single(m => m.ListNumber == 23).FullName == "Монастырев Андрей Николаевич", "Roster slot lost");
        Assert(!c.AllowedUser(0) && !c.CanEdit(0) && c.FindMember(0) is null && !c.AllowedUser(999), "Unbound entry grants access");
        c.Members = [.. c.Members, c.Members[0] with { ListNumber = 25 }];
        try { c.Validate(); throw new Exception("Duplicate bound ID accepted"); } catch (InvalidDataException) { }
        c = Config(); c.Members = c.Members.Select(m => m.Role == "head" ? m with { UserId = null } : m).ToArray();
        try { c.Validate(); throw new Exception("Unbound head accepted"); } catch (InvalidDataException) { }
    });
    Check("future email data persists but is excluded from conversational context", () =>
    {
        var c = Config("email.db");
        c.Members = c.Members.Select(m => m.UserId == 2 ? m with { ShortName = "Дима", Email = "head.future@example.test" } : m).ToArray();
        using var s = new BotStore(c);
        Assert(Json.Read<BotConfig>(s.Get("registry")!).FindMember(2)!.Email == "head.future@example.test", "Email not persisted");
        var context = new Conversation(c, s).Context(Message(c.GroupId, c.ChatTopicId, 1, 2, "Привет"), false);
        Assert(!context.Contains("head.future@example.test") && !context.Contains("\"email\""), "Stored email leaked into the AI prompt");
        Assert(context.Contains("Дима"), "Conversational short name missing");
    });
    Check("schedule fills fixed room and bell time, never invents work number", () =>
    {
        var c = Config();
        var d = Deadlines.Resolve(c, new(SubjectId: "siaod", Work: "Практика №11", Date: "2026-11-30"), null, [], 1, "source", now);
        Assert(d.Missing.Length == 0 && d.Period == 6 && d.Start == "18:00" && d.Room == "А412", "Schedule resolution failed");
        var generic = Deadlines.Resolve(c, new(SubjectId: "siaod", Work: "практику", Date: "2026-11-30"), null, [], 1, "source", now);
        Assert(generic.Missing.Length > 0, "Missing practice number silently accepted");
        var missing = Deadlines.Resolve(c, new(), null, [], 1, "source", now);
        Assert(missing.Missing.Length >= 3, "Missing fields not detected");
    });
    Check("ambiguous lessons require an explicit period; parity and overrides work", () =>
    {
        var c = Config(); c.Lessons = [new("siaod", 1, 6) { Room = "А412" }, new("siaod", 1, 2) { Room = "А412" }];
        Assert(Deadlines.Resolve(c, new(SubjectId: "siaod", Work: "№11", Date: "2026-11-30"), null, [], 1, "", now).Missing.Length > 0, "Ambiguous lesson chosen");
        c.Lessons = [new("siaod", 1, 6, "odd") { Id = "L1", Room = "А412" }, new("siaod", 1, 2, "even") { Id = "L2", Room = "А412" }];
        c.Use(c.Seed());
        Assert(Deadlines.Resolve(c, new(SubjectId: "siaod", Work: "№11", Date: "2026-11-30"), null, [], 1, "", now).Period == 6, "Wrong academic parity");
        c.R.Changes = [new ScheduleChange("2026-11-30", "modify") { Id = "C1", LessonId = "L1", Period = 2, Room = "Б777" }];
        var moved = Deadlines.Resolve(c, new(SubjectId: "siaod", Work: "№11", Date: "2026-11-30"), null, [], 1, "", now);
        Assert(moved.Period == 2 && moved.Room == "Б777", "One-off change ignored");
    });
    Check("durable inbox deduplication, actor order and independent moderation", () =>
    {
        var c = Config("inbox.db");
        using (var s = new BotStore(c))
        {
            s.Ingest(100, 1, "{}", 0); s.Ingest(100, 1, "{}", 0); s.Ingest(101, 1, "{}", 0);
            s.Ingest(102, 1, "{}", 1);
            Assert(s.Claim()!.Id == 100 && s.Claim() is null, "Actor order broken");
            Assert(s.Claim(1)!.Id == 102, "Moderation blocked by chat generation");
        }
        using (var s = new BotStore(c))
        {
            Assert(s.Claim()!.Id == 100, "Running event not recovered after restart");
            s.Finish(100); Assert(s.Claim()!.Id == 101, "Duplicate update created duplicate work");
            Assert(s.Get("offset") == "103", "Offset not persisted");
        }
    });
    Check("deadline transactions, permissions, double click and duplicate protection", () =>
    {
        using var s = new BotStore(Config("roles.db"));
        var bad = Make(s, "Практика №11", actor: 3); s.SaveDraft(bad);
        Throws(() => s.Confirm(3, bad.Token, now));
        var good = Make(s, "Практика №11"); var d = Save(s, good);
        Assert(d.Id == 1 && d.Room == "А412", "Deadline not committed");
        Throws(() => s.Confirm(1, good.Token, now));
        var duplicate = Make(s, "Практика №11"); s.SaveDraft(duplicate);
        Throws(() => s.Confirm(1, duplicate.Token, now));
        Assert(s.AllDeadlines().Count == 1, "Duplicate deadline persisted");
    });
    Check("conflicting edits reject stale confirmation", () =>
    {
        using var s = new BotStore(Config("versions.db"));
        var d = Save(s, Make(s, "Практика №11"));
        var one = Deadlines.Resolve(s.Config, new("update", d.Id, Work: "Практика №12"), null, s.AllDeadlines(), 1, "first", now);
        var two = Deadlines.Resolve(s.Config, new("update", d.Id, Work: "Практика №13"), null, s.AllDeadlines(), 2, "second", now);
        s.SaveDraft(one); s.SaveDraft(two); s.Confirm(2, two.Token, now);
        Throws(() => s.Confirm(1, one.Token, now));
        Assert(s.AllDeadlines().Single().Work == "Практика №13", "Stale editor overwrote current deadline");
    });
    Check("prepared morning aggregates multiple submissions and survives restart", () =>
    {
        var c = Config("morning.db");
        using (var s = new BotStore(c))
        {
            Save(s, Make(s, "Практика №11")); Save(s, Make(s, "Практика №12"));
        }
        using (var s = new BotStore(c))
        {
            var morning = s.Due(DateTimeOffset.Parse("2026-11-30T06:00:00Z")).Single();
            Assert(morning.Text.Contains("№11") && morning.Text.Contains("№12") && morning.TopicId == 20, "Morning not prepared correctly");
            Assert(s.Due(DateTimeOffset.Parse("2026-11-30T04:59:00Z")).Count == 0, "Wrong Moscow delivery time");
            s.Delivered(morning);
            Assert(s.Due(DateTimeOffset.Parse("2026-11-30T06:00:00Z")).Count == 0, "Delivered post repeated");
        }
    });
    Check("move recalculates timetable and invalidates the old day's text", () =>
    {
        using var s = new BotStore(Config("move.db"));
        var d = Save(s, Make(s, "Практика №11")); Save(s, Make(s, "Практика №12"));
        var move = Deadlines.Resolve(s.Config, new("update", d.Id, Date: "2026-12-02"), null, s.AllDeadlines(), 2, "move", now);
        Assert(move.Period == 2 && move.Start == "10:40", "Move reused old period");
        Save(s, move);
        var oldMorning = s.Due(DateTimeOffset.Parse("2026-11-30T06:00:00Z")).Single(x => x.Key == "morning:2026-11-30");
        Assert(!oldMorning.Text.Contains("№11") && oldMorning.Text.Contains("№12"), "Old morning still has moved deadline");
        Assert(s.Due(DateTimeOffset.Parse("2026-12-02T06:00:00Z")).Any(x => x.Text.Contains("№11")), "New morning missing moved deadline");
    });
    Check("cancellation removes scheduled publication and restart does not revive it", () =>
    {
        var c = Config("cancel.db");
        using (var s = new BotStore(c))
        {
            var d = Save(s, Make(s, "Практика №11"));
            Save(s, Deadlines.Resolve(c, new("cancel", d.Id), null, s.AllDeadlines(), 2, "cancel", now));
        }
        using var loaded = new BotStore(c);
        Assert(loaded.AllDeadlines().Single().Status == "cancelled", "Cancellation lost");
        Assert(!loaded.Due(DateTimeOffset.Parse("2026-11-30T06:00:00Z")).Any(x => x.Key.StartsWith("morning:")), "Cancelled morning revived");
    });
    Check("online backup can be opened with saved deadlines", () =>
    {
        var c = Config("backup.db");
        using var s = new BotStore(c); Save(s, Make(s, "Практика №11")); s.Maintain(now);
        var backup = Config(Path.Combine("backups", now.ToString("yyyy-MM-dd") + ".db"));
        using var restored = new BotStore(backup);
        Assert(restored.AllDeadlines().Count == 1, "Backup is missing committed state");
    });
    Check("editor note is backstage only: shown on confirmation, never printed in posts", () =>
    {
        using var s = new BotStore(Config("notes.db"));
        var d = Save(s, Make(s, "Практика №11"));
        var edit = Deadlines.Resolve(s.Config, new("update", d.Id, Work: "Практика №12", Note: "Цынгалев сообщил об изменении сдачи."),
            null, s.AllDeadlines(), 2, "PRIVATE EDITOR SMALL TALK", now);
        Assert(Deadlines.Preview(s.Config, edit).Contains("Комментарий для бота"), "Editor note missing from confirmation");
        var changed = Save(s, edit);
        // The note only feeds the model's lead; the program never prints it into the post.
        Assert(!Deadlines.Card(s.Config, changed, now).Contains("Цынгалев") && !Deadlines.Card(s.Config, changed, now).Contains("PRIVATE EDITOR"), "Card prints the editor note or the DM");
        var news = s.Due(now).Single(x => x.Key.StartsWith("change:")).Text;
        Assert(!news.Contains("Цынгалев") && news.Contains("<blockquote>") && news.Contains("🔄"), "Change news format wrong: " + news);
    });
    Check("mentions use entities and replies identify the bot", () =>
    {
        Assert(!Conversation.CallsBot(Message(-100123, 10, 1, 3, "слышал про @vozduhanAIbot"), 99, "vozduhanAIbot"), "Plain substring triggered bot");
        Assert(Conversation.CallsBot(Message(-100123, 10, 1, 3, "Воздухан, что завтра?"), 99, "vozduhanAIbot") && Conversation.CallsBot(Message(-100123, 10, 1, 3, "бот, создай опрос"), 99, "vozduhanAIbot") &&
            !Conversation.CallsBot(Message(-100123, 10, 1, 3, "кто в Воздуханстве работает"), 99, "vozduhanAIbot"), "Name trigger wrong");
        Assert(Conversation.CallsBot(Message(-100123, 10, 2, 3, "@vozduhanAIbot ответь", entities: [new { type = "mention", offset = 0, length = 14 }]), 99, "vozduhanAIbot"), "Mention ignored");
        var reply = Message(-100123, 10, 3, 99, "Bot response");
        Assert(Conversation.CallsBot(Message(-100123, 10, 4, 3, "а почему?", reply), 99, "vozduhanAIbot"), "Reply to bot ignored");
    });
    Check("context includes exact focus, quote, full identities, older parent and only current topic", () =>
    {
        using var s = new BotStore(Config("history.db"));
        var parent = Message(-100123, 10, 1, 99, "original bot response");
        s.Remember(-100123, 10, 1, now.ToUnixTimeSeconds(), parent.GetRawText());
        for (var i = 2; i < 40; i++)
        {
            var m = Message(-100123, 10, i, 2, "public text " + i);
            s.Remember(-100123, 10, i, now.ToUnixTimeSeconds(), m.GetRawText());
        }
        var secret = Message(2, 0, 41, 2, "PRIVATE SECRET"); s.Remember(2, 0, 41, now.ToUnixTimeSeconds(), secret.GetRawText());
        var other = Message(-100123, 20, 42, 2, "OTHER TOPIC SECRET"); s.Remember(-100123, 20, 42, now.ToUnixTimeSeconds(), other.GetRawText());
        var focus = Message(-100123, 10, 40, 2, "@vozduhanAIbot ответь Никите", parent, new { text = "original", is_manual = true });
        s.Remember(-100123, 10, 40, now.ToUnixTimeSeconds(), focus.GetRawText());
        var context = new Conversation(s.Config, s).Context(focus, false);
        using var doc = JsonDocument.Parse(context);
        Assert(doc.RootElement.Field("focus_message").Number("message_id") == 40, "Wrong focus");
        Assert(doc.RootElement.Field("focus_message").Text("selected_quote") == "original", "Selected quote lost");
        Assert(doc.RootElement.Field("reply_ancestors").GetArrayLength() == 1, "Older parent lost");
        Assert(context.Contains("Староста Тестовый") || context.Contains("\\u0421"), "Roster full name absent");
        Assert(!context.Contains("PRIVATE SECRET") && !context.Contains("OTHER TOPIC SECRET"), "Cross-context leak");
        Assert(doc.RootElement.Field("recent_messages").GetArrayLength() == 30, "Wrong history length");
    });

    using (var routingHttp = new HttpClient(new FakeHandler(request =>
    {
        Assert(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "api.telegram.org", "Token colon was interpreted as a URI scheme");
        Assert(request.RequestUri.AbsolutePath == "/bot123456789:dummy_token/getUpdates", "Wrong Telegram token/method path");
        return Task.FromResult<object>(new { ok = true, result = Array.Empty<object>() });
    })) { BaseAddress = new Uri("https://api.telegram.org/") })
    {
        await new TelegramClient(routingHttp, "123456789:dummy_token").Call("getUpdates", new { timeout = 0 }, CancellationToken.None);
        count++; Console.WriteLine("PASS realistic token containing colon stays in HTTPS request path");
    }
    Check("temporary Telegram outages survive five retries; permanent failures release the actor", () =>
    {
        var c = Config("retry.db");
        using var s = new BotStore(c);
        s.Ingest(1, 3, "{}", 0); s.Ingest(2, 3, "{}", 0);
        s.Retry(s.Claim()! with { Attempts = 9 }, new TelegramError("sendMessage", 503, "unavailable"));
        Assert(s.Claim() is null, "Transient outage abandoned the first update");
        s.Retry(new Inbox(1, "{}", 10, 3), new TelegramError("sendMessage", 403, "blocked"));
        Assert(s.Claim()!.Id == 2, "Permanent failure blocks following messages");
    });
    var rateCalls = 0;
    using (var rateHttp = new HttpClient(new FakeHandler(_ =>
    {
        rateCalls++;
        return Task.FromResult<object>(new { ok = false, error_code = 429, description = "Too Many Requests", parameters = new { retry_after = 60 } });
    })) { BaseAddress = new Uri("https://telegram.test/") })
    {
        var tg = new TelegramClient(rateHttp, "test");
        foreach (var method in new[] { "sendMessage", "getUpdates" })
        {
            try { await tg.Call(method, new { }, CancellationToken.None); throw new Exception("429 accepted"); }
            catch (TelegramError e) { Assert(e.Code == 429 && e.RetryAfter > 0 && e.Method == method, "Retry diagnostics lost"); }
        }
        Assert(rateCalls == 1, "Cooldown did not protect other Telegram methods");
        count++; Console.WriteLine("PASS Telegram 429 shares cooldown without blocking a worker");
    }
    using (var store = new BotStore(Config("loading-failure.db")))
    {
        var aiCalls = 0;
        var answerAttempts = 0;
        using var tgHttp = new HttpClient(new FakeHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = payload.RootElement;
            if (request.RequestUri!.Segments[^1] == "sendMessage")
            {
                if (body.Text("text")!.StartsWith("⏳"))
                    return new { ok = false, error_code = 400, description = "indicator rejected" };
                answerAttempts++;
                if (answerAttempts == 1) return new { ok = false, error_code = 503, description = "temporary outage" };
                return new { ok = true, result = Message(3, 0, 800, 99, body.Text("text")!) };
            }
            return new { ok = true, result = true };
        })) { BaseAddress = new Uri("https://telegram.test/") };
        using var aiHttp = new HttpClient(new FakeHandler(_ =>
        {
            aiCalls++;
            return Task.FromResult<object>(new { choices = new[] { new { message = new { content = "Готовый ответ" } } } });
        })) { BaseAddress = new Uri("https://openrouter.test/") };
        var worker = new BotWorker(store.Config, store, new TelegramClient(tgHttp, "test"),
            new OpenRouterClient(aiHttp, store.Config, "test"), NullLogger<BotWorker>.Instance);
        var update = Element(new { message = Message(3, 0, 7, 3, "привет") });
        var process = typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try { await (Task)process.Invoke(worker, [update, CancellationToken.None])!; throw new Exception("Expected send failure"); }
        catch (TelegramError e) when (e.Code == 503) { }
        Assert(store.Post("answer:reply:3:7")?.Text == "Готовый ответ", "Generated answer not durable");
        await (Task)process.Invoke(worker, [update, CancellationToken.None])!;
        Assert(aiCalls == 1 && answerAttempts == 2 && store.Post("reply:3:7") is not null, "Retry regenerated or lost the answer");
        count++; Console.WriteLine("PASS failed loading is optional; failed answer delivery reuses durable AI output");
    }
    var sent = new List<(string Method, JsonElement Payload)>();
    var handler = new FakeHandler(async request =>
    {
        var body = await request.Content!.ReadAsStringAsync();
        using var payload = JsonDocument.Parse(body);
        var method = request.RequestUri!.Segments[^1]; sent.Add((method, payload.RootElement.Clone()));
        if (method == "sendMessage")
        {
            var p = payload.RootElement;
            return new { ok = true, result = Message(p.Number("chat_id"), (int)p.Number("message_thread_id"), 1000 + sent.Count, 99, p.Text("text")!) };
        }
        return new { ok = true, result = (object)true };
    });
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://telegram.test/") };
    using (var store = new BotStore(Config("worker.db")))
    {
        var aiCalls = 0;
        using var aiHttp = new HttpClient(new FakeHandler(_ =>
        {
            aiCalls++;
            return Task.FromResult<object>(new { choices = new[] { new { message = new { content = "Тестовый ответ" } } } });
        })) { BaseAddress = new Uri("https://openrouter.test/") };
        var worker = new BotWorker(store.Config, store, new TelegramClient(client, "test"), new OpenRouterClient(aiHttp, store.Config, "test"), NullLogger<BotWorker>.Instance);
        typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, 99L);
        typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, "vozduhanAIbot");
        var process = typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Process(JsonElement message) => await (Task)process.Invoke(worker, [Element(new { message }), CancellationToken.None])!;
        await Process(Message(999, 0, 1, 999, "/start"));
        await Process(Message(-100999, 10, 2, 1, "/start"));
        Assert(sent.Count == 0 && aiCalls == 0, "Unauthorized chat received a reply");
        await Process(Message(-100123, 20, 3, 999, "sticker or text"));
        Assert(sent.Single().Method == "deleteMessage", "Unknown user's AI-topic message was not silently deleted");
        sent.Clear();
        await Process(Message(3, 0, 4, 3, "/deadline siaod | №11 | 2026-11-30 | 6"));
        Assert(store.AllDeadlines().Count == 0 && aiCalls == 0, "Ordinary student invoked editor AI or wrote a deadline");
        sent.Clear();
        var mention = Message(-100123, 10, 5, 3, "@vozduhanAIbot привет", entities: [new { type = "mention", offset = 0, length = 14 }]);
        await Process(mention); await Process(mention);
        Assert(aiCalls == 1 && sent.Count(x => x.Method == "sendMessage") == 1, "Response was repeated on update replay");
        Assert(sent.Any(x => x.Method == "setMessageReaction") && sent.Any(x => x.Method == "sendChatAction"), "Reaction/typing UX incomplete");
        Assert(store.Log(chat: -100123).Any(x => x.Direction == "out" && x.Text == "Тестовый ответ"), "Outgoing answer not logged");
        count++; Console.WriteLine("PASS worker authorization, moderation, real response path and replay idempotence (mock APIs)");
        sent.Clear();
        await Process(Message(1, 0, 6, 1, "/deadline siaod | Практика №99 | 2026-11-30 | 6"));
        // /deadline needs no AI for the facts; the lively lead is written by the model (with a fallback when it is down).
        Assert(store.Draft(1) is { Missing.Length: 0, Intro: "Тестовый ответ" }, "Command draft lost or lead not generated");
        var token = store.Draft(1)!.Token;
        var callback = Element(new { callback_query = new { id = "fake_cb", from = new { id = 3 }, data = "save:" + token,
            message = new { message_id = 88, chat = new { id = 3 } } } });
        await (Task)process.Invoke(worker, [callback, CancellationToken.None])!;
        Assert(store.AllDeadlines().Count == 0, "Student confirmed somebody else's draft");
        callback = Element(new { callback_query = new { id = "fake_cb", from = new { id = 1 }, data = "save:" + token,
            message = new { message_id = 88, chat = new { id = 1 } } } });
        await (Task)process.Invoke(worker, [callback, CancellationToken.None])!;
        await (Task)process.Invoke(worker, [callback, CancellationToken.None])!;
        Assert(store.AllDeadlines().Count == 1, "Owner callback not saved exactly once");
        count++; Console.WriteLine("PASS offline administrator flow and callback ownership/double click");
    }
    sent.Clear();
    using (var store = new BotStore(Config("general-topic.db")))
    {
        store.Config.ChatTopicId = 1;
        var focus = Element(new
        {
            message_id = 71, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            chat = new { id = store.Config.GroupId, type = "supergroup" },
            from = new { id = 3, first_name = "Студент" },
            text = "@vozduhanAIbot привет", entities = new[] { new { type = "mention", offset = 0, length = 14 } }
        });
        Assert(focus.TopicId() == 1 && Message(3, 0, 1, 3, "private").TopicId() == 0 &&
            Message(store.Config.GroupId, 5, 2, 3, "deadline").TopicId() == 5, "Topic normalization broken");
        store.Remember(store.Config.GroupId, 1, 71, focus.Number("date"), focus.GetRawText());
        using var aiHttp = new HttpClient(new FakeHandler(_ => Task.FromResult<object>(new
            { choices = new[] { new { message = new { content = "Привет из Общей" } } } })))
            { BaseAddress = new Uri("https://openrouter.test/") };
        var worker = new BotWorker(store.Config, store, new TelegramClient(client, "test"),
            new OpenRouterClient(aiHttp, store.Config, "test"), NullLogger<BotWorker>.Instance);
        typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, 99L);
        typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, "vozduhanAIbot");
        await (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [Element(new { message = focus }), CancellationToken.None])!;
        var replies = sent.Where(x => x.Method == "sendMessage").ToArray();
        Assert(replies.Length == 1 && replies.All(x => x.Payload.Field("message_thread_id").ValueKind == JsonValueKind.Undefined),
            "General-topic reply ignored or sent an invalid topic 1 parameter");
        Assert(store.History(store.Config.GroupId, 1, int.MaxValue, 30).Count == 2, "General-topic bot response stored in a different history");
        count++; Console.WriteLine("PASS General topic accepts topic-less updates and sends replies without thread ID 1");
    }
    sent.Clear();
    using (var store = new BotStore(Config("publisher.db")))
    {
        store.Config.MorningTime = "00:00";
        store.Config.R.Periods = [new(2, "00:00", "23:59"), new(6, "00:00", "23:59")];
        var realNow = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(store.Config.LocalNow(realNow)).ToString("yyyy-MM-dd");
        var draft = Deadlines.Resolve(store.Config, new("create", SubjectId: "siaod", Work: "Практика №1", Date: today, Period: 6, Room: "А412"),
            null, [], 1, "source", realNow);
        store.SaveDraft(draft);
        var first = store.Confirm(1, draft.Token, realNow);
        using var unavailableAi = new HttpClient(new FakeHandler(_ => throw new Exception("Publisher must not call OpenRouter")))
            { BaseAddress = new Uri("https://openrouter.test/") };
        var worker = new BotWorker(store.Config, store, new TelegramClient(client, "test"), new OpenRouterClient(unavailableAi, store.Config, null), NullLogger<BotWorker>.Instance);
        var publish = typeof(BotWorker).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Publish() => await (Task)publish.Invoke(worker, [CancellationToken.None])!;
        await Publish(); await Publish();
        Assert(sent.Count(x => x.Method == "sendMessage") == 2, "Card/morning duplicated or not published");
        Assert(sent.Count(x => x.Method == "pinChatMessage") == 1, "Urgent deadline not pinned exactly once");
        Assert(sent.Where(x => x.Method == "sendMessage").All(x => x.Payload.Number("chat_id") == store.Config.GroupId &&
            x.Payload.Number("message_thread_id") == store.Config.DeadlinesTopicId), "Publication escaped the configured topic");
        var second = Deadlines.Resolve(store.Config, new("create", SubjectId: "siaod", Work: "Практика №2", Date: today, Period: 6, Room: "А412"),
            null, store.AllDeadlines(), 1, "source", realNow);
        store.SaveDraft(second); store.Confirm(1, second.Token, realNow);
        sent.Clear(); await Publish();
        Assert(sent.Count(x => x.Method == "sendMessage") == 1 && sent.Any(x => x.Method == "editMessageText" && x.Payload.Text("text")!.Contains("№2")),
            "Additional same-day submission did not edit existing morning post");
        var cancelled = Deadlines.Resolve(store.Config, new("cancel", first.Id), null, store.AllDeadlines(), 2, "cancel", realNow);
        store.SaveDraft(cancelled); store.Confirm(2, cancelled.Token, realNow);
        sent.Clear(); await Publish();
        Assert(sent.Any(x => x.Method == "unpinChatMessage") && sent.Any(x => x.Method == "editMessageText" && x.Payload.Text("text")!.Contains("отменена")),
            "Cancellation did not update and unpin original card");
        var morning = store.Post("morning:" + today)!;
        Assert(!morning.Text.Contains("№1") && morning.Text.Contains("№2"), "Published morning contains cancelled submission");
        count++; Console.WriteLine("PASS publisher uses no AI, pins urgent cards, edits one morning post and handles cancellation");
    }
    using (var http = new HttpClient(new FakeHandler(_ => Task.FromResult<object>(new { choices = new[] { new { message = new { content = "" }, finish_reason = "length" } } })))
        { BaseAddress = new Uri("https://openrouter.test/") })
    {
        try { await new OpenRouterClient(http, Config(), "test").Chat("", "{}", "test", true, CancellationToken.None); throw new Exception("Empty model output accepted"); }
        catch (InvalidDataException) { count++; Console.WriteLine("PASS empty visible model answer is rejected"); }
    }
    count += await NewFeatureTests.Run(temp, f => Config(f));
    count += await ReviewRegressionTests.Run(f => Config(f));
    count += await WakeupTests.Run(f => Config(f));
    count += await DeadlinePreviewTests.Run(f => Config(f));
    count += await DevNoteTests.Run(f => Config(f));
    count += await UsageTests.Run(f => Config(f));
    count += await DeadlineLifecycleTests.Run(f => Config(f));
    count += await PipelineTests.Run(f => Config(f));
    count += await RouterTests.Run(f => Config(f));
    count += await CommandlessTests.Run(f => Config(f));
    count += await WriterDietTests.Run(f => Config(f));
    Console.WriteLine($"All {count} checks passed. No real Telegram or OpenRouter calls.");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(temp, true); }

sealed class FakeHandler(Func<HttpRequestMessage, Task<object>> response) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await response(request)), Encoding.UTF8, "application/json") };
}
