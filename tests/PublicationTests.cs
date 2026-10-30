using System.Reflection;
using System.Text.Json;
using Vozduhan.AI;

/// <summary>Block 6: one table for the bot's own posts, the developer's preview window (E2), deadline mentions and anti-dup (E1).</summary>
static partial class WakeupTests
{
    static async Task RunPublications(Func<string, BotConfig> make)
    {
        const long Owner = 1;
        List<(string Method, JsonElement Body)> Messages(Env e) => e.Sent.Where(x => x.Method == "sendMessage").ToList();
        List<string> Group(Env e) => Messages(e).Where(x => x.Body.Number("chat_id") == e.C.GroupId).Select(x => x.Body.Text("text")!).ToList();
        List<JsonElement> Dm(Env e) => Messages(e).Where(x => x.Body.Number("chat_id") == Owner).Select(x => x.Body).ToList();
        List<string> Edits(Env e) => e.Sent.Where(x => x.Method == "editMessageText").Select(x => x.Body.Text("text")!).ToList();
        int MessageId(Env e, JsonElement body) => 1000 + e.Sent.FindIndex(x => x.Body.GetRawText() == body.GetRawText()) + 1;
        Task Process(Env e, object update) => (Task)typeof(BotWorker).GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.W, [JsonSerializer.SerializeToElement(update, Json.Options), CancellationToken.None])!;
        Task Press(Env e, long actor, int message, string data) => Process(e, new
        {
            callback_query = new { id = "cb" + Random.Shared.Next(), from = new { id = actor }, data, message = new { message_id = message, chat = new { id = actor } } }
        });
        Task Say(Env e, string text) => Process(e, new
        {
            message = new { message_id = Random.Shared.Next(10, 100000), date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat = new { id = Owner, type = "private" },
                from = new { id = Owner, first_name = "Dev" }, text }
        });
        Env Previewing(string file)
        {
            var e = new Env(make, file);
            e.C.Features.PublicationModes = new(); // the default: auto_preview everywhere
            return e;
        }

        await Check("publications: preview 30 min before with 4 buttons, no answer — sent at due once", async () =>
        {
            using var e = Previewing("pub-preview.db");
            var due = T.AddHours(1);
            var key = "wake:" + e.S.AddWakeup(0, due.ToUnixTimeSeconds(), "сходка в 15:00", "post", Owner);
            await e.Tick(due.AddMinutes(-31));
            Assert(Messages(e).Count == 0 && e.Pub(key).Status == "planned" && e.Pub(key).Mode == BotWorker.AutoPreview, "Preview too early");
            await e.Tick(due.AddMinutes(-30));
            var preview = Dm(e).Single();
            var buttons = preview.Field("reply_markup").Field("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray()).ToList();
            Assert(buttons.Select(b => b.Text("text")).SequenceEqual(["✏️ Править", "🔄 Другой вариант", "🚫 Не отправлять", "✅ Отправить сейчас"]) &&
                preview.Text("text")!.Contains("шевелитесь") && e.Pub(key) is { Status: "ready" } p && p.Preview == MessageId(e, preview), "Preview wrong: " + preview);
            await e.Tick(due.AddMinutes(-10));
            Assert(Messages(e).Count == 1 && Group(e).Count == 0, "Sent before due");
            await e.Tick(due); await e.Tick(due.AddMinutes(1));
            Assert(Group(e).SequenceEqual(["Гайс, сдаём СиАОД, шевелитесь"]) && e.Pub(key).Status == "sent" && e.Wake(long.Parse(key[5..])).Status == "fired" &&
                Edits(e).Any(x => x.StartsWith("✅ Отправлено")), "Not sent once at due");
        });

        await Check("publications: 🚫 stops the post; someone else's press or an old preview is ignored", async () =>
        {
            using var e = Previewing("pub-cancel.db");
            var due = T.AddHours(1);
            var key = "wake:" + e.S.AddWakeup(0, due.ToUnixTimeSeconds(), "сходка", "post", Owner);
            await e.Tick(due.AddMinutes(-30));
            var p = e.Pub(key);
            await Press(e, 2, p.Preview, $"pub:c:{p.Id}");
            await Press(e, Owner, p.Preview + 50, $"pub:c:{p.Id}");
            Assert(e.Pub(key).Status == "ready", "Foreign or stale press acted");
            await Press(e, Owner, p.Preview, $"pub:c:{p.Id}");
            await e.Tick(due); await e.Tick(due.AddMinutes(30));
            Assert(Group(e).Count == 0 && e.Pub(key).Status == "cancelled" && Edits(e).Any(x => x.StartsWith("🚫 Не отправлю")), "Cancelled post went out");
        });

        await Check("publications: ✏️ then the developer's message becomes the text; 🔄 writes another variant", async () =>
        {
            using var e = Previewing("pub-edit.db");
            var due = T.AddHours(1);
            var key = "wake:" + e.S.AddWakeup(0, due.ToUnixTimeSeconds(), "сходка", "post", Owner);
            var n = 0;
            e.Text = _ => Task.FromResult($"Вариант {++n}, гайс");
            await e.Tick(due.AddMinutes(-30));
            var first = e.Pub(key);
            await Press(e, Owner, first.Preview, $"pub:r:{first.Id}");
            var second = e.Pub(key);
            Assert(second.Text == "Вариант 2, гайс" && second.Preview != first.Preview && Edits(e).Any(x => x.StartsWith("🔄 Заменено")) &&
                Dm(e).Last().Text("text")!.Contains("Вариант 2") && e.Ai.Last().Contains("previous_variant") && e.Ai.Last().Contains("Вариант 1"), "🔄 did not replace the text");
            await Press(e, Owner, second.Preview, $"pub:e:{second.Id}");
            Assert(Dm(e).Last().Text("text")!.StartsWith("✏️ Пришли новый текст"), "No edit prompt");
            await Say(e, "Сходка в 15:00 у А-13, приходите");
            var third = e.Pub(key);
            Assert(third is { Source: "manual", Text: "Сходка в 15:00 у А-13, приходите", Status: "ready" } && third.Preview != second.Preview &&
                Edits(e).Any(x => x.StartsWith("✏️ Заменено твоим текстом")), "Edited text not taken");
            await e.Tick(due);
            Assert(Group(e).SequenceEqual(["Сходка в 15:00 у А-13, приходите"]), "Developer's text not sent: " + string.Join(" | ", Group(e)));
        });

        await Check("publications: ✅ sends at once; changed facts bring a new preview, close to due a fresh text without one", async () =>
        {
            using var e = Previewing("pub-facts.db");
            var d = e.Add("2026-12-14");
            var due = T.AddHours(1);
            e.Text = body => Task.FromResult(body.Contains("Б777") ? "СиАОД теперь в Б777" : body.Contains("В888") ? "СиАОД теперь в В888" : "СиАОД в А412");
            var key = "wake:" + e.S.AddWakeup(d.Id, due.ToUnixTimeSeconds(), "напомнить", "manual");
            await e.Tick(due.AddMinutes(-30));
            var before = e.Pub(key);
            d = e.Change(d, new(Room: "Б777"));
            await e.Tick(due.AddMinutes(-20));
            Assert(e.Pub(key) is { Text: "СиАОД теперь в Б777", Status: "ready" } p && p.Preview != before.Preview && Edits(e).Any(x => x.Contains("факты изменились")), "No new preview after a change");
            d = e.Change(d, new(Room: "В888"));
            var previews = Dm(e).Count;
            await e.Tick(due.AddMinutes(-3));
            Assert(Dm(e).Count == previews, "Regenerated with less than 5 minutes left");
            await e.Tick(due);
            Assert(Group(e).SequenceEqual(["СиАОД теперь в В888"]) && Dm(e).Count == previews && e.Log.Lines.Any(x => x.Contains(key) && x.Contains("facts changed")), "Stale text sent or new preview");

            var other = "wake:" + e.S.AddWakeup(0, due.AddHours(3).ToUnixTimeSeconds(), "пост", "post", Owner);
            await e.Tick(due.AddHours(3).AddMinutes(-30));
            var o = e.Pub(other);
            await Press(e, Owner, o.Preview, $"pub:s:{o.Id}");
            Assert(Group(e).Count == 2 && e.Pub(other).Status == "sent", "✅ did not send at once");
            await e.Tick(due.AddHours(3));
            Assert(Group(e).Count == 2, "✅ post sent twice");
        });

        await Check("publications: manual waits for ✅, skipped 2 h after due", async () =>
        {
            using var e = Previewing("pub-manual.db");
            e.C.Features.PublicationModes = new() { ["post"] = BotWorker.ManualMode };
            var due = T.AddHours(1);
            var key = "wake:" + e.S.AddWakeup(0, due.ToUnixTimeSeconds(), "идея", "post", Owner);
            await e.Tick(due.AddMinutes(-30));
            Assert(Dm(e).Single().Text("text")!.Contains("только после ✅"), "Manual preview wrong");
            foreach (var t in new[] { due, due.AddHours(1), due.AddHours(2) }) await e.Tick(t);
            Assert(Group(e).Count == 0 && e.Pub(key).Status == "ready", "Manual post went without ✅");
            await e.Tick(due.AddHours(2).AddSeconds(1));
            Assert(Group(e).Count == 0 && e.Pub(key) is { Status: "skipped", Note: "не подтверждено вовремя" }, "Manual not skipped");
        });

        await Check("publications: a restart repeats neither the preview nor the post", async () =>
        {
            using var e = Previewing("pub-restart.db");
            var due = T.AddHours(1);
            var key = "wake:" + e.S.AddWakeup(0, due.ToUnixTimeSeconds(), "сходка", "post", Owner);
            await e.Tick(due.AddMinutes(-30));
            e.Restart();
            await e.Tick(due.AddMinutes(-29)); await e.Tick(due.AddMinutes(-1));
            Assert(Dm(e).Count == 1, "Preview repeated after restart");
            await e.Tick(due);
            e.Restart();
            await e.Tick(due.AddMinutes(1));
            Assert(Group(e).Count == 1 && Dm(e).Count == 1 && e.Pub(key).Status == "sent", "Post repeated after restart");
        });

        await Check("publications: an upgrade takes over finished events and warmed texts of the old storage", async () =>
        {
            using var e = new Env(make, "pub-upgrade.db");
            e.C.SummaryTopicId = 30; e.C.Ambient.MorningTime = e.C.Ambient.SummaryTime = e.C.LocalNow(T).ToString("HH:mm");
            var date = e.C.LocalNow(T).ToString("yyyy-MM-dd");
            e.S.Set($"ambient:summary:{date}", "done");
            e.S.Set($"prep:ambient:morning:{date}", Json.Write(new { text = "Подготовленное утро", at = 1, source = "manual" }));
            await e.Tick(T);
            Assert(e.Pub($"ambient:summary:{date}").Status == "sent" && Group(e).SequenceEqual(["Подготовленное утро"]) && e.Ai.Count == 0, "Old storage not taken over");
        });

        await Check("anti-dup: two posts 5 min apart — the second moves by 15; a moved post past Late is skipped", async () =>
        {
            using var e = new Env(make, "dup-gap.db");
            var a = "wake:" + e.S.AddWakeup(0, T.ToUnixTimeSeconds(), "первый", "post", Owner);
            var b = "wake:" + e.S.AddWakeup(0, T.AddMinutes(5).ToUnixTimeSeconds(), "второй", "post", Owner);
            await e.Tick(T); await e.Tick(T.AddMinutes(5));
            Assert(Group(e).Count == 1 && e.Pub(b) is { Status: "planned" } moved && moved.Due == T.AddMinutes(15).ToUnixTimeSeconds() && moved.FirstDue == T.AddMinutes(5).ToUnixTimeSeconds() &&
                moved.Note!.StartsWith("сдвинуто на"), "Second post not moved: " + Json.Write(e.Pub(b)));
            await e.Tick(T.AddMinutes(15));
            Assert(Group(e).Count == 2 && e.Pub(a).Status == "sent" && e.Pub(b).Status == "sent", "Moved post not sent");
            // Moved long ago already: one more move would put it past Late from its first due, so it is skipped.
            var late = "wake:" + e.S.AddWakeup(0, T.AddMinutes(-105).ToUnixTimeSeconds(), "поздний", "post", Owner);
            e.S.EnsurePublication(late, "post", T.AddMinutes(-105).ToUnixTimeSeconds(), 0, BotWorker.AutoMode);
            e.S.MovePublicationDue(late, T.AddMinutes(16).ToUnixTimeSeconds(), "сдвинуто раньше");
            await e.Tick(T.AddMinutes(16));
            Assert(Group(e).Count == 2 && e.Pub(late) is { Status: "skipped" } sk && sk.Note!.StartsWith("слишком часто"), "Post past Late not skipped: " + Json.Write(e.Pub(late)));
        });

        await Check("anti-dup: a wake-up 30 min after the evening reminder with its deadline moves or stays silent; brief mentions do not count", async () =>
        {
            using var e = new Env(make, "dup-cooldown.db");
            e.C.Ambient.EveReminders = true; e.C.Ambient.EveReminderTime = "20:00";
            var d = e.Add("2026-12-14");
            var eveAt = e.C.Utc(new DateTime(2026, 12, 13, 20, 0, 0));
            var moved = "wake:" + e.S.AddWakeup(d.Id, eveAt.AddMinutes(30).ToUnixTimeSeconds(), "через полчаса", "manual");
            await e.Tick(eveAt); await e.Tick(eveAt.AddMinutes(30));
            Assert(e.S.Mentions(0).Any(m => m.Deadline == d.Id && m.Kind == "full" && m.Key == "ambient:eve:2026-12-13"), "Evening reminder not logged as full");
            Assert(e.Pub(moved) is { Status: "planned" } m1 && m1.Due == eveAt.AddMinutes(90).ToUnixTimeSeconds() && m1.Note!.Contains("уже напоминал в 20:00"), "Wake-up not moved: " + Json.Write(e.Pub(moved)));
            await e.Tick(eveAt.AddMinutes(90));
            Assert(e.Pub(moved).Status == "sent" && Group(e).Count == 2, "Moved wake-up not sent");

            var start = e.C.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start)));
            var first = "wake:" + e.S.AddWakeup(d.Id, start.AddMinutes(-80).ToUnixTimeSeconds(), "перед парой", "manual");
            var silent = "wake:" + e.S.AddWakeup(d.Id, start.AddMinutes(-60).ToUnixTimeSeconds(), "ещё раз", "manual");
            await e.Tick(start.AddMinutes(-80)); await e.Tick(start.AddMinutes(-60));
            Assert(e.Pub(first).Status == "sent" && e.Pub(silent) is { Status: "silent" } s && s.Note!.StartsWith("уже напоминал в") &&
                e.Wake(long.Parse(silent[5..])).Status == "silent", "Wake-up close to the pair not silenced: " + Json.Write(e.Pub(silent)));

            using var b = new Env(make, "dup-brief.db");
            var x = b.Add("2026-12-14");
            var day = new DateOnly(2026, 12, 12);
            b.C.Ambient.SpontaneousPerDay = 1;
            b.S.Set($"ambient:plan:{day:yyyy-MM-dd}", Json.Write(new[] { "13:00" }));
            var noon = b.C.Utc(day.ToDateTime(new TimeOnly(13, 0)));
            var after = "wake:" + b.S.AddWakeup(x.Id, noon.AddMinutes(20).ToUnixTimeSeconds(), "напомнить", "manual");
            b.Text = body => Task.FromResult(body.Contains("ЗАДАЧА: ты сам решил") ? "Кто уже сделал СиАОД? Пора бы" : "Гайс, СиАОД через два дня");
            await b.Tick(noon); await b.Tick(noon.AddMinutes(20));
            Assert(b.S.Mentions(0).Any(m => m.Deadline == x.Id && m.Kind == "brief") && b.Pub(after).Status == "sent" && Group(b).Count == 2, "Brief mention blocked a wake-up");
            Assert(Context(b.Ai.Last()).Field("recent_deadline_mentions").EnumerateArray().Any(m => m.Text("kind") == "brief" && m.Text("deadline")!.Contains("#" + x.Id)),
                "Ambient context lacks recent_deadline_mentions");
        });

        await Check("anti-dup: change news and cards are never held back or logged as mentions", async () =>
        {
            using var e = new Env(make, "dup-news.db");
            var d = e.Add("2026-12-14");
            var now = DateTimeOffset.UtcNow;
            e.S.AddWakeup(0, now.AddMinutes(-1).ToUnixTimeSeconds(), "пост", "post", Owner);
            await e.Tick(now);
            Task Publish() => (Task)typeof(BotWorker).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e.W, [CancellationToken.None])!;
            await Publish();
            e.S.ConfirmDirect(1, Deadlines.Resolve(e.C, new("update", d.Id, Room: "Б777"), null, e.S.AllDeadlines(), 1, "t", now), now);
            var before = e.Sent.Count;
            await Publish();
            Assert(Group(e).Count >= 2 && e.Sent.Skip(before).Any(x => x.Body.Number("message_thread_id") == e.C.DeadlinesTopicId), "Deadline topic held back after a bot post");
            Assert(e.S.Mentions(0).All(m => m.Key.StartsWith("wake:")), "Cards or news logged as mentions");
        });
    }
}
