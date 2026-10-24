using System.Text.Json;

namespace Vozduhan.AI;

/// <summary>
/// Semantic registry edits shared by the Telegram planner and tests. Every operation is applied to a copy,
/// the copy is validated as a whole and permissions are checked on the before/after difference.
/// </summary>
public static class RegistryOps
{
    public const string Schema = """
        Операции (поле op обязательно, лишние поля не добавляй):
        member_add {fullName, shortName, username?, userId?, listNumber?, telegramName?, email?}
        member_update {member: ссылка, fullName?, shortName?, username?, userId?, unbind?: true, listNumber?, telegramName?, email?, note?, adminNote?}
          note — техническая заметка (почему ушёл); adminNote — скрытая заметка для шуток бота о человеке (только разработчик)
        member_deactivate {member: ссылка, reason?} — отчислен/перевёлся/ушёл: запись сохраняется неактивной
        member_activate {member: ссылка}
        head_set {member: ссылка} — назначить старосту (только разработчик); head_clear {} — снять старосту (только разработчик)
          ссылка на участника: {key?} | {userId?} | {listNumber?} | {username?} | {name?} — используй key из реестра, если он известен
        subject_upsert {id, name?, shortName?, aliases?: [..] (полная замена), addAliases?: [..], removeAliases?: [..], adminComment?, archived?}
        lesson_add {subjectId, kind: lecture|practice|lab, day: 1..7, period, weeks?: all|odd|even, room?, teacher?, campus?, online?, note?, from?, until?}
        lesson_update {lessonId, from?: YYYY-MM-DD, subjectId?, kind?, day?, period?, weeks?, room?, teacher?, campus?, online?, note?, until?}
          from — с какой даты изменение действует постоянно; без from правится сама строка расписания целиком
        lesson_remove {lessonId, from?: YYYY-MM-DD} — с from пара перестаёт идти с этой даты, без from удаляется из расписания
        change_add {date, type: add|cancel|modify|dayoff, lessonId?, subjectId?, kind?, period?, room?, teacher?, campus?, online?, note?}
          разовое изменение только на одну дату: add — дополнительная пара (subjectId, kind, period обязательны);
          cancel — отмена (lessonId или subjectId [+period]); modify — замена кабинета/пары/формата у lessonId; dayoff — весь день без пар
        change_remove {changeId}
        period_set {number, start: ЧЧ:ММ, end: ЧЧ:ММ}; period_remove {number}
        settings_set {weekOneMonday?, termEnd? ("" — без конца), defaultCampus?}
        knowledge_set {text}; knowledge_append {text}
        """;

    public static (Registry Result, List<string> Lines) ApplyAll(Registry current, IEnumerable<JsonElement> ops, long ownerId, bool actorIsOwner)
    {
        var r = current.Clone().Normalize();
        var lines = new List<string>();
        foreach (var op in ops) lines.Add(Apply(r, op, actorIsOwner));
        if (lines.Count == 0) throw new InvalidOperationException("Нет изменений.");
        r.Normalize();
        try { r.Validate(ownerId); }
        catch (InvalidDataException e) { throw new InvalidOperationException(e.Message); }
        CheckPermissions(current, r, actorIsOwner);
        return (r, lines);
    }

    /// <summary>The head may do everything except changing who the head is. The owner is fixed in the server config.</summary>
    public static void CheckPermissions(Registry before, Registry after, bool actorIsOwner)
    {
        if (actorIsOwner) return;
        string Heads(Registry x) => string.Join(",", x.Members.Where(m => m.Active && m.Role == "head").Select(m => m.Key + ":" + m.UserId).OrderBy(k => k));
        if (Heads(before) != Heads(after)) throw new InvalidOperationException("Сменить или снять старосту может только разработчик.");
        if (before.SecretPrint() != after.SecretPrint())
            throw new InvalidOperationException("Сленг, комментарии к предметам, заметки о людях и текст знаний меняет только разработчик.");
    }
    /// <summary>Lines about owner-only data; they are never shown to the head.</summary>
    public static bool IsSecret(string line) => line.StartsWith("🤫") || line.StartsWith("🧠");
    public static List<string> ForViewer(IEnumerable<string> lines, bool owner) => lines.Where(l => owner || !IsSecret(l)).ToList();

    static string? S(JsonElement e, string k) => e.Field(k).ValueKind switch
    {
        JsonValueKind.String => e.Field(k).GetString()?.Trim(),
        JsonValueKind.Number => e.Field(k).GetRawText(),
        _ => null
    };
    static bool Has(JsonElement e, string k) => e.Field(k).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
    static int? I(JsonElement e, string k) => e.Field(k).ValueKind switch
    {
        JsonValueKind.Number when e.Field(k).TryGetInt32(out var v) => v,
        JsonValueKind.String when int.TryParse(e.Field(k).GetString(), out var v) => v,
        _ => null
    };
    static long? L(JsonElement e, string k) => e.Field(k).ValueKind switch
    {
        JsonValueKind.Number when e.Field(k).TryGetInt64(out var v) => v,
        JsonValueKind.String when long.TryParse(e.Field(k).GetString(), out var v) => v,
        _ => null
    };
    static bool? B(JsonElement e, string k) => e.Field(k).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    static string[]? A(JsonElement e, string k) => e.Field(k).ValueKind == JsonValueKind.Array
        ? e.Field(k).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToArray() : null;
    static string Req(JsonElement e, string k, string what) => S(e, k) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Не указано: {what}.");
    static string? Date(JsonElement e, string k)
    {
        var v = S(e, k);
        if (string.IsNullOrEmpty(v)) return null;
        return DateOnly.TryParseExact(v, "yyyy-MM-dd", out _) ? v : throw new InvalidOperationException($"Дата {k} должна быть в формате YYYY-MM-DD, получено «{v}».");
    }

    public static int FindMember(Registry r, JsonElement reference)
    {
        if (reference.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Не понял, о каком участнике речь.");
        IEnumerable<int> all = Enumerable.Range(0, r.Members.Length);
        List<int> Pick(Func<Member, bool> f) => all.Where(i => f(r.Members[i])).OrderByDescending(i => r.Members[i].Active).ToList();
        List<int> hits;
        if (S(reference, "key") is { } key) hits = Pick(m => m.Key == key);
        else if (L(reference, "userId") is { } id) hits = Pick(m => m.UserId == id);
        else if (I(reference, "listNumber") is { } n) { hits = Pick(m => m.ListNumber == n && m.Active); if (hits.Count == 0) hits = Pick(m => m.ListNumber == n); }
        else if (S(reference, "username") is { Length: > 0 } u) hits = Pick(m => m.Username.Equals(u.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
        else if (S(reference, "name") is { Length: > 0 } name)
        {
            var words = name.ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            hits = Pick(m => words.All(w => (m.FullName + " " + m.ShortName + " " + m.TelegramName).ToLowerInvariant().Replace('ё', 'е').Contains(w)));
            var exact = hits.Where(i => r.Members[i].FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) hits = exact;
            var active = hits.Where(i => r.Members[i].Active).ToList();
            if (hits.Count > 1 && active.Count == 1) hits = active;
        }
        else throw new InvalidOperationException("Укажи участника: номер по списку, @username или ФИО.");
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw new InvalidOperationException("Такого участника нет в списке: " + reference.GetRawText());
        throw new InvalidOperationException("Подходит несколько участников: " + string.Join("; ", hits.Take(5).Select(i => Who(r.Members[i]))) + ". Уточни номер по списку.");
    }
    public static string Who(Member m) => $"№{m.ListNumber} {m.FullName}" + (string.IsNullOrWhiteSpace(m.Username) ? "" : " @" + m.Username) + (m.Active ? "" : " (неактивен)");

    public static string Apply(Registry r, JsonElement op, bool actorIsOwner)
    {
        var name = S(op, "op") ?? "";
        switch (name)
        {
            case "member_add":
            {
                var userId = L(op, "userId");
                if (userId is not null && r.Members.FirstOrDefault(m => m.UserId == userId) is { } existing)
                    throw new InvalidOperationException($"Этот Telegram ID уже у {Who(existing)}." + (existing.Active ? "" : " Верни запись через member_activate."));
                var number = I(op, "listNumber") ?? 0;
                if (number <= 0) number = r.Members.Where(m => m.Active).Select(m => m.ListNumber).DefaultIfEmpty(0).Max() + 1;
                var m = new Member(userId, Req(op, "fullName", "ФИО"), Req(op, "shortName", "краткое имя"), (S(op, "username") ?? "").TrimStart('@'),
                    number, "member", true, S(op, "telegramName"), S(op, "email"));
                r.Members = [.. r.Members, m];
                r.Normalize();
                return "➕ В список: " + Who(r.Members[^1]) + (userId is null ? " (без Telegram ID — доступа пока нет)" : "");
            }
            case "member_update":
            {
                var i = FindMember(r, op.Field("member"));
                var m = r.Members[i];
                var before = Who(m);
                if (L(op, "userId") is { } uid && r.Members.Where((x, j) => j != i).FirstOrDefault(x => x.UserId == uid) is { } other)
                    throw new InvalidOperationException($"Этот Telegram ID уже у {Who(other)}.");
                m = m with
                {
                    FullName = S(op, "fullName") ?? m.FullName, ShortName = S(op, "shortName") ?? m.ShortName,
                    Username = S(op, "username")?.TrimStart('@') ?? m.Username, ListNumber = I(op, "listNumber") ?? m.ListNumber,
                    UserId = B(op, "unbind") == true ? null : L(op, "userId") ?? m.UserId,
                    TelegramName = Has(op, "telegramName") ? S(op, "telegramName") : m.TelegramName,
                    Email = Has(op, "email") ? S(op, "email") : m.Email, Note = Has(op, "note") ? S(op, "note") : m.Note,
                    AdminNote = Has(op, "adminNote") ? (S(op, "adminNote") is { Length: > 0 } an ? an : null) : m.AdminNote
                };
                if (m.UserId is null && m.Role == "head") throw new InvalidOperationException("У старосты нельзя убрать Telegram ID — сначала назначь другого.");
                var onlyNote = r.Members[i] with { AdminNote = m.AdminNote } == m;
                r.Members[i] = m;
                if (onlyNote && Has(op, "adminNote")) return $"🤫 Заметка о {m.ShortName}: " + (m.AdminNote is null ? "убрана" : "«" + Conversation.Limit(m.AdminNote, 150) + "»");
                return $"✏️ {before} → {Who(m)}" + (m.UserId is { } bound ? $" · ID {bound}" : " · без Telegram ID");
            }
            case "member_deactivate":
            {
                var i = FindMember(r, op.Field("member"));
                var m = r.Members[i];
                if (!m.Active) throw new InvalidOperationException(Who(m) + " уже неактивен.");
                if (m.Role == "head") throw new InvalidOperationException("Нельзя убрать старосту из списка — сначала разработчик назначает другого.");
                var reason = S(op, "reason");
                r.Members[i] = m with { Active = false, Note = string.IsNullOrWhiteSpace(reason) ? m.Note : reason };
                return $"➖ Убран из активного списка: {Who(m)}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})") + ". Запись и история сохранены.";
            }
            case "member_activate":
            {
                var i = FindMember(r, op.Field("member"));
                var m = r.Members[i];
                if (m.Active) throw new InvalidOperationException(Who(m) + " и так активен.");
                var number = m.ListNumber;
                if (number > 0 && r.Members.Any(x => x.Active && x.ListNumber == number))
                    number = r.Members.Where(x => x.Active).Select(x => x.ListNumber).DefaultIfEmpty(0).Max() + 1;
                r.Members[i] = m with { Active = true, ListNumber = number };
                return "↩️ Возвращён в список: " + Who(r.Members[i]);
            }
            case "head_set":
            {
                if (!actorIsOwner) throw new InvalidOperationException("Назначить старосту может только разработчик.");
                var i = FindMember(r, op.Field("member"));
                var m = r.Members[i];
                if (!m.Active || m.UserId is null) throw new InvalidOperationException("Старостой может быть только активный участник с Telegram ID.");
                r.Members = r.Members.Select((x, j) => x with { Role = j == i ? "head" : "member" }).ToArray();
                return "⭐ Староста: " + Who(m);
            }
            case "head_clear":
            {
                if (!actorIsOwner) throw new InvalidOperationException("Снять старосту может только разработчик.");
                r.Members = r.Members.Select(x => x with { Role = "member" }).ToArray();
                return "⭐ Старосты больше нет";
            }
            case "subject_upsert":
            {
                var id = Req(op, "id", "ID предмета").ToLowerInvariant();
                var i = Array.FindIndex(r.Subjects, s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var s = i >= 0 ? r.Subjects[i] : new Subject(id, Req(op, "name", "название нового предмета"));
                var aliases = (A(op, "aliases") ?? s.Aliases ?? []).Concat(A(op, "addAliases") ?? [])
                    .Where(a => !(A(op, "removeAliases") ?? []).Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
                s = s with
                {
                    Name = S(op, "name") ?? s.Name, Aliases = aliases,
                    ShortName = Has(op, "shortName") ? S(op, "shortName") : s.ShortName,
                    AdminComment = Has(op, "adminComment") ? (S(op, "adminComment") is { Length: > 0 } c ? c : null) : s.AdminComment,
                    Archived = B(op, "archived") ?? s.Archived
                };
                foreach (var a in aliases)
                    if (r.Subjects.FirstOrDefault(x => x.Id != s.Id && (x.Aliases ?? []).Contains(a, StringComparer.OrdinalIgnoreCase)) is { } clash)
                        throw new InvalidOperationException($"Сленг «{a}» уже у предмета {clash.Name}.");
                if (i >= 0) r.Subjects[i] = s; else r.Subjects = [.. r.Subjects, s];
                var parts = new List<string>();
                if (i < 0) parts.Add("новый предмет");
                if (Has(op, "name")) parts.Add("название");
                if (Has(op, "aliases") || Has(op, "addAliases") || Has(op, "removeAliases")) parts.Add("сленг: " + string.Join(", ", aliases));
                if (Has(op, "adminComment")) parts.Add(s.AdminComment is null ? "комментарий убран" : "комментарий для бота: «" + Conversation.Limit(s.AdminComment, 120) + "»");
                if (Has(op, "archived")) parts.Add(s.Archived ? "в архив" : "из архива");
                if (Has(op, "shortName")) parts.Add("короткое имя " + s.ShortName);
                var secret = Has(op, "aliases") || Has(op, "addAliases") || Has(op, "removeAliases") || Has(op, "adminComment");
                return $"{(secret ? "🤫" : "📚")} {s.Name} ({s.Id}): " + (parts.Count == 0 ? "без изменений" : string.Join("; ", parts));
            }
            case "lesson_add":
            {
                var l = ReadLesson(op, new Lesson(Req(op, "subjectId", "предмет").ToLowerInvariant(), I(op, "day") ?? 0, I(op, "period") ?? 0)
                    { Kind = S(op, "kind") ?? "practice" }) with { Id = r.NextLessonId() };
                r.Lessons = [.. r.Lessons, l];
                return "➕ Пара в расписание: " + Schedule.Describe(r, l);
            }
            case "lesson_update":
            {
                var id = Req(op, "lessonId", "ID пары");
                var i = Array.FindIndex(r.Lessons, l => l.Id == id);
                if (i < 0) throw new InvalidOperationException($"Нет пары {id}.");
                var old = r.Lessons[i];
                var from = Date(op, "from");
                var changed = ReadLesson(op, old);
                if (from is not null && (old.From is null || string.CompareOrdinal(old.From, from) < 0))
                {
                    var until = DateOnly.ParseExact(from, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd");
                    r.Lessons[i] = old with { Until = until };
                    var next = changed with { Id = r.NextLessonId(), From = from };
                    r.Lessons = [.. r.Lessons, next];
                    return $"🔄 С {from} постоянно: {Schedule.Describe(r, next)} (раньше: {Schedule.Describe(r, old)})";
                }
                r.Lessons[i] = changed;
                return $"🔄 {Schedule.Describe(r, changed)} (было: {Schedule.Describe(r, old)})";
            }
            case "lesson_remove":
            {
                var id = Req(op, "lessonId", "ID пары");
                var i = Array.FindIndex(r.Lessons, l => l.Id == id);
                if (i < 0) throw new InvalidOperationException($"Нет пары {id}.");
                var old = r.Lessons[i];
                var from = Date(op, "from");
                if (from is not null && (old.From is null || string.CompareOrdinal(old.From, from) < 0))
                {
                    r.Lessons[i] = old with { Until = DateOnly.ParseExact(from, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd") };
                    return $"➖ С {from} больше не идёт: {Schedule.Describe(r, old)}";
                }
                if (r.Changes.Any(c => c.LessonId == id)) throw new InvalidOperationException($"На пару {id} ссылаются разовые изменения — укажи from или сначала убери их.");
                r.Lessons = r.Lessons.Where(l => l.Id != id).ToArray();
                return "➖ Удалена из расписания: " + Schedule.Describe(r, old);
            }
            case "change_add":
            {
                var type = Req(op, "type", "тип изменения");
                var c = new ScheduleChange(Date(op, "date") ?? throw new InvalidOperationException("Не указана дата изменения."), type)
                {
                    Id = r.NextChangeId(), LessonId = S(op, "lessonId"), SubjectId = S(op, "subjectId")?.ToLowerInvariant(), Kind = S(op, "kind"),
                    Period = I(op, "period"), Room = S(op, "room"), Teacher = S(op, "teacher"), Campus = S(op, "campus"),
                    Online = B(op, "online"), Note = S(op, "note")
                };
                if (c.LessonId is not null && r.Lessons.FirstOrDefault(l => l.Id == c.LessonId) is { } lesson &&
                    !Schedule.Applies(r, lesson, DateOnly.ParseExact(c.Date, "yyyy-MM-dd")))
                    throw new InvalidOperationException($"Пара {c.LessonId} не стоит в расписании на {c.Date}: {Schedule.Describe(r, lesson)}.");
                r.Changes = [.. r.Changes, c];
                return "📌 Разово " + Schedule.Describe(r, c);
            }
            case "change_remove":
            {
                var id = Req(op, "changeId", "ID изменения");
                var c = r.Changes.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException($"Нет изменения {id}.");
                var text = Schedule.Describe(r, c);
                r.Changes = r.Changes.Where(x => x.Id != id).ToArray();
                return "🗑 Убрано разовое изменение " + text;
            }
            case "period_set":
            {
                var n = I(op, "number") ?? throw new InvalidOperationException("Номер пары?");
                var p = new Period(n, Req(op, "start", "начало"), Req(op, "end", "конец"));
                r.Periods = [.. r.Periods.Where(x => x.Number != n), p];
                return $"🔔 {n} пара: {p.Start}–{p.End}";
            }
            case "period_remove":
            {
                var n = I(op, "number") ?? throw new InvalidOperationException("Номер пары?");
                if (r.Lessons.Any(l => l.Period == n) || r.Changes.Any(c => c.Period == n)) throw new InvalidOperationException($"{n}-я пара используется в расписании.");
                r.Periods = r.Periods.Where(x => x.Number != n).ToArray();
                return $"🔔 Убрана {n}-я пара из звонков";
            }
            case "settings_set":
            {
                var parts = new List<string>();
                if (Date(op, "weekOneMonday") is { } w) { r.WeekOneMonday = w; parts.Add("первая неделя с " + w); }
                if (Has(op, "termEnd")) { r.TermEnd = Date(op, "termEnd"); parts.Add("конец семестра: " + (r.TermEnd ?? "не задан")); }
                if (Has(op, "defaultCampus")) { r.DefaultCampus = S(op, "defaultCampus") ?? ""; parts.Add("корпус по умолчанию: " + r.DefaultCampus); }
                if (parts.Count == 0) throw new InvalidOperationException("Какие настройки менять?");
                return "⚙️ " + string.Join("; ", parts);
            }
            case "knowledge_set":
                r.Knowledge = S(op, "text") ?? "";
                return $"🧠 Текст знаний заменён ({r.Knowledge.Length} символов)";
            case "knowledge_append":
            {
                var text = Req(op, "text", "текст");
                r.Knowledge = string.IsNullOrWhiteSpace(r.Knowledge) ? text : r.Knowledge.TrimEnd() + "\n" + text;
                return "🧠 В знания добавлено: «" + Conversation.Limit(text, 200) + "»";
            }
            default:
                throw new InvalidOperationException($"Неизвестная операция «{name}».");
        }
    }

    static Lesson ReadLesson(JsonElement op, Lesson l) => l with
    {
        SubjectId = S(op, "subjectId")?.ToLowerInvariant() ?? l.SubjectId,
        Kind = S(op, "kind") ?? l.Kind,
        DayOfWeek = I(op, "day") ?? I(op, "dayOfWeek") ?? l.DayOfWeek,
        Period = I(op, "period") ?? l.Period,
        Weeks = S(op, "weeks") ?? l.Weeks,
        Room = Has(op, "room") ? S(op, "room") : l.Room,
        Teacher = Has(op, "teacher") ? S(op, "teacher") : l.Teacher,
        Campus = Has(op, "campus") ? S(op, "campus") : l.Campus,
        Online = B(op, "online") ?? l.Online,
        Note = Has(op, "note") ? S(op, "note") : l.Note,
        Until = Has(op, "until") ? Date(op, "until") : l.Until,
        From = l.From ?? (op.Field("op").GetString() == "lesson_add" ? Date(op, "from") : null)
    };

    /// <summary>Human summary of the difference between two registry versions (full replace, undo, restore).</summary>
    public static List<string> Diff(Registry a, Registry b)
    {
        var lines = new List<string>();
        var am = a.Members.ToDictionary(m => m.Key ?? "", m => m);
        foreach (var m in b.Members)
        {
            if (!am.TryGetValue(m.Key ?? "", out var old)) { lines.Add("➕ В список: " + Who(m)); continue; }
            if (old.Active && !m.Active) lines.Add("➖ Неактивен: " + Who(old));
            else if (!old.Active && m.Active) lines.Add("↩️ Возвращён: " + Who(m));
            else if (old.Role != m.Role) lines.Add($"⭐ {Who(m)}: роль {old.Role} → {m.Role}");
            else
            {
                if (old with { AdminNote = null } != m with { AdminNote = null })
                    lines.Add($"✏️ {Who(old)} → {Who(m)}" + (old.UserId != m.UserId ? $" (ID {old.UserId?.ToString() ?? "—"} → {m.UserId?.ToString() ?? "—"})" : ""));
                if (old.AdminNote != m.AdminNote) lines.Add($"🤫 Заметка о {m.ShortName}: " + (m.AdminNote is null ? "убрана" : "«" + Conversation.Limit(m.AdminNote, 150) + "»"));
            }
        }
        foreach (var m in a.Members.Where(m => b.Members.All(x => x.Key != m.Key))) lines.Add("🗑 Запись удалена полностью: " + Who(m));
        foreach (var s in b.Subjects)
        {
            var old = a.Subject(s.Id);
            if (old is null) { lines.Add($"📚 Новый предмет: {s.Name} ({s.Id})"); if ((s.Aliases ?? []).Length > 0 || s.AdminComment is not null) lines.Add($"🤫 {s.Name}: сленг/комментарий"); continue; }
            if (Json.Write(old with { Aliases = null, AdminComment = null }) != Json.Write(s with { Aliases = null, AdminComment = null })) lines.Add($"📚 Изменён предмет: {s.Name} ({s.Id})");
            if (Json.Write(old.Aliases ?? []) != Json.Write(s.Aliases ?? [])) lines.Add($"🤫 {s.Name}: сленг — " + string.Join(", ", s.Aliases ?? []));
            if (old.AdminComment != s.AdminComment) lines.Add($"🤫 {s.Name}: комментарий для бота — " + (s.AdminComment is null ? "убран" : "«" + Conversation.Limit(s.AdminComment, 150) + "»"));
        }
        foreach (var s in a.Subjects.Where(s => b.Subject(s.Id) is null)) lines.Add($"🗑 Удалён предмет: {s.Name} ({s.Id})");
        foreach (var l in b.Lessons)
        {
            var old = a.Lessons.FirstOrDefault(x => x.Id == l.Id);
            if (old is null) lines.Add("➕ Пара: " + Schedule.Describe(b, l));
            else if (old != l) lines.Add("🔄 " + Schedule.Describe(b, l));
        }
        foreach (var l in a.Lessons.Where(l => b.Lessons.All(x => x.Id != l.Id))) lines.Add("➖ Пара: " + Schedule.Describe(a, l));
        foreach (var c in b.Changes.Where(c => a.Changes.All(x => x.Id != c.Id || x != c))) lines.Add("📌 Разово " + Schedule.Describe(b, c));
        foreach (var c in a.Changes.Where(c => b.Changes.All(x => x.Id != c.Id))) lines.Add("🗑 Убрано разовое " + Schedule.Describe(a, c));
        if (Json.Write(a.Periods) != Json.Write(b.Periods)) lines.Add("🔔 Звонки: " + string.Join(", ", b.Periods.Select(p => $"{p.Number}: {p.Start}–{p.End}")));
        if (a.WeekOneMonday != b.WeekOneMonday) lines.Add("⚙️ Первая неделя с " + b.WeekOneMonday);
        if (a.TermEnd != b.TermEnd) lines.Add("⚙️ Конец семестра: " + (b.TermEnd ?? "не задан"));
        if (a.DefaultCampus != b.DefaultCampus) lines.Add("⚙️ Корпус по умолчанию: " + b.DefaultCampus);
        if (a.Knowledge != b.Knowledge) lines.Add($"🧠 Текст знаний изменён ({a.Knowledge.Length} → {b.Knowledge.Length} символов)");
        return lines;
    }
}
