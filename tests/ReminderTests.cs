using System.Net;
using System.Reflection;
using System.Text.Json;
using Vozduhan.AI;

/// <summary>Personal reminders: the Router stores a card, unclear time is asked, delivery tags in Воздуханство and writes to DM.</summary>
static partial class WakeupTests
{
    static async Task RunReminders(Func<string, BotConfig> make)
    {
        Task Tick(RouterTests.Env e, DateTimeOffset utc) =>
            (Task)typeof(BotWorker).GetMethod("ReminderTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e.W, [utc, CancellationToken.None])!;
        DateOnly Tomorrow(RouterTests.Env e) => DateOnly.FromDateTime(e.C.LocalNow(DateTimeOffset.UtcNow)).AddDays(1);
        long Due(RouterTests.Env e, DateOnly d, int h, int m) => e.C.Utc(d.ToDateTime(new TimeOnly(h, m))).ToUnixTimeSeconds();

        await Check("reminders: «напомни мне завтра в 9» → one card (who, when, about), a confirmation; a past time is refused", async () =>
        {
            using var e = RouterTests.Env.Make(make, "remind-add.db");
            var d = Tomorrow(e);
            e.Routes.Enqueue(RouterTests.Route("action", "remind", new { date = RouterTests.Ru(d), time = "9:00", about = "сдать лабу по сиаоду" }, "🫡"));
            await e.Process(RouterTests.Group(3, 40, "напомни мне завтра в 9 сдать лабу по сиаоду"));
            var r = e.S.Reminders().Single();
            Assert(r is { UserId: 3, Status: "pending", About: "сдать лабу по сиаоду", FloodId: 0 } && r.Due == Due(e, d, 9, 0) && r.Source.Contains("напомни"), "Card wrong: " + Json.Write(r));
            Assert(e.LastText().Contains("Напомню завтра в 09:00") && e.LastText().Contains("в личке"), "Confirmation wrong: " + e.LastText());
            e.Routes.Enqueue(RouterTests.Route("action", "remind", new { date = RouterTests.Ru(d.AddDays(-2)), time = "10:00", about = "x" }));
            await e.Process(RouterTests.Group(3, 41, "напомни вчера"));
            Assert(e.S.Reminders().Count == 1 && e.LastText().Contains("уже прошло"), "Past time accepted");
        });

        await Check("reminders: no time → the Router's clarify is accepted and asked; the answer completes the card; a bad time is rejected", async () =>
        {
            using var e = RouterTests.Env.Make(make, "remind-ask.db");
            var d = Tomorrow(e);
            var cap = e.W.Catalog.Find("remind")!;
            Assert(!Capabilities.TryArgs(cap, RouterTests.El(new { date = RouterTests.Ru(d), time = "25:00", about = "x" }), d, out _, out _) &&
                !Capabilities.TryArgs(cap, RouterTests.El(new { date = RouterTests.Ru(d), about = "x" }), d, out _, out _) &&
                Capabilities.TryArgs(cap, RouterTests.El(new { date = RouterTests.Ru(d), about = "x" }), d, out _, out _, partial: true), "Time validation wrong");
            e.Routes.Enqueue(Json.Write(new { mode = "clarify", capability = "remind", args = new { date = RouterTests.Ru(d), about = "позвонить в деканат" }, question = "Во сколько напомнить?", reason = "т" }));
            await e.Process(RouterTests.Group(3, 50, "напомни завтра позвонить в деканат"));
            Assert(e.LastText() == "Во сколько напомнить?" && e.S.Reminders().Count == 0, "Unclear time not asked");
            e.Routes.Enqueue(RouterTests.Route("action", "remind", new { date = RouterTests.Ru(d), time = "18:30", about = "позвонить в деканат" }));
            await e.Process(RouterTests.Group(3, 51, "в 18:30"));
            Assert(e.S.Reminders().Single().Due == Due(e, d, 18, 30) && e.LastText().Contains("18:30"), "Answer did not complete the card");
        });

        await Check("reminders: due → AI text, tag in Воздуханство and DM; once only; DM closed → chat only; cancelled and stale are not sent", async () =>
        {
            using var e = RouterTests.Env.Make(make, "remind-send.db");
            e.Reply = kind => kind == "chat" ? (HttpStatusCode.OK, RouterTests.Answer("Лаба по сиаоду сама себя не сдаст, давай")) : null;
            var now = DateTimeOffset.UtcNow;
            var id = e.S.AddReminder(e.C.FindMember(3)!.Key, 3, "Студент", "сдать лабу", now.ToUnixTimeSeconds() - 30, "напомни сдать лабу", now.AddHours(-3).ToUnixTimeSeconds());
            var later = e.S.AddReminder(null, 3, "Студент", "потом", now.AddHours(1).ToUnixTimeSeconds(), "напомни", now.ToUnixTimeSeconds());
            await Tick(e, now);
            var sent = e.Calls("sendMessage").Select(x => x.Body).ToList();
            var flood = sent.Single(x => x.Number("chat_id") == e.C.GroupId);
            var dm = sent.Single(x => x.Number("chat_id") == 3);
            Assert(flood.Number("message_thread_id") == e.C.ChatTopicId && flood.Text("text")!.StartsWith("⏰ @member") && flood.Text("text")!.Contains("сама себя не сдаст"), "Flood wrong: " + flood);
            Assert(dm.Text("text")!.Contains("сама себя не сдаст") && !dm.Text("text")!.Contains("@member"), "DM wrong: " + dm);
            var ai = e.Asked("chat").Single();
            Assert(ai.User.Contains("сдать лабу") && ai.System.Contains("личное напоминание") && e.S.UsageReport(now, 1).ByKind.Any(k => k.Kind == "remind"), "AI not asked for the text");
            var r = e.S.Reminder(id)!;
            Assert(r is { Status: "sent", FloodId: > 0, Dm: > 0 } && e.S.Reminder(later)!.Status == "pending", "Statuses wrong");
            await Tick(e, now.AddSeconds(30));
            Assert(e.Calls("sendMessage").Count == 2, "Sent twice");

            e.Clear();
            e.Fail = (method, p) => method == "sendMessage" && p.Number("chat_id") == 3 ? 403 : null;
            var closed = e.S.AddReminder(null, 3, "Студент", "x", now.ToUnixTimeSeconds() - 5, "напомни", now.ToUnixTimeSeconds());
            var cancelled = e.S.AddReminder(null, 3, "Студент", "y", now.ToUnixTimeSeconds() - 5, "напомни", now.ToUnixTimeSeconds());
            var stale = e.S.AddReminder(null, 3, "Студент", "z", now.AddHours(-13).ToUnixTimeSeconds(), "напомни", now.AddDays(-1).ToUnixTimeSeconds());
            Assert(e.S.FinishReminder(cancelled, "cancelled", now.ToUnixTimeSeconds()) && !e.S.FinishReminder(cancelled, "cancelled", now.ToUnixTimeSeconds()), "Cancel wrong");
            await Tick(e, now);
            Assert(e.S.Reminder(closed) is { Status: "sent", FloodId: > 0, Dm: -1 } && e.S.Reminder(stale)!.Status == "missed" && e.S.Reminder(cancelled)!.Status == "cancelled", "Closed DM / stale / cancelled wrong");
            Assert(e.Calls("sendMessage").Count(x => x.Body.Number("chat_id") == e.C.GroupId) == 1, "Cancelled or stale reminder sent");
        });
    }
}
