using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>PREP: per-attempt token and cost accounting, the developer's spending tab and overspend warnings.</summary>
static partial class UsageTests
{
    const string Key = "sk-or-v1-SECRETKEY123";
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
    sealed class Fake(Func<HttpRequestMessage, string, (HttpStatusCode Code, string Body)> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = f(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    static string Answer(string content, string finish, object? usage = null, string? model = null) =>
        Json.Write(new { model, choices = new[] { new { message = new { content }, finish_reason = finish } }, usage });
    static OpenRouterClient Client(BotConfig c, Func<HttpRequestMessage, string, (HttpStatusCode, string)> f) =>
        new(new HttpClient(new Fake(f)) { BaseAddress = new Uri("https://openrouter.test/") }, c, Key);
    sealed record Row(string Kind, string Tag, string RequestedModel, string? Model, int Attempt, long Prompt, long Cached, long Completion, long Reasoning, double? Cost, string? Finish, long Ms, string? CallId, string? Discard);
    static List<Row> Rows(BotConfig c)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT kind,tag,requested_model,model,attempt,prompt_tokens,cached_tokens,completion_tokens,reasoning_tokens,cost,finish,ms,call_id,discard FROM ai_usage ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<Row>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6),
            r.GetInt64(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetDouble(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13)));
        return list;
    }
    static async Task<bool> Until(Func<bool> done, int ms)
    {
        for (var i = 0; i < ms / 50; i++) { if (done()) return true; await Task.Delay(50); }
        return done();
    }

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var ct = CancellationToken.None;

        // 1. Usage parsing: every field stored as given; an answer without usage gives zeros and no cost.
        {
            var c = make("usage-parse.db"); c.ModelRouting = new();
            using var s = new BotStore(c);
            var bodies = new List<string>();
            var withUsage = true;
            var ai = Client(c, (_, body) =>
            {
                bodies.Add(body);
                return (HttpStatusCode.OK, withUsage
                    ? """{"model":"google/actual-1","choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1200,"completion_tokens":300.0,"prompt_tokens_details":{"cached_tokens":800},"completion_tokens_details":{"reasoning_tokens":120},"cost":0.00123}}"""
                    : Answer("без учёта", "stop"));
            });
            ai.Usage = u => s.AddUsage(u);
            Assert(await ai.Complete("sys", "user", false, ct, "chat", "chat:5:6") == "ok", "Complete result changed");
            var r = Rows(c).Single();
            Assert(r == r with { Kind = "chat", Tag = "chat:5:6", RequestedModel = c.Model, Model = "google/actual-1", Attempt = 1, Prompt = 1200, Cached = 800, Completion = 300,
                Reasoning = 120, Cost = 0.00123, Finish = "stop" } && r.Ms >= 0, "Usage row wrong: " + r);
            withUsage = false;
            Assert(await ai.Complete("sys", "user", false, ct, "plan", "t2") == "без учёта", "Complete without usage failed");
            r = Rows(c)[1];
            Assert(r is { Kind: "plan", Model: null, Prompt: 0, Cached: 0, Completion: 0, Reasoning: 0, Cost: null, Finish: "stop" } && r.RequestedModel == c.Model, "Missing usage not zero/null: " + r);
            Assert(bodies.All(b => b.Contains("\"usage\":{\"include\":true}")), "Request lacks usage.include");
            Console.WriteLine("PASS usage: tokens, cache, reasoning, cost, actual and requested model stored; no usage block gives zeros and null cost");
        }

        // 2. A cut answer and its retry: two rows, only the cut answer wasted, one call.
        {
            var c = make("usage-retry.db"); c.ModelRouting = new();
            using var s = new BotStore(c);
            var n = 0;
            var ai = Client(c, (_, _) => ++n == 1
                ? (HttpStatusCode.OK, Answer("обрыв", "length", new { prompt_tokens = 10, completion_tokens = 100, cost = 0.01 }))
                : (HttpStatusCode.OK, Answer("целый ответ", "stop", new { prompt_tokens = 10, completion_tokens = 50, cost = 0.02 })));
            ai.Usage = u => s.AddUsage(u);
            Assert(await ai.Complete("sys", "user", false, ct, "ambient-morning") == "целый ответ", "Retry result wrong");
            var rows = Rows(c);
            Assert(rows.Count == 2 && rows[0] is { Attempt: 1, Finish: "length", Discard: "length", CallId: not null } && rows[1] is { Attempt: 2, Finish: "stop", Discard: null } && rows[0].CallId == rows[1].CallId, "Retry rows wrong");
            var report = s.UsageReport(DateTimeOffset.UtcNow, 7);
            Assert(Near(report.WastedCost, 0.01) && report.WastedAttempts == 1 && report.Wasted.Single() is { Kind: "ambient-morning", Discard: "length", Attempts: 1 }, "Wasted wrong");
            Assert(report.ByKind.Single() is { Calls: 1, Attempts: 2 } k && Near(k.Cost, 0.03) && Near(k.AvgPerCall!.Value, 0.03), "byKind calls/attempts wrong");
            Console.WriteLine("PASS usage: a cut answer and its retry are two attempts of one call, only the cut answer counted as wasted");
        }

        // 3. HTTP 500 without a body: no row. Compatibility retry after 400: a row only for the successful answer.
        {
            var c = make("usage-http.db"); c.ModelRouting = new();
            using var s = new BotStore(c);
            var bodies = new List<string>();
            var mode = 500;
            var ai = Client(c, (_, body) =>
            {
                bodies.Add(body);
                if (mode == 500) return (HttpStatusCode.InternalServerError, "");
                if (body.Contains("\"reasoning\":{")) return (HttpStatusCode.BadRequest, "");
                return (HttpStatusCode.OK, Answer("совместимо", "stop", new { prompt_tokens = 5, completion_tokens = 5, cost = 0.001 }));
            });
            ai.Usage = u => s.AddUsage(u);
            try { await ai.Complete("sys", "user", false, ct, "chat"); throw new Exception("500 accepted"); }
            catch (HttpRequestException) { }
            Assert(Rows(c).Count == 0, "HTTP 500 created a row");
            mode = 400;
            Assert(await ai.Complete("sys", "user", false, ct, "chat") == "совместимо", "Compat retry failed");
            Assert(Rows(c).Single() is { Attempt: 1, Cost: 0.001 } && bodies.Count == 3 && bodies.All(b => b.Contains("\"usage\":{\"include\":true}")), "Compat retry rows wrong");
            Console.WriteLine("PASS usage: HTTP errors write nothing; the compatibility retry writes one row; every request asks for usage");
        }

        // 4. A failing accounting handler never changes the answer.
        {
            var c = make("usage-throw.db"); c.ModelRouting = new();
            var traced = 0;
            var ai = Client(c, (_, _) => (HttpStatusCode.OK, Answer("живой", "stop", new { cost = 0.5 })));
            ai.Usage = _ => throw new InvalidOperationException("учёт упал");
            ai.Trace = _ => traced++;
            Assert(await ai.Complete("sys", "user", false, ct, "chat") == "живой" && traced == 1, "Usage handler failure broke Complete");
            Console.WriteLine("PASS usage: an exception in the accounting handler does not change the result");
        }

        // 5–7. Aggregates, access and the key balance through the real admin server.
        {
            var c = make("usage-web.db"); c.ModelRouting = new();
            var port = 22000 + Random.Shared.Next(1000);
            c.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            using var s = new BotStore(c);
            var keyCalls = 0; string? auth = null;
            var tg = new HttpClient(new Fake((_, _) => (HttpStatusCode.OK, "{\"ok\":true,\"result\":true}"))) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = Client(c, (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/key")) return (HttpStatusCode.OK, Answer("ok", "stop"));
                keyCalls++; auth = request.Headers.Authorization?.ToString();
                return (HttpStatusCode.OK, """{"data":{"label":"sk-or-v1-SEC...123","usage":12.5,"usage_daily":0.4,"usage_weekly":2,"usage_monthly":7.25,"limit":20,"limit_remaining":7.5}}""");
            });
            var worker = new BotWorker(c, s, new TelegramClient(tg, "t"), ai, NullLogger<BotWorker>.Instance);
            // The second server's key answers 401: the report still works without key data.
            var badAi = Client(c, (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"));
            var badWorker = new BotWorker(c, s, new TelegramClient(tg, "t"), badAi, NullLogger<BotWorker>.Instance);
            var c2 = make("usage-web.db"); c2.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port + 1}/", PublicUrl = "", AllowedNetworks = ["127.0.0.0/8"] };

            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(c.LocalNow(now));
            var midnight = c.Utc(today.ToDateTime(TimeOnly.MinValue));
            DateTimeOffset Day(int back) => c.Utc(today.AddDays(-back).ToDateTime(new TimeOnly(12, 0)));
            var data = new (DateTimeOffset At, AiUsage U)[]
            {
                (midnight.AddSeconds(60), new("chat", "chat:1:1", "req", "m-a", 1, 1000, 400, 100, 10, 0.10, "stop", 900)),
                (midnight.AddSeconds(120), new("plan", "chat:1:2", "req", "m-b", 1, 500, 0, 50, 0, 0.05, "stop", 800)),
                (midnight.AddSeconds(-60), new("chat", "chat:1:3", "req", "m-a", 1, 2000, 1000, 200, 0, 0.20, "stop", 700)),
                (Day(3), new("ambient-morning", "", "req", "m-b", 1, 300, 0, 4000, 0, 0.30, "length", 600)),
                (Day(3).AddSeconds(5), new("ambient-morning", "", "req", "m-b", 2, 300, 0, 2000, 0, 0.40, "stop", 600)),
                (Day(5), new("chat", "chat:1:4", "m-req", null, 1, 100, 0, 10, 0, null, null, 500)),
                (Day(10), new("wakeplan", "", "req", "m-c", 1, 700, 0, 70, 0, 1.00, "stop", 400)),
                (Day(40), new("chat", "old", "req", "m-a", 1, 1, 0, 1, 0, 9.00, "stop", 1))
            };
            foreach (var (at, u) in data) s.AddUsage(u, at);

            using var cts = new CancellationTokenSource();
            var web = new AdminWeb(c, s, worker, NullLogger<AdminWeb>.Instance);
            var badWeb = new AdminWeb(c2, s, badWorker, NullLogger<AdminWeb>.Instance);
            await web.StartAsync(cts.Token); await badWeb.StartAsync(cts.Token);
            try
            {
                await Task.Delay(300);
                async Task<HttpClient> Login(long actor, int p)
                {
                    var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{p}/") };
                    await client.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["t"] = s.CreateWebToken(actor, "login", TimeSpan.FromMinutes(5)) }));
                    client.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                    return client;
                }
                using var owner = await Login(1, port);
                using var head = await Login(2, port);
                using var ownerBad = await Login(1, port + 1);
                var seen = new List<string>();
                async Task<JsonElement> Get(HttpClient h, string url)
                {
                    var r = await h.GetAsync("api/" + url);
                    var text = await r.Content.ReadAsStringAsync();
                    seen.Add(text);
                    Assert(r.StatusCode == HttpStatusCode.OK, url + " → " + r.StatusCode + " " + text);
                    return JsonDocument.Parse(text).RootElement.Clone();
                }
                async Task<(HttpStatusCode, string)> Post(HttpClient h, string url, object body)
                {
                    var r = await h.PostAsync("api/" + url, new StringContent(Json.Write(body), Encoding.UTF8, "application/json"));
                    var text = await r.Content.ReadAsStringAsync(); seen.Add(text);
                    return (r.StatusCode, text);
                }

                // 5. Exact sums, local day boundary, kinds, models, top and forecast.
                var report = await Get(owner, "usage?days=30");
                UsageTotals Expect(Func<DateOnly, bool> on)
                {
                    var rows = data.Where(x => on(DateOnly.FromDateTime(c.LocalNow(x.At)))).Select(x => x.U).ToList();
                    return new(rows.Sum(u => u.CostUsd ?? 0), rows.Count, rows.Sum(u => u.PromptTokens), rows.Sum(u => u.CachedTokens), rows.Sum(u => u.CompletionTokens),
                        rows.Sum(u => u.ReasoningTokens), rows.Count(u => u.CostUsd is null));
                }
                bool Same(JsonElement e, UsageTotals t) => Near(e.GetProperty("cost").GetDouble(), t.Cost) && e.GetProperty("attempts").GetInt64() == t.Attempts &&
                    e.GetProperty("promptTokens").GetInt64() == t.PromptTokens && e.GetProperty("cachedTokens").GetInt64() == t.CachedTokens &&
                    e.GetProperty("completionTokens").GetInt64() == t.CompletionTokens && e.GetProperty("reasoningTokens").GetInt64() == t.ReasoningTokens &&
                    e.GetProperty("costMissing").GetInt64() == t.CostMissing;
                var sum = report.GetProperty("summary");
                var first = new DateOnly(today.Year, today.Month, 1);
                Assert(Same(sum.GetProperty("today"), Expect(d => d == today)) && Near(sum.GetProperty("today").GetProperty("cost").GetDouble(), 0.15), "Today wrong: " + sum);
                Assert(Same(sum.GetProperty("yesterday"), Expect(d => d == today.AddDays(-1))) && Near(sum.GetProperty("yesterday").GetProperty("cost").GetDouble(), 0.20), "Yesterday wrong (midnight boundary)");
                Assert(Same(sum.GetProperty("week"), Expect(d => d >= today.AddDays(-6))) && sum.GetProperty("week").GetProperty("costMissing").GetInt64() == 1, "Week wrong");
                Assert(Same(sum.GetProperty("month"), Expect(d => d >= first)), "Month wrong");
                var byDay = report.GetProperty("byDay").EnumerateArray().ToList();
                Assert(byDay.Count == 30 && byDay[^1].GetProperty("date").GetString() == today.ToString("yyyy-MM-dd") &&
                    byDay.All(d => Same(d.GetProperty("totals"), Expect(x => x.ToString("yyyy-MM-dd") == d.GetProperty("date").GetString()))), "byDay wrong");
                Assert(Near(byDay[^1].GetProperty("totals").GetProperty("cost").GetDouble(), 0.15) && Near(byDay[^2].GetProperty("totals").GetProperty("cost").GetDouble(), 0.20), "Midnight rows in one day");
                var kinds = report.GetProperty("byKind").EnumerateArray().ToList();
                Assert(string.Join(",", kinds.Select(k => k.GetProperty("kind").GetString())) == "wakeplan,ambient-morning,chat,plan", "byKind order wrong");
                var chat = kinds[2]; var morning = kinds[1];
                Assert(chat.GetProperty("calls").GetInt64() == 3 && chat.GetProperty("attempts").GetInt64() == 3 && Near(chat.GetProperty("cost").GetDouble(), 0.30) &&
                    Near(chat.GetProperty("avgPerCall").GetDouble(), 0.10) && Near(chat.GetProperty("cacheShare").GetDouble(), 1400.0 / 3100) &&
                    Near(chat.GetProperty("share").GetDouble(), 0.30 / 2.05) && chat.GetProperty("costMissing").GetInt64() == 1, "chat kind wrong: " + chat);
                Assert(morning.GetProperty("calls").GetInt64() == 1 && morning.GetProperty("attempts").GetInt64() == 2 && Near(morning.GetProperty("avgPerCall").GetDouble(), 0.70) &&
                    Near(morning.GetProperty("cacheShare").GetDouble(), 0), "ambient-morning kind wrong");
                var models = report.GetProperty("byModel").EnumerateArray().ToDictionary(m => m.GetProperty("model").GetString()!, m => m.GetProperty("totals"));
                Assert(models.Count == 4 && Near(models["m-a"].GetProperty("cost").GetDouble(), 0.30) && Near(models["m-b"].GetProperty("cost").GetDouble(), 0.75) &&
                    Near(models["m-c"].GetProperty("cost").GetDouble(), 1.00) && models["m-req"].GetProperty("costMissing").GetInt64() == 1, "byModel wrong");
                var top = report.GetProperty("top").EnumerateArray().ToList();
                Assert(top.Count == 7 && string.Join(",", top.Take(6).Select(x => x.GetProperty("cost").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture))) == "1,0.4,0.3,0.2,0.1,0.05" &&
                    !top[6].TryGetProperty("cost", out _) && top[0].GetProperty("kind").GetString() == "wakeplan" && top[1].GetProperty("attempt").GetInt32() == 2, "top wrong");
                var wasted = report.GetProperty("wasted");
                Assert(Near(wasted.GetProperty("cost").GetDouble(), 0.30) && wasted.GetProperty("attempts").GetInt64() == 1, "wasted wrong");
                var fullDays = Expect(d => d >= today.AddDays(-7) && d < today).Cost;
                Assert(Near(report.GetProperty("forecast").GetDouble(), fullDays / 7 * DateTime.DaysInMonth(today.Year, today.Month)), "forecast wrong");
                Assert((await Get(owner, "usage?days=7")).GetProperty("byDay").GetArrayLength() == 7 && (await Get(owner, "usage?days=13")).GetProperty("byDay").GetArrayLength() == 30, "days param wrong");
                var fresh = make("usage-fresh.db");
                using (var fs = new BotStore(fresh))
                {
                    fs.AddUsage(new("chat", "", "req", "m", 1, 1, 0, 1, 0, 0.5, "stop", 1), DateTimeOffset.UtcNow.AddDays(-2));
                    Assert(fs.UsageReport(DateTimeOffset.UtcNow, 30).Forecast is null, "Forecast from less than 3 days");
                }
                Console.WriteLine("PASS usage: owner report sums, local midnight, kinds, models, top, wasted and forecast are exact");

                // 6. The head gets 403 everywhere and no tab code.
                foreach (var url in new[] { "usage", "usage?days=7", "usage/alert" })
                    Assert((await head.GetAsync("api/" + url)).StatusCode == HttpStatusCode.Forbidden, "Head reached " + url);
                var (headPost, _) = await Post(head, "usage/alert", new { dailyUsd = 1, monthlyUsd = 1 });
                Assert(headPost == HttpStatusCode.Forbidden && s.SpendAlert() == new SpendAlert(0, 0), "Head saved thresholds");
                var headPage = await head.GetStringAsync("");
                var ownerPage = await owner.GetStringAsync("");
                Assert(!headPage.Contains("usage") && !headPage.Contains("Расходы") && !headPage.Contains("💸") && ownerPage.Contains("VIEWS.usage") && ownerPage.Contains("usage/alert"), "Tab code in the head's page");
                var (bad, _) = await Post(owner, "usage/alert", new { dailyUsd = -1, monthlyUsd = 1 });
                var (big, _) = await Post(owner, "usage/alert", new { dailyUsd = 1, monthlyUsd = 100001 });
                var (text, _) = await Post(owner, "usage/alert", new { dailyUsd = "1", monthlyUsd = 1 });
                var (ok, saved) = await Post(owner, "usage/alert", new { dailyUsd = 2.5, monthlyUsd = 0 });
                Assert(bad == HttpStatusCode.BadRequest && big == HttpStatusCode.BadRequest && text == HttpStatusCode.BadRequest && ok == HttpStatusCode.OK &&
                    s.SpendAlert() == new SpendAlert(2.5, 0) && (await Get(owner, "usage/alert")).GetProperty("alert").GetProperty("dailyUsd").GetDouble() == 2.5, "Threshold validation wrong: " + saved);
                Console.WriteLine("PASS usage: the head gets 403 on the report and thresholds and no tab code; thresholds are validated");

                // 7. Key balance: numbers reach the report, cached for 5 minutes; 401 gives no key data; the key never shows.
                var k = (await Get(owner, "usage")).GetProperty("key");
                Assert(k.GetProperty("usageDaily").GetDouble() == 0.4 && k.GetProperty("usageMonthly").GetDouble() == 7.25 && k.GetProperty("limitRemaining").GetDouble() == 7.5 &&
                    k.GetProperty("usageWeekly").GetDouble() == 2 && k.GetProperty("limit").GetDouble() == 20 && k.GetProperty("usage").GetDouble() == 12.5 &&
                    Same(k.GetProperty("bot").GetProperty("daily"), s.UsageUtc(DateTimeOffset.UtcNow).Daily) &&
                    Same(k.GetProperty("bot").GetProperty("monthly"), s.UsageUtc(DateTimeOffset.UtcNow).Monthly), "Key data wrong: " + k);
                Assert(keyCalls == 1 && auth == "Bearer " + Key, "Key not cached or wrong auth: " + keyCalls);
                var badReport = await Get(ownerBad, "usage");
                await Get(ownerBad, "usage");
                Assert((!badReport.TryGetProperty("key", out var none) || none.ValueKind == JsonValueKind.Null) && badReport.GetProperty("byDay").GetArrayLength() == 30, "401 key broke the report");
                var garbageCalls = 0;
                var garbage = Client(c, (_, _) => { garbageCalls++; return (HttpStatusCode.OK, "<html>не json</html>"); });
                var odd = Client(c, (_, _) => (HttpStatusCode.OK, """{"data":{"usage":"3.5","limit":null,"limit_remaining":"x"}}"""));
                Assert(await garbage.KeyInfo(ct) is null && await garbage.KeyInfo(ct) is null && garbageCalls == 1, "Garbage key answer not null or retried at once");
                Assert(await odd.KeyInfo(ct) is { Usage: 3.5, Limit: null, LimitRemaining: null, UsageDaily: null }, "Tolerant key parsing wrong");
                Assert(seen.All(x => !x.Contains("SECRETKEY") && !x.Contains(Key)), "The key leaked into a response");
                Console.WriteLine("PASS usage: key balance reaches the report and is cached; 401 or garbage gives no key data; the key never shows");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); await badWeb.StopAsync(CancellationToken.None); }
        }

        // 8. Overspend warnings: one private message per day, survives a restart, off at 0.
        {
            var c = make("usage-alert.db"); c.ModelRouting = new();
            var sent = new List<(long Chat, string Text)>();
            var tg = new HttpClient(new Fake((request, body) =>
            {
                var b = JsonDocument.Parse(body).RootElement;
                lock (sent) sent.Add((b.Number("chat_id"), b.Text("text") ?? ""));
                return (HttpStatusCode.OK, Json.Write(new { ok = true, result = new { message_id = 1, date = 1, chat = new { id = b.Number("chat_id"), type = "private" } } }));
            })) { BaseAddress = new Uri("https://telegram.test/") };
            var cost = 0.0;
            (BotWorker Worker, OpenRouterClient Ai, BotStore Store) Start()
            {
                var store = new BotStore(c);
                var client = Client(c, (_, _) => (HttpStatusCode.OK, Answer("ok", "stop", new { prompt_tokens = 1, completion_tokens = 1, cost })));
                return (new BotWorker(c, store, new TelegramClient(tg, "t"), client, NullLogger<BotWorker>.Instance), client, store);
            }
            void Unthrottle(BotWorker w) => typeof(BotWorker).GetField("spendCheckedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, long.MinValue / 2);
            Task Alerts(BotWorker w) => (Task)typeof(BotWorker).GetMethod("SpendAlerts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, [DateTimeOffset.UtcNow, CancellationToken.None])!;
            int Warnings() { lock (sent) return sent.Count(x => x.Text.StartsWith("💸")); }

            var (w1, ai1, s1) = Start();
            s1.SetSpendAlert(new(1.0, 0));
            s1.Set("last_owner_warning", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()); // a recent unrelated warning must not swallow this one
            cost = 0.9; await ai1.Complete("s", "u", false, ct, "chat");
            await Task.Delay(300);
            Assert(Warnings() == 0, "Warned below the threshold");
            cost = 0.2; await ai1.Complete("s", "u", false, ct, "chat");
            await Task.Delay(300);
            Assert(Warnings() == 0, "Check ran more than once a minute");
            await Alerts(w1); // the queued check at the end of the minute
            Assert(Warnings() == 1 && sent.Single(x => x.Text.StartsWith("💸")) is { Chat: 1, Text: "💸 Сегодня потрачено $1.10 (порог $1.00)" }, "Warning text or target wrong: " + string.Join(" | ", sent));
            Unthrottle(w1);
            cost = 0.1; await ai1.Complete("s", "u", false, ct, "chat");
            await Task.Delay(300);
            await Alerts(w1);
            Assert(Warnings() == 1, "Second warning the same day");
            s1.Dispose();
            var (_, ai2, s2) = Start(); // restart: a new worker on the same database
            cost = 0.1; await ai2.Complete("s", "u", false, ct, "chat");
            await Task.Delay(300);
            Assert(Warnings() == 1 && s2.Get("usage_alert_sent:day") == DateOnly.FromDateTime(c.LocalNow(DateTimeOffset.UtcNow)).ToString("yyyy-MM-dd") + "|1", "Restart sent the warning again");
            s2.Dispose();

            var z = make("usage-alert-off.db"); z.ModelRouting = new();
            c = z;
            var (w3, ai3, s3) = Start();
            cost = 5; await ai3.Complete("s", "u", false, ct, "chat");
            await Task.Delay(300); await Alerts(w3);
            Assert(Warnings() == 1, "Warned with thresholds at 0");
            s3.SetSpendAlert(new(0, 2.0));
            Unthrottle(w3);
            cost = 0.01; await ai3.Complete("s", "u", false, ct, "chat");
            Assert(await Until(() => Warnings() == 2, 2000) && sent.Last().Text == "💸 За месяц потрачено $5.01 (порог $2.00)", "Monthly warning wrong: " + sent.Last().Text);
            s3.Dispose();
            Console.WriteLine("PASS usage: overspend warning goes once a day to the developer, survives a restart, is off at 0, monthly works");
        }

        // 9. Migration and retention.
        {
            var c = make("usage-migrate.db");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO kv VALUES('keep','old value');" +
                    "CREATE TABLE ai_log(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, kind TEXT NOT NULL, tag TEXT NOT NULL, model TEXT NOT NULL, system TEXT NOT NULL, " +
                    "request TEXT NOT NULL, response TEXT NOT NULL, ms INTEGER NOT NULL, error TEXT); INSERT INTO ai_log(time,kind,tag,model,system,request,response,ms) VALUES(strftime('%s','now'),'chat','t','m','s','r','a',1);" +
                    "PRAGMA user_version=2;";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var s = new BotStore(c);
            Assert(s.Get("keep") == "old value" && s.AiCalls().Count == 1 && Rows(c).Count == 0, "Migration lost data or ai_usage missing");
            var now = DateTimeOffset.UtcNow;
            s.AddUsage(new("chat", "", "req", "m", 1, 1, 0, 1, 0, 0.1, "stop", 1), now.AddDays(-401));
            s.AddUsage(new("chat", "", "req", "m", 1, 1, 0, 1, 0, 0.2, "stop", 1), now.AddDays(-399));
            s.Maintain(now);
            Assert(Rows(c).Single().Cost == 0.2 && s.Get("keep") == "old value", "Retention wrong");
            foreach (var (at, cost) in new[] { ("2026-10-25T23:59:30Z", 1.0), ("2026-10-26T00:00:30Z", 2.0), ("2026-10-28T00:00:10Z", 4.0), ("2026-09-30T12:00:00Z", 8.0) })
                s.AddUsage(new("chat", "", "req", "m", 1, 1, 0, 1, 0, cost, "stop", 1), DateTimeOffset.Parse(at));
            var utc = s.UsageUtc(DateTimeOffset.Parse("2026-10-28T10:00:00Z"));
            Assert(utc.Daily.Cost == 4 && utc.Weekly.Cost == 6 && utc.Monthly.Cost == 7, "UTC day/week (from Monday)/month wrong: " + utc);
            Console.WriteLine("PASS usage: migration creates ai_usage keeping old data; Maintain drops rows older than 400 days");
        }
        return 9 + await RunFix6(make);
    }
}
