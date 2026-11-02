using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

/// <summary>Block 8: the pool of ideas for spontaneous drops (G3) and joining a conversation without a mention (H2).</summary>
static partial class WakeupTests
{
    static async Task RunIdeas(Func<string, BotConfig> make)
    {
        const long Owner = 1;
        static MethodInfo M(string name) => typeof(BotWorker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<JsonElement> Group(Env e) => e.Sent.Where(x => x.Method == "sendMessage" && x.Body.Number("chat_id") == e.C.GroupId).Select(x => x.Body).ToList();
        List<JsonElement> Dm(Env e) => e.Sent.Where(x => x.Method == "sendMessage" && x.Body.Number("chat_id") == Owner).Select(x => x.Body).ToList();
        Task<int> Generate(Env e) => (Task<int>)M("GenerateIdeas").Invoke(e.W, [T, CancellationToken.None])!;
        Idea? Pick(Env e, DateTimeOffset at) => (Idea?)M("PickIdea").Invoke(e.W, [at]);
        long AddIdea(Env e, string text, string[] conditions, string[]? people = null) =>
            e.S.AddIdea(text, conditions, people ?? [], "ai", "new", conditions.Count(c => c != "any"), Now(), Now() + 14 * 86400);
        void Drops(Env e, DateOnly day, params string[] times) => e.S.Set($"ambient:plan:{day:yyyy-MM-dd}", Json.Write(times.ToList()));
        Task Process(Env e, object update) => (Task)M("Process").Invoke(e.W, [JsonSerializer.SerializeToElement(update, Json.Options), CancellationToken.None])!;
        Task Press(Env e, int message, string data) => Process(e, new
        {
            callback_query = new { id = "cb" + Random.Shared.Next(), from = new { id = Owner }, data, message = new { message_id = message, chat = new { id = Owner } } }
        });
        static bool IsIdeas(string body) => body.Contains("придумываешь идеи");
        static bool IsSpont(string body) => body.Contains("ты сам решил вкинуть");
        static bool IsObserver(string body) => body.Contains("стоит ли вмешаться");

        // ---------- G3 ----------

        await Check("ideas: generation keeps dictionary conditions and roster people; junk, unknown people, duplicates, long texts dropped; notes only in kind idea", async () =>
        {
            using var e = new Env(make, "ideas-gen.db");
            e.C.Features.Ideas = true;
            e.C.R.Members = e.C.R.Members.Select(m => m.Key == "m3" ? m with { AdminNote = "вечно опаздывает на первую пару" } : m).ToArray();
            e.Text = body => Task.FromResult(IsIdeas(body) ? Json.Write(new
            {
                ideas = new object[]
                {
                    new { text = "Пятничный вброс про выходные", conditions = new[] { "friday_evening", "full_moon" }, people = Array.Empty<string>() },
                    new { text = "Подколоть Студента за опоздания на СиАОД", conditions = new[] { "lesson_today:siaod", "weekday:9", "after_lesson:physics" }, people = new[] { "m3", "m42" } },
                    new { text = "Про того, кого нет в группе", conditions = new[] { "any" }, people = new[] { "ghost" } },
                    new { text = "пятничный вброс — про выходные!", conditions = new[] { "evening" }, people = Array.Empty<string>() },
                    new { text = new string('а', 301), conditions = new[] { "any" }, people = Array.Empty<string>() },
                    new { text = "Мем про дедлайн", conditions = new[] { "lol:1", "before_deadline:24" }, people = Array.Empty<string>() },
                    new { text = "Просто пошутить", conditions = new[] { "???" }, people = Array.Empty<string>() }
                }
            }) : "Гайс");
            var added = await Generate(e);
            var ideas = e.S.Ideas().OrderBy(i => i.Id).ToList();
            Assert(added == 4 && ideas.Count == 4, "Wrong ideas: " + string.Join(" | ", ideas.Select(i => i.Text)));
            Assert(ideas[0] is { Text: "Пятничный вброс про выходные", Source: "ai", Status: "new" } && ideas[0].Conditions.SequenceEqual(["friday_evening"]) && ideas[0].People.Length == 0, "Junk condition kept");
            Assert(ideas[1].Conditions.SequenceEqual(["lesson_today:siaod"]) && ideas[1].People.SequenceEqual(["m3"]), "Unknown person or condition kept");
            Assert(ideas[2].Conditions.SequenceEqual(["before_deadline:24"]) && ideas[3].Conditions.SequenceEqual(["any"]), "Conditions not cleaned");
            Assert(ideas.All(i => i.Expires - i.Created == 14 * 86400), "TTL not applied");
            var calls = e.S.AiCalls(limit: 200, full: true);
            Assert(calls.Single(c => c.Kind == "idea").Request.Contains("people_notes") && calls.Single(c => c.Kind == "idea").Request.Contains("вечно опаздывает"), "Notes missing from the idea request");
            Assert(calls.Where(c => c.Request.Contains("people_notes")).All(c => c.Kind == "idea"), "people_notes outside kind idea");
        });

        await Check("ideas: a Friday evening drop takes the friday_evening idea, marks it used; no fitting idea — the drop as before", async () =>
        {
            using var e = new Env(make, "ideas-friday.db");
            e.C.Features.Ideas = true;
            var friday = new DateOnly(2026, 12, 4);
            Drops(e, friday, "18:00");
            var monday = AddIdea(e, "Понедельник — день тяжёлый", ["weekday:1"]);
            var morning = AddIdea(e, "Утренний вброс про кофе", ["morning"]);
            var fri = AddIdea(e, "Пятница вечер: кто на выходных за лабы", ["friday_evening"]);
            e.Text = body => Task.FromResult(IsSpont(body) ? "Пятница, гайс, лабы сами себя не сделают" : "Гайс");
            await e.Tick(DateTimeOffset.Parse("2026-12-04T15:00:00Z"));
            var key = "ambient:spont:2026-12-04:0";
            var request = e.Ai.Single(IsSpont);
            Assert(request.Contains("кто на выходных за лабы") && !request.Contains("Понедельник") && !request.Contains("кофе"), "Wrong idea in the drop");
            Assert(Group(e).Select(x => x.Text("text")).SequenceEqual(["Пятница, гайс, лабы сами себя не сделают"]) && e.Pub(key) is { Status: "sent", Mode: BotWorker.AutoMode }, "Drop not sent as configured");
            Assert(e.S.Idea(fri) is { Status: "used", PublicationKey: "ambient:spont:2026-12-04:0", UsedAt: > 0 } && e.S.Idea(monday)!.Status == "new" && e.S.Idea(morning)!.Status == "new", "Idea not marked used");
            Drops(e, friday.AddDays(1), "13:00");
            await e.Tick(DateTimeOffset.Parse("2026-12-05T10:00:00Z"));
            Assert(e.Ai.Count(IsSpont) == 2 && !e.Ai.Last(IsSpont).Contains("\\\"idea\\\"") && Group(e).Count == 2, "Drop without a fitting idea changed");
        });

        await Check("ideas: a person is not taken again within PersonCooldownDays, nor while another idea about them is in work", async () =>
        {
            using var e = new Env(make, "ideas-cooldown.db");
            e.C.Features.Ideas = true;
            e.C.Features.PersonCooldownDays = 3;
            var used = AddIdea(e, "Вчерашний подкол Студента", ["any"], ["m3"]);
            e.S.LinkIdea(used, "ambient:spont:old:0");
            e.S.IdeaFinished("ambient:spont:old:0", true, Now() - 86400 - 60);
            var again = AddIdea(e, "Ещё раз про Студента", ["weekday:2", "no_lessons_today"], ["m3"]);
            var plain = AddIdea(e, "Про погоду и пары", ["any"]);
            Assert(Pick(e, T)?.Id == plain, "Person taken again within the cooldown");
            e.C.Features.PersonCooldownDays = 1;
            Assert(Pick(e, T)?.Id == again, "Person still blocked after the cooldown (or a specific idea lost to a generic one)");
            e.S.SetIdeaStatus(again, "rejected");
            var inWork = AddIdea(e, "Про старосту", ["any"], ["m2"]);
            e.S.LinkIdea(inWork, "ambient:spont:x:0");
            AddIdea(e, "Опять про старосту", ["weekday:2", "no_lessons_today"], ["m2"]);
            Assert(Pick(e, T)?.Id == plain, "Person taken while another idea about them is in work");
            await Task.CompletedTask;
        });

        await Check("ideas: a drop by an idea about people waits for ✅ even in auto_preview; ✅ sends it; no ✅ — skipped, idea back in the pool", async () =>
        {
            using var e = new Env(make, "ideas-manual.db");
            e.C.Features.Ideas = true;
            e.C.Features.PublicationModes = new() { ["spont"] = BotWorker.AutoPreview };
            var day = DateOnly.FromDateTime(e.C.LocalNow(T));
            Drops(e, day, "13:00", "15:00");
            var about = AddIdea(e, "Подколоть Студента за опоздания", ["any"], ["m3"]);
            var plain = AddIdea(e, "Просто вброс про пары", ["any"]);
            e.Text = body => Task.FromResult(!IsSpont(body) ? "Гайс" : body.Contains("Подколоть") ? "@member, опять проспал?" : body.Contains("старосты") ? "@head, где ключи?" : "Гайс, просто вброс");
            string K(DateOnly d, int i) => $"ambient:spont:{d:yyyy-MM-dd}:{i}";
            var due = T.AddHours(1);
            await e.Tick(due.AddMinutes(-30));
            var preview = Dm(e).Single().Text("text")!;
            Assert(e.Pub(K(day, 0)) is { Mode: BotWorker.ManualMode, Status: "ready" } && preview.Contains("только после ✅") && preview.Contains("опять проспал"), "Not manual: " + preview);
            await e.Tick(due); await e.Tick(due.AddMinutes(60));
            Assert(Group(e).Count == 0, "Sent without ✅");
            await Press(e, e.Pub(K(day, 0)).Preview, $"pub:s:{e.Pub(K(day, 0)).Id}");
            Assert(Group(e).Select(x => x.Text("text")).SequenceEqual(["@member, опять проспал?"]) && e.S.Idea(about)!.Status == "used", "✅ did not send the drop");
            await e.Tick(due.AddMinutes(90)); await e.Tick(due.AddMinutes(120));
            Assert(e.Pub(K(day, 1)) is { Mode: BotWorker.AutoPreview, Status: "sent" } && e.S.Idea(plain)!.Status == "used" && Group(e).Count == 2, "A drop without people did not follow the settings");

            var next = day.AddDays(1);
            Drops(e, next, "13:00");
            var head = AddIdea(e, "Спросить старосты про ключи", ["any"], ["m2"]);
            await e.Tick(due.AddDays(1).AddMinutes(-30));
            Assert(e.Pub(K(next, 0)).Mode == BotWorker.ManualMode && e.S.Idea(head)!.PublicationKey == K(next, 0), "Second idea about people not manual");
            await e.Tick(due.AddDays(1)); await e.Tick(due.AddDays(1).AddMinutes(121));
            Assert(e.Pub(K(next, 0)).Status == "skipped" && e.S.Idea(head) is { Status: "new", PublicationKey: null } && Group(e).Count == 2, "Not skipped or idea not back in the pool");
        });

        await Check("ideas: TTL — Maintain marks old ideas expired, an idea in work stays", async () =>
        {
            using var e = new Env(make, "ideas-ttl.db");
            e.C.Features.Ideas = true;
            var old = e.S.AddIdea("Старая идея", ["any"], [], "ai", "new", 0, Now() - 20 * 86400, Now() - 60);
            var busy = e.S.AddIdea("Старая, но в работе", ["any"], [], "ai", "approved", 0, Now() - 20 * 86400, Now() - 60);
            e.S.LinkIdea(busy, "ambient:spont:x:0");
            var fresh = AddIdea(e, "Свежая идея", ["any"]);
            e.S.Maintain(DateTimeOffset.UtcNow);
            Assert(e.S.Idea(old)!.Status == "expired" && e.S.Idea(busy)!.Status == "approved" && e.S.Idea(fresh)!.Status == "new", "TTL wrong");
            Assert(Pick(e, T)?.Id == fresh, "Expired or busy idea picked");
            await Task.CompletedTask;
        });

        await Check("ideas: web — the developer lists, adds (dictionary only), approves, rejects, deletes and generates; the head gets 403", async () =>
        {
            using var e = new Env(make, "ideas-web.db");
            e.Text = body => Task.FromResult(IsIdeas(body) ? Json.Write(new { ideas = new[] { new { text = "Идея из кнопки", conditions = new[] { "evening" }, people = Array.Empty<string>() } } }) : "Гайс");
            var port = 27000 + Random.Shared.Next(1000);
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
                StringContent Body(object o) => new(Json.Write(o), Encoding.UTF8, "application/json");
                using var owner = await Login(1);
                using var head = await Login(2);
                var list = await owner.GetAsync("api/ideas");
                var text = await list.Content.ReadAsStringAsync();
                Assert(list.StatusCode == HttpStatusCode.OK && text.Contains("friday_evening") && text.Contains("person:m3") && text.Contains("lesson_today:siaod"), "Owner list wrong: " + text);
                var bad = await owner.PostAsync("api/ideas", Body(new { action = "save", text = "Своя", conditions = new[] { "full_moon" } }));
                var ghost = await owner.PostAsync("api/ideas", Body(new { action = "save", text = "Своя", conditions = new[] { "any" }, people = new[] { "ghost" } }));
                Assert(bad.StatusCode == HttpStatusCode.BadRequest && ghost.StatusCode == HttpStatusCode.BadRequest && e.S.Ideas().Count == 0, "Junk accepted from the web");
                var ok = await owner.PostAsync("api/ideas", Body(new { action = "save", text = "Своя идея про пятницу", conditions = new[] { "friday_evening", "weekday:5" }, people = new[] { "m3" } }));
                Assert(ok.IsSuccessStatusCode && e.S.Ideas().Single() is { Source: "manual", Status: "approved" } mine && mine.Conditions.SequenceEqual(["friday_evening", "weekday:5"]) && mine.People.SequenceEqual(["m3"]), "Manual idea not saved");
                var id = e.S.Ideas().Single().Id;
                Assert((await owner.PostAsync("api/ideas", Body(new { action = "save", id, text = "Поправленная идея", conditions = new[] { "evening" } }))).IsSuccessStatusCode &&
                    e.S.Idea(id) is { Text: "Поправленная идея", People.Length: 0 }, "Edit not saved");
                Assert((await owner.PostAsync("api/ideas", Body(new { action = "reject", id }))).IsSuccessStatusCode && e.S.Idea(id)!.Status == "rejected", "Reject failed");
                Assert((await owner.PostAsync("api/ideas", Body(new { action = "approve", id }))).IsSuccessStatusCode && e.S.Idea(id)!.Status == "approved", "Approve failed");
                var gen = await (await owner.PostAsync("api/ideas", Body(new { action = "generate" }))).Content.ReadAsStringAsync();
                Assert(JsonDocument.Parse(gen).RootElement.Number("added") == 1 && e.S.Ideas().Any(i => i is { Text: "Идея из кнопки", Source: "ai" }), "Generate now failed: " + gen);
                Assert((await owner.PostAsync("api/ideas", Body(new { action = "delete", id }))).IsSuccessStatusCode && e.S.Idea(id) is null, "Delete failed");
                foreach (var res in new[] { await head.GetAsync("api/ideas"), await head.PostAsync("api/ideas", Body(new { action = "save", text = "Взлом", conditions = new[] { "any" } })),
                    await head.PostAsync("api/ideas", Body(new { action = "generate" })) })
                    Assert(res.StatusCode == HttpStatusCode.Forbidden, "Head got " + res.StatusCode);
                Assert(e.S.Ideas().Count == 1 && !(await head.GetStringAsync("")).Contains("VIEWS.ideas") && (await owner.GetStringAsync("")).Contains("VIEWS.ideas"), "Head reached ideas");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        });

        // ---------- H2 ----------

        var nextId = 100;
        Env Obs(string file)
        {
            var e = new Env(make, file);
            e.C.Features.Observer = true;
            typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e.W, 99L);
            return e;
        }
        JsonElement Say(Env e, long actor, string text, DateTimeOffset at, int topic = 0)
        {
            var id = ++nextId;
            var m = JsonSerializer.SerializeToElement(new
            {
                message_id = id, date = at.ToUnixTimeSeconds(), chat = new { id = e.C.GroupId, type = "supergroup" }, message_thread_id = topic == 0 ? e.C.ChatTopicId : topic,
                from = new { id = actor, first_name = "U" + actor, is_bot = actor == 99 }, text
            }, Json.Options);
            e.S.Remember(e.C.GroupId, topic == 0 ? e.C.ChatTopicId : topic, id, at.ToUnixTimeSeconds(), m.GetRawText());
            return m;
        }
        JsonElement Talk(Env e, DateTimeOffset at, string last = "Хз, глянь в расписании")
        {
            Say(e, 2, "Кто знает, что у нас по сдачам?", at.AddMinutes(-8));
            Say(e, 3, "Без понятия", at.AddMinutes(-6));
            Say(e, 2, "Вроде что-то по СиАОД было", at.AddMinutes(-4));
            return Say(e, 3, last, at.AddMinutes(-1));
        }
        Task<bool> Observe(Env e, JsonElement m, DateTimeOffset at) => (Task<bool>)M("Observe").Invoke(e.W, [m, at, CancellationToken.None])!;
        int Calls(Env e) => e.Ai.Count(IsObserver);
        string answer = "";
        void Answer(Env e, object a)
        {
            answer = Json.Write(a);
            e.Text = body => Task.FromResult(IsObserver(body) ? answer : "Ответ писателя по делу");
        }

        await Check("observer: 4 messages from 2 people → one observer call (no notes in it); join=false → silence", async () =>
        {
            using var e = Obs("obs-call.db");
            e.C.R.Members = e.C.R.Members.Select(m => m.Key == "m3" ? m with { AdminNote = "вечно опаздывает на первую пару" } : m).ToArray();
            Answer(e, new { join = false, reason = "болтовня" });
            var last = Talk(e, T);
            Assert(await Observe(e, last, T) && Calls(e) == 1 && Group(e).Count == 0, "Lively talk not offered or join=false spoke");
            var row = e.S.AiCalls(limit: 50, full: true).Single(c => c.Kind == "observer");
            Assert(row.Request.Contains("Без понятия") && !row.Request.Contains("people_notes") && !row.Request.Contains("опаздывает") && !row.Request.Contains("edit_deadline"), "Observer request wrong: " + row.Request);
        });

        await Check("observer: 3 messages, one person, an old talk, DM or another topic → no call", async () =>
        {
            using var e = Obs("obs-quiet.db");
            Answer(e, new { join = false });
            Say(e, 2, "Кто знает, что у нас по сдачам?", T.AddMinutes(-5));
            Say(e, 3, "Без понятия", T.AddMinutes(-3));
            var third = Say(e, 2, "Ну ладно", T.AddMinutes(-1));
            Assert(!await Observe(e, third, T), "3 messages called the observer");
            var later = T.AddMinutes(30);
            JsonElement solo = default;
            for (var i = 4; i >= 1; i--) solo = Say(e, 2, "сам с собой " + i, later.AddMinutes(-i));
            Assert(!await Observe(e, solo, later), "One person called the observer");
            Assert(!await Observe(e, Talk(e, T.AddHours(2)), T.AddHours(2).AddMinutes(20)), "An old talk called the observer");
            var other = Talk(e, T.AddHours(3), "в другой теме");
            var foreign = Say(e, 3, "в другой теме", T.AddHours(3), topic: 77);
            var dm = JsonSerializer.SerializeToElement(new { message_id = 5, date = T.ToUnixTimeSeconds(), chat = new { id = 3, type = "private" }, from = new { id = 3, first_name = "U3" }, text = "привет" }, Json.Options);
            Assert(!await Observe(e, foreign, T.AddHours(3)) && !await Observe(e, dm, T.AddHours(3)) && Calls(e) == 0, "Another topic or DM called the observer");
            Assert(await Observe(e, other, T.AddHours(3)) && Calls(e) == 1, "Control: lively talk not offered");
        });

        await Check("observer: join=true render → code answers as a reply without the Writer; writer → the Writer answers as a reply; bad decisions do nothing", async () =>
        {
            using var e = Obs("obs-render.db");
            var last = Talk(e, T, "какие у нас дедлайны вообще?");
            Answer(e, new { join = true, reason = "факт", reply_to = last.Number("message_id"), mode = "render", capability = "deadlines_list" });
            Assert(await Observe(e, last, T), "Observer not called");
            var reply = Group(e).Single();
            Assert(reply.Text("text")!.Contains("Дедлайны") && reply.Field("reply_parameters").Number("message_id") == last.Number("message_id") && e.Ai.Count == 1 &&
                e.S.Get("observer:count:2026-12-01") == "1", "Render answer wrong: " + reply);

            var t2 = T.AddMinutes(11);
            var question = Talk(e, t2, "а кто-нибудь помнит, это вообще важно?");
            Answer(e, new { join = true, reply_to = question.Number("message_id"), mode = "writer" });
            await Observe(e, question, t2);
            var writer = Group(e).Last();
            Assert(writer.Text("text") == "Ответ писателя по делу" && writer.Field("reply_parameters").Number("message_id") == question.Number("message_id") &&
                e.S.AiCalls(limit: 50).Count(c => c.Kind == "chat") == 1 && e.S.Get("observer:count:2026-12-01") == "2", "Writer answer wrong");

            var sent = Group(e).Count;
            var t3 = T.AddMinutes(22);
            var editor = Talk(e, t3, "ок");
            var edit = Say(e, 2, "перенесите сдачу СиАОД на пятницу", t3);
            Answer(e, new { join = true, reply_to = edit.Number("message_id"), mode = "render", capability = "deadlines_list" });
            await Observe(e, edit, t3);
            Answer(e, new { join = true, reply_to = 999999, mode = "render", capability = "deadlines_list" });
            await Observe(e, Talk(e, t3.AddMinutes(11)), t3.AddMinutes(11));
            var x = Talk(e, t3.AddMinutes(22));
            Answer(e, new { join = true, reply_to = x.Number("message_id"), mode = "render", capability = "edit_deadline" });
            await Observe(e, x, t3.AddMinutes(22));
            Assert(Calls(e) == 5 && Group(e).Count == sent && editor.ValueKind == JsonValueKind.Object, "Editor's change, foreign reply_to or a non-render capability got an answer");
        });

        await Check("observer: daily limit, the minimal gap after the bot's message and one call per 10 minutes", async () =>
        {
            using var e = Obs("obs-limits.db");
            e.C.Features.ObserverPerDay = 1;
            var first = Talk(e, T);
            Answer(e, new { join = true, reply_to = first.Number("message_id"), mode = "render", capability = "deadlines_list" });
            await Observe(e, first, T);
            Assert(Group(e).Count == 1, "First join missing");
            Assert(!await Observe(e, Talk(e, T.AddMinutes(15)), T.AddMinutes(15)) && Calls(e) == 1, "Daily limit ignored");

            var day2 = T.AddDays(1);
            Say(e, 99, "я тут кое-что ответил", day2.AddMinutes(-20));
            Assert(!await Observe(e, Talk(e, day2), day2) && Calls(e) == 1, "Gap after the bot's message ignored");
            Answer(e, new { join = false });
            var ok = day2.AddMinutes(30);
            Assert(await Observe(e, Talk(e, ok), ok) && Calls(e) == 2, "Gap passed but no call");
            Assert(!await Observe(e, Talk(e, ok.AddMinutes(5)), ok.AddMinutes(5)) && Calls(e) == 2, "Second call within 10 minutes");
            Assert(await Observe(e, Talk(e, ok.AddMinutes(11)), ok.AddMinutes(11)) && Calls(e) == 3, "No call after 10 minutes");
            var night = DateTimeOffset.Parse("2026-12-02T20:00:00Z");
            Assert(!await Observe(e, Talk(e, night), night) && Calls(e) == 3, "Called outside the active window");
        });

        await Check("observer: «бот, молчи» or «не лезь» in the last 15 → silence till the end of the day", async () =>
        {
            using var e = Obs("obs-stop.db");
            Answer(e, new { join = false });
            Say(e, 2, "бот, молчи", T.AddMinutes(-9));
            Assert(!await Observe(e, Talk(e, T), T) && Calls(e) == 0 && e.S.Get("observer:quiet:2026-12-01") is not null, "Stop word ignored");
            var later = T.AddHours(3);
            for (var i = 0; i < 16; i++) Say(e, 2 + i % 2, "болтаем " + i, later.AddMinutes(-30 + i));
            Assert(!await Observe(e, Talk(e, later), later) && Calls(e) == 0, "Spoke again the same day");
            Assert(await Observe(e, Talk(e, T.AddDays(1)), T.AddDays(1)) && Calls(e) == 1, "Silent the next day too");
            using var e2 = Obs("obs-stop2.db");
            Answer(e2, new { join = false });
            Say(e2, 3, "не лезь, мы сами", T.AddMinutes(-9));
            Assert(!await Observe(e2, Talk(e2, T), T) && Calls(e2) == 0, "«не лезь» ignored");
        });

        await Check("observer: flag off → not a single call; on → the message handler asks the observer", async () =>
        {
            using var e = Obs("obs-flag.db");
            e.C.Ambient.ActiveFrom = "00:00"; e.C.Ambient.ActiveTo = "23:59";
            Answer(e, new { join = false });
            e.C.Features.Observer = false;
            var now = DateTimeOffset.UtcNow;
            await Process(e, new { message = Talk(e, now, "ну и ладно") });
            Assert(e.Ai.Count == 0 && e.Sent.Count == 0, "Flag off but something happened");
            e.C.Features.Observer = true;
            await Process(e, new { message = Say(e, 2, "ну и ладно 2", now) });
            for (var i = 0; i < 100 && e.S.AiCalls(limit: 5).All(c => c.Kind != "observer"); i++) await Task.Delay(50);
            Assert(Calls(e) == 1 && Group(e).Count == 0, "Handler did not ask the observer");
        });

        await Check("observer: day cost on an active chat — at most one call per 10 minutes in 10:00–21:30", async () =>
        {
            using var e = Obs("obs-day.db");
            Answer(e, new { join = false });
            var start = DateTimeOffset.Parse("2026-12-01T07:00:00Z");
            for (var t = start; t <= start.AddHours(11.5); t = t.AddMinutes(1)) await Observe(e, Say(e, t.Minute % 2 == 0 ? 2 : 3, "сообщение в " + t.ToString("HH:mm"), t), t);
            var requests = e.S.AiCalls(limit: 200, full: true).Where(c => c.Kind == "observer").ToList();
            Console.WriteLine($"  observer: {requests.Count} calls per active day, request ~{requests.Average(c => c.System.Length + c.Request.Length):0} chars");
            Assert(requests.Count == 69, "Unexpected number of calls: " + requests.Count);
        });
    }
}
