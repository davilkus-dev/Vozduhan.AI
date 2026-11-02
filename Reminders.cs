using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// Personal reminders: the Router turns «напомни мне…» into a card (date, time, what about); at that moment the AI writes the text,
/// the bot tags the person in Воздуханство and writes to their DM if Telegram lets it.
/// </summary>
public sealed partial class BotWorker
{
    public const int RemindersPerPerson = 20, ReminderAboutMax = 300;
    public static readonly TimeSpan ReminderMissed = TimeSpan.FromHours(12);

    private Task<string?> AddReminder(CapabilityCall c, CancellationToken ct)
    {
        var day = (DateOnly)c.Args["date"]; var time = (TimeOnly)c.Args["time"];
        var about = Conversation.Limit(((string)c.Args["about"]).Trim(), ReminderAboutMax);
        var due = config.Utc(day.ToDateTime(time));
        var now = DateTimeOffset.UtcNow;
        if (due <= now.AddSeconds(30)) return Done("⏰ Это время уже прошло — на когда поставить?");
        if (store.PendingReminders(c.Actor) >= RemindersPerPerson) return Done($"⏰ У тебя уже {RemindersPerPerson} напоминаний в ожидании — дождись, пока часть сработает.");
        var member = config.FindMember(c.Actor);
        store.AddReminder(member?.Key, c.Actor, member?.ShortName ?? c.Message.Field("from").Text("first_name") ?? "кто-то", about, due.ToUnixTimeSeconds(),
            Conversation.Limit(c.Text, 1000), now.ToUnixTimeSeconds());
        var today = Today();
        var when = day == today ? "сегодня" : day == today.AddDays(1) ? "завтра" : $"{Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()} {day:dd.MM}";
        return Done($"⏰ Напомню {when} в {time:HH:mm}: «{H(about)}»" + (c.Place == Capabilities.Group ? " — тут и в личке (если ты мне там писал)." : "."));
    }

    /// <summary>Delivers due reminders; one stuck longer than ReminderMissed is given up.</summary>
    internal async Task ReminderTick(DateTimeOffset utc, CancellationToken ct)
    {
        var now = utc.ToUnixTimeSeconds();
        foreach (var r in store.DueReminders(now))
        {
            if (now - r.Due > ReminderMissed.TotalSeconds) { store.FinishReminder(r.Id, r.FloodId > 0 ? "sent" : "missed", now); continue; }
            try { await DeliverReminder(r, utc, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning("Reminder {Id} not delivered yet ({Type})", r.Id, Failure(e)); }
        }
    }

    private async Task DeliverReminder(Reminder r, DateTimeOffset utc, CancellationToken ct)
    {
        var member = config.R.Members.FirstOrDefault(m => m.Key is not null && m.Key == r.Member);
        string Local(long t) => config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(t)).ToString("yyyy-MM-dd HH:mm, dddd", CultureInfo.GetCultureInfo("ru-RU"));
        var text = r.Text;
        if (text is null)
        {
            try
            {
                text = TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(OpenRouterClient.Join(await ai.Remind(new
                {
                    person = new { name = member?.ShortName ?? r.Name, full_name = member?.FullName }, about = r.About, asked = r.Source,
                    set_at = Local(r.Created), due = Local(r.Due), now = Local(utc.ToUnixTimeSeconds()), late = utc.ToUnixTimeSeconds() - r.Due > 600
                }, ct, $"remind:{r.Id}"))));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Reminder {Id} text not generated, sending it plain ({Type})", r.Id, Failure(e));
                text = "Напоминаю: " + H(r.About);
            }
            store.SetReminderText(r.Id, text);
        }
        if (r.FloodId == 0)
        {
            var tag = member?.Username is { Length: > 0 } u ? "@" + u.TrimStart('@') : $"<a href=\"tg://user?id={r.UserId}\">{H(member?.ShortName ?? r.Name)}</a>";
            store.SetReminderFlood(r.Id, await SendReminder(config.GroupId, config.ChatTopicId, $"⏰ {tag}\n{text}", ct));
        }
        if (r.Dm == 0)
        {
            int dm;
            try { dm = await SendReminder(r.UserId, 0, "⏰ " + text, ct); }
            catch (TelegramError e) when (e.Code is 400 or 403) { dm = -1; } // never started the bot, blocked it, or no such chat
            store.SetReminderDm(r.Id, dm);
        }
        store.FinishReminder(r.Id, "sent", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private async Task<int> SendReminder(long chat, int topic, string html, CancellationToken ct)
    {
        JsonElement sent;
        try { sent = await telegram.Send(chat, topic, html, ct, html: true); }
        catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("parse", StringComparison.OrdinalIgnoreCase))
        { sent = await telegram.Send(chat, topic, TelegramHtml.Plain(html), ct); }
        store.Remember(sent.Field("chat").Number("id"), sent.TopicId(), (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
        return (int)sent.Number("message_id");
    }
}
