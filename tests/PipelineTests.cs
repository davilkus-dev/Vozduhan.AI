using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Block 2: feature settings, per-chat handlers and AI pools, message coalescing (D6), Media Digest (D2).</summary>
static class PipelineTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Handler(Func<HttpRequestMessage, string, Task<(HttpStatusCode Code, object Body)>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = await f(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new(code) { Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent(Json.Write(body), Encoding.UTF8, "application/json") };
        }
    }
    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    static object Chat(string content) => new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } };
    static async Task<bool> Until(Func<bool> done, int ms = 5000)
    {
        for (var i = 0; i < ms / 20; i++) { if (done()) return true; await Task.Delay(20); }
        return done();
    }

    /// <summary>A worker over fake Telegram and OpenRouter; records Telegram calls and the user content of every chat request.</summary>
    sealed class Env : IDisposable
    {
        public BotConfig C = null!; public BotStore S = null!; public BotWorker W = null!;
        public readonly List<(string Method, JsonElement Body)> Tg = [];
        public readonly List<string> ChatRequests = [], DigestRequests = [];
        public Func<string, Task<(HttpStatusCode, object)>>? Ai;
        public int Next = 1000;
        public void Dispose() => S.Dispose();
        public List<(string Method, JsonElement Body)> Calls(string method) { lock (Tg) return Tg.Where(x => x.Method == method).ToList(); }
        public Task Process(JsonElement m) =>
            (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(W, [El(new { message = m }), CancellationToken.None])!;
        public void Restart() => W = Worker();
        public BotWorker Worker()
        {
            var tg = new HttpClient(new Handler(async (request, body) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/file/")) { lock (Tg) Tg.Add(("download", default)); return (HttpStatusCode.OK, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }); }
                var method = request.RequestUri.Segments[^1];
                var p = JsonDocument.Parse(body).RootElement.Clone();
                lock (Tg) Tg.Add((method, p));
                if (method == "getFile") return (HttpStatusCode.OK, new { ok = true, result = new { file_id = p.Text("file_id"), file_size = 5000, file_path = "f/" + p.Text("file_id") } });
                if (method != "sendMessage") return (HttpStatusCode.OK, new { ok = true, result = true });
                return (HttpStatusCode.OK, new { ok = true, result = new { message_id = Interlocked.Increment(ref Next), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    chat = new { id = p.Number("chat_id"), type = p.Number("chat_id") > 0 ? "private" : "supergroup" }, message_thread_id = p.Number("message_thread_id"),
                    from = new { id = 99, is_bot = true }, text = p.Text("text") } });
            })) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = new HttpClient(new Handler(async (_, body) =>
            {
                var root = JsonDocument.Parse(body).RootElement;
                var system = root.Field("messages")[0].Text("content") ?? "";
                var user = root.Field("messages")[1].Field("content");
                if (system.Contains("Ты описываешь медиа")) lock (DigestRequests) DigestRequests.Add(body);
                else lock (ChatRequests) ChatRequests.Add(user.ValueKind == JsonValueKind.String ? user.GetString()! : user.GetRawText());
                return Ai is null ? (HttpStatusCode.OK, Chat("Ответ")) : await Ai(body);
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            var w = new BotWorker(C, S, new TelegramClient(tg, "t"), new OpenRouterClient(ai, C, "k"), NullLogger<BotWorker>.Instance);
            typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, 99L);
            typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, "vozduhanAIbot");
            return w;
        }
        public static Env Make(Func<string, BotConfig> make, string file)
        {
            var e = new Env { C = make(file) };
            e.C.Ambient.Enabled = false; e.C.SplitReplies = false;
            e.S = new BotStore(e.C); e.W = e.Worker();
            return e;
        }
    }
    static object Msg(long chat, int id, long actor, string? text = null, object? photo = null, object? voice = null, object? reply = null, object[]? entities = null, string? caption = null) => new
    {
        message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = chat, type = chat > 0 ? "private" : "supergroup" },
        message_thread_id = chat > 0 ? (int?)null : 10, from = new { id = actor, first_name = "Студент" }, text, caption, photo, voice, reply_to_message = reply, entities
    };
    static object[] Photo(string unique, long size = 50000) =>
        [new { file_id = unique + "-s", file_unique_id = unique + "-s", width = 90, height = 60, file_size = 900 },
         new { file_id = unique, file_unique_id = unique, width = 1280, height = 853, file_size = size },
         new { file_id = unique + "-4k", file_unique_id = unique + "-4k", width = 3840, height = 2560, file_size = size * 4 }];
    static JsonElement Remember(Env e, object message, long? arrived = null)
    {
        var m = El(message);
        e.S.Remember(m.Field("chat").Number("id"), m.TopicId(), (int)m.Number("message_id"), m.Number("date"), m.GetRawText(), arrived ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return m;
    }
    static string FocusText(string request) => JsonDocument.Parse(request).RootElement.Field("focus_message").Text("text") ?? "";

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var count = 0;
        void Pass(string name) { count++; Console.WriteLine("PASS " + name); }

        // A. Feature settings: everything off by default, stored in the database, developer only.
        {
            using var e = Env.Make(make, "features.db");
            var f = e.C.Features;
            Assert(!f.Coalesce && !f.MediaDigest && !f.DigestVoice && !f.DigestAll && !f.DigestVideo && f.DigestDailyLimit == 200, "Feature defaults are not off");
            var port = 23000 + Random.Shared.Next(1000);
            e.C.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            using var cts = new CancellationTokenSource();
            var web = new AdminWeb(e.C, e.S, e.W, NullLogger<AdminWeb>.Instance);
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
                using var owner = await Login(1);
                using var head = await Login(2);
                static async Task<HttpStatusCode> Post(HttpClient h, string json) =>
                    (await h.PostAsync("api/features", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode;
                var got = JsonDocument.Parse(await owner.GetStringAsync("api/features")).RootElement.Field("features");
                Assert(got.Field("coalesce").ValueKind == JsonValueKind.False && got.Number("digestDailyLimit") == 200, "GET features wrong");
                Assert((await head.GetAsync("api/features")).StatusCode == HttpStatusCode.Forbidden, "Head reads features");
                Assert(await Post(head, """{"settings":{"coalesce":true}}""") == HttpStatusCode.Forbidden && !e.C.Features.Coalesce, "Head changed features");
                foreach (var bad in new[] { """{"settings":{"coalesce":"yes"}}""", """{"settings":{"digestDailyLimit":-1}}""", """{"settings":{"digestDailyLimit":"5"}}""",
                    """{"settings":{"wat":true}}""", """{"settings":[1]}""", """{}""" })
                    Assert(await Post(owner, bad) == HttpStatusCode.BadRequest, "Garbage accepted: " + bad);
                Assert(!e.C.Features.Coalesce && e.C.Features.DigestDailyLimit == 200, "Rejected request changed settings");
                Assert(await Post(owner, """{"settings":{"coalesce":true,"mediaDigest":true,"digestDailyLimit":50}}""") == HttpStatusCode.OK, "Owner could not save");
                Assert(e.C.Features is { Coalesce: true, MediaDigest: true, DigestVoice: false, DigestDailyLimit: 50 }, "Saved flags not live");
                Assert(await Post(owner, """{"settings":{"digestVoice":true}}""") == HttpStatusCode.OK && e.C.Features is { Coalesce: true, DigestVoice: true }, "Partial update lost fields");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
            using var reloaded = new BotStore(make("features.db"));
            Assert(reloaded.Config.Features is { Coalesce: true, MediaDigest: true, DigestVoice: true, DigestDailyLimit: 50 }, "Features not persisted");
            Pass("features: defaults off; developer saves and reads back; head 403; garbage 400; survives restart");
        }

        // B1. Claim: one item per chat at a time, order within the chat, other chats go in parallel.
        {
            using var s = new BotStore(make("claim-chat.db"));
            s.Ingest(1, 3, "{}", 0, 3); s.Ingest(2, 3, "{}", 0, 3); s.Ingest(3, 1, "{}", 0, -100123); s.Ingest(4, 2, "{}", 0, -100123); s.Ingest(5, 3, "{}", 0, -100123);
            Assert(s.Claim()!.Id == 1 && s.Claim()!.Id == 3 && s.Claim() is null, "Chats not independent or one chat ran twice");
            s.Finish(3); Assert(s.Claim()!.Id == 4, "Group order broken");
            s.Finish(1); Assert(s.Claim()!.Id == 2 && s.Claim() is null, "Private order broken");
            Pass("inbox: never two items of one chat at once, order kept, private chats and the group independent");
        }

        // B2. Handlers: two chats answer at the same time, two messages of one chat strictly in turn.
        {
            using var e = Env.Make(make, "parallel.db");
            var active = 0; var peak = 0;
            e.Ai = async _ =>
            {
                var now = Interlocked.Increment(ref active);
                lock (e) peak = Math.Max(peak, now);
                await Task.Delay(400);
                Interlocked.Decrement(ref active);
                return (HttpStatusCode.OK, Chat("Ответ"));
            };
            using var cts = new CancellationTokenSource();
            var loop = typeof(BotWorker).GetMethod("ProcessLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var loops = Enumerable.Range(0, 3).Select(_ => (Task)loop.Invoke(e.W, [0, cts.Token])!).ToArray();
            void In(long update, object m) { var x = El(m); e.S.Ingest(update, x.Field("from").Number("id"), El(new { update_id = update, message = m }).GetRawText(), 0, x.Field("chat").Number("id")); }
            In(1, Msg(3, 11, 3, "привет"));
            In(2, Msg(-100123, 12, 3, "@vozduhanAIbot привет", entities: [new { type = "mention", offset = 0, length = 14 }]));
            Assert(await Until(() => e.Calls("sendMessage").Count == 2), "Two chats not answered");
            Assert(peak == 2, "Two chats were not answered at the same time: peak " + peak);
            peak = 0; e.Tg.Clear();
            In(3, Msg(3, 21, 3, "раз")); In(4, Msg(3, 22, 3, "два"));
            Assert(await Until(() => e.Calls("sendMessage").Count == 2), "Same chat not answered");
            var order = e.Calls("sendMessage").Select(x => x.Body.Field("reply_parameters").Number("message_id")).ToArray();
            Assert(peak == 1 && order.SequenceEqual([21L, 22L]), $"Same chat overlapped or reordered: peak {peak}, order {string.Join(",", order)}");
            cts.Cancel();
            try { await Task.WhenAll(loops); } catch (OperationCanceledException) { }
            Pass("handlers: two chats in parallel, one chat strictly in order (fake AI with a delay)");
        }

        // B3. AI pools: background kinds never take the interactive slots.
        {
            var c = make("pools.db");
            var gate = new TaskCompletionSource();
            var entered = new List<string>();
            var ai = new OpenRouterClient(new HttpClient(new Handler(async (_, body) =>
            {
                var tag = JsonDocument.Parse(body).RootElement.Field("messages")[1].Text("content")!;
                lock (entered) entered.Add(tag);
                await gate.Task;
                return (HttpStatusCode.OK, Chat("ок"));
            })) { BaseAddress = new Uri("https://openrouter.test/") }, c, "k");
            var chats = new[] { "chat1", "chat2", "chat3" }.Select(x => ai.Complete("s", x, false, CancellationToken.None, "chat")).ToArray();
            Assert(await Until(() => entered.Count == 2), "Interactive slots not used");
            var ambient = ai.Complete("s", "ambient", false, CancellationToken.None, "ambient-summary");
            var bg = ai.Complete("s", "digest-bg", false, CancellationToken.None, "digest-bg");
            Assert(await Until(() => entered.Contains("ambient")) && !entered.Contains("chat3"), "Ambient waited for chats or a third chat got in");
            await Task.Delay(100);
            Assert(!entered.Contains("digest-bg") && entered.Count == 3, "Background pool has more than one slot");
            gate.SetResult();
            await Task.WhenAll([.. chats, ambient, bg]);
            Assert(entered.Count == 5, "Requests lost");
            Pass("pools: chat uses 2 interactive slots, ambient runs alongside, background has 1 slot");
        }

        // D6. Coalescing, with the windows scaled down (1.5 s → 300 ms, 6 s → 1.2 s).
        var (quiet, max) = (BotWorker.CoalesceQuiet, BotWorker.CoalesceMax);
        BotWorker.CoalesceQuiet = TimeSpan.FromMilliseconds(300); BotWorker.CoalesceMax = TimeSpan.FromMilliseconds(1200);
        try
        {
            using var e = Env.Make(make, "coalesce.db");
            e.C.Features.Coalesce = true;
            // Private: «привет» and 200 ms later «где пара?» — one answer, to the second.
            var m1 = Remember(e, Msg(3, 31, 3, "привет"));
            var first = e.Process(m1);
            await Task.Delay(200);
            var m2 = Remember(e, Msg(3, 32, 3, "где пара?"));
            await first;
            await e.Process(m2);
            var sends = e.Calls("sendMessage");
            Assert(e.ChatRequests.Count == 1 && sends.Count == 1 && sends[0].Body.Field("reply_parameters").Number("message_id") == 32, "Coalesced pair not answered once to the newest");
            Assert(e.Calls("setMessageReaction").All(x => x.Body.Number("message_id") == 32) && e.Calls("setMessageReaction").Count == 1, "Reaction not only on the newest");
            Assert(e.S.Post("reply:3:31")?.Text == "coalesced:32" && e.S.Log(chat: 3).Any(x => x.Direction == "coalesced" && x.Message == 31), "Coalesced message not marked in posts and log");
            Assert(FocusText(e.ChatRequests[0]) == "где пара?" && e.ChatRequests[0].Contains("привет"), "The newest answer lacks the earlier message in history");
            // A restart replays the first message: no answer.
            e.Restart(); e.Tg.Clear();
            await e.Process(m1);
            Assert(e.ChatRequests.Count == 1 && e.Calls("sendMessage").Count == 0, "Restart answered the coalesced message");

            // Group: a mention and then a message without one from the same author: one answer, to the second.
            e.Tg.Clear(); e.ChatRequests.Clear();
            var g1 = Remember(e, Msg(-100123, 41, 3, "@vozduhanAIbot слушай", entities: [new { type = "mention", offset = 0, length = 14 }]));
            var gFirst = e.Process(g1);
            await Task.Delay(200);
            var g2 = Remember(e, Msg(-100123, 42, 3, "где пара?"));
            var other = Remember(e, Msg(-100123, 43, 2, "а мне?"));
            await gFirst; await e.Process(g2); await e.Process(other);
            sends = e.Calls("sendMessage");
            Assert(sends.Count == 1 && sends[0].Body.Field("reply_parameters").Number("message_id") == 42, "Group follow-up without a mention not answered as one");

            // A message after the window gets its own answer.
            e.Tg.Clear(); e.ChatRequests.Clear();
            var late1 = Remember(e, Msg(3, 51, 3, "раз"));
            await e.Process(late1);
            await Task.Delay(1400);
            await e.Process(Remember(e, Msg(3, 52, 3, "два")));
            Assert(e.Calls("sendMessage").Count == 2 && e.ChatRequests.Count == 2, "A late message was swallowed");

            // Without the flag both are answered.
            e.C.Features.Coalesce = false; e.Tg.Clear();
            var n1 = Remember(e, Msg(3, 61, 3, "раз"));
            var n2 = Remember(e, Msg(3, 62, 3, "два"));
            await e.Process(n1); await e.Process(n2);
            Assert(e.Calls("sendMessage").Count == 2, "Without the flag a message was swallowed");
            Pass("coalescing: two quick messages get one answer to the newest with one reaction; group follow-up counts as addressed; restart is silent; late message and flag off answer separately");
        }
        finally { (BotWorker.CoalesceQuiet, BotWorker.CoalesceMax) = (quiet, max); }

        // B4. Waiting for a coalescing window does not hold a handler: a third chat is answered meanwhile.
        BotWorker.CoalesceQuiet = TimeSpan.FromMilliseconds(1500);
        try
        {
            using var e = Env.Make(make, "coalesce-wait.db");
            e.C.Features.Coalesce = true;
            using var cts = new CancellationTokenSource();
            var loop = typeof(BotWorker).GetMethod("ProcessLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var loops = Enumerable.Range(0, 6).Select(_ => (Task)loop.Invoke(e.W, [0, cts.Token])!).ToArray();
            var id = 0;
            foreach (var who in new long[] { 1, 2, 3 })
            {
                var m = Remember(e, Msg(who, 70 + (int)who, who, "привет"));
                e.S.Ingest(++id, who, El(new { update_id = id, message = m }).GetRawText(), 0, who);
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(100);
            var command = Remember(e, Msg(-100123, 80, 3, "/today@vozduhanAIbot", entities: [new { type = "bot_command", offset = 0, length = 20 }]));
            e.S.Ingest(++id, 3, El(new { update_id = id, message = command }).GetRawText(), 0, -100123);
            Assert(await Until(() => e.Calls("sendMessage").Any(x => x.Body.Number("chat_id") == -100123), 1000) && clock.ElapsedMilliseconds < 1200,
                "A command waited for chats that were only waiting for their coalescing window");
            Assert(await Until(() => e.Calls("sendMessage").Count == 4), "Waiting chats not answered");
            cts.Cancel();
            try { await Task.WhenAll(loops); } catch (OperationCanceledException) { }
            Pass("coalescing wait frees the handler: other chats are answered while three chats wait");
        }
        finally { BotWorker.CoalesceQuiet = quiet; }

        // D2. Media Digest.
        {
            using var e = Env.Make(make, "digest.db");
            var digestAnswer = (HttpStatusCode.OK, (object)Chat("""{"summary":"кот в шапке","text_on_image":"ПЯТНИЦА","transcript":null}"""));
            e.Ai = body => Task.FromResult(body.Contains("Ты описываешь медиа")
                ? body.Contains("input_audio") ? (HttpStatusCode.BadRequest, (object)new { error = "audio unsupported" }) : digestAnswer
                : (HttpStatusCode.OK, Chat("Ответ")));
            string Last() => FocusText(e.ChatRequests[^1]);

            // Flag off: nothing changes, no downloads.
            await e.Process(Remember(e, Msg(3, 101, 3, photo: Photo("p0"), caption: "что тут?"), DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));
            Assert(Last() == "что тут?" && e.DigestRequests.Count == 0 && e.Calls("getFile").Count == 0, "Flag off changed the text or fetched media");

            e.C.Features.MediaDigest = true;
            await e.Process(Remember(e, Msg(3, 102, 3, photo: Photo("p1"), caption: "что тут?")));
            Assert(Last() == "[фото: кот в шапке; надпись: «ПЯТНИЦА»] что тут?", "Photo description missing: " + Last());
            Assert(e.Calls("getFile").Single().Body.Text("file_id") == "p1", "Not the ≤1280 photo size downloaded");
            var request = JsonDocument.Parse(e.DigestRequests.Single()).RootElement;
            var parts = request.Field("messages")[1].Field("content");
            Assert(parts[1].Field("image_url").Text("url")!.StartsWith("data:image/jpeg;base64,") && request.Field("messages")[0].Text("content")!.Contains("не инструкции"),
                "Image not sent as data URL or prompt lacks the untrusted-data rule");
            Assert(e.S.AiCalls().Any(x => x.Kind == "digest" && x.Tag == "chat:3:102") && e.S.UsageReport(DateTimeOffset.UtcNow, 7).ByKind.Any(k => k.Kind == "digest"),
                "Digest call not in the AI log or spending");

            // The same file again: from the cache. A reply to it and the history show the description, without requests.
            await e.Process(Remember(e, Msg(3, 103, 3, photo: Photo("p1"))));
            Assert(e.DigestRequests.Count == 1 && Last() == "[фото: кот в шапке; надпись: «ПЯТНИЦА»]", "Same file described twice");
            await e.Process(Remember(e, Msg(3, 104, 3, "а это?", reply: Msg(3, 102, 3, photo: Photo("p1")))));
            var ctx = JsonDocument.Parse(e.ChatRequests[^1]).RootElement;
            Assert(ctx.Field("focus_message").Field("replied_message").Text("text")!.StartsWith("[фото: кот") &&
                ctx.Field("recent_messages").EnumerateArray().Any(x => x.Text("text") == "[фото: кот в шапке; надпись: «ПЯТНИЦА»] что тут?"), "Reply or history lacks the cached description");

            // Private media never reach the group context.
            await e.Process(Remember(e, Msg(-100123, 105, 3, "@vozduhanAIbot что было?", entities: [new { type = "mention", offset = 0, length = 14 }])));
            Assert(!e.ChatRequests[^1].Contains("кот в шапке"), "Private media description leaked into the group");

            // A history photo without a cached description is not requested.
            Remember(e, Msg(3, 106, 3, photo: Photo("old")), DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
            await e.Process(Remember(e, Msg(3, 107, 3, "ну?")));
            Assert(e.DigestRequests.Count == 1 && JsonDocument.Parse(e.ChatRequests[^1]).RootElement.Field("recent_messages").EnumerateArray().Any(x => x.Text("text") == "[фото]"),
                "History media requested or wrong marker");

            // Voice without DigestVoice: duration only, nothing fetched. With it, a refused audio is «не расшифровано», no retry.
            var voice = new { file_id = "v1", file_unique_id = "v1", duration = 14, file_size = 30000 };
            var files = e.Calls("getFile").Count;
            await e.Process(Remember(e, Msg(3, 108, 3, voice: voice)));
            Assert(Last() == "[голосовое 0:14]" && e.DigestRequests.Count == 1 && e.Calls("getFile").Count == files, "Voice described without DigestVoice");
            e.C.Features.DigestVoice = true;
            await e.Process(Remember(e, Msg(3, 109, 3, voice: voice)));
            var audio = JsonDocument.Parse(e.DigestRequests[^1]).RootElement.Field("messages")[1].Field("content")[1].Field("input_audio");
            Assert(Last() == "[голосовое 0:14, не расшифровано]" && audio.Text("format") == MediaDigest.AudioFormat, "Refused audio wrong: " + Last());
            var audioRequests = e.DigestRequests.Count;
            await e.Process(Remember(e, Msg(3, 110, 3, voice: voice)));
            Assert(e.DigestRequests.Count == audioRequests, "Refused audio retried");

            // Invalid JSON twice: «[фото]» without description.
            e.Ai = body => Task.FromResult<(HttpStatusCode, object)>(body.Contains("Ты описываешь медиа") ? (HttpStatusCode.OK, Chat("не json")) : (HttpStatusCode.OK, Chat("Ответ")));
            var before = e.DigestRequests.Count;
            await e.Process(Remember(e, Msg(3, 111, 3, photo: Photo("bad"))));
            Assert(Last() == "[фото]" && e.DigestRequests.Count > before, "Invalid digest not replaced by the bare marker");
            Assert(MediaDigest.Parse("""{"summary":"кот","text_on_image":null,"transcript":null}""") is not null &&
                MediaDigest.Parse("""{"summary":"кот","transcript":7}""") is null && MediaDigest.Parse($$"""{"summary":"{{new string('я', 301)}}"}""") is null &&
                MediaDigest.Parse($$"""{"summary":"кот","transcript":"{{new string('я', 1501)}}"}""") is null && MediaDigest.Parse("""{"summary":"猫猫猫"}""") is null,
                "Digest JSON checks wrong");

            // A file over 10 MB is not downloaded.
            e.Ai = body => Task.FromResult(body.Contains("Ты описываешь медиа") ? digestAnswer : (HttpStatusCode.OK, Chat("Ответ")));
            files = e.Calls("getFile").Count; before = e.DigestRequests.Count;
            await e.Process(Remember(e, Msg(3, 112, 3, photo: Photo("huge", 11 * 1024 * 1024))));
            Assert(Last() == "[фото]" && e.Calls("getFile").Count == files && e.DigestRequests.Count == before, "Huge file fetched");

            // Daily limit: over it the photo stays «[фото]» and the model is not called.
            e.C.Features.DigestDailyLimit = int.Parse(e.S.Get($"digest_budget:{e.C.LocalNow(DateTimeOffset.UtcNow):yyyy-MM-dd}")!);
            await e.Process(Remember(e, Msg(3, 113, 3, photo: Photo("p2"))));
            Assert(Last() == "[фото]" && e.DigestRequests.Count == before, "Daily digest limit ignored");
            Pass("digest: photo described for the Writer, cached by file, reply and history from cache, private stays private, voice rules, invalid JSON, 10 MB and daily limits, flag off");
        }

        // D2+. Every media in Воздуханство is described in the background (digest-bg), when enabled.
        {
            using var e = Env.Make(make, "digest-all.db");
            e.C.Features.MediaDigest = true; e.C.Features.DigestAll = true;
            e.Ai = body => Task.FromResult<(HttpStatusCode, object)>((HttpStatusCode.OK, Chat("""{"summary":"мем про сессию","text_on_image":null,"transcript":null}""")));
            var served = 0;
            var tg = new HttpClient(new Handler(async (request, body) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/file/")) return (HttpStatusCode.OK, new byte[] { 0xFF, 0xD8, 0xFF });
                var method = request.RequestUri.Segments[^1];
                if (method == "getFile") return (HttpStatusCode.OK, new { ok = true, result = new { file_size = 100, file_path = "x.jpg" } });
                if (method == "getUpdates" && Interlocked.Increment(ref served) == 1)
                    return (HttpStatusCode.OK, new { ok = true, result = new[] { new { update_id = 1, message = Msg(-100123, 201, 3, photo: Photo("meme")) } } });
                if (method == "getUpdates") await Task.Delay(200);
                return (HttpStatusCode.OK, new { ok = true, result = Array.Empty<object>() });
            })) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = (OpenRouterClient)typeof(BotWorker).GetField("ai", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(e.W)!;
            var w = new BotWorker(e.C, e.S, new TelegramClient(tg, "t"), ai, NullLogger<BotWorker>.Instance);
            using var cts = new CancellationTokenSource();
            Task Loop(string name) => (Task)typeof(BotWorker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, [cts.Token])!;
            var loops = new[] { Loop("Poll"), Loop("DigestLoop") };
            Assert(await Until(() => e.S.Digest("meme") is not null), "Background digest did not run");
            cts.Cancel();
            try { await Task.WhenAll(loops); } catch (OperationCanceledException) { }
            var history = new Conversation(e.C, e.S).AmbientContext("spontaneous");
            Assert(history.Contains("[фото: мем про сессию]") && e.S.AiCalls().Any(x => x.Kind == "digest-bg"), "Background description not in history or not logged");
            Pass("digest: with «all media» every photo in Воздуханство is described in the background and logged as digest-bg");
        }
        return count;
    }
}
