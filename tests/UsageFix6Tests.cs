using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

static partial class UsageTests
{
    static async Task<int> RunFix6(Func<string, BotConfig> make)
    {
        var count = 0;
        async Task Check(string name, Func<Task> test) { await test(); count++; Console.WriteLine("PASS " + name); }
        var ct = CancellationToken.None;
        await Check("FIX-6.11: rejected TextGuard response is wasted; successful regeneration is used", async () =>
        {
            var c = make("fix6-usage-rejected.db"); c.ModelRouting = new();
            using var s = new BotStore(c);
            var calls = 0;
            var ai = Client(c, (_, _) => (HttpStatusCode.OK, Answer(++calls == 1 ? "Гайс, 集合" : "Гайс, сдаём работу", "stop", new { cost = 0.1 })));
            ai.Usage = u => s.AddUsage(u); ai.DiscardUsage = s.DiscardUsage;
            Assert(await ai.CompleteText("s", "u", ct, "chat", "chat:1:2", 3900) == "Гайс, сдаём работу", "Regeneration failed");
            var rows = Rows(c);
            Assert(rows.Count == 2 && rows[0].Discard == "rejected:cjk" && rows[1].Discard is null && rows.All(r => r.CallId is not null) &&
                rows[0].CallId != rows[1].CallId, "Wrong call marked rejected");
            var report = s.UsageReport(DateTimeOffset.UtcNow, 7);
            Assert(report.WastedAttempts == 1 && Near(report.WastedCost, 0.1) && report.Wasted.Single().Discard == "rejected:cjk", "Rejection not accounted");
            calls = 0;
            ai.DiscardUsage = (_, _) => throw new InvalidOperationException("accounting down");
            Assert(await ai.CompleteText("s", "u", ct, "chat", "chat:1:3", 3900) == "Гайс, сдаём работу", "Discard accounting broke regeneration");
        });

        await Check("FIX-6.11: invalid JSON with stop, empty output and visible thinking are discarded", async () =>
        {
            var c = make("fix6-usage-invalid.db"); c.ModelRouting = new();
            using var s = new BotStore(c);
            var calls = 0;
            var content = "{broken";
            var ai = Client(c, (_, _) => (HttpStatusCode.OK, Answer(++calls == 1 ? content : "{}", "stop", new { cost = 0.2 })));
            ai.Usage = u => s.AddUsage(u);
            Assert(await ai.Complete("s", "u", true, ct, callId: "json-call") == "{}", "JSON retry failed");
            var rows = Rows(c);
            Assert(rows.Count == 2 && rows[0].Discard == "invalid_json" && rows[0].Finish == "stop" && rows[1].Discard is null &&
                rows.All(x => x.CallId == "json-call"), "Invalid JSON or explicit call ID not tracked");
            calls = 0; content = "";
            await ai.Complete("s", "u", false, ct);
            Assert(Rows(c)[2].Discard == "empty" && Rows(c)[3].Discard is null, "Empty response not tracked");
            calls = 0; content = "<think>рассуждения</think>Ответ";
            try { await ai.Complete("s", "u", false, ct); throw new Exception("Visible thinking accepted"); }
            catch (InvalidDataException) { }
            Assert(Rows(c)[4].Discard == "think" && s.UsageReport(DateTimeOffset.UtcNow, 7).WastedAttempts == 3, "Thinking counted as used");
        });

        await Check("FIX-6.11: old usage rows survive additive migration; only length/max_tokens are wasted", () =>
        {
            var c = make("fix6-usage-old.db");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE ai_usage(id INTEGER PRIMARY KEY AUTOINCREMENT,time INTEGER NOT NULL,kind TEXT NOT NULL,tag TEXT NOT NULL," +
                    "requested_model TEXT NOT NULL,model TEXT NULL,attempt INTEGER NOT NULL,prompt_tokens INTEGER NOT NULL,cached_tokens INTEGER NOT NULL," +
                    "completion_tokens INTEGER NOT NULL,reasoning_tokens INTEGER NOT NULL,cost REAL NULL,finish TEXT NULL,ms INTEGER NOT NULL);" +
                    "INSERT INTO ai_usage VALUES(1,strftime('%s','now'),'chat','old','m','m',1,1,0,1,0,0.1,'length',1);" +
                    "INSERT INTO ai_usage VALUES(2,strftime('%s','now'),'chat','old','m','m',2,1,0,1,0,0.2,'stop',1);" +
                    "INSERT INTO ai_usage VALUES(3,strftime('%s','now'),'chat','old','m','m',3,1,0,1,0,0.3,'max_tokens',1);";
                cmd.ExecuteNonQuery();
            }
            using (var s = new BotStore(c))
            {
                var rows = Rows(c);
                var report = s.UsageReport(DateTimeOffset.UtcNow, 7);
                Assert(rows.Count == 3 && rows.All(x => x.CallId is null && x.Discard is null) && report.WastedAttempts == 2 && Near(report.WastedCost, 0.4), "Legacy rule or data changed");
                s.AddUsage(new("chat", "new", "m", "m", 2, 1, 0, 1, 0, 0.5, "stop", 1, "new-call"));
                Assert(s.UsageReport(DateTimeOffset.UtcNow, 7).WastedAttempts == 2, "Successful retry wasted");
            }
            using (var s = new BotStore(c)) Assert(Rows(c).Count == 4, "Migration not repeatable");
            return Task.CompletedTask;
        });

        await Check("FIX-6.13: changing daily or monthly threshold permits one new warning and survives restart", async () =>
        {
            var c = make("fix6-alert.db");
            using var s = new BotStore(c);
            var sent = new List<string>();
            var tg = new TelegramClient(new HttpClient(new Fake((_, body) =>
            {
                var b = JsonDocument.Parse(body).RootElement;
                Assert(b.Number("chat_id") == c.OwnerId, "Warning not private to owner");
                sent.Add(b.Text("text")!);
                return (HttpStatusCode.OK, Json.Write(new { ok = true, result = new { message_id = sent.Count, date = 1, chat = new { id = c.OwnerId, type = "private" } } }));
            })) { BaseAddress = new Uri("https://telegram.test/") }, "test");
            BotWorker Worker() => new(c, s, tg, Client(c, (_, _) => throw new Exception("Unexpected AI call")), NullLogger<BotWorker>.Instance);
            var w = Worker();
            var now = DateTimeOffset.UtcNow;
            Task Alert() => (Task)typeof(BotWorker).GetMethod("SpendAlerts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, [now, ct])!;
            void Add(double cost) => s.AddUsage(new("chat", "", "m", "m", 1, 1, 0, 1, 0, cost, "stop", 1), now);
            s.SetSpendAlert(new(1, 0)); Add(1.2);
            await Alert(); await Alert();
            Assert(sent.Count == 1 && sent[0] == "💸 Сегодня потрачено $1.20 (порог $1.00)", "First daily warning wrong");
            s.SetSpendAlert(new(2, 0)); await Alert();
            Assert(sent.Count == 1, "Warned below changed threshold");
            Add(0.9); await Alert(); await Alert();
            Assert(sent.Count == 2 && sent[1] == "💸 Сегодня потрачено $2.10 (порог $2.00)", "Changed daily threshold did not warn once");
            w = Worker(); await Alert();
            Assert(sent.Count == 2 && s.Get("usage_alert_sent:day")!.EndsWith("|2"), "Restart lost threshold marker");
            s.SetSpendAlert(new(0, 1)); await Alert(); await Alert();
            Assert(sent.Count == 3, "Monthly warning wrong");
            s.SetSpendAlert(new(0, 2)); await Alert(); await Alert();
            Assert(sent.Count == 4 && s.Get("usage_alert_sent:month")!.EndsWith("|2"), "Changed monthly threshold did not warn once");
        });
        return count;
    }
}
