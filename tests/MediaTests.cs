using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Block 7: the media library (G1) and the Media Picker (G2).</summary>
static class MediaTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];
    static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8, 1, 2, 3];

    /// <summary>Worker over fake APIs that understand uploads, sticker sets and file downloads. AI kind by the system prompt.</summary>
    sealed class Env : IDisposable
    {
        public BotConfig C = null!; public BotStore S = null!; public BotWorker W = null!;
        public readonly List<(string Method, JsonElement Body)> Tg = [];
        public readonly List<(string Method, Dictionary<string, string> Fields, byte[] File)> Uploads = [];
        public readonly List<(string Kind, string User)> Ai = [];
        public Queue<string> Routes = new();
        public Func<string, string>? PickerAnswer;
        public string Writer = "Дедлайн по сиаоду горит, но мы держимся";
        public string WriterSystem = "";
        public Func<string, int?>? Fail;
        public JsonElement[] Set = [];
        int next = 9000;
        public void Dispose() => S.Dispose();
        public List<(string Method, JsonElement Body)> Calls(string method) { lock (Tg) return Tg.Where(x => x.Method == method).ToList(); }
        public List<(string Kind, string User)> Asked(string kind) { lock (Ai) return Ai.Where(x => x.Kind == kind).ToList(); }
        public void Clear() { lock (Tg) Tg.Clear(); lock (Ai) Ai.Clear(); }
        public Task Process(object m) => (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(W, [El(new { message = m }), CancellationToken.None])!;
        public Task Callback(long actor, string data) => (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(W, [El(new { callback_query = new { id = "cb", from = new { id = actor }, data, message = new { message_id = 77, chat = new { id = actor } } } }), CancellationToken.None])!;
        public Task<bool> DescribeNext() => (Task<bool>)typeof(BotWorker).GetMethod("DescribeNextMedia", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(W, [CancellationToken.None])!;
        public Task Loop(CancellationToken ct) => (Task)typeof(BotWorker).GetMethod("MediaLoop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(W, [ct])!;
        object Sent(long chat, int topic, string field, object value) => new Dictionary<string, object>
        {
            ["message_id"] = Interlocked.Increment(ref next), ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["chat"] = new { id = chat, type = chat > 0 ? "private" : "supergroup" }, ["message_thread_id"] = topic, ["from"] = new { id = 99, is_bot = true }, [field] = value
        };
        public static Env Make(Func<string, BotConfig> make, string file)
        {
            var e = new Env { C = make(file) };
            e.C.Ambient.Enabled = false; e.C.SplitReplies = false;
            e.C.Features.Router = true; e.C.Features.MediaLibrary = true;
            e.S = new BotStore(e.C);
            var tg = new HttpClient(new Bytes(new RouterTests.Handler(async (request, body) =>
            {
                await Task.Yield();
                var method = request.RequestUri!.Segments[^1];
                JsonElement p;
                if (request.Content is MultipartFormDataContent form)
                {
                    var fields = new Dictionary<string, string>(); byte[] bytes = [];
                    foreach (var part in form)
                        if (part.Headers.ContentDisposition?.FileName is null) fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync();
                        else bytes = await part.ReadAsByteArrayAsync();
                    lock (e.Tg) e.Uploads.Add((method, fields, bytes));
                    p = El(fields);
                }
                else p = JsonDocument.Parse(body).RootElement.Clone();
                lock (e.Tg) e.Tg.Add((method, p));
                if (e.Fail?.Invoke(method) is { } code) return (HttpStatusCode.OK, new { ok = false, error_code = code, description = "fake failure" });
                var chat = p.Number("chat_id") != 0 ? p.Number("chat_id") : long.Parse(p.Text("chat_id") ?? "0");
                var topic = (int)p.Number("message_thread_id");
                return (HttpStatusCode.OK, method switch
                {
                    "sendMessage" => new { ok = true, result = e.Sent(chat, topic, "text", p.Text("text")!) },
                    "sendPhoto" => new { ok = true, result = e.Sent(chat, topic, "photo", new[] { new { file_id = "ph-small", file_unique_id = "ph-u-small", width = 90, height = 90 },
                        new { file_id = "ph-big", file_unique_id = "ph-u", width = 800, height = 800 } }) },
                    "sendAnimation" => new { ok = true, result = e.Sent(chat, topic, "animation", new { file_id = "an-1", file_unique_id = "an-u", duration = 2 }) },
                    "sendVideo" => new { ok = true, result = e.Sent(chat, topic, "video", new { file_id = "vi-1", file_unique_id = "vi-u", duration = 2 }) },
                    "sendSticker" => new { ok = true, result = e.Sent(chat, topic, "sticker", new { file_id = p.Text("sticker"), file_unique_id = "x" }) },
                    "getStickerSet" => new { ok = true, result = (object)new { name = p.Text("name"), title = "Котики", stickers = e.Set } },
                    "getFile" => new { ok = true, result = (object)new { file_id = p.Text("file_id"), file_path = "stickers/" + p.Text("file_id") + ".webp", file_size = 100 } },
                    _ => new { ok = true, result = (object)true }
                });
            }))) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = new HttpClient(new RouterTests.Handler(async (_, body) =>
            {
                await Task.Yield();
                var root = JsonDocument.Parse(body).RootElement;
                var system = root.Field("messages")[0].Text("content") ?? "";
                var content = root.Field("messages")[1].Field("content");
                var user = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].Text("text") ?? "";
                var kind = system.Contains("Ты — маршрутизатор") ? "router" : system.Contains("Ты подбираешь стикер, картинку или гифку") ? "picker"
                    : system.Contains("Ты описываешь медиа") ? "digest" : system.Contains("разбираешь личное сообщение редактора") ? "plan" : "chat";
                lock (e.Ai) e.Ai.Add((kind, user));
                if (kind == "chat") e.WriterSystem = system;
                return (HttpStatusCode.OK, RouterTests.Answer(kind switch
                {
                    "router" => e.Routes.Count > 0 ? e.Routes.Dequeue() : """{"reaction":null,"mode":"writer","reason":"болтовня"}""",
                    "picker" => e.PickerAnswer?.Invoke(user) ?? """{"id":null}""",
                    "digest" => """{"summary":"кот смеётся над дедлайном","text_on_image":null,"transcript":null}""",
                    "plan" => """{"intent":"chat"}""",
                    _ => e.Writer
                }));
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            e.W = new BotWorker(e.C, e.S, new TelegramClient(tg, "t"), new OpenRouterClient(ai, e.C, "k"), NullLogger<BotWorker>.Instance);
            typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, 99L);
            typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, "vozduhanAIbot");
            return e;
        }
    }
    /// <summary>Serves /file/ downloads as raw bytes and passes everything else to the JSON fake.</summary>
    sealed class Bytes(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath.StartsWith("/file/")
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Webp) })
                : base.SendAsync(request, ct);
    }

    static object Sticker(string unique, string? set = "cats") => new
        { file_id = "st-" + unique, file_unique_id = unique, emoji = "😂", set_name = set, is_animated = false, is_video = false, width = 512, height = 512 };
    static object StickerDm(long actor, int id, string unique, string? set = "cats") => new
    {
        message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = actor, type = "private" },
        from = new { id = actor, first_name = "Имя" }, sticker = Sticker(unique, set)
    };
    static string[] Buttons(JsonElement send) => send.Field("reply_markup").Field("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray())
        .Select(b => b.Text("text")!).ToArray();
    static long Add(BotStore s, string unique, string description, string kind = "sticker")
    {
        var id = s.MediaAdd(kind, "f-" + unique, unique, null, "", "😂", null, null, 1, DateTimeOffset.UtcNow, true).Id;
        s.MediaDescribed(id, description);
        return id;
    }

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var count = 0;
        void Pass(string name) { count++; Console.WriteLine("PASS " + name); }
        var every = BotWorker.MediaDescribeEvery;

        // G1. A sticker in the developer's DM: buttons, one sticker, the whole set, descriptions one by one.
        using (var e = Env.Make(make, "media-sticker.db"))
        {
            await e.Process(StickerDm(1, 10, "u1"));
            var offer = e.Calls("sendMessage").Single().Body;
            Assert(Buttons(offer).SequenceEqual(["➕ В медиатеку", "➕ Весь набор", "Нет"]) && e.Ai.Count == 0, "Sticker offer wrong: " + offer);
            var slot = e.S.ActiveSlot(1, 1, DateTimeOffset.UtcNow)!;
            Assert(slot.Capability == "media_add", "Offer is not a catalog action");
            await e.Callback(1, $"slot:{slot.Id}:0");
            var item = e.S.MediaByUnique("u1");
            Assert(item is { Kind: "sticker", FileId: "st-u1", Emoji: "😂", Set: "cats", Description: null, Enabled: true } && e.Calls("sendMessage").Any(x => x.Body.Text("text")!.Contains("Добавил")),
                "Sticker not added as waiting");
            // The head and a member get no buttons; media_add itself is the developer's only.
            e.Clear();
            await e.Process(StickerDm(2, 11, "u-head"));
            Assert(e.Calls("sendMessage").All(x => x.Body.Field("reply_markup").ValueKind != JsonValueKind.Object) && e.S.MediaByUnique("u-head") is null, "Head got the media offer");
            var refused = await e.W.Catalog.Execute(2, Capabilities.Dm, "media_add", El(new { scope = "one" }), El(StickerDm(2, 12, "u-head2")), "k", "", CancellationToken.None);
            Assert(refused.Refusal == Refusal.Role && e.S.MediaByUnique("u-head2") is null, "Head ran media_add");

            // The whole set: N new stickers wait for a description; the one already added is not duplicated.
            e.Set = [El(Sticker("u1")), El(Sticker("u2")), El(Sticker("u3")), El(Sticker("u4")), El(Sticker("u5"))];
            e.Clear();
            await e.Process(StickerDm(1, 13, "u2"));
            slot = e.S.ActiveSlot(1, 1, DateTimeOffset.UtcNow)!;
            await e.Callback(1, $"slot:{slot.Id}:1");
            Assert(e.Calls("getStickerSet").Single().Body.Text("name") == "cats", "getStickerSet not asked");
            Assert(e.S.MediaList(null, null).Count == 5 && e.S.MediaList(null, null).All(x => x.Description is null) &&
                e.Calls("sendMessage").Any(x => x.Body.Text("text")!.Contains("добавил 4 из 5")), "Set not added as N waiting");

            // In the background: exactly one per call, not faster than the interval, within the digest limit.
            Assert(await e.DescribeNext() && e.Asked("digest").Count == 1, "Background description did not run");
            Assert(e.S.MediaList(null, null).Count(x => x.Description == "кот смеётся над дедлайном") == 1, "Not exactly one described");
            BotWorker.MediaDescribeEvery = TimeSpan.FromMilliseconds(400);
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600)))
                try { await e.Loop(cts.Token); } catch (OperationCanceledException) { }
            var described = e.S.MediaList(null, null).Count(x => x.Description is not null);
            Assert(described is 2 or 3 && e.Asked("digest").Count == described, $"Loop did not pause between items: {described}");
            e.C.Features.DigestDailyLimit = e.S.DigestSpent(DateTimeOffset.UtcNow);
            Assert(!await e.DescribeNext() && e.S.MediaList(null, null).Count(x => x.Description is not null) == described, "Digest daily limit ignored");
            Assert(e.Asked("digest").All(x => x.User.Contains("стикер")), "Digest input wrong");
            Pass("media: developer's sticker → buttons → added; whole set → N waiting; background descriptions one by one with a pause, within the digest limit");

            // Flag off: a sticker is just a message.
            e.C.Features.MediaLibrary = false; e.Clear();
            await e.Process(StickerDm(1, 14, "u9"));
            Assert(e.Calls("sendMessage").All(x => x.Body.Field("reply_markup").ValueKind != JsonValueKind.Object) && !await e.DescribeNext(), "Library works with the flag off");
            Pass("media flag off: no buttons, no background descriptions");
        }
        BotWorker.MediaDescribeEvery = every;

        // FTS5 (and the fallback without it) finds by description.
        using (var e = Env.Make(make, "media-fts.db"))
        {
            var cat = Add(e.S, "a", "Кот смеётся над дедлайном");
            Add(e.S, "b", "Грустный студент на паре");
            Assert(e.S.MediaFts, "Bundled SQLite has no FTS5");
            Assert(e.S.MediaList("смешной кот", null).Select(x => x.Id).SequenceEqual([cat]) && e.S.MediaList("дедлайны", null).Single().Id == cat &&
                e.S.MediaList("студенты", "sticker").Count == 1 && e.S.MediaList("студенты", "photo").Count == 0, "FTS search by description failed");
            e.S.MediaUpdate(cat, null, null, "мем, ржака", null);
            Assert(e.S.MediaList("ржака", null).Single().Id == cat, "Tags not indexed after an update");
            typeof(BotStore).GetProperty("MediaFts")!.SetValue(e.S, false);
            Assert(e.S.MediaList("смешной кот", null).Select(x => x.Id).SequenceEqual([cat]) && e.S.MediaList("ржака", null).Single().Id == cat, "Fallback search failed");
            Pass("media search: FTS5 finds by description and tags regardless of endings; substring fallback without FTS5");
        }

        // Upload and access through the real admin server.
        using (var e = Env.Make(make, "media-web.db"))
        {
            var port = 26000 + Random.Shared.Next(1000);
            e.C.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            using var cts = new CancellationTokenSource();
            var web = new AdminWeb(e.C, e.S, e.W, NullLogger<AdminWeb>.Instance);
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
                MultipartFormDataContent File(byte[] data, string name) => new() { { new ByteArrayContent(data), "file", name } };
                using var owner = await Login(1);
                using var head = await Login(2);
                var r = await owner.PostAsync("api/media/upload", File(Png, "кот.png"));
                var text = await r.Content.ReadAsStringAsync();
                Assert(r.StatusCode == HttpStatusCode.OK, "Upload failed: " + text);
                var id = JsonDocument.Parse(text).RootElement.Number("id");
                var item = e.S.Media(id)!;
                var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(e.C.DataPath))!, "media");
                var up = e.Uploads.Single();
                Assert(item is { Kind: "photo", FileId: "ph-big", UniqueId: "ph-u", Title: "кот", Description: null } && item.LocalPath is { } local &&
                    System.IO.File.ReadAllBytes(Path.Combine(folder, local)).SequenceEqual(Png), "File not saved or file_id not stored");
                Assert(up.Method == "sendPhoto" && up.Fields["chat_id"] == "1" && up.File.SequenceEqual(Png), "Not sent to the developer's DM as a photo");
                var preview = await owner.GetAsync($"api/media/{id}/preview");
                Assert(preview.StatusCode == HttpStatusCode.OK && preview.Content.Headers.ContentType?.MediaType == "image/png" &&
                    (await preview.Content.ReadAsByteArrayAsync()).SequenceEqual(Png), "Preview of a local file wrong");
                Assert(await e.DescribeNext() && e.S.Media(id)!.Description == "кот смеётся над дедлайном", "Uploaded file not described");

                // Wrong type: the signature decides, not the extension.
                foreach (var (data, name) in new[] { ("GIF89a......"u8.ToArray(), "x.png"), (Encoding.UTF8.GetBytes("<html>"), "x.jpg"), (Png, "x.exe"), (Png, "x.gif") })
                {
                    var bad = await owner.PostAsync("api/media/upload", File(data, name));
                    Assert(bad.StatusCode == HttpStatusCode.BadRequest, $"{name} accepted: {bad.StatusCode}");
                }
                Assert(e.Uploads.Count == 1 && Directory.GetFiles(folder).Length == 1, "Rejected file reached Telegram or disk");

                // The head: 403 everywhere, nothing changes.
                var calls = new (string Method, string Url, HttpContent? Body)[]
                {
                    ("GET", "api/media", null), ("GET", $"api/media/{id}/preview", null), ("POST", "api/media/upload", File(Png, "a.png")),
                    ("POST", "api/media/update", new StringContent(Json.Write(new { id, title = "взлом" }))), ("POST", "api/media/delete", new StringContent(Json.Write(new { id })))
                };
                foreach (var (method, url, body) in calls)
                {
                    var res = method == "GET" ? await head.GetAsync(url) : await head.PostAsync(url, body);
                    Assert(res.StatusCode == HttpStatusCode.Forbidden, $"Head got {res.StatusCode} on {url}");
                }
                Assert(e.S.Media(id) is { Title: "кот", Deleted: false } && e.Uploads.Count == 1, "Head changed the library");

                // The developer: list, edit, switch off, soft delete.
                var list = JsonDocument.Parse(await owner.GetStringAsync("api/media?q=" + Uri.EscapeDataString("кот"))).RootElement;
                Assert(list.Field("items").GetArrayLength() == 1 && list.Text("search") == "fts5", "List wrong: " + list);
                var upd = await owner.PostAsync("api/media/update", new StringContent(Json.Write(new { id, title = "Котяра", tags = "мем", enabled = false })));
                Assert(upd.StatusCode == HttpStatusCode.OK && e.S.Media(id) is { Title: "Котяра", Tags: "мем", Enabled: false, Description: "кот смеётся над дедлайном" }, "Update failed");
                Assert((await owner.PostAsync("api/media/update", new StringContent(Json.Write(new { id, title = new string('я', 101) })))).StatusCode == HttpStatusCode.BadRequest, "Long title accepted");
                Assert((await owner.PostAsync("api/media/delete", new StringContent(Json.Write(new { id })))).StatusCode == HttpStatusCode.OK &&
                    e.S.Media(id) is { Deleted: true } && JsonDocument.Parse(await owner.GetStringAsync("api/media")).RootElement.Field("items").GetArrayLength() == 0, "Soft delete failed");
                Pass("media upload: saved next to the DB, sent to the developer's DM, file_id stored, described; wrong type → 400; head → 403 on every /api/media*");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        }

        // G2. The Writer asks for media with a [МЕДИА: …] tag; the Picker finds it. No chance, no cooldown, one per answer.
        static object GroupMsg(int id, string text) => RouterTests.Group(3, id, text);
        const string Wants = "[МЕДИА: кот, который ржёт до слёз над горящим дедлайном]";
        using (var e = Env.Make(make, "media-picker.db"))
        {
            var f = e.C.Features;
            f.MediaPicker = true;
            var cat = Add(e.S, "a", "кот смеётся над дедлайном");
            var sad = Add(e.S, "b", "грустный студент на паре");
            var dance = Add(e.S, "c", "победный танец");
            Assert(BotWorker.ExtractMedia("Ну ты даёшь\n\n[МЕДИА: кот ржёт]\n[медиа: ещё один]") == ("Ну ты даёшь", "кот ржёт") &&
                BotWorker.ExtractMedia("просто текст") == ("просто текст", null) && BotWorker.ExtractMedia("[МЕДИА:  ]") == ("", null), "Media tag parsing wrong");

            string? input = null;
            e.PickerAnswer = user => { input = user; return Json.Write(new { id = cat }); };
            e.Writer = "Дедлайн по сиаоду горит, но мы держимся\n" + Wants;
            await e.Process(GroupMsg(100, "как там дедлайн?"));
            var order = e.Tg.Select(x => x.Method).Where(m => m is "sendMessage" or "sendSticker").ToList();
            var sticker = e.Calls("sendSticker").Single().Body;
            Assert(order.SequenceEqual(["sendMessage", "sendSticker"]) && e.Calls("sendMessage")[0].Body.Text("text") == "Дедлайн по сиаоду горит, но мы держимся" &&
                sticker.Text("sticker") == "f-a" && sticker.Field("reply_parameters").Number("message_id") == 100 && sticker.Number("message_thread_id") == 10,
                "Media not sent after the answer, or the tag leaked: " + string.Join(",", order));
            var asked = JsonDocument.Parse(input!).RootElement;
            Assert(asked.Text("wanted") == "кот, который ржёт до слёз над горящим дедлайном" && asked.Text("answer")!.StartsWith("Дедлайн") &&
                asked.Field("candidates")[0].Number("id") == cat && e.WriterSystem.Contains("[МЕДИА:"), "Picker input or Writer rules wrong: " + input);
            Assert(e.S.Media(cat) is { Uses: 1 } && e.S.Post("reply:-100123:100:pick") is null, "Uses not counted or keys left");

            // No cooldown: the next answer right away gets its media too (another one: the same is not repeated in the chat for 24 h).
            e.Clear(); e.PickerAnswer = user => { input = user; return Json.Write(new { id = dance }); };
            await e.Process(GroupMsg(101, "сдали!"));
            Assert(e.Calls("sendSticker").Single().Body.Text("sticker") == "f-c" &&
                !JsonDocument.Parse(input!).RootElement.Field("candidates").EnumerateArray().Any(x => x.Number("id") == cat), "Second media blocked or repeated");

            // Only a tag: media without text. A foreign id: nothing. No tag: no Picker at all.
            e.Clear(); e.Writer = Wants; e.PickerAnswer = _ => Json.Write(new { id = sad });
            await e.Process(GroupMsg(102, "ахах"));
            Assert(e.Calls("sendMessage").Count == 0 && e.Calls("sendSticker").Single().Body.Text("sticker") == "f-b", "Tag-only answer wrong");
            var shock = Add(e.S, "e", "кот в шоке");
            e.Clear(); e.Writer = "ну такое\n" + Wants; e.PickerAnswer = _ => """{"id":424242}""";
            await e.Process(GroupMsg(103, "ну и денёк"));
            Assert(e.Asked("picker").Count == 1 && e.Calls("sendSticker").Count == 0 && e.Calls("sendMessage").Single().Body.Text("text") == "ну такое", "Foreign id sent");
            e.Clear(); e.Writer = "Пара в 10:40, А412";
            await e.Process(GroupMsg(104, "где пара?"));
            Assert(e.Asked("picker").Count == 0 && e.Calls("sendSticker").Count == 0, "Picker without a tag");
            Pass("media in answers: the Writer's [МЕДИА: …] tag → the Picker → one media right after the text, no cooldown; tag only → media only; foreign id or no tag → nothing");

            // Necessary checks: no candidates; the flag; DM only with PickerInPrivate (and no rules about media there).
            e.Clear(); e.Writer = "лол\n" + Wants;
            foreach (var id in new[] { cat, sad, dance, shock }) e.S.MediaUpdate(id, null, null, null, false);
            await e.Process(GroupMsg(105, "без кандидатов"));
            Assert(e.Asked("picker").Count == 0 && e.Calls("sendMessage").Single().Body.Text("text") == "лол", "Picker called without candidates");
            foreach (var id in new[] { cat, sad, dance, shock }) e.S.MediaUpdate(id, null, null, null, true);
            await e.Process(RouterTests.Dm(3, 106, "привет"));
            Assert(e.Asked("picker").Count == 0 && !e.WriterSystem.Contains("[МЕДИА:") && e.Calls("sendMessage").Last().Body.Text("text") == "лол", "Media in DM without PickerInPrivate");
            f.PickerInPrivate = true; e.PickerAnswer = _ => Json.Write(new { id = sad });
            await e.Process(RouterTests.Dm(3, 107, "привет"));
            Assert(e.Asked("picker").Count == 1 && e.Calls("sendSticker").Single().Body.Number("chat_id") == 3 && e.WriterSystem.Contains("[МЕДИА:"), "PickerInPrivate ignored");
            f.PickerInPrivate = false; f.MediaPicker = false; e.Clear();
            await e.Process(GroupMsg(108, "флаг выключен"));
            Assert(e.Asked("picker").Count == 0 && !e.WriterSystem.Contains("[МЕДИА:") && e.Calls("sendMessage").Single().Body.Text("text") == "лол", "Flag off still sends media");
            f.MediaPicker = true;
            Pass("media in answers: no candidates → no call; flag off or DM without PickerInPrivate → no rules, tag cut, nothing sent");

            // Restart after the text but before the media: one text, one choice, one media.
            e.Clear();
            var fresh = Add(e.S, "d", "дедлайны горят синим пламенем");
            e.PickerAnswer = _ => Json.Write(new { id = fresh });
            var fails = 1;
            e.Fail = m => m == "sendSticker" && fails-- > 0 ? 503 : null;
            try { await e.Process(GroupMsg(111, "что по дедлайнам")); throw new Exception("Expected the media failure"); }
            catch (TelegramError t) when (t.Code == 503) { }
            await e.Process(GroupMsg(111, "что по дедлайнам"));
            await e.Process(GroupMsg(111, "что по дедлайнам"));
            Assert(e.Calls("sendMessage").Count == 1 && e.Calls("sendSticker").Count == 2 && e.Asked("picker").Count == 1 && e.Asked("chat").Count == 1 &&
                e.S.Media(fresh)!.Uses == 1 && e.S.Post("reply:-100123:111") is not null, "Restart duplicated text or media");

            // Telegram rejects the file: the item is switched off, the answer stays done.
            e.Clear(); e.Fail = m => m == "sendSticker" ? 400 : null; e.PickerAnswer = _ => Json.Write(new { id = shock });
            await e.Process(GroupMsg(112, "а теперь"));
            Assert(e.Calls("sendSticker").Count == 1 && e.S.Media(shock) is { Enabled: false } && e.S.Post("reply:-100123:112") is not null, "Rejected file not disabled");
            Pass("media in answers: restart between text and media → one media; Telegram rejects the file → item disabled");
        }
        return count;
    }
}
