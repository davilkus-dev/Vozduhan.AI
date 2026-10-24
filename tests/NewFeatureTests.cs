using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vozduhan.AI;

static class NewFeatureTests
{
    static int count;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Check(string name, Action test) { test(); count++; Console.WriteLine("PASS " + name); }
    static JsonElement Op(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o, Json.Options);
    static JsonElement Msg(long chat, int topic, int id, long actor, string text) => El(new
    {
        message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        chat = new { id = chat, type = chat > 0 ? "private" : "supergroup" }, message_thread_id = topic,
        from = new { id = actor, first_name = "Имя", username = "user" + actor, is_bot = false }, text
    });

    /// <summary>The real group config (gitignored) when present; otherwise these checks are skipped.</summary>
    static BotConfig? RealConfig(string temp)
    {
        foreach (var path in new[] { "config/bot.local.json", "../config/bot.local.json"})
            if (File.Exists(path))
            {
                var c = Json.Read<BotConfig>(File.ReadAllText(path));
                c.DataPath = Path.Combine(temp, "real-" + Guid.NewGuid().ToString("N")[..6] + ".db");
                c.ReplyDelayScale = 0;
                return c;
            }
        return null;
    }

    public static async Task<int> Run(string temp, Func<string, BotConfig> make)
    {
        count = 0;
        var real = RealConfig(temp);
        if (real is not null)
        {
            Check("real config validates and timetable resolves odd/even weeks, campuses and lecture/practice", () =>
            {
                real.Validate();
                var r = real.R;
                // Week 1 starts 2026-08-31, so 2026-10-29 (Thu) is week 9 (odd) and 2026-10-22 (Thu) is week 8 (even).
                var odd = Schedule.Day(r, new DateOnly(2026, 10, 29));
                var even = Schedule.Day(r, new DateOnly(2026, 10, 22));
                Assert(Schedule.Parity(Schedule.Week(r, new DateOnly(2026, 10, 29))) == "odd", "Parity base wrong");
                Assert(odd.Count == 6 && odd.Single(s => s.Period == 5).SubjectId == "siaod" && odd.Single(s => s.Period == 6).SubjectId == "avmis", "Odd Thursday wrong");
                Assert(even.Count == 5 && even.Single(s => s.Period == 5).SubjectId == "ai", "Even Thursday wrong");
                Assert(Schedule.Day(r, new DateOnly(2026, 10, 28)).Count == 0, "Wednesday must be free");
                var saturday = Schedule.Day(r, new DateOnly(2026, 10, 31));
                Assert(saturday.All(s => s.Campus == "Малая Пироговская") && saturday.Count == 2, "Saturday campus lost");
                var monday = Schedule.Day(r, new DateOnly(2026, 10, 26));
                Assert(monday.All(s => s.Campus == "Вернадка") && monday[0].Kind == "lecture" && monday[0].Room == "А-13", "Default campus/kind wrong");
                Assert(r.Subjects.Single(s => s.Id == "philosophy").Aliases!.Contains("шаманство"), "Slang lost");
                Assert(r.Lessons.Where(l => l.Kind == "lecture" && (l.Campus ?? "Вернадка") == "Вернадка").All(l => l.Room == "А-13"), "Lecture room drift");
                // Java on Friday has two practices: a deadline must ask which one; Thursday matan prefers... only lecture -> it is the only candidate.
                var now = DateTimeOffset.Parse("2026-10-24T09:00:00Z");
                var java = Deadlines.Resolve(real, new(SubjectId: "java", Work: "Практика №5", Date: "2026-10-30"), null, [], real.OwnerId, "", now);
                Assert(java.Missing.Any(m => m.Contains("несколько пар")), "Two Java practices must be ambiguous");
                var siaod = Deadlines.Resolve(real, new(SubjectId: "siaod", Work: "Практика №5", Date: "2026-10-29"), null, [], real.OwnerId, "", now);
                Assert(siaod.Period == 3 && siaod.Room == "И-204-а", "Practice must win over the lecture on the same day");
            });
            Check("one-off remote lecture on Saturday 31.10 via planner operations", () =>
            {
                var (next, lines) = RegistryOps.ApplyAll(real.R, [Op(new { op = "change_add", date = "2026-10-31", type = "add", subjectId = "matan", kind = "lecture", period = 6, online = true })], real.OwnerId, true);
                var day = Schedule.Day(next, new DateOnly(2026, 10, 31));
                Assert(day.Count == 3 && day[^1].Status == "added" && day[^1].RoomText == "дистанционно" && day[^1].Start == "18:00", "Remote lecture missing");
                Assert(Schedule.Day(next, new DateOnly(2026, 11, 7)).All(s => s.SubjectId != "matan"), "One-off change leaked into another week");
                Assert(lines.Single().Contains("31.10"), "Preview line lacks date");
            });
        }

        Check("schedule changes: cancel, modify, dayoff, and next lesson", () =>
        {
            var c = make("sched.db");
            var r = c.R;
            // 2026-11-30 is Monday (siaod 6, java 2); week parity from 2026-09-07.
            r.Changes = [new ScheduleChange("2026-11-30", "cancel") { Id = "C1", SubjectId = "java" },
                new ScheduleChange("2026-12-02", "dayoff") { Id = "C2", Note = "праздник" }];
            var day = Schedule.Day(r, new DateOnly(2026, 11, 30));
            Assert(day.Single(s => s.SubjectId == "java").Status == "cancelled" && day.Single(s => s.SubjectId == "siaod").Status == "regular", "Cancel wrong");
            Assert(Schedule.Day(r, new DateOnly(2026, 12, 2)).All(s => s.Status == "cancelled"), "Dayoff wrong");
            var next = Schedule.Now(r, new DateTime(2026, 11, 30, 9, 0, 0));
            Assert(next.Next?.SubjectId == "siaod" && next.Next.Date == "2026-11-30", "Cancelled lesson offered as next");
            var during = Schedule.Now(r, new DateTime(2026, 11, 30, 18, 30, 0));
            Assert(during.Current?.SubjectId == "siaod", "Current lesson not detected");
            Assert(Schedule.DayText(r, new DateOnly(2026, 11, 30)).Contains("отменена"), "Day text hides cancellation");
        });

        Check("roster edits: expulsion keeps history and revokes access at once; head cannot change the head", () =>
        {
            var c = make("roster.db");
            using var s = new BotStore(c);
            Assert(c.AllowedUser(3), "Student must start allowed");
            var (v, lines) = s.CommitRegistry(2, "test", null, cur => RegistryOps.ApplyAll(cur, [Op(new { op = "member_deactivate", member = new { listNumber = 3 }, reason = "отчислен" })], c.OwnerId, false));
            Assert(!c.AllowedUser(3) && c.R.Members.Any(m => m.UserId == 3 && !m.Active && m.Note == "отчислен"), "Deactivation not applied live");
            Assert(lines.Single().Contains("Студент Тестовый"), "Preview line lacks the name");
            try { RegistryOps.ApplyAll(c.R, [Op(new { op = "head_set", member = new { listNumber = 1 } })], c.OwnerId, false); throw new Exception("Head reassigned the head"); }
            catch (InvalidOperationException) { }
            try { RegistryOps.ApplyAll(c.R, [Op(new { op = "member_deactivate", member = new { userId = 1 } })], c.OwnerId, false); throw new Exception("Owner deactivated"); }
            catch (InvalidOperationException) { }
            var full = c.R.Clone();
            full.Members = full.Members.Select(m => m.UserId == 3 ? m with { Role = "head" } : m.Role == "head" ? m with { Role = "member" } : m).ToArray();
            try { s.CommitRegistry(2, "web", null, cur => (full, RegistryOps.Diff(cur, full))); throw new Exception("Head swapped heads via full replace"); }
            catch (InvalidOperationException) { }
            var added = RegistryOps.ApplyAll(c.R, [Op(new { op = "member_add", fullName = "Новенький Иван Иванович", shortName = "Ваня" })], c.OwnerId, false);
            Assert(added.Result.Members.Last().ListNumber == 3 && added.Result.Members.Last().UserId is null, "New member numbering wrong");
            var back = RegistryOps.ApplyAll(c.R, [Op(new { op = "member_activate", member = new { name = "Студент Тестовый" } })], c.OwnerId, false);
            Assert(back.Result.Members.Single(m => m.UserId == 3).Active, "Reactivation failed");
        });

        Check("registry versions: restart keeps edits, seed is not re-imported, registryVersion bump re-imports with stable keys", () =>
        {
            var c = make("versions-reg.db");
            using (var s = new BotStore(c))
            {
                s.CommitRegistry(1, "test", null, cur => RegistryOps.ApplyAll(cur, [Op(new { op = "subject_upsert", id = "siaod", addAliases = new[] { "структуры" }, adminComment = "там все спят" })], c.OwnerId, true));
                Assert(s.RegistryVersion() == 2, "Version not stored");
            }
            var again = make("versions-reg.db");
            using (var s = new BotStore(again))
            {
                Assert(again.R.Subject("siaod")!.AdminComment == "там все спят", "Edit lost on restart / seed re-imported");
                var key = again.R.Members.Single(m => m.UserId == 3).Key;
                var bumped = make("versions-reg.db"); bumped.RegistryVersion = 2;
                using var s2 = new BotStore(bumped);
                Assert(bumped.R.Subject("siaod")!.AdminComment is null && bumped.R.Members.Single(m => m.UserId == 3).Key == key, "Explicit re-import failed or changed keys");
                Assert(s2.RegistryHistory().First().Source == "config", "Re-import not recorded");
            }
        });

        Check("undo restores the previous version; stale web saves get a conflict", () =>
        {
            var c = make("undo.db");
            using var s = new BotStore(c);
            var v1 = s.RegistryVersion();
            s.CommitRegistry(1, "test", null, cur => RegistryOps.ApplyAll(cur, [Op(new { op = "knowledge_set", text = "ПТУ МИРЭА" })], c.OwnerId, true));
            Assert(c.R.Knowledge == "ПТУ МИРЭА", "Knowledge not set");
            var prev = s.PreviousRegistryVersion()!.Value;
            var target = s.RegistryAt(prev)!;
            s.SaveRegistryDraft(new RegistryDraft("head", 2, s.RegistryVersion(), "restore", [], ["x"], "/undo", prev));
            try { s.ConfirmRegistryDraft(2, "head"); throw new Exception("Head reverted the owner's knowledge edit"); }
            catch (InvalidOperationException) { }
            s.SaveRegistryDraft(new RegistryDraft("tok", 1, s.RegistryVersion(), "restore", [], ["x"], "/undo", prev));
            s.ConfirmRegistryDraft(1, "tok");
            Assert(c.R.Knowledge == "" && prev == v1, "Undo failed");
            try { s.CommitRegistry(1, "web", v1, cur => (cur.Clone(), ["x"])); throw new Exception("Stale save accepted"); }
            catch (RegistryConflict) { }
            try { s.ConfirmRegistryDraft(1, "tok"); throw new Exception("Draft applied twice"); }
            catch (InvalidOperationException) { }
        });

        Check("lesson_update with a start date splits the row; lesson_remove from a date ends it", () =>
        {
            var c = make("split.db");
            var r = c.R;
            var java = r.Lessons.Single(l => l.SubjectId == "java");
            var (next, _) = RegistryOps.ApplyAll(r, [Op(new { op = "lesson_update", lessonId = java.Id, from = "2026-11-09", room = "В-404" })], c.OwnerId, true);
            Assert(Schedule.Day(next, new DateOnly(2026, 11, 2)).Single(s => s.SubjectId == "java").Room == "Б201", "Old room lost before the date");
            Assert(Schedule.Day(next, new DateOnly(2026, 11, 9)).Single(s => s.SubjectId == "java").Room == "В-404", "New room missing after the date");
            var (gone, _) = RegistryOps.ApplyAll(r, [Op(new { op = "lesson_remove", lessonId = java.Id, from = "2026-11-09" })], c.OwnerId, true);
            Assert(Schedule.Day(gone, new DateOnly(2026, 11, 9)).All(s => s.SubjectId != "java") && Schedule.Day(gone, new DateOnly(2026, 11, 2)).Any(s => s.SubjectId == "java"), "Lesson end wrong");
            try { RegistryOps.ApplyAll(r, [Op(new { op = "change_add", date = "2026-11-03", type = "cancel", lessonId = java.Id })], c.OwnerId, true); throw new Exception("Cancel on a day without the lesson accepted"); }
            catch (InvalidOperationException) { }
        });

        Check("Telegram HTML sanitizer keeps supported formatting and neutralises the rest", () =>
        {
            var html = TelegramHtml.Sanitize("<b>Жирно</b> <strong>тоже</strong> <i>к</i> <u>п</u> <s>з</s> <tg-spoiler>сп</tg-spoiler> <span class=\"tg-spoiler\">сп2</span> " +
                "<code>a<b</code> <pre><code class=\"language-cs\">x < y</code></pre> <blockquote expandable>цитата</blockquote> " +
                "<a href=\"https://mirea.ru\">сайт</a> <a href=\"javascript:alert(1)\">плохая</a> <tg-time unix=\"1761904800\" format=\"wdt\">сб</tg-time> " +
                "<script>x</script> <div onclick=1>d</div> 5 > 3 & 2 &amp; <b>незакрыт");
            Assert(html.Contains("<b>Жирно</b>") && html.Contains("<b>тоже</b>") && html.Contains("<i>к</i>") && html.Contains("<u>п</u>") && html.Contains("<s>з</s>"), "Inline tags lost: " + html);
            Assert(html.Contains("<tg-spoiler>сп</tg-spoiler>") && html.Contains("<tg-spoiler>сп2</tg-spoiler>"), "Spoilers lost: " + html);
            Assert(html.Contains("<code>a&lt;b</code>") && html.Contains("<pre><code class=\"language-cs\">x &lt; y</code></pre>"), "Code broken: " + html);
            Assert(html.Contains("<blockquote expandable>цитата</blockquote>") && html.Contains("<a href=\"https://mirea.ru\">сайт</a>"), "Quote/link lost: " + html);
            Assert(!html.Contains("javascript") && html.Contains("плохая") && html.Contains("<tg-time unix=\"1761904800\" format=\"wdt\">сб</tg-time>"), "Link/time handling wrong: " + html);
            Assert(html.Contains("&lt;script&gt;") && html.Contains("&lt;div onclick=1&gt;") && html.Contains("5 &gt; 3 &amp; 2 &amp;amp;") == false && html.Contains("5 &gt; 3 &amp; 2 &amp;"), "Escaping wrong: " + html);
            Assert(html.EndsWith("<b>незакрыт</b>"), "Unclosed tag not balanced: " + html);
            Assert(TelegramHtml.Plain("<b>a</b> &lt;b&gt;") == "a <b>", "Plain fallback wrong");
            Assert(TelegramHtml.Sanitize("<b><i>x</b></i>") == "<b><i>x</i></b>", "Misnesting not repaired: " + TelegramHtml.Sanitize("<b><i>x</b></i>"));
        });

        Check("Markdown slips from the model become Telegram HTML", () =>
        {
            string F(string x) => TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(x));
            Assert(F("лекции в **А-13**") == "лекции в <b>А-13</b>", F("лекции в **А-13**"));
            Assert(F("*курсив* и _тоже_, ~~нет~~, ||спойлер||") == "<i>курсив</i> и <i>тоже</i>, <s>нет</s>, <tg-spoiler>спойлер</tg-spoiler>", F("*курсив* и _тоже_, ~~нет~~, ||спойлер||"));
            Assert(F("`a**b**<c>`") == "<code>a**b**&lt;c&gt;</code>", F("`a**b**<c>`"));
            Assert(F("[сайт](https://mirea.ru)") == "<a href=\"https://mirea.ru\">сайт</a>", F("[сайт](https://mirea.ru)"));
            Assert(F("snake_case_name и 2*3*4 и <b>уже html</b>") == "snake_case_name и 2*3*4 и <b>уже html</b>", F("snake_case_name и 2*3*4 и <b>уже html</b>"));
        });

        Check("owner-only magic: head cannot touch slang/comments/notes/knowledge; notes go only to the group chat prompt", () =>
        {
            var c = make("secrets.db");
            using var s = new BotStore(c);
            foreach (var op in new object[] { new { op = "subject_upsert", id = "java", addAliases = new[] { "ява2" } }, new { op = "subject_upsert", id = "java", adminComment = "душнит" },
                         new { op = "member_update", member = new { userId = 3 }, adminNote = "про видеокарты" }, new { op = "knowledge_append", text = "ПТУ" } })
                try { s.CommitRegistry(2, "t", null, cur => RegistryOps.ApplyAll(cur, [Op(op)], c.OwnerId, false)); throw new Exception("Head changed owner data: " + Json.Write(op)); }
                catch (InvalidOperationException) { }
            var (_, lines) = s.CommitRegistry(1, "t", null, cur => RegistryOps.ApplyAll(cur, [Op(new { op = "member_update", member = new { userId = 3 }, adminNote = "Лёха вечно про видеокарты, опять купил б/у 3060" })], c.OwnerId, true));
            Assert(lines.All(RegistryOps.IsSecret) && RegistryOps.ForViewer(lines, false).Count == 0, "Secret line visible to the head");
            var conv = new Conversation(c, s);
            Assert(conv.AdminContext(false).Contains("3060") && !conv.AdminContext(true).Contains("3060"), "People notes must be only in the group chat");
            var head = Json.Write(conv.PlannerState(2, null, null));
            Assert(!head.Contains("3060") && Json.Write(conv.PlannerState(1, null, null)).Contains("3060"), "Planner leaks notes to the head");
            var full = c.R.Redacted();
            Assert(full.Members.All(m => m.AdminNote is null) && full.WithSecretsFrom(c.R).Members.Single(m => m.UserId == 3).AdminNote!.Contains("3060"), "Redact/merge broken");
        });

        Check("emoji are not escaped for the model and literal escapes from the model are repaired", () =>
        {
            Assert(Json.Write(new { t = "ок 😄" }).Contains("😄") && !Json.Write(new { t = "😄" }).Contains("\\u"), "Emoji escaped for the model");
            Assert(Json.Write(new { t = "\\uD83D\\uDE04 literal" }).Contains("\\\\uD83D"), "Literal backslash text corrupted");
            Assert(Json.Unescape("Миш \\uD83D\\uDE04 ок \\u2764") == "Миш 😄 ок ❤", "Model escapes not repaired: " + Json.Unescape("Миш \\uD83D\\uDE04 ок \\u2764"));
            var r = make("html.db").R;
            Assert(TelegramHtml.Sanitize(ScheduleHtml.Week(r, new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 30))) == ScheduleHtml.Week(r, new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 30)), "Week HTML not canonical");
            Assert(ScheduleHtml.Day(r, new DateOnly(2026, 11, 30), "Сегодня").Contains("<b>СиАОД</b>"), "Day HTML lacks bold subject");
        });

        Check("adaptive effort and regeneration of answers cut by the output limit", () =>
        {
            var bodies = new List<string>();
            var n = 0;
            using var http = new HttpClient(new Fake(async request =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync());
                return ++n == 1 ? new { choices = new[] { new { message = new { content = "{\"a\":" }, finish_reason = "length" } } }
                    : new { choices = new[] { new { message = new { content = "{\"a\":1}" }, finish_reason = "stop" } } };
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            var c = make("effort.db");
            var result = new OpenRouterClient(http, c, "k").Complete("s", "u", true, CancellationToken.None, "ambient-summary").GetAwaiter().GetResult();
            Assert(result == "{\"a\":1}" && bodies.Count == 2, "Cut answer not regenerated");
            Assert(bodies[0].Contains("\"max_tokens\":4096") && bodies[1].Contains("\"max_tokens\":8192"), "Budget not raised");
            Assert(bodies[0].Contains("\"effort\":\"medium\"") && c.Effort("chat") == "low", "Effort per kind wrong");
            Assert(bodies[0].Contains("\"model\":\"z-ai/glm-5.3-flash\"") && c.ModelFor("plan") == "z-ai/glm-5.3" && c.ModelFor("wakeplan") == "z-ai/glm-5.3" &&
                c.ModelFor("deadline") == "z-ai/glm-5.3" && c.ModelFor("ambient-deadline") == c.Model && c.ModelFor("chat") == c.Model, "Model routing wrong");
        });

        Check("models: compatibility retry, fallback to the main model, explicit override", () =>
        {
            var bodies = new List<string>();
            using var http = new HttpClient(new FakeStatus(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync(); bodies.Add(body);
                if (body.Contains("\"model\":\"vendor/strict\"") && body.Contains("\"reasoning\"")) return (HttpStatusCode.BadRequest, new { error = "no provider" });
                if (body.Contains("\"model\":\"vendor/down\"")) return (HttpStatusCode.ServiceUnavailable, new { error = "down" });
                return (HttpStatusCode.OK, new { choices = new[] { new { message = new { content = "ок" }, finish_reason = "stop" } } });
            })) { BaseAddress = new Uri("https://openrouter.test/") };
            var c = make("models.db");
            c.ModelRouting = new() { ["plan"] = "vendor/strict", ["wakeplan"] = "vendor/down" };
            var client = new OpenRouterClient(http, c, "k");
            Assert(client.Complete("s", "u", false, CancellationToken.None, "plan").GetAwaiter().GetResult() == "ок" && bodies.Count == 2 &&
                !bodies[1].Contains("\"reasoning\"") && bodies[1].Contains("vendor/strict"), "Compatibility retry failed");
            bodies.Clear();
            Assert(client.Complete("s", "u", false, CancellationToken.None, "wakeplan").GetAwaiter().GetResult() == "ок" && bodies.Last().Contains(c.Model), "Fallback to main model failed");
            bodies.Clear();
            client.Complete("s", "u", false, CancellationToken.None, "chat", "", "google/gemini-3.8-flash").GetAwaiter().GetResult();
            Assert(bodies.Single().Contains("google/gemini-3.8-flash"), "Override ignored");
            Assert(AiSettings.ValidModel("deepseek/deepseek-v4.1-flash") && !AiSettings.ValidModel("rm -rf"), "Model id validation");
        });

        Check("sent messages: entities become editable HTML", () =>
        {
            var msg = El(new { text = "Бро, А-13 тут", entities = new object[] { new { type = "bold", offset = 0, length = 3 }, new { type = "code", offset = 5, length = 4 },
                new { type = "blockquote", offset = 0, length = 13 } } });
            var html = TelegramHtml.FromEntities(msg.Text("text")!, msg.Field("entities"));
            Assert(html == "<blockquote><b>Бро</b>, <code>А-13</code> тут</blockquote>", "Entities→HTML wrong: " + html);
            var emoji = El(new { text = "😄 <жирно>", entities = new[] { new { type = "italic", offset = 3, length = 7 } } });
            Assert(TelegramHtml.FromEntities(emoji.Text("text")!, emoji.Field("entities")) == "😄 <i>&lt;жирно&gt;</i>", "UTF-16 offsets/escaping wrong");
        });

        Check("chat answers split on separator lines and never produce empty parts", () =>
        {
            Assert(OpenRouterClient.Parts("Бро, сегодня философия\n||\nИ дедлайн…\n||\nСоболезную").SequenceEqual(["Бро, сегодня философия", "И дедлайн…", "Соболезную"]), "Split failed");
            Assert(OpenRouterClient.Parts("один ответ").Count == 1, "Single answer split");
            Assert(OpenRouterClient.Parts("a\n||\n\n||\nb").Count == 2, "Empty part produced");
            Assert(OpenRouterClient.Parts("1\n||\n2\n||\n3\n||\n4\n||\n5").Count == 4, "Too many parts");
        });

        Check("web tokens: login links are single-use, sessions persist", () =>
        {
            using var s = new BotStore(make("tokens.db"));
            var login = s.CreateWebToken(2, "login", TimeSpan.FromMinutes(10));
            Assert(s.WebToken(login, "session") is null, "Login token accepted as session");
            Assert(s.WebToken(login, "login") == 2 && s.WebToken(login, "login") is null, "Login token reusable");
            var session = s.CreateWebToken(2, "session", TimeSpan.FromDays(1));
            Assert(s.WebToken(session, "session") == 2 && s.WebToken(session, "session") == 2, "Session lost");
            s.DeleteWebToken(session);
            Assert(s.WebToken(session, "session") is null, "Logout failed");
        });

        Check("AI context carries schedule facts, slang and knowledge; tone notes stay in the trusted system part", () =>
        {
            var c = make("ctx.db");
            c.Subjects = [new Subject("siaod", "СиАОД", ["сиаод"]) { AdminComment = "препод душнит" }, new Subject("java", "Java", ["джава"])];
            c.Knowledge = "ПТУ МИРЭА — наш мем";
            using var s = new BotStore(c);
            var conv = new Conversation(c, s);
            var context = conv.Context(Msg(c.GroupId, c.ChatTopicId, 5, 3, "какая пара следующая?"), false);
            Assert(context.Contains("next_lesson") && context.Contains("сиаод") && context.Contains("А412"), "Schedule facts missing");
            Assert(!context.Contains("препод душнит") && !context.Contains("ПТУ МИРЭА"), "Admin text leaked into the untrusted part");
            Assert(context.Contains("\"telegram_username\":\"@head\"") && context.Contains("https://t.me/head") && context.Contains("tg://user?id=2"), "Member contacts unclear: " + context);
            var system = conv.AdminContext();
            Assert(system.Contains("препод душнит") && system.Contains("ПТУ МИРЭА"), "Admin context missing");
            Assert(OpenRouterClient.ChatRules(c, true).Contains("староста") && OpenRouterClient.ChatRules(c, true).Contains("||"), "Russian rules / split hint missing");
        });

        // ---- Worker flows with mock Telegram/OpenRouter ----
        var sent = new List<(string Method, JsonElement Payload)>();
        var nextId = 5000;
        var tg = new HttpClient(new Fake(async request =>
        {
            var method = request.RequestUri!.Segments[^1];
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            lock (sent) sent.Add((method, body));
            if (method == "sendMessage")
                return new { ok = true, result = new { message_id = ++nextId, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    chat = new { id = body.Number("chat_id"), type = body.Number("chat_id") > 0 ? "private" : "supergroup" },
                    message_thread_id = body.Number("message_thread_id"), from = new { id = 99, is_bot = true, first_name = "bot" }, text = body.Text("text") } };
            return new { ok = true, result = (object)true };
        })) { BaseAddress = new Uri("https://telegram.test/") };
        string plan = "{}";
        string chatAnswer = "Бро, сегодня философия\n||\nИ дедлайн…\n||\nСоболезную";
        var aiHttp = new HttpClient(new Fake(async request =>
        {
            var text = await request.Content!.ReadAsStringAsync();
            var content = text.Contains("разбираешь личное сообщение редактора") ? plan : chatAnswer;
            return new { choices = new[] { new { message = new { content } } } };
        })) { BaseAddress = new Uri("https://openrouter.test/") };

        {
            var c = make("planner.db");
            using var s = new BotStore(c);
            var worker = new BotWorker(c, s, new TelegramClient(tg, "t"), new OpenRouterClient(aiHttp, c, "k"), NullLogger<BotWorker>.Instance);
            typeof(BotWorker).GetField("botId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, 99L);
            typeof(BotWorker).GetField("username", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, "vozduhanAIbot");
            var process = typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Run(JsonElement update) => await (Task)process.Invoke(worker, [update, CancellationToken.None])!;

            // Head asks for a one-off change in DM -> card -> confirm -> live registry + owner notified.
            plan = Json.Write(new { intent = "registry", operations = new object[] { new { op = "change_add", date = "2026-11-30", type = "cancel", subjectId = "java", note = "препод заболел" } } });
            await Run(El(new { message = Msg(2, 0, 10, 2, "в понедельник 30-го джавы не будет, препод заболел") }));
            var card = sent.Last(x => x.Method == "sendMessage").Payload;
            Assert(card.Text("text")!.Contains("Изменения реестра") && card.Field("reply_markup").ValueKind == JsonValueKind.Object, "Registry card missing");
            Assert(c.R.Changes.Length == 0, "Change applied before confirmation");
            var token = s.RegistryDraft(2)!.Token;
            sent.Clear();
            var cb = El(new { callback_query = new { id = "cb", from = new { id = 2 }, data = "rok:" + token, message = new { message_id = 77, chat = new { id = 2 } } } });
            await Run(cb); await Run(cb);
            Assert(c.R.Changes.Length == 1 && Schedule.Day(c.R, new DateOnly(2026, 11, 30)).Single(x => x.SubjectId == "java").Status == "cancelled", "Confirmed change not live");
            Assert(sent.Any(x => x.Method == "sendMessage" && x.Payload.Number("chat_id") == 1 && x.Payload.Text("text")!.Contains("Староста")), "Owner not notified");
            Assert(sent.Count(x => x.Method == "editMessageText") == 1, "Double confirm edited twice");

            // Ordinary chat in the group: split into three messages, only the first one is a reply.
            sent.Clear();
            var mention = El(new { message = El(new
            {
                message_id = 20, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = c.GroupId, type = "supergroup" }, message_thread_id = c.ChatTopicId,
                from = new { id = 3, first_name = "Студент" }, text = "@vozduhanAIbot что сегодня?", entities = new[] { new { type = "mention", offset = 0, length = 14 } }
            }) });
            await Run(mention); await Run(mention);
            var replies = sent.Where(x => x.Method == "sendMessage").ToArray();
            Assert(replies.Length == 3 && replies[0].Payload.Field("reply_parameters").ValueKind == JsonValueKind.Object &&
                replies.Skip(1).All(x => x.Payload.Field("reply_parameters").ValueKind == JsonValueKind.Undefined), "Split reply wrong");
            Assert(replies.Select(x => x.Payload.Text("text")).SequenceEqual(["Бро, сегодня философия", "И дедлайн…", "Соболезную"]), "Split text wrong");

            // Editor small talk goes to chat after the planner.
            sent.Clear();
            plan = Json.Write(new { intent = "chat" });
            await Run(El(new { message = Msg(1, 0, 30, 1, "как дела?") }));
            Assert(sent.Count(x => x.Method == "sendMessage") == 3, "Editor chat not answered");
            Assert(s.AiCalls().Any(x => x.Kind == "plan") && s.AiCalls().Any(x => x.Kind == "chat" && x.Tag == "chat:1:30"), "AI calls not logged");

            // Deterministic schedule commands work without AI.
            sent.Clear();
            await Run(El(new { message = Msg(3, 0, 40, 3, "/date 30.11.2026") }));
            Assert(sent.Single(x => x.Method == "sendMessage").Payload.Text("text")!.Contains("СиАОД"), "/date failed");

            // Group commands are deterministic HTML, never AI, never editor data.
            async Task<string> GroupCommand(int id, string cmd)
            {
                sent.Clear();
                var text = cmd + "@vozduhanAIbot";
                await Run(El(new { message = El(new { message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = c.GroupId, type = "supergroup" },
                    message_thread_id = c.ChatTopicId, from = new { id = 1, first_name = "Миша" }, text, entities = new[] { new { type = "bot_command", offset = 0, length = text.Length } } }) }));
                var reply = sent.Single(x => x.Method == "sendMessage").Payload;
                Assert(reply.Text("parse_mode") == "HTML" && TelegramHtml.Sanitize(reply.Text("text")!) == reply.Text("text"), "Command HTML not clean: " + reply.Text("text"));
                return reply.Text("text")!;
            }
            var aiBefore = s.AiCalls().Count;
            Assert((await GroupCommand(60, "/roster")).Contains("Студент Тестовый") && !(await GroupCommand(61, "/roster")).Contains("@"), "Group roster wrong or leaks usernames");
            Assert((await GroupCommand(62, "/help")).Contains("/today"), "Group help wrong");
            Assert((await GroupCommand(63, "/roaster")).Contains("Не знаю команду"), "Unknown command went to AI");
            Assert((await GroupCommand(64, "/web")).Contains("личк"), "Management command answered in the group");
            Assert((await GroupCommand(65, "/week")).Contains("<blockquote>") && (await GroupCommand(66, "/today")).Contains("📅"), "Schedule HTML wrong");
            Assert(s.AiCalls().Count == aiBefore, "Commands must not call the AI");

            // Incoming log + seen users (reflection: Poll is not running in tests).
            var logIn = typeof(BotWorker).GetMethod("LogIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!;
            logIn.Invoke(worker, [El(new { update_id = 1, message = Msg(c.GroupId, c.ChatTopicId, 50, 777, "я тут новенький") })]);
            Assert(s.SeenUsers().Any(u => u.Id == 777) && s.Log(sender: 777).Single().Text == "я тут новенький", "Incoming message not logged");
            count++; Console.WriteLine("PASS planner registry card, confirm once, notify, split chat, editor chat, /date, logs (mock APIs)");

            // ---- The bot's own initiative ----
            {
                var ac = make("ambient.db");
                ac.SummaryTopicId = 30; ac.Ambient.SpontaneousPerDay = 0; ac.Ambient.MorningTime = "08:00";
                using var st = new BotStore(ac);
                var aiRequests = new List<string>();
                string wakePlan = "{}";
                var ambientAi = new HttpClient(new Fake(async request =>
                {
                    var text = await request.Content!.ReadAsStringAsync();
                    lock (aiRequests) aiRequests.Add(text);
                    var content = text.Contains("планируешь, когда тебе") ? wakePlan
                        : text.Contains("утреннее сообщение группе в теме «Сводка»") ? "Понедельник, держитесь, @user3"
                        : text.Contains("ЗАДАЧА: утреннее") ? "Доброе утро!\n||\nСегодня СиАОД"
                        : text.Contains("ЗАДАЧА: ты сам решил") ? "SKIP"
                        : text.Contains("ЗАДАЧА: ты проснулся") ? "СиАОД через 3 дня, <b>шевелитесь</b>"
                        : text.Contains("ЗАДАЧА: разработчик попросил") ? "Сходка в 15:00, все к А-13"
                        : text.Contains("вводные строки к постам о дедлайнах") ? "{\"news\":\"!!! Свежак по СиАОД\",\"morning\":\"Сегодня сдаём!\"}"
                        : "Завтра сдаём СиАОД";
                    return new { choices = new[] { new { message = new { content } } } };
                })) { BaseAddress = new Uri("https://openrouter.test/") };
                var w = new BotWorker(ac, st, new TelegramClient(tg, "t"), new OpenRouterClient(ambientAi, ac, "k"), NullLogger<BotWorker>.Instance);
                var tick = typeof(BotWorker).GetMethod("AmbientTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                async Task Tick(string utc) => await (Task)tick.Invoke(w, [DateTimeOffset.Parse(utc), CancellationToken.None])!;
                var proc = typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!;
                async Task Upd(JsonElement u) => await (Task)proc.Invoke(w, [u, CancellationToken.None])!;

                // 2026-11-30 (Mon) 08:01 MSK = 05:01Z: summary in «Сводка», morning drop in Воздуханство, exactly once.
                sent.Clear();
                await Tick("2026-11-30T05:01:00Z"); await Tick("2026-11-30T05:02:00Z");
                var posts = sent.Where(x => x.Method == "sendMessage").ToList();
                Assert(posts.Count(x => x.Payload.Number("message_thread_id") == 30) == 1 && posts.Single(x => x.Payload.Number("message_thread_id") == 30).Payload.Text("text")!.Contains("Понедельник, держитесь"),
                    "Morning summary missing or duplicated");
                Assert(posts.Count(x => x.Payload.Number("message_thread_id") == ac.ChatTopicId) == 2, "Morning drop not split into two chat messages");
                sent.Clear();
                await Tick("2026-12-01T08:30:00Z"); // 11:30 MSK: three and a half hours late
                Assert(!sent.Any(x => x.Method == "sendMessage") && st.Get("ambient:morning:2026-12-01") == "skipped", "Late morning not skipped");

                // A confirmed deadline gets AI wake-ups; invalid points are dropped; due ones fire into the chat.
                var draft = Deadlines.Resolve(ac, new("create", SubjectId: "siaod", Work: "Практика №2", Date: "2026-12-07"), null, [], 1, "t", DateTimeOffset.Parse("2026-12-01T09:00:00Z"));
                st.SaveDraft(draft); var dl = st.Confirm(1, draft.Token, DateTimeOffset.Parse("2026-12-01T09:00:00Z"));
                wakePlan = Json.Write(new { wakeups = new object[] {
                    new { at = "2026-12-04 19:00", reason = "за три дня" }, new { at = "2026-12-04 19:30", reason = "слишком близко" },
                    new { at = "2026-12-05 03:00", reason = "ночью" }, new { at = "2026-12-07 16:00", reason = "за два часа" },
                    new { at = "2026-12-08 10:00", reason = "после сдачи" }, new { at = "2026-11-01 10:00", reason = "в прошлом" } } });
                sent.Clear();
                await Tick("2026-12-01T09:00:00Z");
                var wplan = st.Wakeups(dl.Id);
                Assert(wplan.Count == 2 && wplan.All(x => x.Source == "ai") && wplan[0].Reason == "за три дня" && wplan[1].Reason == "за два часа", "Wake-up plan not validated: " + Json.Write(wplan));
                await Tick("2026-12-04T16:01:00Z");
                Assert(sent.Any(x => x.Method == "sendMessage" && x.Payload.Number("message_thread_id") == 30 && x.Payload.Text("text")!.Contains("<b>шевелитесь</b>")), "Wake-up did not fire into «Сводка»");
                Assert(aiRequests.Any(r => r.Contains("обязательно тегни")), "Tagging rule missing");
                Assert(st.Wakeups(dl.Id).First().Status == "fired", "Wake-up fired twice risk");
                Assert(aiRequests.Any(r => r.Contains("за три дня") && r.Contains("time_left")), "Wake-up context lacks reason/time left");

                // Moving the deadline re-plans; cancelling drops pending wake-ups.
                var move = Deadlines.Resolve(ac, new("update", dl.Id, Date: "2026-12-14"), null, st.AllDeadlines(), 1, "m", DateTimeOffset.Parse("2026-12-04T17:00:00Z"));
                st.SaveDraft(move); st.Confirm(1, move.Token, DateTimeOffset.Parse("2026-12-04T17:00:00Z"));
                wakePlan = Json.Write(new { wakeups = new[] { new { at = "2026-12-12 12:00", reason = "новая дата" } } });
                await Tick("2026-12-04T17:01:00Z");
                Assert(st.Wakeups(dl.Id).Count(x => x.Status == "pending") == 1 && st.Wakeups(dl.Id).Single(x => x.Status == "pending").Reason == "новая дата", "Move did not re-plan");
                var cancel = Deadlines.Resolve(ac, new("cancel", dl.Id), null, st.AllDeadlines(), 1, "c", DateTimeOffset.Parse("2026-12-04T17:00:00Z"));
                st.SaveDraft(cancel); st.Confirm(1, cancel.Token, DateTimeOffset.Parse("2026-12-04T17:00:00Z"));
                await Tick("2026-12-04T17:02:00Z");
                Assert(st.Wakeups(dl.Id).All(x => x.Status != "pending"), "Cancelled deadline keeps wake-ups");
                Assert(st.PendingDeliveries().All(x => !x.Key.StartsWith("morning:")), "Deadline-topic morning post must be replaced by the summary");

                // Spontaneous drop with no reason stays silent.
                ac.Ambient.SpontaneousPerDay = 1; ac.Ambient.SpontaneousChance = 1;
                st.Set("ambient:plan:2026-12-02", Json.Write(new[] { "12:00" }));
                st.Set("ambient:morning:2026-12-02", "done"); st.Set("ambient:summary:2026-12-02", "done");
                sent.Clear(); await Tick("2026-12-02T09:05:00Z");
                Assert(!sent.Any(x => x.Method == "sendMessage") && st.Get("ambient:spont:2026-12-02:0") == "done", "SKIP was posted");

                // Owner triggers a post manually: preview card, then send.
                sent.Clear();
                await Upd(El(new { message = Msg(1, 0, 900, 1, "/post сходка в 15:00") }));
                var pcard = sent.Single(x => x.Method == "sendMessage").Payload;
                Assert(pcard.Text("text")!.Contains("Сходка в 15:00") && pcard.Field("reply_markup").ValueKind == JsonValueKind.Object, "Post preview missing");
                Assert(aiRequests.Last().Contains("сходка в 15:00"), "Idea not passed to the model");
                var ptoken = Json.Read<Dictionary<string, JsonElement>>(st.Get("postdraft:1")!)["token"].GetString();
                sent.Clear();
                await Upd(El(new { callback_query = new { id = "p", from = new { id = 1 }, data = "apost:" + ptoken, message = new { message_id = 901, chat = new { id = 1 } } } }));
                Assert(sent.Any(x => x.Method == "sendMessage" && x.Payload.Number("chat_id") == ac.GroupId && x.Payload.Number("message_thread_id") == 30 && x.Payload.Text("text") == "Сходка в 15:00, все к А-13"), "Manual post not sent to «Сводка»");
                sent.Clear();
                await Upd(El(new { message = Msg(2, 0, 902, 2, "/post спам") }));
                Assert(!sent.Single(x => x.Method == "sendMessage").Payload.Text("text")!.Contains("Черновик"), "Head could trigger posts");
                // Warm-up: the summary is prepared in advance, edited, and exactly that text is posted later without a new AI call.
                var warm = typeof(BotWorker).GetMethod("Warm", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var prep = await (Task<BotWorker.Prep?>)warm.Invoke(w, ["ambient:summary:2026-12-03", CancellationToken.None])!;
                Assert(prep!.Text.Contains("Понедельник, держитесь") && prep.Source == "ai", "Warm-up did not prepare the summary");
                w.SavePrep("ambient:summary:2026-12-03", "Среда сегодня, отдыхайте", "manual");
                Assert(w.Preview("ambient:summary:2026-12-03", "Среда сегодня, отдыхайте")!.Contains("Среда сегодня, отдыхайте") &&
                    w.Preview("ambient:summary:2026-12-03", "x")!.Contains("📅"), "Preview must show the full composed post");
                st.Set("ambient:morning:2026-12-03", "done");
                var callsBefore = aiRequests.Count;
                sent.Clear(); await Tick("2026-12-03T05:00:30Z");
                var summaryPost = sent.Single(x => x.Method == "sendMessage" && x.Payload.Number("message_thread_id") == 30).Payload.Text("text")!;
                Assert(summaryPost.StartsWith("Среда сегодня, отдыхайте") && summaryPost.Contains("📅"), "Prepared summary not used: " + summaryPost);
                Assert(aiRequests.Count == callsBefore && w.Prepared("ambient:summary:2026-12-03") is null, "Prepared post regenerated or not consumed");
                // A cancelled morning post is never sent.
                st.Set("ambient:summary:2026-12-05", "cancelled"); st.Set("ambient:morning:2026-12-05", "cancelled");
                sent.Clear(); await Tick("2026-12-05T05:01:00Z");
                Assert(!sent.Any(x => x.Method == "sendMessage" && x.Payload.Number("message_thread_id") == 30), "Cancelled summary was sent");
                count++; Console.WriteLine("PASS ambient: summary+morning once, late skip, AI wake-ups validated/fired/re-planned/cancelled, SKIP, manual /post, warm-up, cancel");
            }

            // ---- A cancelled deadline whose card was deleted by people: one notification, not two ----
            {
                var pc = make("cancel-deleted.db");
                using var ps = new BotStore(pc);
                var calls = new List<(string Method, string Text)>();
                var tgDel = new HttpClient(new Fake(async request =>
                {
                    var method = request.RequestUri!.Segments[^1];
                    var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                    lock (calls) calls.Add((method, body.Text("text") ?? ""));
                    if (method == "editMessageText") return new { ok = false, error_code = 400, description = "Bad Request: message to edit not found" };
                    if (method == "sendMessage") return new { ok = true, result = new { message_id = 7000 + calls.Count, date = 1, chat = new { id = pc.GroupId, type = "supergroup" },
                        message_thread_id = body.Number("message_thread_id"), text = body.Text("text") } };
                    return new { ok = true, result = (object)true };
                })) { BaseAddress = new Uri("https://telegram.test/") };
                var pw = new BotWorker(pc, ps, new TelegramClient(tgDel, "t"), new OpenRouterClient(aiHttp, pc, null), NullLogger<BotWorker>.Instance);
                var publish = typeof(BotWorker).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var realNow = DateTimeOffset.UtcNow;
                var day = DateOnly.FromDateTime(pc.LocalNow(realNow)).AddDays(5).ToString("yyyy-MM-dd");
                var dd = Deadlines.Resolve(pc, new("create", SubjectId: "siaod", Work: "Практика №9", Date: day, Period: 6, Room: "А412"), null, [], 1, "t", realNow);
                ps.SaveDraft(dd with { Intro = "Свежак" }); var saved = ps.Confirm(1, dd.Token, realNow);
                await (Task)publish.Invoke(pw, [CancellationToken.None])!;
                Assert(calls.Count(x => x.Method == "sendMessage") == 1 && calls[0].Text.StartsWith("Свежак") && calls[0].Text.Contains("<blockquote>"), "Card not published as HTML");
                calls.Clear();
                var cd = Deadlines.Resolve(pc, new("cancel", saved.Id), null, ps.AllDeadlines(), 1, "c", realNow);
                ps.SaveDraft(cd with { Intro = "Отбой" }); ps.Confirm(1, cd.Token, realNow);
                ps.Set("post_probe:deadline:" + saved.Id, "0");
                await (Task)publish.Invoke(pw, [CancellationToken.None])!;
                var sends = calls.Where(x => x.Method == "sendMessage").ToList();
                Assert(sends.Count == 1 && sends[0].Text.Contains("❌ <b>Отмена</b>"), "Cancel produced duplicates: " + string.Join(" | ", sends.Select(x => x.Text)));
                count++; Console.WriteLine("PASS cancelled deadline with a deleted card: card not resurrected, one news post");
            }

            // ---- Web admin over a real local HttpListener ----
            var port = 18000 + Random.Shared.Next(1000);
            c.Web = new WebSettings { Enabled = true, Listen = $"http://127.0.0.1:{port}/", PublicUrl = $"http://127.0.0.1:{port}", AllowedNetworks = ["127.0.0.0/8"] };
            using var cts = new CancellationTokenSource();
            var web = new AdminWeb(c, s, worker, NullLogger<AdminWeb>.Instance);
            await web.StartAsync(cts.Token);
            try
            {
                await Task.Delay(300);
                using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer(), AllowAutoRedirect = true, UseProxy = false };
                using var browser = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                Assert((await browser.GetAsync("api/registry")).StatusCode == HttpStatusCode.Unauthorized, "API open without session");
                Assert((await browser.GetStringAsync("")).Contains("/web"), "Login page missing");
                var link = s.CreateWebToken(2, "login", TimeSpan.FromMinutes(5));
                var home = await browser.GetStringAsync("login?t=" + link);
                Assert(!home.Contains("недействительна"), "Login failed");
                Assert((await browser.GetAsync("login?t=" + link)).StatusCode == HttpStatusCode.Unauthorized, "Login link reusable");
                // Owner secrets exist before the head opens the admin.
                s.CommitRegistry(1, "test", null, cur => RegistryOps.ApplyAll(cur, [Op(new { op = "subject_upsert", id = "java", adminComment = "СЕКРЕТ-КОММЕНТ", addAliases = new[] { "СЕКРЕТ-СЛЕНГ" } }),
                    Op(new { op = "member_update", member = new { userId = 3 }, adminNote = "СЕКРЕТ-ЗАМЕТКА" }), Op(new { op = "knowledge_set", text = "СЕКРЕТ-ЗНАНИЯ" })], c.OwnerId, true));
                var rawState = await browser.GetStringAsync("api/registry");
                Assert(!rawState.Contains("СЕКРЕТ"), "Owner-only data visible to the head");
                Assert((await browser.GetAsync("api/ai")).StatusCode == HttpStatusCode.Forbidden, "AI log with system prompt visible to the head");
                var state = JsonDocument.Parse(rawState).RootElement;
                var version = state.Field("version").GetInt32();
                var registry = Json.Read<Registry>(state.Field("registry").GetRawText());
                registry.Lessons = registry.Lessons.Select(x => x.SubjectId == "java" ? x with { Room = "Г-999" } : x).ToArray();
                var body = new StringContent(Json.Write(new { baseVersion = version, registry }), Encoding.UTF8, "application/json");
                Assert((await browser.PostAsync("api/registry", body)).StatusCode == HttpStatusCode.Forbidden, "POST without CSRF header accepted");
                browser.DefaultRequestHeaders.Add("X-Vozduhan", "1");
                var dry = JsonDocument.Parse(await (await browser.PostAsync("api/registry?dry=1", new StringContent(Json.Write(new { baseVersion = version, registry }), Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement;
                Assert(dry.Field("ok").ValueKind == JsonValueKind.True && dry.Field("lines").GetArrayLength() == 1 && dry.Field("lines")[0].GetString()!.Contains("Г-999"), "Dry run diff wrong: " + dry.GetRawText());
                var save = await browser.PostAsync("api/registry", new StringContent(Json.Write(new { baseVersion = version, registry }), Encoding.UTF8, "application/json"));
                Assert(save.IsSuccessStatusCode && c.R.Lessons.Single(l => l.SubjectId == "java").Room == "Г-999", "Web save failed");
                Assert(c.R.Subject("java")!.AdminComment == "СЕКРЕТ-КОММЕНТ" && c.R.Subject("java")!.Aliases!.Contains("СЕКРЕТ-СЛЕНГ") &&
                    c.R.Members.Single(m => m.UserId == 3).AdminNote == "СЕКРЕТ-ЗАМЕТКА" && c.R.Knowledge == "СЕКРЕТ-ЗНАНИЯ", "Head save wiped owner secrets");
                Assert(!(await browser.GetStringAsync("api/export")).Contains("СЕКРЕТ"), "Export leaks secrets to the head");
                var stale = await browser.PostAsync("api/registry", new StringContent(Json.Write(new { baseVersion = version, registry }), Encoding.UTF8, "application/json"));
                Assert(stale.StatusCode == HttpStatusCode.Conflict, "Stale web save accepted");
                var week = JsonDocument.Parse(await browser.GetStringAsync("api/week?date=2026-11-30")).RootElement;
                Assert(week.Field("days").GetArrayLength() == 7, "Week view broken");
                var planResp = await browser.GetAsync("api/plan");
                Assert(planResp.IsSuccessStatusCode && JsonDocument.Parse(await planResp.Content.ReadAsStringAsync()).RootElement.Field("days").GetArrayLength() == 7, "Plan API broken: " + await planResp.Content.ReadAsStringAsync());
                var chats = JsonDocument.Parse(await browser.GetStringAsync("api/chats")).RootElement;
                Assert(chats.Field("chats").GetArrayLength() > 0, "Chat list empty");
                var dl = await browser.PostAsync("api/deadline", new StringContent(Json.Write(new { action = "create", subjectId = "siaod", work = "Практика №3", date = "2026-12-07" }), Encoding.UTF8, "application/json"));
                Assert(dl.IsSuccessStatusCode && s.AllDeadlines().Any(d => d.Work == "Практика №3" && d.Room == "А412"), "Web deadline failed");
                Assert((await browser.GetStringAsync("")).Contains("<"), "Admin page not served");
                count++; Console.WriteLine("PASS web admin: VPN allowlist, one-time login, CSRF header, dry run, save, conflict, week, deadlines");
            }
            finally { cts.Cancel(); await web.StopAsync(CancellationToken.None); }
        }
        return count;
    }

    sealed class FakeStatus(Func<HttpRequestMessage, Task<(HttpStatusCode, object)>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = await response(request);
            return new(code) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") };
        }
    }
    sealed class Fake(Func<HttpRequestMessage, Task<object>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(await response(request)), Encoding.UTF8, "application/json") };
    }
}
