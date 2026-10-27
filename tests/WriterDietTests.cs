using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Block 5: knowledge in pieces (migration), the Writer's diet and a stable system prompt (D5), web search by the Router (F1).</summary>
static class WriterDietTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    static JsonElement Op(object o) => El(o);

    /// <summary>Worker over fake APIs; every AI request body is kept, the answer is chosen by the test.</summary>
    sealed class Env : IDisposable
    {
        public BotConfig C = null!; public BotStore S = null!; public BotWorker W = null!;
        public readonly List<(string Kind, string System, string User, JsonElement Body)> Ai = [];
        public readonly List<(string Method, JsonElement Body)> Tg = [];
        public Queue<string> Routes = new();
        /// <summary>Answer for a Writer call; null — the default text.</summary>
        public Func<JsonElement, (HttpStatusCode, object)?>? Writer;
        int next = 9000;
        public void Dispose() => S.Dispose();
        public List<(string Kind, string System, string User, JsonElement Body)> Asked(string kind) { lock (Ai) return Ai.Where(x => x.Kind == kind).ToList(); }
        public List<string> Texts() { lock (Tg) return Tg.Where(x => x.Method == "sendMessage").Select(x => x.Body.Text("text")!).ToList(); }
        public void Clear() { lock (Ai) Ai.Clear(); lock (Tg) Tg.Clear(); }
        public Task Process(object m) =>
            (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(W, [El(new { message = m }), CancellationToken.None])!;
        public static Env Make(Func<string, BotConfig> make, string file, Action<BotConfig>? setup = null)
        {
            var e = new Env { C = make(file) };
            e.C.Ambient.Enabled = false; e.C.SplitReplies = false; e.C.Features.Router = true;
            setup?.Invoke(e.C);
            e.S = new BotStore(e.C);
            var tg = new HttpClient(new RouterTests.Handler(async (request, body) =>
            {
                await Task.Yield();
                var method = request.RequestUri!.Segments[^1];
                var p = JsonDocument.Parse(body).RootElement.Clone();
                lock (e.Tg) e.Tg.Add((method, p));
                if (method != "sendMessage") return (HttpStatusCode.OK, new { ok = true, result = true });
                return (HttpStatusCode.OK, new { ok = true, result = new { message_id = Interlocked.Increment(ref e.next), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    chat = new { id = p.Number("chat_id"), type = p.Number("chat_id") > 0 ? "private" : "supergroup" }, message_thread_id = p.Number("message_thread_id"),
                    from = new { id = 99, is_bot = true }, text = p.Text("text") } });
            })) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = new HttpClient(new RouterTests.Handler(async (_, body) =>
            {
                await Task.Yield();
                var root = JsonDocument.Parse(body).RootElement.Clone();
                var system = root.Field("messages")[0].Text("content") ?? "";
                var user = root.Field("messages")[1].Text("content") ?? "";
                var kind = system.Contains("Ты — маршрутизатор") ? "router" : "chat";
                lock (e.Ai) e.Ai.Add((kind, system, user, root));
                if (kind == "router")
                    return (HttpStatusCode.OK, RouterTests.Answer(e.Routes.Count > 0 ? e.Routes.Dequeue() : """{"reaction":null,"mode":"writer","reason":"болтовня"}"""));
                return e.Writer?.Invoke(root) ?? (HttpStatusCode.OK, RouterTests.Answer("Ответ писателя"));
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            e.W = new BotWorker(e.C, e.S, new TelegramClient(tg, "t"), new OpenRouterClient(ai, e.C, "k"), NullLogger<BotWorker>.Instance);
            typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, 99L);
            typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, "vozduhanAIbot");
            return e;
        }
    }
    static string Writer(string[]? blocks = null, string? web = null) =>
        Json.Write(new { reaction = (string?)null, mode = "writer", blocks, web = web is null ? null : new { query = web }, reason = "тест" });

    /// <summary>Secrets of the test group: two knowledge pieces, one tone note, two people with memes.</summary>
    static void Secrets(BotConfig c)
    {
        c.Knowledge = "ОБЩЕЕ-ЗНАНИЕ про ПТУ МИРЭА";
        c.KnowledgeBlocks = [new("local_memes", "МЕМ-ШТАНЫ-ЛЕСТНИЦА") { Hint = "мемы группы — для шуток и прожарки" },
            new("rules", "ПРАВИЛО-БАЛЛОВ") { Hint = "правила баллов", Always = true }];
        c.Subjects = [new Subject("siaod", "СиАОД", ["сиад", "сиаод"]) { AdminComment = "ТОН-ДУШНИТ" }, new Subject("java", "Java", ["джава"])];
        c.Members = c.Members.Select(m => m.UserId switch
        {
            3 => m with { AdminNote = "ЗАМЕТКА-СТУДЕНТ про видеокарты" },
            2 => m with { AdminNote = "ЗАМЕТКА-СТАРОСТА опаздывает" },
            _ => m
        }).ToArray();
    }

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var count = 0;
        void Pass(string name) { count++; Console.WriteLine("PASS " + name); }

        // Migration: the single knowledge text becomes «general_info», old versions keep working, nothing is lost.
        {
            var c = make("kn-migrate.db");
            c.Knowledge = "СТАРОЕ-ЗНАНИЕ";
            using (var s = new BotStore(c))
            {
                Assert(c.R.Knowledge == "" && c.R.KnowledgeBlocks.Single() is { Key: KnowledgeBlock.General, Text: "СТАРОЕ-ЗНАНИЕ" }, "Seed text not moved to general_info");
                // A version written by the previous program: only the old field, no pieces.
                var legacy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Json.Write(c.R), Json.Options)!;
                legacy.Remove("knowledgeBlocks"); legacy["knowledge"] = El("ЗНАНИЕ-ИЗ-СТАРОЙ-ВЕРСИИ");
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + c.DataPath);
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO registry_versions(time,actor,source,summary,payload) VALUES(1,1,'old','old',$p)";
                cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(legacy, Json.Options));
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var s = new BotStore(c))
            {
                var v = s.RegistryVersion();
                Assert(c.R.KnowledgeText() == "ЗНАНИЕ-ИЗ-СТАРОЙ-ВЕРСИИ" && c.R.Knowledge == "", "Old stored version not migrated on load");
                Assert(new Conversation(c, s).AdminContext().Contains("ЗНАНИЕ-ИЗ-СТАРОЙ-ВЕРСИИ"), "Migrated knowledge not in the prompt");
                s.CommitRegistry(1, "t", null, cur => RegistryOps.ApplyAll(cur, [Op(new { op = "knowledge_block_set", key = "local_memes", text = "МЕМ", hint = "мемы" }),
                    Op(new { op = "knowledge_append", text = "ДОПИСАНО" })], c.OwnerId, true));
                Assert(c.R.KnowledgeBlocks.Select(b => b.Key).SequenceEqual([KnowledgeBlock.General, "local_memes"]) && c.R.KnowledgeText().EndsWith("ДОПИСАНО") &&
                    c.R.Block("local_memes")!.Hint == "мемы", "Block operations wrong");
                Assert(s.RegistryHistory(1)[0].Summary.Contains("[local_memes]"), "History line for a piece missing");
                // The head can't touch any piece; a redacted copy has none; a head save keeps them.
                try { RegistryOps.ApplyAll(c.R, [Op(new { op = "knowledge_block_set", key = "x", text = "y" })], c.OwnerId, false); throw new Exception("Head changed knowledge"); }
                catch (InvalidOperationException) { }
                Assert(c.R.Redacted().KnowledgeBlocks.Length == 0 && c.R.Redacted().WithSecretsFrom(c.R).KnowledgeBlocks.Length == 2, "Redact/merge of pieces broken");
                foreach (var bad in new object[] { new { op = "knowledge_block_set", key = "Плохой ключ", text = "x" }, new { op = "knowledge_block_remove", key = "nope" } })
                    try { RegistryOps.ApplyAll(c.R, [Op(bad)], c.OwnerId, true); throw new Exception("Bad piece op accepted: " + Json.Write(bad)); }
                    catch (InvalidOperationException) { }
                // Undo to the old-format version brings its text back as general_info.
                s.SaveRegistryDraft(new RegistryDraft("tok", 1, s.RegistryVersion(), "restore", [], ["x"], "/undo", v));
                s.ConfirmRegistryDraft(1, "tok");
                Assert(c.R.KnowledgeBlocks.Single().Text == "ЗНАНИЕ-ИЗ-СТАРОЙ-ВЕРСИИ", "Undo to an old version lost the knowledge");
            }
            Pass("knowledge migration: old text → general_info (seed and stored versions), pieces by key, head can't touch them, undo to an old version works");
        }

        // D5. Diet: only the chosen blocks; people notes never in DM; Router sees hints, not texts; stable system.
        {
            using var e = Env.Make(make, "diet.db", c => { Secrets(c); c.Features.WriterDiet = true; });
            var conv = new Conversation(e.C, e.S);
            var now = DateTimeOffset.UtcNow;
            var draft = Deadlines.Resolve(e.C, new("create", SubjectId: "siaod", Work: "Практика №7", Date: "2026-12-30"), null, [], 1, "t", now);
            e.S.SaveDraft(draft); e.S.Confirm(1, draft.Token, now);
            for (var i = 1; i <= 20; i++)
                e.S.Remember(e.C.GroupId, e.C.ChatTopicId, i, now.ToUnixTimeSeconds(), El(RouterTests.Group(2, i, "история " + i)).GetRawText());

            var (trusted, user) = conv.Writer(El(RouterTests.Group(3, 30, "какая пара?")), false, [ContextBlocks.ScheduleToday]);
            using (var doc = JsonDocument.Parse(user))
            {
                var root = doc.RootElement;
                Assert(root.Field("known_group_members").ValueKind == JsonValueKind.Undefined && root.Field("confirmed_upcoming_deadlines").ValueKind == JsonValueKind.Undefined &&
                    !user.Contains("Практика №7") && !user.Contains("tg://user"), "Members or deadlines in a schedule_today request: " + user);
                Assert(root.Field("schedule").Field("days").GetArrayLength() == 1 && root.Field("schedule").Field("now").Text("week_parity") is not null &&
                    root.Field("schedule").Field("subjects").ValueKind == JsonValueKind.Undefined, "Schedule part wrong: " + root.Field("schedule").GetRawText());
                Assert(root.Field("recent_messages").GetArrayLength() == Conversation.ShortHistory && root.Field("focus_message").Number("message_id") == 30, "Short history or focus wrong");
                Assert(root.Field("context_note").GetString()!.Contains("дедлайнов") && !root.Field("context_note").GetString()!.Contains("расписания на сегодня"), "No note about missing data");
            }
            Assert(trusted.Contains("ПРАВИЛО-БАЛЛОВ") && !trusted.Contains("МЕМ-ШТАНЫ") && !trusted.Contains("ОБЩЕЕ-ЗНАНИЕ") && !trusted.Contains("ТОН-ДУШНИТ") &&
                !trusted.Contains("ЗАМЕТКА-"), "Only the «always» piece expected: " + trusted);

            // Humour: a meme piece and one person for the roast; group ids expand.
            var roast = conv.Writer(El(RouterTests.Group(3, 31, "прожарь старосту")), false, ["knowledge:local_memes", "person:m2", "members", "bogus"]).Trusted;
            Assert(roast.Contains("МЕМ-ШТАНЫ") && roast.Contains("ЗАМЕТКА-СТАРОСТА") && !roast.Contains("ЗАМЕТКА-СТУДЕНТ"), "Roast pieces wrong: " + roast);
            Assert(conv.Writer(El(RouterTests.Group(3, 32, "x")), false, [ContextBlocks.PeopleNotes]).Trusted.Contains("ЗАМЕТКА-СТУДЕНТ"), "people_notes did not expand");

            // DM: notes about people never, whatever was asked; knowledge and tone notes follow the old policy.
            var dm = conv.Writer(El(RouterTests.Dm(3, 33, "прожарь старосту")), true, [ContextBlocks.PeopleNotes, "person:m2", ContextBlocks.Knowledge, ContextBlocks.SubjectNotes]).Trusted;
            Assert(!dm.Contains("ЗАМЕТКА-") && dm.Contains("МЕМ-ШТАНЫ") && dm.Contains("ТОН-ДУШНИТ"), "DM policy broken: " + dm);
            Assert(!conv.Writer(El(RouterTests.Dm(3, 34, "x")), true, null).Trusted.Contains("ЗАМЕТКА-") && !conv.AdminContext(true).Contains("ЗАМЕТКА-"), "People notes in DM with the full set");
            Assert(conv.AdminContext(false) == conv.Writer(El(RouterTests.Group(3, 35, "x")), false, null).Trusted && conv.AdminContext(false).Contains("[local_memes]"), "Full set differs from AdminContext");

            // The Router's catalog: ids with hints, never the texts; no people in DM. Unknown ids are dropped by code.
            var group = conv.BlockCatalog(false); var inDm = conv.BlockCatalog(true);
            Assert(group.Any(b => b.Id == "knowledge:local_memes" && b.About.Contains("прожарки")) && group.Any(b => b.Id == "person:m3") &&
                !inDm.Any(b => b.Id.StartsWith(ContextBlocks.PersonPrefix) || b.Id == ContextBlocks.PeopleNotes) && !Json.Write(group).Contains("МЕМ-ШТАНЫ") && !Json.Write(group).Contains("ЗАМЕТКА-"), "Block catalog wrong");
            var parsed = BotWorker.ParseRoute(Writer(["schedule_today", "bogus", "person:m3", "schedule_today"]), [], ["👍"], new DateOnly(2026, 10, 27), out _, inDm.Select(b => b.Id).ToHashSet());
            Assert(parsed!.Blocks!.SequenceEqual(["schedule_today"]), "Unknown or DM-forbidden blocks kept: " + Json.Write(parsed.Blocks));
            Assert(BotWorker.ParseRoute(Writer(["schedule_today"]), [], ["👍"], new DateOnly(2026, 10, 27), out _)!.Blocks is null, "Blocks taken with the diet off");

            // Through the worker: the request carries only what was chosen, the system is byte-identical for two messages.
            e.Routes.Enqueue(Writer([ContextBlocks.ScheduleToday])); e.Routes.Enqueue(Writer([ContextBlocks.ScheduleToday]));
            await e.Process(RouterTests.Group(3, 40, "какая пара сейчас?"));
            await e.Process(RouterTests.Group(2, 41, "а у нас что дальше?"));
            var chats = e.Asked("chat");
            Assert(chats.Count == 2 && chats[0].System == chats[1].System && chats[0].User != chats[1].User, "System differs between two messages");
            Assert(!chats[0].System.Contains("message_id") && !chats[0].System.Contains(now.ToString("yyyy-MM-dd")) && !chats[0].System.Contains("now_unix"), "Volatile data in the system part");
            Assert(!chats[0].User.Contains("known_group_members") && !chats[0].User.Contains("confirmed_upcoming_deadlines"), "Diet not applied in the worker");
            var router = e.Asked("router")[0];
            Assert(router.User.Contains("context_blocks") && router.User.Contains("knowledge:local_memes") && router.System.Contains("blocks — только для mode=writer") &&
                !router.User.Contains("МЕМ-ШТАНЫ"), "Router did not get the block catalog");
            Assert(e.Texts().All(t => t == "Ответ писателя"), "Answer changed");
            Pass("diet: schedule_today → no members/deadlines, short history, «always» piece; roast pulls memes and one person; people notes never in DM; catalog has hints only; system byte-identical for two messages");
        }

        // Diet flag off: everything as before, Router gets no block catalog and its blocks are ignored.
        {
            using var e = Env.Make(make, "diet-off.db", Secrets);
            e.Routes.Enqueue(Writer([ContextBlocks.ScheduleToday]));
            await e.Process(RouterTests.Group(3, 50, "какая пара?"));
            var chat = e.Asked("chat").Single();
            var conv = new Conversation(e.C, e.S);
            Assert(chat.User.Contains("known_group_members") && chat.User.Contains("confirmed_upcoming_deadlines") && chat.System.Contains("МЕМ-ШТАНЫ") &&
                chat.System.Contains("ЗАМЕТКА-СТУДЕНТ") && chat.System == OpenRouterClient.ChatRules(e.C, false) + conv.AdminContext(false), "Full context changed without the flag");
            Assert(!e.Asked("router")[0].User.Contains("context_blocks") && !e.Asked("router")[0].System.Contains("blocks —"), "Block catalog sent without the flag");
            Pass("diet off: the full context and the old system prompt, blocks from the Router ignored");
        }

        // Size of the Writer's input before and after on ten typical messages (a fuller group).
        {
            using var e = Env.Make(make, "diet-size.db", c =>
            {
                Secrets(c);
                c.Members = [.. c.Members, .. Enumerable.Range(4, 22).Select(i => new Member(100 + i, $"Студентов Студент{i} Студентович", $"Имя{i}", $"user{i}", i)
                    { AdminNote = $"Мемы про Имя{i}: " + string.Join(" ", Enumerable.Repeat("вечно опаздывает и спорит с преподом", 6)) })];
                c.KnowledgeBlocks = [.. c.KnowledgeBlocks, new("easter_eggs", string.Join("\n", Enumerable.Repeat("Пасхалка: если спросят про штаны-лестницу — это легенда корпуса В.", 40))) { Hint = "пасхалки" }];
                c.Knowledge = string.Join("\n", Enumerable.Repeat("Общее: корпуса, баллы, традиции, где столовая, как сдавать практики.", 60));
            });
            var conv = new Conversation(e.C, e.S);
            var now = DateTimeOffset.UtcNow;
            for (var i = 1; i <= 30; i++)
                e.S.Remember(e.C.GroupId, e.C.ChatTopicId, i, now.ToUnixTimeSeconds(), El(RouterTests.Group(2 + i % 2, i, "обычная болтовня в чате номер " + i)).GetRawText());
            var set = new (string Text, string[] Blocks)[]
            {
                ("какая пара сейчас?", [ContextBlocks.ScheduleToday]), ("что завтра и где?", [ContextBlocks.ScheduleTomorrow, ContextBlocks.Subjects]),
                ("что сдаём на неделе?", [ContextBlocks.Deadlines, ContextBlocks.ScheduleWeek]), ("кто ведёт сиад?", [ContextBlocks.Subjects]),
                ("прожарь Имя5", ["knowledge:local_memes", "person:m5", ContextBlocks.Members]), ("как дела?", []), ("тегни старосту", [ContextBlocks.Members]),
                ("расскажи пасхалку", ["knowledge:easter_eggs"]), ("что там по баллам?", ["knowledge:general_info", "knowledge:rules"]),
                ("о чём мы говорили час назад?", [ContextBlocks.HistoryLong, ContextBlocks.ReplyChain])
            };
            double before = 0, after = 0;
            foreach (var (i, (text, blocks)) in set.Index())
            {
                var m = El(RouterTests.Group(3, 100 + i, text));
                before += OpenRouterClient.ChatRules(e.C, false).Length + conv.AdminContext(false).Length + conv.Context(m, false).Length;
                var (trusted, user) = conv.Writer(m, false, blocks);
                after += OpenRouterClient.WriterSystem(e.C, false, trusted, false).Length + user.Length;
            }
            before /= set.Length; after /= set.Length;
            Console.WriteLine($"INFO writer input on 10 test messages: full {before:0} chars on average, diet {after:0} chars ({after / before:P0}); " +
                $"≈{before / 3.5:0} → ≈{after / 3.5:0} tokens (rough estimate 3.5 chars/token, no tokenizer in the project); " +
                $"of it the fixed rules (cacheable prefix) {OpenRouterClient.ChatRules(e.C, false).Length} chars");
            Assert(after < before * 0.6, "Diet saves too little");
            Pass("diet input size measured on ten typical messages");
        }

        // F1. Web search: plugin in the body, kind chat-web, sources only from annotations, limits, plugin failure, flag off.
        {
            using var e = Env.Make(make, "web.db", c => { c.Features.WebSearch = true; c.Features.WriterDiet = true; });
            object Cited(string content) => new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content, annotations = new object[]
                {
                    new { type = "url_citation", url_citation = new { url = "https://www.mirea.ru/news/1", title = "Новость" } },
                    new { type = "url_citation", url_citation = new { url = "https://www.mirea.ru/news/1", title = "Дубль" } },
                    new { type = "url_citation", url_citation = new { url = "javascript:alert(1)", title = "Плохая" } },
                    new { type = "url_citation", url_citation = new { url = "https://cbr.ru/a?b=1&c=2", title = "ЦБ" } },
                    new { type = "file", file = new { url = "https://not-a-citation.test" } },
                    new { type = "url_citation", url_citation = new { url = "https://third.example/x_y_z", title = "3" } },
                    new { type = "url_citation", url_citation = new { url = "https://fourth.example/", title = "4" } }
                } } } },
                usage = new { prompt_tokens = 900, completion_tokens = 50, cost = 0.01 }
            };
            e.Writer = body => (HttpStatusCode.OK, Cited("Курс сегодня такой-то. Подробнее на https://evil.example"));
            e.Routes.Enqueue(Writer([], "курс доллара ЦБ сегодня"));
            await e.Process(RouterTests.Group(2, 60, "какой курс доллара?"));
            var call = e.Asked("chat").Single().Body;
            Assert(call.Field("plugins")[0].Text("id") == OpenRouterClient.WebPluginId && call.Field("plugins")[0].Number("max_results") == OpenRouterClient.WebMaxResults &&
                call.Field("messages")[call.Field("messages").GetArrayLength() - 1].Text("content")!.Contains("курс доллара ЦБ сегодня"), "Plugin or query missing: " + call.GetRawText());
            Assert(e.Asked("chat").Single().System.Contains("недоверенные данные из интернета"), "Untrusted-results line missing");
            var text = e.Texts().Single();
            var tail = text[text.LastIndexOf("Источники:", StringComparison.Ordinal)..];
            Assert(text.StartsWith("Курс сегодня") && tail.Contains("href=\"https://www.mirea.ru/news/1\">mirea.ru</a>") && tail.Contains("cbr.ru") && tail.Contains("third.example") &&
                !tail.Contains("fourth.example") && !tail.Contains("evil") && !tail.Contains("javascript") && !tail.Contains("not-a-citation") && tail.Contains("x_y_z\">"), "Sources wrong: " + text);
            Assert(e.S.UsageReport(DateTimeOffset.UtcNow, 1).ByKind.Any(k => k.Kind == "chat-web"), "Usage kind chat-web missing");
            Assert(e.S.Post("tail:reply:-100123:60") is null && e.S.Post("reply:-100123:60") is not null, "Tail not cleaned");

            // Per person: three a day, the fourth answers without the search and says so; the developer has no personal limit.
            e.Clear();
            for (var i = 0; i < 3; i++) { e.Routes.Enqueue(Writer([], "новости вуза " + i)); await e.Process(RouterTests.Group(3, 61 + i, "что нового в вузе?")); }
            e.Routes.Enqueue(Writer([], "ещё новости"));
            await e.Process(RouterTests.Group(3, 64, "а ещё?"));
            var chats = e.Asked("chat");
            Assert(chats.Count == 4 && chats.Take(3).All(x => x.Body.Field("plugins").ValueKind == JsonValueKind.Array) && chats[3].Body.Field("plugins").ValueKind == JsonValueKind.Undefined &&
                chats[3].User.Contains("исчерпан") && e.Texts()[^1].Contains("Поиск на сегодня исчерпан"), "Personal limit not applied");
            e.Clear();
            for (var i = 0; i < 4; i++) { e.Routes.Enqueue(Writer([], "запрос разработчика " + i)); await e.Process(RouterTests.Group(1, 70 + i, "найди")); }
            Assert(e.Asked("chat").All(x => x.Body.Field("plugins").ValueKind == JsonValueKind.Array), "Developer hit a personal limit");
            // Day total: 1 + 3 + 4 = 8 used; with the limit at 8 nobody searches any more, developer included.
            e.C.Features.WebSearchDailyLimit = 8;
            e.Clear(); e.Routes.Enqueue(Writer([], "ещё раз"));
            await e.Process(RouterTests.Group(1, 80, "найди ещё"));
            Assert(e.Asked("chat").Single().Body.Field("plugins").ValueKind == JsonValueKind.Undefined && e.Texts()[^1].Contains("исчерпан"), "Daily limit not applied");
            Pass("web: plugin and query in the body, kind chat-web, up to 3 sources from annotations only (domain + link), personal and daily limits, developer without a personal limit");
        }
        {
            using var e = Env.Make(make, "web-fail.db", c => c.Features.WebSearch = true);
            e.Writer = body => body.Field("plugins").ValueKind == JsonValueKind.Array
                ? (HttpStatusCode.BadRequest, new { error = new { message = "plugin not supported" } }) : null;
            e.Routes.Enqueue(Writer(null, "погода в Москве"));
            await e.Process(RouterTests.Group(3, 90, "какая погода?"));
            var chats = e.Asked("chat");
            Assert(chats.Count == 2 && chats[0].Body.Field("plugins").ValueKind == JsonValueKind.Array && chats[1].Body.Field("plugins").ValueKind == JsonValueKind.Undefined &&
                e.Texts().Single() == "Ответ писателя", "Plugin failure: no single retry without it");
            Assert(chats[0].User.Contains("known_group_members"), "Diet off: web call lost the full context");
            // A query longer than 200 characters is invalid (one retry); a valid web field with the flag off is ignored.
            Assert(BotWorker.ParseRoute(Writer(null, new string('я', 201)), [], ["👍"], new DateOnly(2026, 10, 27), out var err, web: true) is null && err!.Contains("200"), "Long query accepted");
            Assert(BotWorker.ParseRoute(Writer(null, "  курс   евро "), [], ["👍"], new DateOnly(2026, 10, 27), out _, web: true)!.Web == "курс евро", "Query not normalised");
            Assert(BotWorker.ParseRoute(Writer(null, "курс"), [], ["👍"], new DateOnly(2026, 10, 27), out _, web: false)!.Web is null, "Web taken with the flag off");
            Pass("web: plugin error → exactly one retry without the plugin; query ≤ 200 checked by code");
        }
        {
            using var e = Env.Make(make, "web-off.db");
            e.Routes.Enqueue(Writer(null, "курс доллара"));
            await e.Process(RouterTests.Group(3, 95, "какой курс?"));
            Assert(e.Asked("chat").Single().Body.Field("plugins").ValueKind == JsonValueKind.Undefined && !e.Asked("router")[0].System.Contains("web —") &&
                e.Texts().Single() == "Ответ писателя" && e.S.UsageReport(DateTimeOffset.UtcNow, 1).ByKind.All(k => k.Kind != "chat-web"), "Web used with the flag off");
            Pass("web flag off: Router's web ignored, no plugin, no rules about it");
        }
        return count;
    }
}
