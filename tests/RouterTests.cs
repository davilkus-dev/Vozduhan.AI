using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Block 3: the action catalog with role/place checks (D1) and the Router with a quick reaction (D3).</summary>
static class RouterTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal sealed class Handler(Func<HttpRequestMessage, string, Task<(HttpStatusCode Code, object Body)>> f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = await f(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new(code) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") };
        }
    }
    internal static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    internal static object Answer(string content) => new { choices = new[] { new { message = new { content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 100, completion_tokens = 10, cost = 0.0001 } };

    /// <summary>Worker over fake APIs. Seq keeps the order of every Telegram method and AI kind; AI answers are chosen per kind.</summary>
    internal sealed class Env : IDisposable
    {
        public BotConfig C = null!; public BotStore S = null!; public BotWorker W = null!;
        public readonly List<string> Seq = [];
        public readonly List<(string Method, JsonElement Body)> Tg = [];
        public readonly List<(string Kind, string System, string User)> Ai = [];
        public Func<string, (HttpStatusCode, object)?>? Reply;
        public Queue<string> Routes = new();
        public string Plan = "{}";
        int next = 7000;
        public void Dispose() => S.Dispose();
        public List<(string Method, JsonElement Body)> Calls(string method) { lock (Tg) return Tg.Where(x => x.Method == method).ToList(); }
        public List<(string Kind, string System, string User)> Asked(string kind) { lock (Ai) return Ai.Where(x => x.Kind == kind).ToList(); }
        public string LastText() => Calls("sendMessage")[^1].Body.Text("text")!;
        public void Clear() { lock (Tg) Tg.Clear(); lock (Ai) Ai.Clear(); lock (Seq) Seq.Clear(); }
        public Task Process(object m) =>
            (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(W, [El(new { message = m }), CancellationToken.None])!;
        public Task Callback(long actor, string data, int message = 77) =>
            (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(W,
                [El(new { callback_query = new { id = "cb", from = new { id = actor }, data, message = new { message_id = message, chat = new { id = actor } } } }), CancellationToken.None])!;
        static string Kind(string system) =>
            system.Contains("Ты — маршрутизатор") ? "router" : system.Contains("разбираешь личное сообщение редактора") ? "plan"
            : system.Contains("ЗАДАЧА: живой текст поста") ? "intro" : system.Contains("Извлеки учебный дедлайн") ? "deadline" : "chat";
        public static Env Make(Func<string, BotConfig> make, string file, bool router = true)
        {
            var e = new Env { C = make(file) };
            e.C.Ambient.Enabled = false; e.C.SplitReplies = false; e.C.Features.Router = router;
            e.S = new BotStore(e.C);
            var tg = new HttpClient(new Handler(async (request, body) =>
            {
                var method = request.RequestUri!.Segments[^1];
                var p = JsonDocument.Parse(body).RootElement.Clone();
                lock (e.Tg) e.Tg.Add((method, p));
                lock (e.Seq) e.Seq.Add("tg:" + method);
                if (method != "sendMessage") return (HttpStatusCode.OK, new { ok = true, result = true });
                return (HttpStatusCode.OK, new { ok = true, result = new { message_id = Interlocked.Increment(ref e.next), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    chat = new { id = p.Number("chat_id"), type = p.Number("chat_id") > 0 ? "private" : "supergroup" }, message_thread_id = p.Number("message_thread_id"),
                    from = new { id = 99, is_bot = true }, text = p.Text("text") } });
            })) { BaseAddress = new Uri("https://telegram.test/") };
            var ai = new HttpClient(new Handler(async (_, body) =>
            {
                await Task.Yield();
                var root = JsonDocument.Parse(body).RootElement;
                var system = root.Field("messages")[0].Text("content") ?? "";
                var user = root.Field("messages")[1].Text("content") ?? "";
                var kind = Kind(system);
                lock (e.Ai) e.Ai.Add((kind, system, user));
                lock (e.Seq) e.Seq.Add("ai:" + kind);
                if (e.Reply?.Invoke(kind) is { } custom) return custom;
                return (HttpStatusCode.OK, Answer(kind switch
                {
                    "router" => e.Routes.Count > 0 ? e.Routes.Dequeue() : """{"reaction":null,"mode":"writer","capability":null,"args":null,"question":null,"reason":"болтовня"}""",
                    "plan" => e.Plan,
                    "intro" => "Гайс, сдачу двигают.",
                    _ => "Ответ писателя"
                }));
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            e.W = new BotWorker(e.C, e.S, new TelegramClient(tg, "t"), new OpenRouterClient(ai, e.C, "k"), NullLogger<BotWorker>.Instance);
            typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, 99L);
            typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, "vozduhanAIbot");
            return e;
        }
    }
    internal static object Dm(long actor, int id, string text) => new
    {
        message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = actor, type = "private" },
        from = new { id = actor, first_name = "Имя" }, text
    };
    internal static object Group(long actor, int id, string text) => new
    {
        message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = -100123, type = "supergroup" }, message_thread_id = 10,
        from = new { id = actor, first_name = "Имя" }, text = "@vozduhanAIbot " + text, entities = new[] { new { type = "mention", offset = 0, length = 14 } }
    };
    internal static string Route(string mode, string? capability = null, object? args = null, string? reaction = null, string? question = null) =>
        Json.Write(new { reaction, mode, capability, args, question, reason = "тест" });
    internal static string Ru(DateOnly d) => d.ToString("yyyy-MM-dd");

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var count = 0;
        void Pass(string name) { count++; Console.WriteLine("PASS " + name); }
        var none = El(new { });

        // D1. Catalog: rights at execution, place, arguments.
        {
            using var e = Env.Make(make, "caps.db", router: false); // slash commands below exist only with the Router off (block 4)
            var cat = e.W.Catalog;
            var m = El(Dm(3, 1, "добавь пару"));
            var member = await cat.Execute(3, Capabilities.Dm, "edit_registry", none, m, "reply:3:1", "добавь пару", CancellationToken.None);
            Assert(member.Refusal == Refusal.Role && e.Ai.Count == 0 && e.Tg.Count == 0 && e.S.RegistryDraft(3) is null, "Member ran edit_registry");
            var inGroup = await cat.Execute(1, Capabilities.Group, "web_link", none, El(Group(1, 2, "ссылку")), "reply:-100123:2", "", CancellationToken.None);
            Assert(inGroup.Refusal == Refusal.Place && e.Tg.Count == 0, "DM-only action ran in the group");
            Assert((await cat.Execute(2, Capabilities.Dm, "post_idea", none, m, "k", "", CancellationToken.None)).Refusal == Refusal.Role, "Head ran a developer action");
            Assert((await cat.Execute(999, Capabilities.Dm, "help", none, m, "k", "", CancellationToken.None)).Refusal == Refusal.Role, "Stranger ran a public action");
            Assert((await cat.Execute(3, Capabilities.Dm, "drop_tables", none, m, "k", "", CancellationToken.None)).Refusal == Refusal.Unknown, "Unknown capability ran");
            foreach (var (id, args) in new (string, object)[] { ("schedule_day", new { }), ("schedule_day", new { date = "в пятницу" }), ("schedule_day", new { date = "2026-11-30", extra = 1 }),
                ("schedule_week", new { next = "да" }), ("delete_deadline", new { id = "abc" }), ("delete_deadline", new { id = -5 }) })
            {
                var r = await cat.Execute(1, Capabilities.Dm, id, El(args), m, "k", "", CancellationToken.None);
                Assert(r.Refusal == Refusal.Args && r.Html is null, $"Bad args accepted: {id} {Json.Write(args)}");
            }
            Assert(e.Tg.Count == 0 && e.Ai.Count == 0, "A refused call did something");
            var ok = await cat.Execute(3, Capabilities.Group, "schedule_day", El(new { date = "30.11.2026" }), m, "k", "", CancellationToken.None);
            Assert(ok.Ok && ok.Html!.Contains("СиАОД"), "Valid call refused");
            Assert(cat.For("member", Capabilities.Group).All(c => c.Roles.Contains("member")) && !cat.For("head", Capabilities.Dm).Any(c => c.Id is "post_idea" or "where") &&
                cat.For("developer", Capabilities.Dm).Any(c => c.Id == "post_idea") && !cat.For("developer", Capabilities.Group).Any(c => c.Places.SequenceEqual(Capabilities.DmOnly)), "Catalog filter wrong");

            // Router off: slash commands run through the catalog, same answers as before.
            e.Clear();
            await e.Process(new { message_id = 10, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = -100123, type = "supergroup" },
                message_thread_id = 10, from = new { id = 3, first_name = "Имя" }, text = "/web@vozduhanAIbot", entities = new[] { new { type = "bot_command", offset = 0, length = 18 } } });
            Assert(e.LastText().Contains("личк"), "Group /web not refused as a DM command");
            await e.Process(Dm(3, 11, "/post идея"));
            Assert(e.LastText().Contains("Не знаю команду") && !e.LastText().Contains("/post [идея]") && !e.LastText().Contains("/web"), "Member saw editor or developer commands");
            await e.Process(Dm(2, 12, "/help"));
            Assert(e.LastText().Contains("/undo") && !e.LastText().Contains("/post") && !e.LastText().Contains("/where"), "Head help not built from the catalog by role");
            await e.Process(Dm(1, 13, "/help"));
            Assert(e.LastText().Contains("/post") && e.LastText().Contains("/where"), "Developer help incomplete");
            await e.Process(Dm(3, 14, "/date"));
            Assert(e.LastText().Contains("Формат"), "/date without a date lost its hint");
            await e.Process(Dm(3, 15, "/deadline siaod | №1 | 2026-11-30 | 6"));
            Assert(e.LastText().Contains("Дедлайны меняют только") && e.S.Draft(3) is null, "Member /deadline message changed");
            Assert(e.Ai.Count == 0, "Commands called the AI");
            Pass("catalog: member/head cannot run others' actions, DM-only refused in the group, bad args refused, slash commands and help by role through the catalog");
        }

        // D3. Router: render without the Writer; one usage row of kind router.
        {
            using var e = Env.Make(make, "route-render.db");
            var tomorrow = DateOnly.FromDateTime(e.C.LocalNow(DateTimeOffset.UtcNow)).AddDays(1);
            e.Routes.Enqueue(Route("render", "schedule_day", new { date = Ru(tomorrow) }, "👀"));
            await e.Process(Group(3, 20, "что завтра?"));
            var reply = e.Calls("sendMessage").Single().Body;
            Assert(reply.Text("text")!.Contains("Завтра") && reply.Text("parse_mode") == "HTML" && reply.Field("reply_parameters").Number("message_id") == 20, "Render answer wrong");
            Assert(e.Asked("chat").Count == 0 && e.Asked("router").Count == 1, "Writer called for a render");
            var usage = e.S.UsageReport(DateTimeOffset.UtcNow, 1).ByKind;
            Assert(usage.Single().Kind == "router" && usage.Single().Calls == 1, "Usage rows wrong: " + Json.Write(usage));
            Assert(e.Calls("setMessageReaction").Single().Body.Field("reaction")[0].Text("emoji") == "👀", "Router reaction not set");
            Assert(e.S.Get($"chat_budget:{e.C.LocalNow(DateTimeOffset.UtcNow):yyyy-MM-dd}:3") is null, "Render spent the chat budget");
            var saved = JsonDocument.Parse(e.S.Get("route:-100123:20")!).RootElement;
            Assert(saved.Field("decision").Text("capability") == "schedule_day" && saved.Number("expires") > DateTimeOffset.UtcNow.AddDays(1.9).ToUnixTimeSeconds(), "Decision not kept for debugging");
            Assert(e.S.AiCalls().Any(x => x.Kind == "router" && x.Tag == "chat:-100123:20"), "Router call not in ai_log");
            // Replay: nothing new; expiry cleanup after two days.
            await e.Process(Group(3, 20, "что завтра?"));
            Assert(e.Calls("sendMessage").Count == 1 && e.Asked("router").Count == 1, "Replay routed or answered again");
            e.S.Maintain(DateTimeOffset.UtcNow.AddDays(1)); Assert(e.S.Get("route:-100123:20") is not null, "Decision removed too early");
            e.S.Maintain(DateTimeOffset.UtcNow.AddDays(3)); Assert(e.S.Get("route:-100123:20") is null, "Decision kept past two days");
            Pass("router render: schedule for tomorrow by code, no Writer, one router usage row, reaction, decision kept 2 days, replay silent");
        }

        // react_only, clarify, writer (and its budget), reaction before the Writer, bad emoji dropped.
        {
            using var e = Env.Make(make, "route-modes.db");
            e.Routes.Enqueue(Route("react_only", reaction: "❤️"));
            await e.Process(Group(3, 30, "спасибо!"));
            Assert(e.Calls("sendMessage").Count == 0 && e.Calls("setMessageReaction").Single().Body.Field("reaction")[0].Text("emoji") == "❤" &&
                e.Asked("chat").Count == 0 && e.S.Post("reply:-100123:30") is not null, "react_only did more than a reaction");
            e.Clear();
            e.Routes.Enqueue(Route("clarify", question: "Какой дедлайн удалить — #3 или #4?", reaction: "🤔"));
            await e.Process(Dm(1, 31, "удали тот дедлайн"));
            Assert(e.LastText() == "Какой дедлайн удалить — #3 или #4?" && e.Asked("chat").Count == 0 && e.Asked("plan").Count == 0, "Clarify wrong");
            e.Clear();
            e.Routes.Enqueue(Route("writer", reaction: "😁"));
            await e.Process(Group(3, 32, "расскажи анекдот про сиаод"));
            Assert(e.LastText() == "Ответ писателя" && e.Asked("chat").Count == 1, "Writer not called");
            var react = e.Seq.IndexOf("tg:setMessageReaction"); var writer = e.Seq.IndexOf("ai:chat");
            Assert(e.Seq[0] == "ai:router" && react > 0 && react < writer && e.Calls("setMessageReaction").Count == 1, "Reaction not before the Writer: " + string.Join(",", e.Seq));
            Assert(e.S.Get($"chat_budget:{e.C.LocalNow(DateTimeOffset.UtcNow):yyyy-MM-dd}:3") == "1", "Writer did not spend the chat budget");
            e.Clear();
            e.Routes.Enqueue(Route("writer", reaction: "🍆"));
            await e.Process(Group(3, 33, "ну чё"));
            Assert(e.Calls("setMessageReaction").Count == 0 && e.Asked("chat").Count == 1, "Disallowed reaction was sent");
            Pass("router modes: react_only only reacts, clarify asks, writer as before (budget spent), reaction before the Writer, bad emoji dropped");
        }

        // Invalid twice -> writer; a capability the role may not have counts as invalid; head never sees developer actions.
        {
            using var e = Env.Make(make, "route-invalid.db");
            e.Routes.Enqueue("не json"); e.Routes.Enqueue("""{"mode":"dance"}""");
            await e.Process(Group(3, 40, "привет"));
            Assert(e.Asked("router").Count == 2 && e.Asked("router")[1].User.Contains("Прошлый ответ отклонён") && e.LastText() == "Ответ писателя" &&
                e.Calls("setMessageReaction").Count == 0, "Invalid twice did not fall back to the Writer without a reaction");
            Assert(e.S.UsageReport(DateTimeOffset.UtcNow, 1).Wasted.Any(w => w.Kind == "router"), "Rejected router answers not counted as waste");
            e.Clear();
            e.Routes.Enqueue(Route("action", "edit_registry", reaction: "🫡")); e.Routes.Enqueue(Route("action", "edit_registry", reaction: "🫡"));
            await e.Process(Dm(3, 41, "отчисли Никиту"));
            Assert(e.Asked("plan").Count == 0 && e.S.RegistryDraft(3) is null && e.LastText() == "Ответ писателя", "Member reached the registry via the model");
            var memberCatalog = e.Asked("router")[0].User;
            Assert(!memberCatalog.Contains("edit_registry") && !memberCatalog.Contains("web_link") && memberCatalog.Contains("schedule_day"), "Member catalog not filtered");
            e.Clear();
            e.Routes.Enqueue(Route("action", "web_link")); e.Routes.Enqueue(Route("action", "web_link"));
            await e.Process(Group(1, 42, "дай ссылку на админку"));
            Assert(!e.Calls("sendMessage").Any(x => x.Body.Text("text")!.Contains("login?t=")) && !e.Asked("router")[0].User.Contains("web_link"), "DM-only action offered or run in the group");
            e.Clear();
            await e.Process(Dm(2, 43, "что нового?"));
            var head = e.Asked("router").Single().User;
            Assert(!head.Contains("post_idea") && !head.Contains("where") && head.Contains("edit_registry") && head.Contains("delete_deadline"), "Head router catalog wrong: " + head);
            e.Clear();
            await e.Process(Dm(1, 44, "что нового?"));
            Assert(e.Asked("router").Single().User.Contains("post_idea"), "Developer catalog lacks post_idea");
            var body = e.Asked("router").Single().User;
            var system = e.Asked("router").Single().System;
            Console.WriteLine($"INFO router input on test data: system {system.Length} chars, user {body.Length} chars (developer DM, full catalog)");
            if (Environment.GetEnvironmentVariable("ROUTER_DUMP") is { Length: > 0 } dump) File.WriteAllText(dump, system + "\n=====\n" + body);
            Pass("router validation: invalid twice → Writer; forbidden capability is invalid; catalog in the request filtered by role and place");
        }

        // Editor in DM: «перенеси сиаод на среду» → action edit_deadline → existing confirmation card, planner called once.
        {
            using var e = Env.Make(make, "route-editor.db");
            var now = DateTimeOffset.UtcNow;
            var draft = Deadlines.Resolve(e.C, new("create", SubjectId: "siaod", Work: "Практика №7", Date: "2026-11-30"), null, [], 1, "t", now);
            e.S.SaveDraft(draft); var d = e.S.Confirm(1, draft.Token, now);
            e.Routes.Enqueue(Route("action", "edit_deadline", reaction: "🫡"));
            e.Plan = Json.Write(new { intent = "deadline", deadline = new { action = "update", targetId = d.Id, date = "2026-12-02" } });
            await e.Process(Dm(2, 50, "перенеси сиаод на среду"));
            var card = e.Calls("sendMessage").Single().Body;
            Assert(e.Asked("plan").Count == 1 && e.Asked("chat").Count == 0 && card.Text("text")!.Contains("Так будет изменение") &&
                card.Field("reply_markup").GetRawText().Contains("save:"), "Editor action did not give the existing card: " + card.GetRawText());
            Assert(e.S.Draft(2) is { Date: "2026-12-02", Period: 2 }, "Draft wrong");
            Assert(e.Seq.IndexOf("tg:setMessageReaction") is >= 0 and var reacted && reacted < e.Seq.IndexOf("ai:plan"), "Reaction missing or after the planner");
            Assert(e.S.Get($"chat_budget:{e.C.LocalNow(now):yyyy-MM-dd}:2") is null, "Action spent the chat budget");

            // delete_deadline: card with buttons, confirm deletes once, a member's button does nothing.
            e.Clear();
            e.Routes.Enqueue(Route("action", "delete_deadline", new { id = d.Id }));
            await e.Process(Dm(2, 51, $"удали дедлайн #{d.Id}, он ошибочный"));
            var del = e.Calls("sendMessage").Single().Body;
            var data = del.Field("reply_markup").Field("inline_keyboard")[0][0].Text("callback_data")!;
            Assert(del.Text("text")!.Contains("Удалить дедлайн") && data == $"ddel:{d.Id}:{e.S.AllDeadlines().Single().Version}", "Delete card wrong");
            await e.Callback(3, data);
            Assert(e.S.AllDeadlines().Single().Status == "active", "A member deleted a deadline");
            await e.Callback(2, data); await e.Callback(2, data);
            Assert(e.S.AllDeadlines().Single().Status == "deleted" && e.Calls("editMessageText").Count == 1, "Delete not exactly once");
            Pass("editor DM: перенос → edit_deadline → planner once → existing card; delete_deadline with confirmation buttons");
        }

        // Flag off: behaviour and calls as before the block.
        {
            using var e = Env.Make(make, "route-off.db", router: false);
            await e.Process(Group(3, 60, "что завтра?"));
            Assert(e.Asked("router").Count == 0 && e.Asked("chat").Count == 1 && e.Calls("sendMessage").Count == 1 && e.LastText() == "Ответ писателя", "Group chat changed without the flag");
            Assert(e.Calls("setMessageReaction").All(x => x.Body.Field("reaction")[0].Text("emoji") is "👀" or "👍" or "👌"), "Random reaction outside 👀/👍/👌");
            e.Clear();
            e.Plan = Json.Write(new { intent = "chat" });
            await e.Process(Dm(2, 61, "как дела?"));
            Assert(e.Asked("router").Count == 0 && e.Asked("plan").Count == 1 && e.Asked("chat").Count == 1, "Editor DM changed without the flag");
            await e.Process(Dm(2, 62, "/help"));
            Assert(!e.LastText().Contains("удали дедлайн"), "Router-only action in help without the flag");
            Pass("flag off: no router call, group chat and editor planner exactly as before, random reactions only 👀/👍/👌");
        }

        // OpenRouter down: keywords answer by code, otherwise the usual fallback.
        {
            using var e = Env.Make(make, "route-down.db");
            e.Reply = _ => (HttpStatusCode.ServiceUnavailable, new { error = "down" });
            await e.Process(Group(3, 70, "что завтра?"));
            Assert(e.LastText().Contains("Завтра") && e.Asked("chat").Count == 0, "Schedule not answered by code without AI");
            await e.Process(Dm(3, 71, "какие дедлайны?"));
            Assert(e.LastText().Contains("Дедлайны"), "Deadlines not answered by code");
            await e.Process(Dm(3, 72, "как жизнь?"));
            Assert(e.LastText().Contains("не достучался до нейронки") && e.Asked("chat").Count == 0, "Fallback answer wrong");
            Assert(JsonDocument.Parse(e.S.Get("route:3:72")!).RootElement.Field("decision").Text("mode") == "unavailable", "Unavailable decision not kept");
            Pass("AI unavailable: «что завтра?» and deadlines by code, anything else gets the usual fallback");
        }

        // Unit: parsing rules.
        {
            var c = make("route-parse.db");
            using var s = new BotStore(c);
            using var e = Env.Make(make, "route-parse2.db");
            var member = e.W.Catalog.For("member", Capabilities.Group);
            var today = new DateOnly(2026, 10, 27);
            string[] reactions = ["👀", "❤", "👍"];
            Assert(BotWorker.ParseRoute(Route("render", "schedule_day", new { date = "2026-10-28" }, "❤️"), member, reactions, today, out _) is { Reaction: "❤", Mode: "render" }, "Valid render rejected");
            Assert(BotWorker.ParseRoute(Route("action", "schedule_day", new { date = "2026-10-28" }), member, reactions, today, out _)?.Mode == "render", "Mode not taken from the catalog");
            Assert(BotWorker.ParseRoute(Route("render", "schedule_day"), member, reactions, today, out var err) is null && err!.Contains("date"), "Missing arg accepted");
            Assert(BotWorker.ParseRoute(Route("clarify", question: new string('?', 301)), member, reactions, today, out _) is null, "Long question accepted");
            Assert(BotWorker.ParseRoute(Route("clarify", question: "你好你好你好"), member, reactions, today, out _) is null, "CJK question accepted");
            Assert(BotWorker.ParseRoute(Route("render", "edit_registry"), member, reactions, today, out _) is null, "Forbidden capability accepted");
            Assert(BotWorker.ParseRoute("[1]", member, reactions, today, out _) is null, "Array accepted");
            Assert(BotWorker.ParseRoute(Route("react_only"), member, reactions, today, out _)?.Reaction == "👍", "react_only without a reaction left the person silent");
            Assert(e.C.Reactions.All(TelegramClient.ReactionEmoji.Contains) && e.C.Reactions.Length == 10 && e.C.ModelFor("router") == "z-ai/glm-5.3-flash", "Default reactions or router model wrong");
            Pass("router JSON checks: enum, catalog, args, question length and language, emoji normalisation, defaults valid for Telegram");
        }
        return count;
    }
}
