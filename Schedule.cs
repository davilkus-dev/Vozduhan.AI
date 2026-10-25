using System.Globalization;
using System.Text;

namespace Vozduhan.AI;

/// <summary>One effective lesson on a concrete date after weekly rules and one-off changes are applied.</summary>
public sealed record Slot(string Date, int Period, string Start, string End, string SubjectId, string Subject, string Kind,
    string? Room, string? Teacher, string? Campus, bool Online, string? Note, string Status, string? LessonId, string? ChangeId)
{
    public string RoomText => Online ? (string.IsNullOrWhiteSpace(Room) ? "дистанционно" : Room + " (дистанционно)") : Room ?? "";
}

public static class Schedule
{
    public static readonly string[] DayNames = ["", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];
    public static readonly string[] DayShort = ["", "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС"];
    public static int Dow(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
    public static int Week(Registry r, DateOnly d) =>
        (int)Math.Floor((d.DayNumber - DateOnly.ParseExact(r.WeekOneMonday, "yyyy-MM-dd").DayNumber) / 7d) + 1;
    public static string Parity(int week) => Math.Abs(week % 2) == 1 ? "odd" : "even";
    public static string ParityRu(string parity) => parity == "odd" ? "нечётная" : "чётная";
    public static string KindRu(string kind) => kind switch { "lecture" => "ЛК", "lab" => "ЛАБ", _ => "ПР" };
    public static string KindLong(string kind) => kind switch { "lecture" => "лекция", "lab" => "лабораторная", _ => "практика" };
    public static string WeeksRu(string weeks) => weeks switch { "odd" => "нечётные", "even" => "чётные", _ => "все недели" };
    public static bool InTerm(Registry r, DateOnly d) =>
        d >= DateOnly.ParseExact(r.WeekOneMonday, "yyyy-MM-dd") && (r.TermEnd is null || d <= DateOnly.ParseExact(r.TermEnd, "yyyy-MM-dd"));

    public static bool Applies(Registry r, Lesson l, DateOnly d)
    {
        if (l.DayOfWeek != Dow(d) || !InTerm(r, d)) return false;
        if (l.From is not null && d < DateOnly.ParseExact(l.From, "yyyy-MM-dd")) return false;
        if (l.Until is not null && d > DateOnly.ParseExact(l.Until, "yyyy-MM-dd")) return false;
        return l.Weeks == "all" || l.Weeks == Parity(Week(r, d));
    }

    /// <summary>Effective lessons for a date, cancelled ones included with Status = "cancelled".</summary>
    public static List<Slot> Day(Registry r, DateOnly d)
    {
        var date = d.ToString("yyyy-MM-dd");
        var slots = r.Lessons.Where(l => Applies(r, l, d)).Select(l => FromLesson(r, l, date, "regular", null)).ToList();
        var changes = r.Changes.Where(c => c.Date == date)
            .OrderBy(c => Array.IndexOf(["dayoff", "cancel", "modify", "add"], c.Type)).ThenBy(c => int.TryParse(c.Id?[1..], out var id) ? id : 0).ThenBy(c => c.Id);
        foreach (var c in changes)
        {
            switch (c.Type)
            {
                case "dayoff":
                    for (var i = 0; i < slots.Count; i++) slots[i] = slots[i] with { Status = "cancelled", ChangeId = c.Id, Note = c.Note ?? "нет занятий" };
                    break;
                case "cancel":
                    for (var i = 0; i < slots.Count; i++)
                    {
                        var s = slots[i];
                        var hit = c.LessonId is not null ? s.LessonId == c.LessonId
                            : s.SubjectId == c.SubjectId && (c.Period is null || s.Period == c.Period) && (c.Kind is null || s.Kind == c.Kind);
                        if (hit) slots[i] = s with { Status = "cancelled", ChangeId = c.Id, Note = c.Note ?? s.Note };
                    }
                    break;
                case "modify":
                    for (var i = 0; i < slots.Count; i++)
                    {
                        var s = slots[i];
                        if (s.LessonId != c.LessonId || s.Status == "cancelled") continue;
                        var subject = c.SubjectId ?? s.SubjectId;
                        var period = c.Period ?? s.Period;
                        var bell = r.PeriodOf(period);
                        slots[i] = s with
                        {
                            SubjectId = subject, Subject = r.SubjectName(subject), Period = period,
                            Start = bell?.Start ?? s.Start, End = bell?.End ?? s.End, Kind = c.Kind ?? s.Kind,
                            Room = c.Room ?? s.Room, Teacher = c.Teacher ?? s.Teacher, Campus = c.Campus ?? s.Campus,
                            Online = c.Online ?? s.Online, Note = c.Note ?? s.Note, Status = "changed", ChangeId = c.Id
                        };
                    }
                    break;
                case "add":
                    var p = r.PeriodOf(c.Period ?? 0);
                    slots.Add(new Slot(date, c.Period ?? 0, p?.Start ?? "", p?.End ?? "", c.SubjectId ?? "", r.SubjectName(c.SubjectId),
                        c.Kind ?? "practice", c.Room, c.Teacher, c.Campus ?? DefaultCampusFor(r, c.SubjectId), c.Online ?? false, c.Note, "added", null, c.Id));
                    break;
            }
        }
        return slots.OrderBy(s => s.Period).ThenBy(s => s.Status == "cancelled" ? 1 : 0).ToList();
    }
    private static string? DefaultCampusFor(Registry r, string? subjectId) => string.IsNullOrWhiteSpace(r.DefaultCampus) ? null : r.DefaultCampus;
    private static Slot FromLesson(Registry r, Lesson l, string date, string status, string? change)
    {
        var p = r.PeriodOf(l.Period);
        return new(date, l.Period, p?.Start ?? "", p?.End ?? "", l.SubjectId, r.SubjectName(l.SubjectId), l.Kind, l.Room, l.Teacher,
            string.IsNullOrWhiteSpace(l.Campus) ? (string.IsNullOrWhiteSpace(r.DefaultCampus) ? null : r.DefaultCampus) : l.Campus,
            l.Online, l.Note, status, l.Id, change);
    }

    public sealed record NowInfo(Slot? Current, Slot? Next, int MinutesToNext);
    /// <summary>The lesson in progress and the next one starting later (searching up to two weeks ahead).</summary>
    public static NowInfo Now(Registry r, DateTime local)
    {
        var today = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        Slot? current = null;
        foreach (var s in Day(r, today).Where(s => s.Status != "cancelled"))
            if (TimeOnly.TryParse(s.Start, out var a) && TimeOnly.TryParse(s.End, out var b) && time >= a && time < b) { current = s; break; }
        for (var i = 0; i < 15; i++)
        {
            var d = today.AddDays(i);
            foreach (var s in Day(r, d).Where(s => s.Status != "cancelled"))
            {
                if (!TimeOnly.TryParse(s.Start, out var start)) continue;
                var at = d.ToDateTime(start);
                if (at > local) return new(current, s, (int)Math.Round((at - local).TotalMinutes));
            }
        }
        return new(current, null, 0);
    }

    public static string Line(Slot s, bool withDate = false)
    {
        var sb = new StringBuilder();
        if (withDate) sb.Append(DateOnly.ParseExact(s.Date, "yyyy-MM-dd").ToString("dd.MM", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append($"{s.Period} пара {s.Start}–{s.End} · {KindRu(s.Kind)} {s.Subject}");
        if (!string.IsNullOrWhiteSpace(s.RoomText)) sb.Append(" · ").Append(s.RoomText);
        if (!string.IsNullOrWhiteSpace(s.Teacher)) sb.Append(" · ").Append(s.Teacher);
        if (!string.IsNullOrWhiteSpace(s.Campus)) sb.Append(" · ").Append(s.Campus);
        if (!string.IsNullOrWhiteSpace(s.Note)) sb.Append(" — ").Append(s.Note);
        return s.Status switch
        {
            "cancelled" => "❌ " + sb + " (отменена)",
            "changed" => "🔄 " + sb + " (изменено)",
            "added" => "➕ " + sb + " (разово)",
            _ => sb.ToString()
        };
    }

    public static string DayText(Registry r, DateOnly d, string? title = null)
    {
        var week = Week(r, d);
        var head = $"📅 {title ?? Capital(DayNames[Dow(d)])}, {d:dd.MM.yyyy}" + (InTerm(r, d) ? $" · {week}-я неделя ({ParityRu(Parity(week))})" : " · вне семестра");
        var slots = Day(r, d);
        return slots.Count == 0 ? head + "\nПар нет." : head + "\n" + string.Join("\n", slots.Select(s => Line(s)));
    }
    public static string WeekText(Registry r, DateOnly anyDay)
    {
        var monday = anyDay.AddDays(1 - Dow(anyDay));
        var week = Week(r, monday);
        var sb = new StringBuilder($"🗓 Неделя {monday:dd.MM}–{monday.AddDays(6):dd.MM}" + (InTerm(r, monday) || InTerm(r, monday.AddDays(6)) ? $" · {week}-я ({ParityRu(Parity(week))})" : ""));
        for (var i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            var slots = Day(r, d);
            sb.Append($"\n\n{DayShort[Dow(d)]} {d:dd.MM}");
            sb.Append(slots.Count == 0 ? " — пар нет" : "\n" + string.Join("\n", slots.Select(s => Line(s))));
        }
        return sb.ToString();
    }
    public static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public static string Describe(Registry r, Lesson l) =>
        $"{l.Id}: {DayShort[Math.Clamp(l.DayOfWeek, 0, 7)]} · {l.Period} пара · {KindRu(l.Kind)} {r.SubjectName(l.SubjectId)}" +
        (string.IsNullOrWhiteSpace(l.Room) ? "" : " · " + l.Room) + (l.Online ? " · дистанционно" : "") +
        (string.IsNullOrWhiteSpace(l.Teacher) ? "" : " · " + l.Teacher) + (string.IsNullOrWhiteSpace(l.Campus) ? "" : " · " + l.Campus) +
        " · " + WeeksRu(l.Weeks) + (l.From is null ? "" : $" · с {l.From}") + (l.Until is null ? "" : $" · по {l.Until}");
    public static string Describe(Registry r, ScheduleChange c)
    {
        var d = DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", out var day) ? $"{day:dd.MM} ({DayShort[Dow(day)]})" : c.Date;
        var target = c.LessonId is not null && r.Lessons.FirstOrDefault(l => l.Id == c.LessonId) is { } l
            ? $"{l.Period} пара {KindRu(l.Kind)} {r.SubjectName(l.SubjectId)}" : c.SubjectId is not null ? r.SubjectName(c.SubjectId) + (c.Period is null ? "" : $", {c.Period} пара") : "";
        var details = string.Join(", ", new[]
        {
            c.Kind is null ? null : KindLong(c.Kind), c.Period is null || c.Type is "cancel" ? null : $"{c.Period} пара",
            c.Room is null ? null : "кабинет " + c.Room, c.Online == true ? "дистанционно" : c.Online == false ? "очно" : null,
            c.Teacher, c.Campus, c.Note
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return c.Type switch
        {
            "dayoff" => $"{c.Id}: {d} — без пар" + (c.Note is null ? "" : $" ({c.Note})"),
            "cancel" => $"{c.Id}: {d} — отмена: {target}" + (c.Note is null ? "" : $" ({c.Note})"),
            "modify" => $"{c.Id}: {d} — {target} → {details}",
            _ => $"{c.Id}: {d} — разово: {r.SubjectName(c.SubjectId)}" + (details.Length == 0 ? "" : ", " + details)
        };
    }
}
