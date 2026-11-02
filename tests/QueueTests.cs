using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Submission queues in «Очередь на сдачу»: start by message or web, ✋/🚪 buttons, edits, lock after the lesson.</summary>
static partial class WakeupTests
{
    static async Task RunQueues(Func<string, BotConfig> make)
    {
        const int Topic = 30;
        static MethodInfo M(string name) => typeof(BotWorker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Env Make(string file) { var e = new Env(make, file); e.C.QueueTopicId = Topic; return e; }
        Task Process(Env e, object update) => (Task)M("Process").Invoke(e.W, [JsonSerializer.SerializeToElement(update, Json.Options), CancellationToken.None])!;
        Task Dm(Env e, string text) => Process(e, new
        {
            message = new { message_id = Random.Shared.Next(10, 100000), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = 1, type = "private" }, from = new { id = 1, first_name = "Dev" }, text }
        });
        Task Press(Env e, long actor, int message, string data) => Process(e, new
        {
            callback_query = new { id = "cb" + Random.Shared.Next(), from = new { id = actor }, data,
                message = new { message_id = message, message_thread_id = Topic, chat = new { id = e.C.GroupId, type = "supergroup" } } }
        });
        List<JsonElement> Posts(Env e) => e.Sent.Where(x => x.Method == "sendMessage" && x.Body.Number("message_thread_id") == Topic).Select(x => x.Body).ToList();
        List<JsonElement> Edits(Env e) => e.Sent.Where(x => x.Method == "editMessageText").Select(x => x.Body).ToList();
        string Toast(Env e) => e.Sent.Last(x => x.Method == "answerCallbackQuery").Body.Text("text")!;
        string Reply(Env e) => e.Sent.Last(x => x.Method == "sendMessage" && x.Body.Number("chat_id") == 1).Body.Text("text")!;
        DateOnly Next(Env e, DayOfWeek dow)
        {
            var d = DateOnly.FromDateTime(e.C.LocalNow(DateTimeOffset.UtcNow)).AddDays(1);
            while (d.DayOfWeek != dow) d = d.AddDays(1);
            return d;
        }
        object PlanQueue(DateOnly date, string subject = "siaod") => new { intent = "queue", deadline = new { subjectId = subject, date = date.ToString("yyyy-MM-dd") } };
        void Planner(Env e, Func<object> plan) => e.Text = body => Task.FromResult(body.Contains("разбираешь личное сообщение") ? Json.Write(plan()) : "Гайс");

        await Check("queues: «создай очередь…» → a list of 10 places with ✋/🚪 in «Очередь на сдачу»; the same queue twice is not repeated", async () =>
        {
            using var e = Make("queue-start.db");
            var wed = Next(e, DayOfWeek.Wednesday);
            Planner(e, () => PlanQueue(wed));
            await Dm(e, "Создай очередь на сдачу в среду по сиаоду");
            var post = Posts(e).Single();
            var text = post.Text("text")!;
            var buttons = post.Field("reply_markup").Field("inline_keyboard")[0].EnumerateArray().Select(b => b.Text("text")).ToList();
            Assert(post.Number("chat_id") == e.C.GroupId && text.Contains("Очередь на сдачу") && text.Contains("СиАОД") && text.Contains("2 пара · 10:40–12:10") &&
                text.Contains("А412") && text.Contains("1. ———") && text.Contains("10. ———") && !text.Contains("11.") && text.Contains("В очереди: <b>0</b>"), "Queue post wrong: " + text);
            Assert(buttons.SequenceEqual(["✋ Записаться", "🚪 Выйти"]) && Reply(e).Contains("Открыл очередь"), "Buttons or confirmation wrong");
            var q = e.S.Queues().Single();
            Assert(q is { Status: "open", Period: 2, End: "12:10" } && q.MessageId > 0 && q.Synced == q.Version, "Queue not stored");
            await Dm(e, "Создай очередь на сдачу в среду по сиаоду");
            Assert(Posts(e).Count == 1 && e.S.Queues().Count == 1 && Reply(e).Contains("уже есть"), "Duplicate queue posted");
            Assert(e.S.AllDeadlines().Count == 0, "A queue created a deadline");
        });

        await Check("queues: no lesson that day or a past lesson → a question, nothing posted; the queue topic off → refused", async () =>
        {
            using var e = Make("queue-errors.db");
            var tue = Next(e, DayOfWeek.Tuesday);
            Planner(e, () => PlanQueue(tue));
            await Dm(e, "очередь на сиаод во вторник");
            Assert(Posts(e).Count == 0 && Reply(e).Contains("нет пары") && e.S.Get("planner_question:1") is not null, "No-lesson day not asked");
            var past = DateOnly.FromDateTime(e.C.LocalNow(DateTimeOffset.UtcNow)).AddDays(-7);
            var (q, error, _) = await (Task<(SubmissionQueue?, string?, bool)>)M("CreateQueue").Invoke(e.W, [1L, "siaod", past.ToString("yyyy-MM-dd"), (int?)6, null, CancellationToken.None])!;
            Assert(q is null && error!.Contains("закончилась"), "Past lesson accepted");
            e.C.QueueTopicId = 0;
            (q, error, _) = await (Task<(SubmissionQueue?, string?, bool)>)M("CreateQueue").Invoke(e.W, [1L, "siaod", Next(e, DayOfWeek.Wednesday).ToString("yyyy-MM-dd"), null, null, CancellationToken.None])!;
            Assert(q is null && error!.Contains("queueTopicId") && Posts(e).Count == 0, "Queue without the topic");
        });

        await Check("queues: ✋ adds to the end, twice — the same place; 🚪 removes and the rest move up; someone outside the roster is refused", async () =>
        {
            using var e = Make("queue-buttons.db");
            var wed = Next(e, DayOfWeek.Wednesday);
            Planner(e, () => PlanQueue(wed));
            await Dm(e, "очередь");
            var q = e.S.Queues().Single();
            var join = $"queue:j:{q.Id}"; var leave = $"queue:l:{q.Id}";
            await Press(e, 3, q.MessageId, join);
            Assert(Toast(e) == "Записал: ты 1-й" && Edits(e).Last().Text("text")!.Contains("🥇 Студент Тестовый") && Edits(e).Last().Field("reply_markup").ValueKind == JsonValueKind.Object, "Join wrong");
            var edits = Edits(e).Count;
            await Press(e, 3, q.MessageId, join);
            Assert(Toast(e) == "Ты уже в очереди: 1-й" && Edits(e).Count == edits, "Second join changed something");
            await Press(e, 2, q.MessageId, join); await Press(e, 1, q.MessageId, join);
            Assert(Toast(e) == "Записал: ты 3-й", "Third place wrong");
            await Press(e, 3, q.MessageId, leave);
            var text = Edits(e).Last().Text("text")!;
            Assert(Toast(e).StartsWith("Вычеркнул") && text.Contains("🥇 Староста Тестовый") && text.Contains("🥈 Разработчик Тестовый") && text.Contains("3. ———") &&
                !text.Contains("Студент") && text.Contains("В очереди: <b>2</b>"), "Leave did not move the queue: " + text);
            await Press(e, 3, q.MessageId, leave);
            Assert(Toast(e) == "Тебя и так нет в очереди", "Leave twice");
            await Press(e, 999, q.MessageId, join);
            Assert(Toast(e) == "Тебя нет в списке группы" && e.S.QueueEntries(q.Id).Count == 2, "Stranger joined");
            Assert(BotWorker.CallbackAllowed(e.C, 3, e.C.GroupId, join) && !BotWorker.CallbackAllowed(e.C, 999, e.C.GroupId, join) && !BotWorker.CallbackAllowed(e.C, 3, 3, join),
                "Queue buttons not accepted from the roster in the group (or accepted elsewhere)");
        });

        await Check("queues: more than 10 people — the list grows; locked — only who signed up", async () =>
        {
            using var e = Make("queue-grow.db");
            var q = new SubmissionQueue(1, "siaod", "Практика №5", "2026-12-02", 2, "10:40", "12:10", "А412", 0, "open", 0, 1, 0, 1, 0);
            var people = Enumerable.Range(1, 11).Select(i => new QueueEntry(i, "x" + i, 0, "Человек " + i, 0)).ToList();
            string Html(SubmissionQueue x, List<QueueEntry> list) => (string)M("QueueHtml").Invoke(e.W, [x, list])!;
            var open = Html(q, people);
            Assert(open.Contains("Практика №5") && open.Contains("🥉 Человек 3") && open.Contains("11. Человек 11") && open.Contains("12. ———") && !open.Contains("13."), "Open list did not grow: " + open);
            var locked = Html(q with { Status = "locked" }, people.Take(2).ToList());
            Assert(locked.Contains("🥈 Человек 2") && !locked.Contains("———") && locked.Contains("🔒 <b>Очередь закрыта</b>"), "Locked list wrong: " + locked);
            await Task.CompletedTask;
        });

        await Check("queues: after the lesson the buttons go, «закрыта» is added, the message stays; ✋ then answers «закрыта»", async () =>
        {
            using var e = Make("queue-lock.db");
            var wed = Next(e, DayOfWeek.Wednesday);
            Planner(e, () => PlanQueue(wed));
            await Dm(e, "очередь");
            var q = e.S.Queues().Single();
            await Press(e, 3, q.MessageId, $"queue:j:{q.Id}");
            var tick = (Func<DateTimeOffset, Task>)(utc => (Task)M("QueueTick").Invoke(e.W, [utc, CancellationToken.None])!);
            await tick(DateTimeOffset.FromUnixTimeSeconds(q.Closes - 60));
            Assert(e.S.Queue(q.Id)!.Status == "open", "Locked before the end of the lesson");
            await tick(DateTimeOffset.FromUnixTimeSeconds(q.Closes));
            var last = Edits(e).Last();
            Assert(e.S.Queue(q.Id)!.Status == "locked" && last.Number("message_id") == q.MessageId && last.Text("text")!.Contains("🔒 <b>Очередь закрыта</b>") &&
                last.Text("text")!.Contains("Студент Тестовый") && last.Field("reply_markup").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null &&
                e.Sent.All(x => x.Method != "deleteMessage"), "Lock wrong: " + last);
            var edits = Edits(e).Count;
            await tick(DateTimeOffset.FromUnixTimeSeconds(q.Closes + 60));
            await Press(e, 2, q.MessageId, $"queue:j:{q.Id}");
            Assert(Edits(e).Count == edits && Toast(e) == "Очередь уже закрыта" && e.S.QueueEntries(q.Id).Count == 1, "Locked queue changed");
        });

        await Check("queues: web — the head creates, adds, moves, removes, locks, reopens and deletes; every change edits the message", async () =>
        {
            using var e = Make("queue-web.db");
            var port = 28000 + Random.Shared.Next(1000);
            e.C.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            using var cts = new CancellationTokenSource();
            var web = new AdminWeb(e.C, e.S, e.W, NullLogger<AdminWeb>.Instance);
            await web.StartAsync(cts.Token);
            try
            {
                await Task.Delay(300);
                var head = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                await head.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = e.S.CreateWebToken(2, "login", TimeSpan.FromMinutes(5)) }));
                head.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                async Task<HttpResponseMessage> Post(object o) => await head.PostAsync("api/queue", new StringContent(Json.Write(o), Encoding.UTF8, "application/json"));
                var wed = Next(e, DayOfWeek.Wednesday).ToString("yyyy-MM-dd");
                var created = await Post(new { action = "create", subjectId = "siaod", date = wed, work = "Практика №5" });
                Assert(created.IsSuccessStatusCode && Posts(e).Single().Text("text")!.Contains("Практика №5"), "Create failed: " + await created.Content.ReadAsStringAsync());
                Assert((await Post(new { action = "create", subjectId = "siaod", date = wed })).StatusCode == HttpStatusCode.Conflict, "Duplicate created");
                var id = e.S.Queues().Single().Id;
                foreach (var m in new[] { "m3", "m2" }) Assert((await Post(new { id, action = "add", member = m })).IsSuccessStatusCode, "Add failed");
                Assert((await Post(new { id, action = "add", member = "ghost" })).StatusCode == HttpStatusCode.BadRequest, "Unknown member added");
                Assert((await Post(new { id, action = "up", member = "m2" })).IsSuccessStatusCode && e.S.QueueEntries(id).Select(x => x.Member).SequenceEqual(["m2", "m3"]) &&
                    Edits(e).Last().Text("text")!.Contains("🥇 Староста Тестовый"), "Move up failed");
                Assert((await Post(new { id, action = "up", member = "m2" })).StatusCode == HttpStatusCode.BadRequest, "Moved above the top");
                Assert((await Post(new { id, action = "lock" })).IsSuccessStatusCode && Edits(e).Last().Text("text")!.Contains("закрыта"), "Lock failed");
                Assert((await Post(new { id, action = "remove", member = "m3" })).IsSuccessStatusCode && !Edits(e).Last().Text("text")!.Contains("Студент"), "Remove from a locked queue failed");
                Assert((await Post(new { id, action = "reopen", until = "23:59" })).IsSuccessStatusCode && Edits(e).Last().Field("reply_markup").ValueKind == JsonValueKind.Object &&
                    Edits(e).Last().Text("text")!.Contains("запись до 23:59"), "Reopen failed");
                var list = await head.GetStringAsync("api/queues");
                Assert(list.Contains("Староста Тестовый") && list.Contains("\"status\":\"open\""), "List wrong: " + list);
                Assert((await Post(new { id, action = "delete" })).IsSuccessStatusCode && e.Sent.Any(x => x.Method == "deleteMessage") && e.S.Queues().Count == 0, "Delete failed");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        });
    }
}
