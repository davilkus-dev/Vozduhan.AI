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
    /// <summary>
    /// System.Text.Json escapes emoji as \uD83D\uDE04 even in relaxed mode. Models copy that literally into answers,
    /// so emoji are written as real characters (still valid JSON).
    /// </summary>
    public static string Write<T>(T value) => Emoji.Replace(JsonSerializer.Serialize(value, Options),
        m => char.ConvertFromUtf32(char.ConvertToUtf32((char)Convert.ToInt32(m.Groups[1].Value, 16), (char)Convert.ToInt32(m.Groups[2].Value, 16))));
    private static readonly Regex Emoji = new(@"(?<=(?:^|[^\\])(?:\\\\)*)\\u(D[89AB][0-9A-F]{2})\\u(D[C-F][0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Turns literal \uXXXX escapes a model may still produce into characters.</summary>
    public static string Unescape(string text) => Regex.Replace(text, @"\\u(D[89AB][0-9A-F]{2})\\u(D[C-F][0-9A-F]{2})|\\u([0-9A-F]{4})", m =>
    {
        if (m.Groups[3].Success) { var c = Convert.ToInt32(m.Groups[3].Value, 16); return c is >= 0xD800 and <= 0xDFFF ? "" : ((char)c).ToString(); }
        return char.ConvertFromUtf32(char.ConvertToUtf32((char)Convert.ToInt32(m.Groups[1].Value, 16), (char)Convert.ToInt32(m.Groups[2].Value, 16)));
    }, RegexOptions.IgnoreCase);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException("Empty JSON value");
}

public sealed record Member(long? UserId, string FullName, string ShortName, string Username = "",
    int ListNumber = 0, string Role = "member", bool Active = true, string? TelegramName = null, string? Email = null)
{
    /// <summary>Stable internal key: survives renumbering, rebinding and deactivation.</summary>
    public string? Key { get; init; }
    /// <summary>Editor note, e.g. why the student left. Not sent to the AI.</summary>
    public string? Note { get; init; }
    /// <summary>Owner-only flavour for jokes in the group chat ("Лёха вечно про видеокарты"). Hidden from the head.</summary>
    public string? AdminNote { get; init; }
}

public sealed record Subject(string Id, string Name, string[]? Aliases = null)
{
    public string? ShortName { get; init; }
    /// <summary>Private tone hint for the bot ("там все спят"). Never shown to students verbatim.</summary>
    public string? AdminComment { get; init; }
    public bool Archived { get; init; }
    /// <summary>Legacy fallback room; real rooms live on lessons.</summary>
    public string? Room { get; init; }
}

public sealed record Period(int Number, string Start, string End);

/// <summary>A recurring weekly lesson. dayOfWeek: 1 = Monday … 7 = Sunday; weeks: all / odd / even.</summary>
public sealed record Lesson(string SubjectId, int DayOfWeek, int Period, string Weeks = "all",
    string? From = null, string? Until = null)
{
    public string? Id { get; init; }
    /// <summary>lecture / practice / lab</summary>
    public string Kind { get; init; } = "practice";
    public string? Room { get; init; }
    public string? Teacher { get; init; }
    public string? Campus { get; init; }
    public bool Online { get; init; }
    public string? Note { get; init; }
}

/// <summary>A one-date exception. type: add / cancel / modify / dayoff.</summary>
public sealed record ScheduleChange(string Date, string Type)
{
    public string? Id { get; init; }
    public string? LessonId { get; init; }
    public string? SubjectId { get; init; }
    public string? Kind { get; init; }
    public int? Period { get; init; }
    public string? Room { get; init; }
    public string? Teacher { get; init; }
    public string? Campus { get; init; }
    public bool? Online { get; init; }
    public string? Note { get; init; }
}

/// <summary>The bot's own initiative: morning message, random drops during the day, evening reminders.</summary>
public sealed class AmbientSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Morning post in «Сводка»: a lively message about the day, the schedule and the nearest submissions.</summary>
    public string SummaryTime { get; set; } = "08:00";
    /// <summary>Optional extra morning drop in Воздуханство; empty = off (the morning summary in «Сводка» covers it).</summary>
    public string MorningTime { get; set; } = "";
    /// <summary>Up to this many spontaneous drops per day, each happens with SpontaneousChance.</summary>
    public int SpontaneousPerDay { get; set; } = 2;
    public double SpontaneousChance { get; set; } = 0.5;
    public string ActiveFrom { get; set; } = "10:00";
    public string ActiveTo { get; set; } = "21:30";
    /// <summary>Reminder about tomorrow's submissions in the deadlines topic.</summary>
    public bool EveReminders { get; set; } = true;
    public string EveReminderTime { get; set; } = "20:00";
}

/// <summary>Model choice editable from the web admin (owner). Any OpenRouter model id works.</summary>
public sealed class AiSettings
{
    public string Model { get; set; } = "";
    public Dictionary<string, string> ModelRouting { get; set; } = new();
    public Dictionary<string, string> ReasoningEffort { get; set; } = new();
    public static bool ValidModel(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, @"^[\w.\-]+/[\w.:\-]+$");
}

public sealed class WebSettings
{
    public bool Enabled { get; set; } = true;
    public string Listen { get; set; } = "http://*:8080/";
    public string PublicUrl { get; set; } = "http://10.8.0.23:8080";
    public string[] AllowedNetworks { get; set; } = ["10.8.0.0/24", "127.0.0.0/8"];
    public int SessionDays { get; set; } = 7;
}

/// <summary>Everything editors may change at runtime. The live copy lives in SQLite; the config file only seeds it.</summary>
public sealed class Registry
{
    public Member[] Members { get; set; } = [];
    public Subject[] Subjects { get; set; } = [];
    public Period[] Periods { get; set; } = [];
    public Lesson[] Lessons { get; set; } = [];
    public ScheduleChange[] Changes { get; set; } = [];
    public string WeekOneMonday { get; set; } = "2026-08-31";
    public string? TermEnd { get; set; }
    public string DefaultCampus { get; set; } = "";
    public string Knowledge { get; set; } = "";

    public static readonly string[] Kinds = ["lecture", "practice", "lab"];
    public static readonly string[] ChangeTypes = ["add", "cancel", "modify", "dayoff"];

    public Registry Clone() => Json.Read<Registry>(Json.Write(this));
    public Subject? Subject(string? id) => id is null ? null : Subjects.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public string SubjectName(string? id) => Subject(id)?.Name ?? id ?? "?";
    public Period? PeriodOf(int number) => Periods.FirstOrDefault(p => p.Number == number);
    public Member? Active(long id) => id > 0 ? Members.FirstOrDefault(m => m.UserId == id && m.Active) : null;

    /// <summary>Gives every row a stable ID. Idempotent.</summary>
    public Registry Normalize()
    {
        var keys = new HashSet<string>(Members.Where(m => !string.IsNullOrWhiteSpace(m.Key)).Select(m => m.Key!));
        var n = 1;
        Members = Members.Select(m =>
        {
            if (!string.IsNullOrWhiteSpace(m.Key)) return m with { Username = (m.Username ?? "").TrimStart('@') };
            string key; do key = "m" + n++; while (!keys.Add(key));
            return m with { Key = key, Username = (m.Username ?? "").TrimStart('@') };
        }).ToArray();
        Lessons = NumberRows(Lessons, l => l.Id, (l, id) => l with { Id = id }, "L");
        Changes = NumberRows(Changes, c => c.Id, (c, id) => c with { Id = id }, "C");
        Periods = Periods.OrderBy(p => p.Number).ToArray();
        Subjects = Subjects.Select(s => s with { Aliases = (s.Aliases ?? []).Select(a => a.Trim()).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray();
        return this;
    }
    private static T[] NumberRows<T>(T[] rows, Func<T, string?> get, Func<T, string, T> set, string prefix)
    {
        var used = new HashSet<string>(rows.Select(get).Where(x => !string.IsNullOrWhiteSpace(x))!, StringComparer.OrdinalIgnoreCase);
        var next = used.Select(x => int.TryParse(x![1..], out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
        return rows.Select(r =>
        {
            if (!string.IsNullOrWhiteSpace(get(r))) return r;
            string id; do id = prefix + next++; while (!used.Add(id));
            return set(r, id);
        }).ToArray();
    }
    /// <summary>
    /// Owner-only "magic": slang, subject tone notes, notes about people, knowledge text. The head never sees or changes it.
    /// </summary>
    public string SecretPrint() => Json.Write(new
    {
        subjects = Subjects.Where(s => (s.Aliases ?? []).Length > 0 || !string.IsNullOrWhiteSpace(s.AdminComment))
            .OrderBy(s => s.Id).Select(s => new { s.Id, s.Aliases, s.AdminComment }),
        members = Members.Where(m => !string.IsNullOrWhiteSpace(m.AdminNote)).OrderBy(m => m.Key).Select(m => new { m.Key, m.AdminNote }),
        Knowledge
    });
    public Registry Redacted()
    {
        var r = Clone();
        r.Subjects = r.Subjects.Select(s => s with { Aliases = [], AdminComment = null }).ToArray();
        r.Members = r.Members.Select(m => m with { AdminNote = null }).ToArray();
        r.Knowledge = "";
        return r;
    }
    /// <summary>For a save by the head: secret fields are always taken from the current version.</summary>
    public Registry WithSecretsFrom(Registry current)
    {
        Subjects = Subjects.Select(s => current.Subject(s.Id) is { } old ? s with { Aliases = old.Aliases, AdminComment = old.AdminComment } : s with { Aliases = [], AdminComment = null }).ToArray();
        Members = Members.Select(m => m with { AdminNote = current.Members.FirstOrDefault(o => o.Key == m.Key)?.AdminNote }).ToArray();
        Knowledge = current.Knowledge;
        return this;
    }

    public string NextLessonId() => "L" + (Lessons.Select(l => int.TryParse(l.Id?[1..], out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1);
    public string NextChangeId() => "C" + (Changes.Select(l => int.TryParse(l.Id?[1..], out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1);

    /// <summary>Throws InvalidDataException with a Russian message editors can act on.</summary>
    public void Validate(long ownerId)
    {
        void Fail(string message) => throw new InvalidDataException(message);
        foreach (var m in Members)
        {
            var who = string.IsNullOrWhiteSpace(m.FullName) ? $"запись №{m.ListNumber}" : m.FullName;
            if (string.IsNullOrWhiteSpace(m.FullName) || string.IsNullOrWhiteSpace(m.ShortName)) Fail($"У участника {who} должны быть ФИО и краткое имя.");
            if (m.UserId is <= 0) Fail($"Telegram ID у {who} должен быть положительным числом или пустым.");
            if (m.Role is not ("member" or "head")) Fail($"Неизвестная роль у {who}: {m.Role}.");
            if (m.Role == "head" && (m.UserId is null || !m.Active)) Fail($"Староста ({who}) должен быть активным и с Telegram ID.");
            if (m.ListNumber < 0) Fail($"Номер по списку у {who} не может быть отрицательным.");
        }
        var ids = Members.Where(m => m.UserId.HasValue).Select(m => m.UserId!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length) Fail("Один Telegram ID привязан к нескольким записям списка.");
        if (Members.Select(m => m.Key).Distinct().Count() != Members.Length) Fail("Повторяющиеся внутренние ключи участников.");
        if (Members.Count(m => m.Active && m.Role == "head") > 1) Fail("Староста может быть только один.");
        if (!Members.Any(m => m.UserId == ownerId && m.Active)) Fail("Владелец (ownerId из серверного конфига) должен оставаться активным в списке.");
        var numbers = Members.Where(m => m.Active && m.ListNumber > 0).Select(m => m.ListNumber).ToArray();
        if (numbers.Distinct().Count() != numbers.Length) Fail("Номера по списку у активных участников повторяются.");

        foreach (var s in Subjects)
        {
            if (!Regex.IsMatch(s.Id ?? "", "^[a-z0-9_-]{1,32}$")) Fail($"ID предмета «{s.Id}» — только латиница в нижнем регистре, цифры, _ и -.");
            if (string.IsNullOrWhiteSpace(s.Name)) Fail($"У предмета {s.Id} нет названия.");
            if (s.AdminComment?.Length > 1500) Fail($"Комментарий к предмету {s.Id} длиннее 1500 символов.");
        }
        if (Subjects.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Subjects.Length) Fail("ID предметов повторяются.");

        foreach (var p in Periods)
            if (p.Number < 1 || !TimeOnly.TryParseExact(p.Start, "HH:mm", out var a) || !TimeOnly.TryParseExact(p.End, "HH:mm", out var b) || b <= a)
                Fail($"Неверное время {p.Number}-й пары: нужно ЧЧ:ММ и конец позже начала.");
        if (Periods.Select(p => p.Number).Distinct().Count() != Periods.Length) Fail("Номера пар в звонках повторяются.");
        if (!DateOnly.TryParseExact(WeekOneMonday, "yyyy-MM-dd", out var monday) || monday.DayOfWeek != DayOfWeek.Monday)
            Fail("weekOneMonday должен быть понедельником первой (нечётной) недели в формате YYYY-MM-DD.");
        if (TermEnd is not null && !ValidDate(TermEnd)) Fail("Конец семестра — дата YYYY-MM-DD или пусто.");
        if (Knowledge.Length > 30000) Fail("Текст знаний длиннее 30000 символов.");
        if (Members.Any(m => m.AdminNote?.Length > 1500)) Fail("Заметка о человеке длиннее 1500 символов.");

        foreach (var l in Lessons)
        {
            var where = $"пара {l.Id}";
            if (Subject(l.SubjectId) is null) Fail($"{where}: нет предмета {l.SubjectId}.");
            if (PeriodOf(l.Period) is null) Fail($"{where}: нет {l.Period}-й пары в звонках.");
            if (l.DayOfWeek is < 1 or > 7) Fail($"{where}: день недели 1–7.");
            if (l.Weeks is not ("all" or "odd" or "even")) Fail($"{where}: недели all/odd/even.");
            if (!Kinds.Contains(l.Kind)) Fail($"{where}: тип lecture/practice/lab.");
            if (!ValidDate(l.From) || !ValidDate(l.Until) || (l.From is not null && l.Until is not null && string.CompareOrdinal(l.From, l.Until) > 0))
                Fail($"{where}: неверный период действия from/until.");
        }
        if (Lessons.Select(l => l.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Lessons.Length) Fail("ID пар повторяются.");
        foreach (var c in Changes)
        {
            var where = $"изменение {c.Id} на {c.Date}";
            if (!ValidDate(c.Date) || c.Date is null) Fail($"{where}: неверная дата.");
            if (!ChangeTypes.Contains(c.Type)) Fail($"{where}: тип add/cancel/modify/dayoff.");
            if (c.SubjectId is not null && Subject(c.SubjectId) is null) Fail($"{where}: нет предмета {c.SubjectId}.");
            if (c.Period is not null && PeriodOf(c.Period.Value) is null) Fail($"{where}: нет {c.Period}-й пары в звонках.");
            if (c.Kind is not null && !Kinds.Contains(c.Kind)) Fail($"{where}: тип пары lecture/practice/lab.");
            if (c.LessonId is not null && !Lessons.Any(l => l.Id == c.LessonId)) Fail($"{where}: нет пары {c.LessonId}.");
            if (c.Type == "add" && (c.SubjectId is null || c.Period is null)) Fail($"{where}: для добавления нужны предмет и номер пары.");
            if (c.Type == "modify" && c.LessonId is null) Fail($"{where}: для изменения укажи конкретную пару (lessonId).");
            if (c.Type == "cancel" && c.LessonId is null && c.SubjectId is null) Fail($"{where}: что отменяем? Укажи пару или предмет.");
            if (c.Note?.Length > 500) Fail($"{where}: примечание длиннее 500 символов.");
        }
        if (Changes.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Changes.Length) Fail("ID изменений повторяются.");
    }
    public static bool ValidDate(string? s) => s is null || DateOnly.TryParseExact(s, "yyyy-MM-dd", out _);
}

public sealed class BotConfig
{
    public long OwnerId { get; set; }
    public long GroupId { get; set; }
    public int ChatTopicId { get; set; }
    public int DeadlinesTopicId { get; set; }
    /// <summary>"Сводка": morning summary and reminders. 0 = reminders go to the deadlines topic.</summary>
    public int SummaryTopicId { get; set; }
    public int[] OtherAiTopicIds { get; set; } = [];
    public string DataPath { get; set; } = "data/vozduhan.db";
    public string TimeZone { get; set; } = "Europe/Moscow";
    public string MorningTime { get; set; } = "08:00";
    public int PinDays { get; set; } = 3;
    public int MaxPins { get; set; } = 3;
    public int ContextMessages { get; set; } = 30;
    public int HistoryDays { get; set; } = 30;
    public int LogDays { get; set; } = 365;
    public int AiLogDays { get; set; } = 30;
    public string Model { get; set; } = "z-ai/glm-5.3-flash";
    public int AiTimeoutSeconds { get; set; } = 90;
    public int MaxOutputTokens { get; set; } = 4096;
    public int MaxDailyChatRequestsPerUser { get; set; } = 40;
    /// <summary>Upper limit for regenerating an answer that was cut by maxOutputTokens.</summary>
    public int MaxOutputTokensCeiling { get; set; } = 16384;
    /// <summary>Reasoning depth per task: chat answers stay fast, posts and planning think more.</summary>
    public Dictionary<string, string> ReasoningEffort { get; set; } = new()
    {
        ["chat"] = "low", ["deadline"] = "low", ["intro"] = "low", ["plan"] = "medium", ["wakeplan"] = "medium", ["ambient"] = "medium"
    };
    /// <summary>
    /// Model per task. Planning (editor requests, deadline parsing, wake-up plans) goes to the full model;
    /// everything else uses Model (Flash) to save money. Unlisted kinds use Model.
    /// </summary>
    public Dictionary<string, string> ModelRouting { get; set; } = new()
    {
        ["plan"] = "z-ai/glm-5.3", ["deadline"] = "z-ai/glm-5.3", ["wakeplan"] = "z-ai/glm-5.3"
    };
    public AiSettings AiSnapshot() => new() { Model = Model, ModelRouting = new(ModelRouting), ReasoningEffort = new(ReasoningEffort) };
    public void ApplyAi(AiSettings a)
    {
        if (a.Model.Length > 0) Model = a.Model;
        ModelRouting = new(a.ModelRouting.Where(x => x.Value.Length > 0));
        var effort = new Dictionary<string, string>(ReasoningEffort);
        foreach (var (k, v) in a.ReasoningEffort) effort[k] = v;
        ReasoningEffort = effort; // swapped whole: readers never see a half-updated dictionary
    }
    [JsonIgnore] public AiSettings? FileAi { get; set; }
    public string ModelFor(string kind) =>
        ModelRouting.TryGetValue(kind, out var m) || ModelRouting.TryGetValue(kind.Split('-')[0], out m) ? m : Model;
    public string Effort(string kind) =>
        ReasoningEffort.TryGetValue(kind, out var e) || ReasoningEffort.TryGetValue(kind.Split('-')[0], out e) ? e : "low";
    public string GroupName { get; set; } = "ИКБО-66-25";
    public string University { get; set; } = "РТУ МИРЭА";
    public WebSettings Web { get; set; } = new();
    public AmbientSettings Ambient { get; set; } = new();
    /// <summary>The ambient block as written in the config file (for "reset to config" in the admin).</summary>
    [JsonIgnore] public AmbientSettings? FileAmbient { get; set; }
    /// <summary>Chance of an instant 👀/👍/👌 reaction before a chat answer.</summary>
    public double ReactionChance { get; set; } = 0.7;
    public string[] Reactions { get; set; } = ["👀", "👍", "👌"];
    /// <summary>Chat answers may come as 1–3 separate messages. Never applies to deadline posts or admin cards.</summary>
    public bool SplitReplies { get; set; } = true;
    public double TypingCharsPerSecond { get; set; } = 18;
    /// <summary>1 = realistic typing pauses; 0 disables them (tests).</summary>
    public double ReplyDelayScale { get; set; } = 1;

    // Registry seed. Imported into the database on first start or when registryVersion grows.
    public int RegistryVersion { get; set; } = 1;
    public Member[] Members { get; set; } = [];
    public Subject[] Subjects { get; set; } = [];
    public Period[] Periods { get; set; } = [];
    public Lesson[] Lessons { get; set; } = [];
    public ScheduleChange[] ScheduleChanges { get; set; } = [];
    public string WeekOneMonday { get; set; } = "2026-08-31";
    public string? TermEnd { get; set; }
    public string DefaultCampus { get; set; } = "";
    public string Knowledge { get; set; } = "";

    private Registry? current;
    /// <summary>Live registry. Replaced atomically on every confirmed edit; take one snapshot per operation.</summary>
    [JsonIgnore] public Registry R => Volatile.Read(ref current) ?? Use(Seed());
    public Registry Use(Registry registry) { Volatile.Write(ref current, registry); return registry; }
    /// <summary>Registry from the config file. With previous, member keys are carried over by Telegram ID or full name.</summary>
    public Registry Seed(Registry? previous = null)
    {
        var seed = new Registry
        {
            Members = Members, Subjects = Subjects, Periods = Periods, Lessons = Lessons, Changes = ScheduleChanges,
            WeekOneMonday = WeekOneMonday, TermEnd = TermEnd, DefaultCampus = DefaultCampus, Knowledge = Knowledge
        }.Clone();
        if (previous is not null)
        {
            var taken = new HashSet<string>(seed.Members.Where(m => m.Key is not null).Select(m => m.Key!));
            seed.Members = seed.Members.Select(m =>
            {
                if (m.Key is not null) return m;
                var match = previous.Members.FirstOrDefault(p => m.UserId is not null && p.UserId == m.UserId)
                    ?? previous.Members.FirstOrDefault(p => p.FullName.Equals(m.FullName, StringComparison.OrdinalIgnoreCase));
                return match?.Key is { } key && taken.Add(key) ? m with { Key = key } : m;
            }).ToArray();
            // Keys of people missing from the new seed must not be reused for somebody else.
            var reserved = previous.Members.Select(p => p.Key).Where(k => k is not null && !taken.Contains(k)).ToHashSet();
            var n = 1;
            seed.Members = seed.Members.Select(m =>
            {
                if (m.Key is not null) return m;
                string key; do key = "m" + n++; while (taken.Contains(key) || reserved.Contains(key));
                taken.Add(key);
                return m with { Key = key };
            }).ToArray();
        }
        return seed.Normalize();
    }

    [JsonIgnore] public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
    public DateTime LocalNow(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;
    public DateTimeOffset Utc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone));
    public Member? FindMember(long id) => R.Active(id);
    public bool AllowedUser(long id) => id > 0 && (id == OwnerId || FindMember(id) is not null);
    public bool CanEdit(long id) => id > 0 && (id == OwnerId || FindMember(id)?.Role == "head");
    public bool AiTopic(int id) => id == DeadlinesTopicId || OtherAiTopicIds.Contains(id);
    public string Role(long id) => id == OwnerId ? "developer" : FindMember(id)?.Role ?? "unknown";

    public void Validate()
    {
        if (OwnerId <= 0 || GroupId >= 0 || ChatTopicId <= 0 || DeadlinesTopicId <= 0 || ChatTopicId == DeadlinesTopicId)
            throw new InvalidDataException("Set ownerId, negative groupId and distinct positive topic IDs in BOT_CONFIG_PATH");
        _ = Zone;
        if (!TimeOnly.TryParseExact(MorningTime, "HH:mm", out _) || PinDays is < 0 or > 30 ||
            MaxPins is < 0 or > 10 || ContextMessages is < 1 or > 50 || HistoryDays is < 1 or > 365 ||
            LogDays is < 1 or > 3650 || AiLogDays is < 1 or > 365 ||
            AiTimeoutSeconds is < 10 or > 180 || MaxOutputTokens is < 1024 or > 16384 || MaxOutputTokensCeiling < MaxOutputTokens || MaxOutputTokensCeiling > 65536 ||
            ReasoningEffort.Values.Any(v => v is not ("minimal" or "low" or "medium" or "high")) || MaxDailyChatRequestsPerUser is < 1 or > 1000)
            throw new InvalidDataException("Invalid time or limits in bot config");
        if (Web.Enabled && (!Web.Listen.StartsWith("http://") || !Web.Listen.EndsWith('/') || Web.SessionDays is < 1 or > 90 ||
            Web.AllowedNetworks.Length == 0 || Web.AllowedNetworks.Any(n => !Cidr.TryParse(n, out _))))
            throw new InvalidDataException("web.listen must look like http://*:8080/, allowedNetworks must be CIDRs");
        if (OtherAiTopicIds.Contains(ChatTopicId)) throw new InvalidDataException("Chat topic cannot be moderated");
        if (SummaryTopicId < 0 || (SummaryTopicId > 0 && (SummaryTopicId == ChatTopicId || SummaryTopicId == DeadlinesTopicId)))
            throw new InvalidDataException("summaryTopicId must be a separate topic (or 0)");
        if (new[] { Ambient.SummaryTime, Ambient.MorningTime.Length == 0 ? "00:00" : Ambient.MorningTime, Ambient.ActiveFrom, Ambient.ActiveTo, Ambient.EveReminderTime }.Any(t => !TimeOnly.TryParseExact(t, "HH:mm", out _)) ||
            Ambient.SpontaneousPerDay is < 0 or > 10 || Ambient.SpontaneousChance is < 0 or > 1 ||
            TimeOnly.Parse(Ambient.ActiveTo) <= TimeOnly.Parse(Ambient.ActiveFrom))
            throw new InvalidDataException("ambient: times HH:mm, activeFrom < activeTo, spontaneousPerDay 0..10, chance 0..1");
        Seed().Validate(OwnerId);
    }
}

public sealed record Cidr(System.Net.IPAddress Network, int Bits)
{
    public static bool TryParse(string text, out Cidr cidr)
    {
        cidr = null!;
        var parts = text.Split('/');
        if (!System.Net.IPAddress.TryParse(parts[0], out var ip)) return false;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var bits = max;
        if (parts.Length > 2 || (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > max))) return false;
        cidr = new(ip, bits);
        return true;
    }
    public bool Contains(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != Network.AddressFamily) return false;
        var a = address.GetAddressBytes(); var n = Network.GetAddressBytes();
        for (var i = 0; i < a.Length; i++)
        {
            var bits = Math.Clamp(Bits - i * 8, 0, 8);
            var mask = (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}

public sealed record Deadline(int Id, int Version, string SubjectId, string Work, string Date,
    int Period, string Start, string End, string Room, string Status, long EditorId, string Source,
    string Intro, string MorningIntro, string? Note = null);
public sealed record Draft(string Token, long ActorId, int? TargetId, int ExpectedVersion, string Action,
    string? SubjectId, string? Work, string? Date, int? Period, string? Start, string? End, string? Room,
    string Source, string Intro, string MorningIntro, string[] Missing, string? Note = null);
public sealed record ParsedDeadline(string Action = "create", int? TargetId = null, string? SubjectId = null,
    string? Work = null, string? Date = null, int? Period = null, string? Question = null, string? Note = null, string? Room = null, int? ExpectedVersion = null);
public sealed record Inbox(long Id, string Payload, int Attempts, long ActorId);
public sealed record Delivery(string Key, long ChatId, int TopicId, string Text, long Due, long Expires, int Attempts);
public sealed record SavedPost(string Key, int MessageId, string Text, bool Pinned);
public sealed record RegistryDraft(string Token, long ActorId, int BaseVersion, string Kind, string[] Operations,
    string[] Lines, string Source, int? RestoreVersion = null);
public sealed record RegistryVersionInfo(int Version, long Time, long Actor, string Source, string Summary);

public static class Deadlines
{
    public static Draft Resolve(BotConfig c, ParsedDeadline p, Draft? previous, IReadOnlyList<Deadline> existing,
        long actor, string source, DateTimeOffset now)
    {
        var r = c.R;
        var action = p.Action is "update" or "cancel" ? p.Action : "create";
        var id = action == "create" ? null : p.TargetId ?? previous?.TargetId;
        if (p.TargetId is not null && previous?.TargetId != p.TargetId) previous = null;
        var target = existing.FirstOrDefault(d => d.Id == id);
        var missing = new List<string>();
        var note = p.Note ?? previous?.Note ?? target?.Note;
        if (note?.Length > 400) missing.Add("Сократи публичное примечание до 400 символов.");
        if (action != "create" && target is null) missing.Add("Укажи ID дедлайна из /deadlines.");
        var subjectId = p.SubjectId ?? previous?.SubjectId ?? target?.SubjectId;
        var subject = r.Subject(subjectId);
        if (subject is null) missing.Add("Какой предмет? Он должен быть в реестре предметов.");
        var work = (p.Work ?? previous?.Work ?? target?.Work)?.Trim();
        if (string.IsNullOrWhiteSpace(work) || Regex.IsMatch(work, @"^(?:сдача\s+)?(?:практик\w*|лабораторн\w*(?:\s+работ\w*)?)$", RegexOptions.IgnoreCase))
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
        var slots = validDate && subject is not null
            ? Schedule.Day(r, day).Where(s => s.SubjectId == subject.Id && s.Status != "cancelled").ToList() : [];
        if (validDate && subject is not null && periodNumber is null)
        {
            // Submissions happen at practices: when a day has both kinds, the lecture is not a candidate.
            var pool = slots.Any(s => s.Kind != "lecture") && slots.Any(s => s.Kind == "lecture") ? slots.Where(s => s.Kind != "lecture").ToList() : slots;
            var candidates = pool.Select(s => s.Period).Distinct().ToArray();
            if (candidates.Length == 1) periodNumber = candidates[0];
            else missing.Add(candidates.Length == 0 ? "В расписании нет пары этого предмета в эту дату. На какой паре сдаём?" :
                $"В этот день несколько пар: {string.Join(", ", candidates)}. На какой сдаём?");
        }
        var period = periodNumber is null ? null : r.PeriodOf(periodNumber.Value);
        if (periodNumber is not null && period is null) missing.Add("Номер пары отсутствует в расписании звонков.");
        var slot = slots.FirstOrDefault(s => s.Period == periodNumber);
        var room = p.Room ?? (sameSchedule && previous?.Period == periodNumber ? previous?.Room : null)
            ?? (target is not null && target.SubjectId == subjectId && target.Date == date && target.Period == periodNumber ? target.Room : null)
            ?? (slot is not null ? slot.RoomText : null) ?? subject?.Room;
        if (action == "cancel" && target is not null)
            return new(Guid.NewGuid().ToString("N")[..16], actor, target.Id, target.Version, action, target.SubjectId,
                target.Work, target.Date, target.Period, target.Start, target.End, target.Room, source, "", "", missing.ToArray(), note);
        if (period is not null && string.IsNullOrWhiteSpace(room)) missing.Add("В расписании нет этой пары, поэтому кабинет неизвестен. Где сдаём?");
        if (validDate && period is not null && c.Utc(day.ToDateTime(TimeOnly.Parse(period.End))) <= now)
            missing.Add("Эта пара уже закончилась. Выбери будущее время.");
        return new(Guid.NewGuid().ToString("N")[..16], actor, id, target?.Version ?? 0, action, subject?.Id, work, date,
            period?.Number, period?.Start, period?.End, room, source, "", "", missing.Distinct().ToArray(), note);
    }

    public static DateTimeOffset Ends(BotConfig c, Deadline d) => c.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.End)));
    public static string Facts(BotConfig c, Deadline d) =>
        $"#{d.Id} · {c.R.SubjectName(d.SubjectId)} — {d.Work}\n" +
        $"{DateOnly.Parse(d.Date):dd.MM.yyyy} · {d.Period}-я пара · {d.Start}–{d.End}\nКабинет: {d.Room}";

    static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    /// <summary>The technical block of a deadline post, built by the program (never by the model).</summary>
    public static string FactsHtml(BotConfig c, Deadline d, bool struck = false)
    {
        var day = DateOnly.Parse(d.Date);
        var slot = Schedule.Day(c.R, day).FirstOrDefault(s => s.SubjectId == d.SubjectId && s.Period == d.Period && s.Status != "cancelled");
        var title = $"<b>{H(c.R.SubjectName(d.SubjectId))}</b> — {H(d.Work)}";
        var place = new[] { $"🚪 <code>{H(d.Room)}</code>", string.IsNullOrWhiteSpace(slot?.Teacher) ? "" : "👤 " + H(slot!.Teacher),
            string.IsNullOrWhiteSpace(slot?.Campus) ? "" : "📍 " + H(slot!.Campus) }.Where(x => x.Length > 0);
        return "<blockquote>" + (struck ? $"<s>{title}</s>" : title) +
            $"\n📅 <b>{Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()}, {day:dd.MM}</b> · {d.Period} пара · {d.Start}–{d.End}" +
            "\n" + string.Join(" · ", place) + "</blockquote>";
    }
    /// <summary>Model-written lead, sanitised. The editor's note is never printed: it only feeds the model.</summary>
    public static string Lead(string? intro, string fallback) =>
        TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(OpenRouterClient.Join(string.IsNullOrWhiteSpace(intro) ? fallback : intro)));
    public static string Card(BotConfig c, Deadline d) => d.Status == "cancelled"
        ? Lead(d.Intro, "Отбой, эту сдачу отменили.") + $"\n\n❌ <b>Сдача отменена</b> · #{d.Id}\n" + FactsHtml(c, d, true)
        : Lead(d.Intro, "Гайс, держим в голове.") + $"\n\n📌 <b>Дедлайн</b> · #{d.Id}\n" + FactsHtml(c, d);
    /// <summary>A not yet saved draft rendered like the future post.</summary>
    public static string DraftCard(BotConfig c, Draft d)
    {
        var fake = new Deadline(d.TargetId ?? 0, 0, d.SubjectId!, d.Work!, d.Date!, d.Period!.Value, d.Start!, d.End!, d.Room!,
            d.Action == "cancel" ? "cancelled" : "active", d.ActorId, "", d.Intro, d.MorningIntro, d.Note);
        var head = d.Action switch { "cancel" => "Так будет отмена", "update" => "Так будет изменение", _ => "Так будет в «Дедлайнах»" };
        return $"👀 <b>{head}:</b>\n\n" + Card(c, fake).Replace(" · #0", "");
    }
    /// <summary>Exactly what saving the draft publishes: the card and, for update/cancel, the change news.</summary>
    public static (string Card, string? News) PostsPreview(BotConfig c, Draft d, Deadline? old)
    {
        var next = new Deadline(old?.Id ?? 0, (old?.Version ?? 0) + 1, d.SubjectId!, d.Work!, d.Date!, d.Period!.Value, d.Start!, d.End!, d.Room!,
            d.Action == "cancel" ? "cancelled" : "active", d.ActorId, d.Source, d.Intro, d.MorningIntro, d.Note);
        return old is null ? (Card(c, next).Replace(" · #0", ""), null) : (Card(c, next), ChangeNews(c, next, old));
    }
    public static string ChangeNews(BotConfig c, Deadline d, Deadline old)
    {
        var oldDay = DateOnly.Parse(old.Date);
        var was = old.Date != d.Date || old.Period != d.Period || old.Room != d.Room
            ? $"\n<i>Было: {Schedule.DayShort[Schedule.Dow(oldDay)].ToLowerInvariant()}, {oldDay:dd.MM}, {old.Period} пара ({old.Start}), {H(old.Room)}</i>" : "";
        return d.Status == "cancelled"
            ? Lead(d.Intro, "Отбой, эту сдачу отменили.") + $"\n\n❌ <b>Отмена</b> · #{d.Id}\n" + FactsHtml(c, d, true)
            : Lead(d.Intro, "Гайс, по сдаче новости.") + $"\n\n🔄 <b>Изменение</b> · #{d.Id}\n" + FactsHtml(c, d) + was;
    }
    public static string Preview(BotConfig c, Draft d) =>
        $"{(d.Action == "cancel" ? "Отменяем" : d.Action == "update" ? "Обновляем" : "Добавляем")} дедлайн{(d.TargetId is not null ? $" #{d.TargetId}" : "")}\n\n" +
        $"{(d.SubjectId is null ? "Предмет: ?" : c.R.SubjectName(d.SubjectId))} — {d.Work ?? "Работа: ?"}\n" +
        $"Дата: {d.Date ?? "?"}\nПара: {d.Period?.ToString() ?? "?"} · {d.Start ?? "--:--"}–{d.End ?? "--:--"}\nКабинет: {d.Room ?? "?"}\n\n" +
        (string.IsNullOrWhiteSpace(d.Note) ? "" : "Комментарий для бота (в пост дословно не попадёт): " + d.Note + "\n\n") +
        (d.Missing.Length > 0 ? string.Join("\n", d.Missing) : "Проверь факты. Публикую только после подтверждения.");
}
