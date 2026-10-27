using System.Net;
using System.Text;

namespace Vozduhan.AI;

/// <summary>Readable Telegram HTML for schedule commands. Everything user-provided is escaped.</summary>
public static class ScheduleHtml
{
    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    static readonly string[] DayRu = ["", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];
    static readonly string[] DayCap = ["", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
    static readonly string[] Digits = ["0️⃣", "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣"];
    static string Num(int n) => n is >= 0 and <= 9 ? Digits[n] : n + ".";

    static string WeekInfo(Registry r, DateOnly d) => Schedule.InTerm(r, d)
        ? $"{Schedule.Week(r, d)}-я неделя, {Schedule.ParityRu(Schedule.Parity(Schedule.Week(r, d)))}" : "вне семестра";

    /// <summary>Campus shown once in the header when the whole day is in one place.</summary>
    static string? DayCampus(List<Slot> live) => live.Select(s => s.Campus).Distinct().Count() == 1 ? live[0].Campus : null;

    static string Room(Slot s) => s.Online
        ? "💻 дистанционно" + (string.IsNullOrWhiteSpace(s.Room) ? "" : $" · <code>{H(s.Room)}</code>")
        : string.IsNullOrWhiteSpace(s.Room) ? "" : $"🚪 <code>{H(s.Room)}</code>";

    static string Status(Slot s) => s.Status switch
    {
        "cancelled" => " ❌ <b>отменена</b>",
        "changed" => " 🔄 <b>изменено</b>",
        "added" => " ➕ <b>разово</b>",
        _ => ""
    };

    /// <summary>One lesson as a small card: number and time, name, room and teacher.</summary>
    static string Card(Slot s, bool showCampus)
    {
        var name = s.Status == "cancelled" ? $"<s>{H(s.Subject)}</s>" : $"<b>{H(s.Subject)}</b>";
        var sb = new StringBuilder($"{Num(s.Period)} <b>{s.Start}–{s.End}</b> · <i>{Schedule.KindLong(s.Kind)}</i>{Status(s)}\n{name}");
        var details = new[] { Room(s), string.IsNullOrWhiteSpace(s.Teacher) ? "" : "👤 " + H(s.Teacher), showCampus && !string.IsNullOrWhiteSpace(s.Campus) ? "📍 " + H(s.Campus) : "" }
            .Where(x => x.Length > 0).ToArray();
        if (details.Length > 0) sb.Append('\n').Append(string.Join(" · ", details));
        if (!string.IsNullOrWhiteSpace(s.Note)) sb.Append($"\n<i>{H(s.Note)}</i>");
        return sb.ToString();
    }

    public static string Day(Registry r, DateOnly d, string? label = null)
    {
        var slots = Schedule.Day(r, d);
        var live = slots.Where(s => s.Status != "cancelled").ToList();
        var campus = live.Count > 0 ? DayCampus(live) : null;
        var title = (label is null ? "" : label + " · ") + $"{DayRu[Schedule.Dow(d)]}, {d:dd.MM}";
        var sb = new StringBuilder($"📅 <b>{H(Schedule.Capital(title))}</b>\n<i>{WeekInfo(r, d)}</i>");
        if (campus is not null) sb.Append($" · 📍 {H(campus)}");
        if (slots.Count == 0) return sb.Append("\n\nПар нет — отдыхаем 😌").ToString();
        if (live.Count > 0)
        {
            var first = live.Min(s => s.Start); var last = live.Max(s => s.End);
            sb.Append($"\n{live.Count} {Pairs(live.Count)}, {first}–{last}");
        }
        foreach (var s in slots) sb.Append("\n\n").Append(Card(s, campus is null));
        return sb.ToString();
    }

    public static string Week(Registry r, DateOnly anyDay, DateOnly today, bool commands = true)
    {
        var monday = anyDay.AddDays(1 - Schedule.Dow(anyDay));
        var sb = new StringBuilder($"🗓 <b>Неделя {monday:dd.MM}–{monday.AddDays(6):dd.MM}</b>\n<i>{WeekInfo(r, monday)}</i>");
        for (var i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            var slots = Schedule.Day(r, d);
            var live = slots.Where(s => s.Status != "cancelled").ToList();
            var campus = live.Count > 0 ? DayCampus(live) : null;
            sb.Append($"\n\n{(d == today ? "👉 " : "")}<b>{DayCap[Schedule.Dow(d)]} {d:dd.MM}</b>");
            if (campus is not null && campus != r.DefaultCampus) sb.Append($" · 📍 {H(campus)}");
            if (slots.Count == 0) { sb.Append(" — выходной"); continue; }
            sb.Append("\n<blockquote>");
            sb.Append(string.Join("\n", slots.Select(s =>
            {
                var name = H(r.Subject(s.SubjectId)?.ShortName ?? s.Subject);
                var room = s.Online ? "дист." : H(s.Room);
                var line = $"{Num(s.Period)} {s.Start} · {(s.Status == "cancelled" ? $"<s>{name}</s>" : $"<b>{name}</b>")} · {Schedule.KindRu(s.Kind)}" +
                           (string.IsNullOrWhiteSpace(room) ? "" : $" · {room}");
                return line + s.Status switch { "cancelled" => " ❌", "changed" => " 🔄", "added" => " ➕", _ => "" };
            })));
            sb.Append("</blockquote>");
        }
        sb.Append(commands ? "\n<i>Подробно на день: /today, /tomorrow, /date 31.10</i>" : "\n<i>Подробно на день — спроси «что в среду?»</i>");
        return sb.ToString();
    }

    public static string Next(Registry r, DateTime local)
    {
        var now = Schedule.Now(r, local);
        var sb = new StringBuilder();
        if (now.Current is not null)
            sb.Append($"🟢 <b>Сейчас идёт</b> (до {now.Current.End})\n").Append(Card(now.Current, true)).Append("\n\n");
        if (now.Next is null) return sb.Append("Ближайших пар не вижу 🤷").ToString();
        var date = DateOnly.ParseExact(now.Next.Date, "yyyy-MM-dd");
        var today = DateOnly.FromDateTime(local);
        var when = date == today ? "сегодня" : date == today.AddDays(1) ? "завтра" : $"{DayRu[Schedule.Dow(date)]}, {date:dd.MM}";
        var left = now.MinutesToNext < 60 ? $"через {now.MinutesToNext} мин" : now.MinutesToNext < 24 * 60 ? $"через {now.MinutesToNext / 60} ч {now.MinutesToNext % 60} мин" : null;
        sb.Append($"⏭ <b>Следующая — {when}</b>" + (left is null ? "" : $" ({left})") + "\n").Append(Card(now.Next, true));
        return sb.ToString();
    }

    static string Pairs(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "пара",
        (2 or 3 or 4, not (12 or 13 or 14)) => "пары",
        _ => "пар"
    };
}
