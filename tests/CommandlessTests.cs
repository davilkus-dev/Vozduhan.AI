using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vozduhan.AI;
using static RouterTests;

/// <summary>Block 4: no slash commands with the Router on (D4), clarifications with buttons (pending_slots), editors without AI.</summary>
static class CommandlessTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>A command named to the person: «/today», but not «</b>» or a path inside a URL.</summary>
    static readonly Regex Command = new(@"(?<![<\w/.:])/[a-z]+", RegexOptions.Compiled);
    static readonly List<string> Said = [];
    /// <summary>Everything the bot showed: messages, edits, button answers.</summary>
    static void Collect(Env e)
    {
        lock (e.Tg)
            Said.AddRange(e.Tg.Where(x => x.Method is "sendMessage" or "editMessageText" or "answerCallbackQuery").Select(x => WebUtility.HtmlDecode(x.Body.Text("text") ?? "")));
    }
    /// <summary>The last message as the person sees it (plain answers go out HTML-encoded: «» as &#171;&#187;).</summary>
    static string Last(Env e) => WebUtility.HtmlDecode(e.LastText());
    static object Plain(long actor, int id, string text) => new
    {
        message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = -100123, type = "supergroup" }, message_thread_id = 10,
        from = new { id = actor, first_name = "Имя" }, text
    };
    static Task Press(Env e, long actor, long chat, int message, string data) =>
        (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e.W,
            [El(new { callback_query = new { id = "cb", from = new { id = actor }, data,
                message = new { message_id = message, chat = new { id = chat, type = chat > 0 ? "private" : "supergroup" }, message_thread_id = chat > 0 ? 0 : 10 } } }),
             CancellationToken.None])!;
    static Task Sync(Env e) => (Task)typeof(BotWorker).GetMethod("SyncCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e.W, [CancellationToken.None])!;
    static string Clarify(string question, string capability, object? args, params (string Label, object? Args)[] options) =>
        Json.Write(new { reaction = (string?)null, mode = "clarify", capability, args, question, options = options.Select(o => new { label = o.Label, args = o.Args }), reason = "тест" });
    static string[] Buttons(JsonElement send) => send.Field("reply_markup").Field("inline_keyboard").EnumerateArray()
        .Select(row => row[0].Text("callback_data")!).ToArray();
    static Deadline Add(Env e, string work, string date)
    {
        var d = Deadlines.Resolve(e.C, new("create", SubjectId: "siaod", Work: work, Date: date), null, e.S.AllDeadlines(), 1, "t", DateTimeOffset.UtcNow);
        e.S.SaveDraft(d); return e.S.Confirm(1, d.Token, DateTimeOffset.UtcNow);
    }

    public static async Task<int> Run(Func<string, BotConfig> make)
    {
        var count = 0;
        void Pass(string name) { count++; Console.WriteLine("PASS " + name); }
        Said.Clear();

        // Flag on: «/today» is text for the Router, «/start» is a greeting, «/cmd@bot» in the group is addressed, help in prose.
        {
            using var e = Env.Make(make, "nocmd.db");
            var today = DateOnly.FromDateTime(e.C.LocalNow(DateTimeOffset.UtcNow));
            e.Routes.Enqueue(Route("render", "schedule_day", new { date = Ru(today) }));
            await e.Process(Dm(3, 1, "/today"));
            Assert(e.Asked("router").Single().User.Contains("/today") && Last(e).Contains("Сегодня") && e.Asked("chat").Count == 0, "«/today» not routed to schedule_day");
            Collect(e); e.Clear();
            await e.Process(Dm(3, 2, "/start"));
            var hello = Last(e);
            Assert(e.Asked("router").Count == 0 && hello.Contains("просто спроси") && !hello.Contains("/") && !hello.Contains("•"), "«/start» is not a short prose greeting: " + hello);
            Collect(e); e.Clear();
            e.Routes.Enqueue(Route("render", "schedule_day", new { date = Ru(today.AddDays(1)) }));
            await e.Process(new { message_id = 3, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = -100123, type = "supergroup" }, message_thread_id = 10,
                from = new { id = 3, first_name = "Имя" }, text = "/tomorrow@vozduhanAIbot", entities = new[] { new { type = "bot_command", offset = 0, length = 23 } } });
            Assert(e.Asked("router").Count == 1 && Last(e).Contains("Завтра"), "«/tomorrow@bot» in the group not routed");
            await e.Process(Plain(3, 4, "/today"));
            Assert(e.Asked("router").Count == 1 && e.Calls("sendMessage").Count == 1, "A bare «/today» to nobody in the group was answered");
            Collect(e); e.Clear();
            foreach (var (actor, id) in new[] { (3L, 5), (2L, 6), (1L, 7) })
            {
                e.Routes.Enqueue(Route("render", "help"));
                await e.Process(Dm(actor, id, "что ты умеешь?"));
                var help = Last(e);
                Assert(help.Contains("«что завтра?»") && help.Contains("Команды не нужны"), "Help without phrases: " + help);
                Assert((actor == 3) == !help.Contains("удали дедлайн #12") && (actor == 1) == help.Contains("напиши в Воздуханство"), $"Help by role wrong for {actor}");
            }
            e.Routes.Enqueue(Route("render", "schedule_week", new { next = false }));
            await e.Process(Dm(3, 8, "расписание на неделю"));
            Assert(Last(e).Contains("спроси «что в среду?»"), "Week hint not in plain words");
            Collect(e); e.Clear();
            // Chat budget over, the Writer failing, a missing deadline: answers without commands.
            e.S.Set($"chat_budget:{e.C.LocalNow(DateTimeOffset.UtcNow):yyyy-MM-dd}:3", "1000");
            await e.Process(Dm(3, 9, "поболтаем?"));
            Assert(Last(e).Contains("лимит болтовни") && Last(e).Contains("«что завтра?»"), "Budget text wrong");
            e.Reply = kind => kind == "chat" ? (HttpStatusCode.ServiceUnavailable, new { error = "down" }) : null;
            await e.Process(Dm(2, 10, "поболтаем?"));
            Assert(Last(e).Contains("не достучался до нейронки"), "Writer failure text wrong");
            e.Reply = null;
            e.Routes.Enqueue(Route("action", "delete_deadline", new { id = 999 }));
            await e.Process(Dm(2, 11, "удали дедлайн #999"));
            Assert(Last(e).Contains("Дедлайна #999 нет") && Last(e).Contains("«какие дедлайны?»"), "Missing deadline text wrong");
            e.Routes.Enqueue(Route("action", "edit_deadline"));
            e.Plan = Json.Write(new { intent = "deadline", deadline = new { action = "update", targetId = 999, date = "2026-12-02" } });
            await e.Process(Dm(2, 12, "/move 999 2026-12-02"));
            Assert(e.Asked("plan").Count == 1 && Last(e).Contains("Укажи номер дедлайна") && e.S.Draft(2) is not null,
                "«/move» with the Router on did not go to the planner as text: " + Last(e));
            Collect(e);
            var d = Add(e, "Практика №5", "2026-11-30");
            var dup = Deadlines.Resolve(e.C, new("create", SubjectId: "siaod", Work: "Практика №5", Date: "2026-11-30"), null, [], 1, "t", DateTimeOffset.UtcNow);
            e.S.SaveDraft(dup);
            try { e.S.Confirm(1, dup.Token, DateTimeOffset.UtcNow); throw new Exception("Duplicate saved"); }
            catch (InvalidOperationException x) { Said.Add(x.Message); Assert(x.Message.Contains("уже существует"), "Duplicate text wrong"); }

            // deleteMyCommands: all four scopes at start, nothing while on, again after off → on; nothing restored when off.
            e.Clear();
            await Sync(e); await Sync(e);
            var scopes = e.Calls("deleteMyCommands").Select(x => x.Body.Field("scope")).ToList();
            Assert(scopes.Count == 4 && scopes.Select(x => x.Text("type")).SequenceEqual(["default", "all_private_chats", "all_group_chats", "chat"]) &&
                scopes[3].Number("chat_id") == -100123, "deleteMyCommands scopes wrong: " + string.Join(",", scopes.Select(x => x.GetRawText())));
            e.C.Features.Router = false; await Sync(e);
            Assert(e.Calls("deleteMyCommands").Count == 4 && e.Calls("setMyCommands").Count == 0, "Commands touched with the flag off");
            e.C.Features.Router = true; await Sync(e);
            Assert(e.Calls("deleteMyCommands").Count == 8, "Commands not removed again when the flag was switched on");
            Pass("no commands: «/today» → schedule_day via Router, «/start» greeting in prose, «/cmd@bot» addressed, help with phrases by role, deleteMyCommands for all scopes");
        }

        // AI down: an editor's change gets the web admin link, «что завтра» and old command words are answered by code.
        {
            using var e = Env.Make(make, "nocmd-down.db");
            e.C.Web.Enabled = true;
            e.Reply = _ => (HttpStatusCode.ServiceUnavailable, new { error = "down" });
            await e.Process(Dm(2, 1, "перенеси сиаод на среду"));
            Assert(Last(e).Contains("Нейронка недоступна") && Last(e).Contains("веб-админке") && Last(e).Contains("/login?t="), "Editor got no web link: " + Last(e));
            await e.Process(Dm(2, 2, "что завтра?"));
            Assert(Last(e).Contains("Завтра"), "Editor schedule not answered by code");
            await e.Process(Dm(3, 3, "/today"));
            Assert(Last(e).Contains("Сегодня"), "«/today» not answered by code without AI");
            await e.Process(Dm(3, 4, "перенеси дедлайн по сиаоду"));
            Assert(Last(e).Contains("Дедлайны") && !Last(e).Contains("login"), "A member got the web link");
            // The planner failing after the Router chose an edit: the same link.
            e.Reply = kind => kind == "plan" ? (HttpStatusCode.ServiceUnavailable, new { error = "down" }) : null;
            e.Routes.Enqueue(Route("action", "edit_deadline"));
            await e.Process(Dm(1, 5, "добавь сиаод практику №9 на пятницу"));
            Assert(Last(e).Contains("веб-админке") && Last(e).Contains("/login?t="), "Planner failure gave no web link");
            Collect(e);
            Pass("AI unavailable with the Router: editor's change → web admin link, schedule and old command words by keywords, members never get the link");
        }

        // Clarification with buttons in DM: buttons, the author's press runs the action, cancel, second press stale, replay asks nothing new.
        {
            using var e = Env.Make(make, "slots-dm.db");
            var d1 = Add(e, "Практика №1", "2026-11-30"); var d2 = Add(e, "Практика №2", "2026-11-30");
            var ask = Clarify("Какой дедлайн удалить?", "delete_deadline", null, ($"#{d1.Id} Практика №1", new { id = d1.Id }), ($"#{d2.Id} Практика №2", new { id = d2.Id }));
            e.Routes.Enqueue(ask);
            await e.Process(Dm(2, 1, "удали тот дедлайн"));
            var sent = e.Calls("sendMessage").Single().Body;
            var slot = e.S.ActiveSlot(2, 2, DateTimeOffset.UtcNow)!;
            var data = Buttons(sent);
            Assert(sent.Text("text") == "Какой дедлайн удалить?" && data.SequenceEqual([$"slot:{slot.Id}:0", $"slot:{slot.Id}:1", $"slot:{slot.Id}:x"]) &&
                sent.Field("reply_markup").Field("inline_keyboard")[2][0].Text("text") == "Отмена" && sent.Field("reply_parameters").Number("message_id") == 1,
                "Buttons wrong: " + sent.GetRawText());
            Assert(slot.Capability == "delete_deadline" && slot.Expires - slot.Created == 600 && slot.Message > 0, "Slot row wrong");
            await e.Process(Dm(2, 1, "удали тот дедлайн"));
            Assert(e.Calls("sendMessage").Count == 1 && e.S.ActiveSlot(2, 2, DateTimeOffset.UtcNow)!.Id == slot.Id, "Replay asked again or opened another slot");
            e.Clear();
            await Press(e, 2, 2, slot.Message, data[1]);
            var card = e.Calls("sendMessage").Single().Body;
            Assert(card.Text("text")!.Contains($"Удалить дедлайн #{d2.Id}") && card.Field("reply_parameters").Number("message_id") == 1, "Press did not run delete_deadline for the chosen option");
            Assert(e.Calls("editMessageText").Single().Body.Text("text") == $"✓ #{d2.Id} Практика №2" && e.S.Slot(slot.Id)!.State == "done", "Question not edited to ✓ label");
            await Press(e, 2, 2, slot.Message, data[0]);
            Assert(e.Calls("sendMessage").Count == 1 && e.Calls("answerCallbackQuery")[^1].Body.Text("text") == "Вопрос устарел", "Second press ran again");
            Collect(e); e.Clear();
            e.Routes.Enqueue(ask);
            await e.Process(Dm(2, 2, "удали дедлайн"));
            var cancel = e.S.ActiveSlot(2, 2, DateTimeOffset.UtcNow)!;
            await Press(e, 2, 2, cancel.Message, $"slot:{cancel.Id}:x");
            Assert(e.S.Slot(cancel.Id)!.State == "cancelled" && e.Calls("editMessageText").Single().Body.Text("text") == "Отменено." && e.Calls("sendMessage").Count == 1, "Cancel wrong");
            Collect(e);
            Pass("clarify in DM: question with option buttons and «Отмена», author's press runs the capability and edits to «✓ label», second press stale, cancel, replay asks nothing new");
        }

        // In the group: a stranger's press is refused and the slot lives; expired slot; text answer fills; new clarify replaces the old one.
        {
            using var e = Env.Make(make, "slots-group.db");
            var today = DateOnly.FromDateTime(e.C.LocalNow(DateTimeOffset.UtcNow));
            var (wed, thu) = (today.AddDays(1), today.AddDays(2));
            var ask = Clarify("На какой день?", "schedule_day", null, ("Завтра", new { date = Ru(wed) }), ("Послезавтра", new { date = Ru(thu) }));
            e.Routes.Enqueue(ask);
            await e.Process(Group(3, 1, "какие пары?"));
            var slot = e.S.ActiveSlot(3, -100123, DateTimeOffset.UtcNow)!;
            var data = Buttons(e.Calls("sendMessage").Single().Body);
            Assert(BotWorker.CallbackAllowed(e.C, 1, -100123, data[0]) && BotWorker.CallbackAllowed(e.C, 3, 3, "slot:1:0") && !BotWorker.CallbackAllowed(e.C, 999, -100123, data[0]) &&
                !BotWorker.CallbackAllowed(e.C, 3, -100999, data[0]) && !BotWorker.CallbackAllowed(e.C, 3, 3, "save:x") && BotWorker.CallbackAllowed(e.C, 2, 2, "save:x") &&
                !BotWorker.CallbackAllowed(e.C, 2, -100123, "save:x"), "Poll callback filter wrong");
            e.Clear();
            await Press(e, 1, -100123, slot.Message, data[0]);
            Assert(e.Calls("answerCallbackQuery").Single().Body.Text("text") == "Это не твой вопрос" && e.Calls("sendMessage").Count == 0 &&
                e.S.Slot(slot.Id)!.State == "active", "Stranger's press not refused or slot closed");
            await Press(e, 3, -100123, slot.Message, data[0]);
            Assert(Last(e).Contains("Завтра") && e.Calls("sendMessage").Single().Body.Number("message_thread_id") == 10 && e.S.Slot(slot.Id)!.State == "done", "Author's press in the group wrong");
            Collect(e); e.Clear();

            // Expired.
            BotWorker.SlotLife = TimeSpan.FromSeconds(-1);
            try
            {
                e.Routes.Enqueue(ask);
                await e.Process(Group(3, 2, "а пары?"));
            }
            finally { BotWorker.SlotLife = TimeSpan.FromMinutes(10); }
            var old = e.S.Slot(slot.Id + 1)!;
            Assert(e.S.ActiveSlot(3, -100123, DateTimeOffset.UtcNow) is null, "Expired slot still active");
            await Press(e, 3, -100123, old.Message, $"slot:{old.Id}:0");
            Assert(e.Calls("answerCallbackQuery")[^1].Body.Text("text") == "Вопрос устарел" && e.S.Slot(old.Id)!.State == "expired" &&
                e.Calls("sendMessage").Count == 1, "Expired slot ran");
            BotWorker.SlotLife = TimeSpan.FromSeconds(-1);
            try { e.Routes.Enqueue(ask); await e.Process(Group(3, 20, "а пары?")); }
            finally { BotWorker.SlotLife = TimeSpan.FromMinutes(10); }
            var stale = e.S.Slot(old.Id + 1)!;
            e.S.Maintain(DateTimeOffset.UtcNow);
            Assert(stale.State == "active" && e.S.Slot(stale.Id)!.State == "expired" && e.S.Slot(slot.Id)!.State == "done", "Maintain did not mark only expired slots");
            Collect(e); e.Clear();

            // Text answer, not addressed to the bot: goes to the Router with the slot and fills it.
            e.Routes.Enqueue(ask);
            await e.Process(Group(3, 3, "какие пары?"));
            var open = e.S.ActiveSlot(3, -100123, DateTimeOffset.UtcNow)!;
            e.Routes.Enqueue(Route("render", "schedule_day", new { date = Ru(thu) }));
            await e.Process(Plain(3, 4, "послезавтра"));
            var input = e.Asked("router")[^1].User;
            Assert(input.Contains("pending_question") && input.Contains("На какой день?") && input.Contains("Послезавтра"), "Slot not given to the Router");
            Assert(e.Calls("sendMessage").Count == 2 && e.S.Slot(open.Id)!.State == "answered" && e.Calls("editMessageReplyMarkup").Single().Body.Number("message_id") == open.Message,
                "Text answer did not fill the slot");
            // Someone else's plain message is ignored; the author's unrelated chatter closes the slot silently.
            e.Routes.Enqueue(ask);
            await e.Process(Group(3, 5, "какие пары?"));
            var next = e.S.ActiveSlot(3, -100123, DateTimeOffset.UtcNow)!;
            var routed = e.Asked("router").Count; var sentBefore = e.Calls("sendMessage").Count;
            await e.Process(Plain(1, 6, "кто идёт в столовку?"));
            Assert(e.Asked("router").Count == routed, "Another person's plain message reached the Router");
            e.Routes.Enqueue(Route("writer", reaction: "😁"));
            await e.Process(Plain(3, 7, "ахаха ну ты даёшь"));
            Assert(e.Calls("sendMessage").Count == sentBefore && e.Calls("setMessageReaction").Count == 0 && e.Asked("chat").Count == 0 &&
                e.S.Slot(next.Id)!.State == "closed", "Unrelated chatter answered or slot left open");
            Collect(e); e.Clear();

            // A new clarify replaces the old one: old buttons go away, pressing them is stale.
            e.Routes.Enqueue(ask);
            await e.Process(Group(3, 8, "какие пары?"));
            var first = e.S.ActiveSlot(3, -100123, DateTimeOffset.UtcNow)!;
            e.Routes.Enqueue(Clarify("Текущая или следующая неделя?", "schedule_week", null, ("Эта", new { next = false }), ("Следующая", new { next = true })));
            await e.Process(Group(3, 9, "а на неделю?"));
            var second = e.S.ActiveSlot(3, -100123, DateTimeOffset.UtcNow)!;
            Assert(second.Id != first.Id && e.S.Slot(first.Id)!.State == "replaced" && e.Calls("editMessageReplyMarkup").Any(x => x.Body.Number("message_id") == first.Message),
                "New clarify did not replace the old one");
            await Press(e, 3, -100123, first.Message, $"slot:{first.Id}:0");
            Assert(e.Calls("answerCallbackQuery")[^1].Body.Text("text") == "Вопрос устарел", "Replaced slot ran");
            await Press(e, 3, -100123, second.Message, $"slot:{second.Id}:1");
            Assert(Last(e).Contains("Неделя"), "Second slot did not run");
            Collect(e);
            Pass("clarify in the group: stranger refused and slot lives, expired → «Вопрос устарел», text answer fills the slot, chatter closes it silently, new clarify replaces the old");
        }

        // Code checks of the Router's options.
        {
            using var e = Env.Make(make, "slots-parse.db");
            var head = e.W.Catalog.For("head", Capabilities.Dm); var member = e.W.Catalog.For("member", Capabilities.Group);
            var today = new DateOnly(2026, 10, 27); string[] reactions = ["👍"];
            RouteDecision? P(string json, IReadOnlyList<Capability>? caps = null) => BotWorker.ParseRoute(json, caps ?? head, reactions, today, out _);
            var ok = P(Clarify("Какой?", "delete_deadline", null, ("#1", new { id = 1 }), ("#2", new { id = 2 })));
            Assert(ok is { Mode: "clarify", Capability: "delete_deadline" } && ok.Options!.Count == 2 && ok.Options[1].Args!.Value.Number("id") == 2, "Valid options rejected");
            Assert(P(Clarify("Какой?", "delete_deadline", null, (new string('я', 41), new { id = 1 }))) is null, "Label over 40 accepted");
            Assert(P(Clarify("Какой?", "delete_deadline", null, ("#1", new { id = "abc" }))) is null, "Invalid option args accepted");
            Assert(P(Clarify("Какой?", "delete_deadline", null, ("#1", null))) is null, "Incomplete option accepted");
            Assert(P(Clarify("Какой?", "delete_deadline", null, ("#1", new { id = 1 }), ("#2", new { id = 2 }), ("#3", new { id = 3 }), ("#4", new { id = 4 }), ("#5", new { id = 5 }))) is null, "Five options accepted");
            Assert(P(Json.Write(new { mode = "clarify", question = "Какой?", options = new[] { new { label = "#1", args = new { id = 1 } } } })) is null, "Options without capability accepted");
            Assert(P(Clarify("Какой?", "delete_deadline", null, ("#1", new { id = 1 })), member) is null, "Option for a forbidden capability accepted");
            Assert(P(Clarify("Какой день?", "schedule_day", new { date = "в пятницу" }, ("Пт", new { date = "2026-10-30" }))) is null, "Invalid known args accepted");
            Assert(P(Clarify("Какой?", "delete_deadline", null)) is { Capability: "delete_deadline", Options: null }, "Clarify for a capability without options rejected");
            Assert(P(Json.Write(new { mode = "clarify", question = "Ты о чём?" })) is { Capability: null }, "Plain clarify rejected");
            Pass("router options checked by code: label ≤ 40, up to 4, each a complete valid call of an allowed capability");
        }

        Assert(Said.Count > 25, "Too few texts collected: " + Said.Count);
        var named = Said.Where(t => Command.IsMatch(t)).ToList();
        Assert(named.Count == 0, "A bot answer names a command: " + string.Join(" | ", named.Select(t => Conversation.Limit(t, 120))));
        Pass($"flag on: none of {Said.Count} bot texts in these tests names a command");

        // Flag off: commands and their texts exactly as before.
        {
            using var e = Env.Make(make, "nocmd-off.db", router: false);
            await e.Process(Dm(3, 1, "/today"));
            Assert(e.Asked("router").Count == 0 && Last(e).Contains("Сегодня"), "/today changed without the flag");
            await e.Process(Dm(3, 2, "/start"));
            Assert(Last(e).Contains("/today") && Last(e).Contains("/deadlines"), "/start help changed without the flag");
            await e.Process(Dm(3, 3, "/week"));
            Assert(Last(e).Contains("/today, /tomorrow"), "Week hint changed without the flag");
            e.S.Set($"chat_budget:{e.C.LocalNow(DateTimeOffset.UtcNow):yyyy-MM-dd}:3", "1000");
            await e.Process(Dm(3, 4, "поболтаем?"));
            Assert(Last(e) == "На сегодня лимит болтовни со мной закончился. Дедлайны и /today продолжают работать.", "Budget text changed without the flag");
            await e.Process(Dm(2, 5, "/move 999 2026-12-02"));
            Assert(Last(e).Contains("Укажи ID дедлайна из /deadlines") && e.Asked("plan").Count == 0, "/move changed without the flag");
            await Sync(e);
            Assert(e.Calls("deleteMyCommands").Count == 0 && e.Calls("setMyCommands").Count == 0, "Commands touched without the flag");
            Pass("flag off: /today, /start, /week, /move and the old texts unchanged; bot commands not touched");
        }
        return count;
    }
}
