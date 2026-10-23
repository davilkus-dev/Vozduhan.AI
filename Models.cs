using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vozduhan.AI;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException("Empty JSON value");
}

public sealed record Member(long UserId, string FullName, string ShortName, string Username = "",
    int ListNumber = 0, string Role = "member", bool Active = true);
public sealed record Subject(string Id, string Name, string Room, string[] Aliases);
public sealed record Period(int Number, string Start, string End);
public sealed record Lesson(string SubjectId, int DayOfWeek, int Period, string Weeks = "all",
    string? From = null, string? Until = null);
public sealed record ScheduleOverride(string SubjectId, string Date, int Period);

public sealed class BotConfig
{
    public long OwnerId { get; set; }
    public long GroupId { get; set; }
    public int ChatTopicId { get; set; }
    public int DeadlinesTopicId { get; set; }
    public int[] OtherAiTopicIds { get; set; } = [];
    public string DataPath { get; set; } = "data/vozduhan.db";
    public string TimeZone { get; set; } = "Europe/Moscow";
    public string MorningTime { get; set; } = "08:00";
    public int PinDays { get; set; } = 3;
    public int MaxPins { get; set; } = 3;
    public int ContextMessages { get; set; } = 30;
    public int HistoryDays { get; set; } = 30;
    public string WeekOneMonday { get; set; } = "2026-09-07";
    public string Model { get; set; } = "z-ai/glm-5.3-flash";
    public int AiTimeoutSeconds { get; set; } = 90;
    public int MaxOutputTokens { get; set; } = 4096;
    public int MaxDailyChatRequestsPerUser { get; set; } = 40;
    public Member[] Members { get; set; } = [];
    public Subject[] Subjects { get; set; } = [];
    public Period[] Periods { get; set; } = [];
    public Lesson[] Lessons { get; set; } = [];
    public ScheduleOverride[] ScheduleOverrides { get; set; } = [];

    [JsonIgnore] public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
    public DateTime LocalNow(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;
    public DateTimeOffset Utc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone));
    public Member? FindMember(long id) => Members.FirstOrDefault(m => m.UserId == id && m.Active);
    public bool AllowedUser(long id) => id == OwnerId || FindMember(id) is not null;
    public bool CanEdit(long id) => id == OwnerId || FindMember(id)?.Role == "head";
    public bool AiTopic(int id) => id == DeadlinesTopicId || OtherAiTopicIds.Contains(id);

    public void Validate()
    {
        if (OwnerId <= 0 || GroupId >= 0 || ChatTopicId <= 0 || DeadlinesTopicId <= 0 || ChatTopicId == DeadlinesTopicId)
            throw new InvalidDataException("Set ownerId, negative groupId and distinct positive topic IDs in BOT_CONFIG_PATH");
        _ = Zone;
        if (!TimeOnly.TryParseExact(MorningTime, "HH:mm", out _) || PinDays is < 0 or > 30 ||
            MaxPins is < 0 or > 10 || ContextMessages is < 1 or > 50 || HistoryDays is < 1 or > 365 ||
            AiTimeoutSeconds is < 10 or > 180 || MaxOutputTokens is < 1024 or > 16384 || MaxDailyChatRequestsPerUser is < 1 or > 1000)
            throw new InvalidDataException("Invalid time or limits in bot config");
        if (Members.Any(m => m.UserId <= 0 || string.IsNullOrWhiteSpace(m.FullName) || string.IsNullOrWhiteSpace(m.ShortName) ||
                             m.Role is not ("member" or "head")) ||
            Members.Select(m => m.UserId).Distinct().Count() != Members.Length ||
            Members.Count(m => m.Active && m.Role == "head") > 1)
            throw new InvalidDataException("Members require unique positive user IDs, names and at most one active head");
        if (!Members.Any(m => m.UserId == OwnerId && m.Active)) throw new InvalidDataException("Owner must also have an active roster entry with real names");
        var numbers = Members.Where(m => m.Active && m.ListNumber > 0).Select(m => m.ListNumber).ToArray();
        if (numbers.Distinct().Count() != numbers.Length) throw new InvalidDataException("Duplicate roster numbers");
        if (Subjects.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Room) || s.Room.StartsWith("REPLACE_")) ||
            Subjects.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Subjects.Length)
            throw new InvalidDataException("Subjects require unique IDs, names and fixed rooms");
        if (Periods.Select(p => p.Number).Distinct().Count() != Periods.Length || Periods.Any(p => p.Number < 1 ||
            !TimeOnly.TryParseExact(p.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(p.End, "HH:mm", out _) ||
            TimeOnly.Parse(p.End) <= TimeOnly.Parse(p.Start))) throw new InvalidDataException("Invalid bell periods");
        if (!DateOnly.TryParseExact(WeekOneMonday, "yyyy-MM-dd", out var monday) || monday.DayOfWeek != DayOfWeek.Monday)
            throw new InvalidDataException("weekOneMonday must be a Monday");
        foreach (var l in Lessons)
            if (!Subjects.Any(s => s.Id == l.SubjectId) || !Periods.Any(p => p.Number == l.Period) || l.DayOfWeek is < 1 or > 7 ||
                l.Weeks is not ("all" or "odd" or "even") || !ValidDate(l.From) || !ValidDate(l.Until))
                throw new InvalidDataException("Invalid weekly lesson");
        foreach (var l in ScheduleOverrides)
            if (!Subjects.Any(s => s.Id == l.SubjectId) || !Periods.Any(p => p.Number == l.Period) || !ValidDate(l.Date) || l.Date is null)
                throw new InvalidDataException("Invalid schedule override");
        if (OtherAiTopicIds.Contains(ChatTopicId)) throw new InvalidDataException("Chat topic cannot be moderated");
    }
    private static bool ValidDate(string? s) => s is null || DateOnly.TryParseExact(s, "yyyy-MM-dd", out _);
}

public sealed record Deadline(int Id, int Version, string SubjectId, string Work, string Date,
    int Period, string Start, string End, string Room, string Status, long EditorId, string Source,
    string Intro, string MorningIntro, string? Note = null);
public sealed record Draft(string Token, long ActorId, int? TargetId, int ExpectedVersion, string Action,
    string? SubjectId, string? Work, string? Date, int? Period, string? Start, string? End, string? Room,
    string Source, string Intro, string MorningIntro, string[] Missing, string? Note = null);
public sealed record ParsedDeadline(string Action = "create", int? TargetId = null, string? SubjectId = null,
    string? Work = null, string? Date = null, int? Period = null, string? Question = null, string? Note = null);
public sealed record Inbox(long Id, string Payload, int Attempts, long ActorId);
public sealed record Delivery(string Key, long ChatId, int TopicId, string Text, long Due, long Expires, int Attempts);
public sealed record SavedPost(string Key, int MessageId, string Text, bool Pinned);

public static class Deadlines
{
    public static Draft Resolve(BotConfig c, ParsedDeadline p, Draft? previous, IReadOnlyList<Deadline> existing,
        long actor, string source, DateTimeOffset now)
    {
        var action = p.Action is "update" or "cancel" ? p.Action : "create";
        var id = p.TargetId ?? (action != "create" ? previous?.TargetId : null);
        var target = existing.FirstOrDefault(d => d.Id == id);
        var missing = new List<string>();
        var note = p.Note ?? previous?.Note;
        if (note?.Length > 400) missing.Add("Сократи публичное примечание до 400 символов.");
        if (action != "create" && target is null) missing.Add("Укажи ID дедлайна из /deadlines.");
        var subjectId = p.SubjectId ?? previous?.SubjectId ?? target?.SubjectId;
        var subject = c.Subjects.FirstOrDefault(s => s.Id == subjectId);
        if (subject is null) missing.Add("Какой предмет? Он должен быть в реестре предметов.");
        var work = (p.Work ?? previous?.Work ?? target?.Work)?.Trim();
        if (string.IsNullOrWhiteSpace(work) || Regex.IsMatch(work, @"^(?:сдача\s+)?(?:практик\w*|лабораторн\w*(?:\s+работ\w*)?|контрольн\w*(?:\s+работ\w*)?)$", RegexOptions.IgnoreCase))
            missing.Add("Что именно сдаём? Укажи номер или название конкретной работы.");
        if (work?.Length > 300) missing.Add("Сократи описание работы до 300 символов.");
        var date = p.Date ?? previous?.Date ?? target?.Date;
        var validDate = DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day);
        if (!validDate) missing.Add("Назови точную дату сдачи.");
        else if (action != "cancel" && (day < DateOnly.FromDateTime(c.LocalNow(now)) || day > DateOnly.FromDateTime(c.LocalNow(now)).AddYears(2)))
            missing.Add("Дата должна быть не в прошлом и не дальше двух лет.");
        var periodNumber = p.Period;
        var sameSchedule = previous is not null && previous.SubjectId == subjectId && previous.Date == date;
        if (periodNumber is null && sameSchedule) periodNumber = previous?.Period;
        if (periodNumber is null && target is not null && target.SubjectId == subjectId && target.Date == date) periodNumber = target.Period;
        if (validDate && subject is not null && periodNumber is null)
        {
            var overrides = c.ScheduleOverrides.Where(l => l.SubjectId == subject.Id && l.Date == date).Select(l => l.Period).Distinct().ToArray();
            var week = (int)Math.Floor((day.DayNumber - DateOnly.ParseExact(c.WeekOneMonday, "yyyy-MM-dd").DayNumber) / 7d) + 1;
            var candidates = overrides.Length > 0 ? overrides : c.Lessons.Where(l => l.SubjectId == subject.Id &&
                l.DayOfWeek == ((int)day.DayOfWeek == 0 ? 7 : (int)day.DayOfWeek) &&
                (l.From is null || day >= DateOnly.Parse(l.From)) && (l.Until is null || day <= DateOnly.Parse(l.Until)) &&
                (l.Weeks == "all" || l.Weeks == (Math.Abs(week % 2) == 1 ? "odd" : "even")))
                .Select(l => l.Period).Distinct().ToArray();
            if (candidates.Length == 1) periodNumber = candidates[0];
            else missing.Add(candidates.Length == 0 ? "В расписании нет пары этого предмета в эту дату. На какой паре сдаём?" :
                $"В этот день несколько пар: {string.Join(", ", candidates)}. На какой сдаём?");
        }
        var period = c.Periods.FirstOrDefault(x => x.Number == periodNumber);
        if (periodNumber is not null && period is null) missing.Add("Номер пары отсутствует в расписании звонков.");
        if (action == "cancel" && target is not null)
            return new(Guid.NewGuid().ToString("N")[..16], actor, target.Id, target.Version, action, target.SubjectId,
                target.Work, target.Date, target.Period, target.Start, target.End, target.Room, source, "", "", missing.ToArray(), note);
        if (validDate && period is not null && c.Utc(day.ToDateTime(TimeOnly.Parse(period.End))) <= now)
            missing.Add("Эта пара уже закончилась. Выбери будущее время.");
        return new(Guid.NewGuid().ToString("N")[..16], actor, id, target?.Version ?? 0, action, subject?.Id, work, date,
            period?.Number, period?.Start, period?.End, subject?.Room, source, "", "", missing.Distinct().ToArray(), note);
    }

    public static DateTimeOffset Ends(BotConfig c, Deadline d) => c.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.End)));
    public static string Facts(BotConfig c, Deadline d) =>
        $"#{d.Id} · {c.Subjects.FirstOrDefault(s => s.Id == d.SubjectId)?.Name ?? d.SubjectId} — {d.Work}\n" +
        $"{DateOnly.Parse(d.Date):dd.MM.yyyy} · {d.Period}-я пара · {d.Start}–{d.End}\nКабинет: {d.Room}";
    public static string Card(BotConfig c, Deadline d) => $"{(d.Status == "cancelled" ? "❌ Сдача отменена" : "📌 Дедлайн")}\n{(string.IsNullOrWhiteSpace(d.Intro) ? "Гайс, держим в голове:" : d.Intro)}\n\n{Facts(c, d)}" +
        (string.IsNullOrWhiteSpace(d.Note) ? "" : "\n\nКомментарий редактора: " + d.Note);
    public static string Preview(BotConfig c, Draft d) =>
        $"{(d.Action == "cancel" ? "Отменяем" : d.Action == "update" ? "Обновляем" : "Добавляем")} дедлайн{(d.TargetId is not null ? $" #{d.TargetId}" : "")}\n\n" +
        $"{c.Subjects.FirstOrDefault(s => s.Id == d.SubjectId)?.Name ?? "Предмет: ?"} — {d.Work ?? "Работа: ?"}\n" +
        $"Дата: {d.Date ?? "?"}\nПара: {d.Period?.ToString() ?? "?"} · {d.Start ?? "--:--"}–{d.End ?? "--:--"}\nКабинет: {d.Room ?? "?"}\n\n" +
        (string.IsNullOrWhiteSpace(d.Note) ? "" : "Публичное примечание: " + d.Note + "\n\n") +
        (d.Missing.Length > 0 ? string.Join("\n", d.Missing) : "Проверь факты. Публикую только после подтверждения.");
}
