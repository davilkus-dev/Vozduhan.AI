using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Vozduhan.AI;

/// <summary>A simple argument: type date | int | string | bool | enum.</summary>
public sealed record CapabilityArg(string Name, string Type, bool Required = false, string[]? Options = null, string? Note = null);

/// <summary>What a handler gets: checked role and place, validated arguments, the message it answers and its reply key.</summary>
public sealed record CapabilityCall(long Actor, string Role, string Place, IReadOnlyDictionary<string, object> Args, JsonElement Message, string Key, string Text);

/// <summary>
/// One thing the bot can do. Mode render — a ready answer built by code (HTML), no AI; action — a change or an existing flow
/// (the handler answers itself and returns null, or returns HTML to send).
/// </summary>
public sealed record Capability(string Id, string Title, string Description, string[] Roles, string[] Places, CapabilityArg[] Args, string Mode,
    Func<CapabilityCall, CancellationToken, Task<string?>> Handler)
{
    /// <summary>Help section: schedule, study, editor, developer.</summary>
    public string Section { get; init; } = "study";
    /// <summary>Help lines (Telegram HTML); null — not listed.</summary>
    public string? Help { get; init; }
    /// <summary>Reachable by a slash command, so listed in help even without the Router.</summary>
    public bool Slash { get; init; } = true;
    /// <summary>Help without commands (Router on): how to ask for it in plain words; null — not listed.</summary>
    public string? Say { get; init; }
}

public enum Refusal { None, Unknown, Role, Place, Args }
public sealed record CapabilityResult(Refusal Refusal, string? Html, string? Error = null)
{
    public bool Ok => Refusal == Refusal.None;
}

/// <summary>The catalog of actions. Rights are checked here, at the moment of execution: whatever a model returned grants nothing.</summary>
public sealed class Capabilities(BotConfig config, IReadOnlyList<Capability> all)
{
    public const string Group = "group", Dm = "dm";
    public const string Render = "render", Action = "action";
    public static readonly string[] Everyone = ["member", "head", "admin", "developer"], Editors = ["head", "admin", "developer"], Developer = ["developer"];
    public static readonly string[] Anywhere = [Group, Dm], DmOnly = [Dm];
    public const int StringMax = 1000;

    public IReadOnlyList<Capability> All => all;
    public Capability? Find(string? id) => id is null ? null : all.FirstOrDefault(c => c.Id == id);
    /// <summary>Only what this role may do in this place: other roles' actions are not even named.</summary>
    public IReadOnlyList<Capability> For(string role, string place) => all.Where(c => c.Roles.Contains(role) && c.Places.Contains(place)).ToList();
    public string Role(long actor) => config.AllowedUser(actor) ? config.Role(actor) : "unknown";

    public async Task<CapabilityResult> Execute(long actor, string place, string id, JsonElement args, JsonElement message, string key, string text, CancellationToken ct)
    {
        var cap = Find(id);
        if (cap is null) return new(Refusal.Unknown, null);
        var role = Role(actor);
        if (!cap.Roles.Contains(role)) return new(Refusal.Role, null);
        if (place is not (Group or Dm) || !cap.Places.Contains(place)) return new(Refusal.Place, null);
        var today = DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow));
        if (!TryArgs(cap, args, today, out var values, out var error)) return new(Refusal.Args, null, error);
        return new(Refusal.None, await cap.Handler(new(actor, role, place, values, message, key, text), ct));
    }

    /// <summary>
    /// Validates arguments against the description: known names, types, required ones, enum options. Null and blank count as absent.
    /// Partial — only what is given (the known part of a clarification), required ones may still be missing.
    /// </summary>
    public static bool TryArgs(Capability cap, JsonElement args, DateOnly today, out Dictionary<string, object> values, out string? error, bool partial = false)
    {
        values = new();
        error = null;
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) args = default;
        else if (args.ValueKind != JsonValueKind.Object) { error = "args должен быть объектом"; return false; }
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var p in args.EnumerateObject())
            {
                var def = cap.Args.FirstOrDefault(a => a.Name == p.Name);
                if (def is null) { error = $"у {cap.Id} нет аргумента {p.Name}"; return false; }
                var v = p.Value;
                if (v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))) continue;
                object? parsed = def.Type switch
                {
                    "date" => v.ValueKind == JsonValueKind.String && BotWorker.ParseDate(v.GetString()!.Trim(), today) is { } d
                              && Math.Abs(d.DayNumber - today.DayNumber) <= 400 ? d : null,
                    "int" => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n
                           : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : null,
                    "bool" => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean()
                            : v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) ? b : null,
                    "string" => v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: <= StringMax } s ? s : null,
                    "enum" => v.ValueKind == JsonValueKind.String && (def.Options ?? []).Contains(v.GetString()) ? v.GetString() : null,
                    _ => null
                };
                if (parsed is null) { error = $"неверное значение {p.Name} ({def.Type})"; return false; }
                values[p.Name] = parsed;
            }
        var given = values;
        if (!partial && cap.Args.FirstOrDefault(a => a.Required && !given.ContainsKey(a.Name)) is { } missing) { error = $"не хватает {missing.Name}"; return false; }
        return true;
    }

    /// <summary>Known args of a clarification with the chosen option's args on top (the option wins). Non-objects count as empty.</summary>
    public static JsonElement Merge(JsonElement? known, JsonElement? chosen)
    {
        var all = new Dictionary<string, JsonElement>();
        foreach (var part in new[] { known, chosen })
            if (part is { ValueKind: JsonValueKind.Object } o)
                foreach (var p in o.EnumerateObject()) all[p.Name] = p.Value.Clone();
        return JsonSerializer.SerializeToElement(all, Json.Options);
    }
}

public sealed partial class BotWorker
{
    public Capabilities Catalog { get; }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private DateOnly Today() => DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow));
    private static Task<string?> Done(string? html) => Task.FromResult(html);

    /// <summary>Wrappers over the existing code; the logic stays where it was.</summary>
    private Capability[] BuildCatalog()
    {
        var all = Capabilities.Everyone; var editors = Capabilities.Editors; var dev = Capabilities.Developer;
        var anywhere = Capabilities.Anywhere; var dm = Capabilities.DmOnly;
        const string render = Capabilities.Render, action = Capabilities.Action;
        return
        [
            new("schedule_day", "Пары на день", "расписание пар на одну дату (сегодня, завтра, любой день)", all, anywhere,
                [new("date", "date", true, Note: "YYYY-MM-DD из calendar")], render, (c, _) =>
                {
                    var d = (DateOnly)c.Args["date"]; var today = Today();
                    return Done(ScheduleHtml.Day(config.R, d, d == today ? "Сегодня" : d == today.AddDays(1) ? "Завтра" : null));
                }) { Section = "schedule", Help = "/today — пары сегодня\n/tomorrow — пары завтра\n/date 31.10 — пары на дату", Say = "пары на день — «что сегодня?», «что завтра?», «какие пары 31.10?»" },
            new("next_lesson", "Следующая пара", "какая пара сейчас и какая следующая", all, anywhere, [], render,
                (_, _) => Done(ScheduleHtml.Next(config.R, config.LocalNow(DateTimeOffset.UtcNow))))
                { Section = "schedule", Help = "/next — какая пара следующая", Say = "следующая пара — «какая сейчас пара?», «что дальше?»" },
            new("schedule_week", "Неделя", "расписание на текущую или следующую неделю", all, anywhere,
                [new("next", "bool", Note: "true — следующая неделя")], render, (c, _) =>
                {
                    var today = Today();
                    return Done(ScheduleHtml.Week(config.R, c.Args.TryGetValue("next", out var n) && (bool)n ? today.AddDays(7) : today, today, !config.Features.Router));
                }) { Section = "schedule", Help = "/week — неделя (<code>/week next</code> — следующая)", Say = "неделя целиком — «расписание на неделю», «что на следующей неделе?»" },
            new("deadlines_list", "Дедлайны", "список ближайших подтверждённых сдач", all, anywhere, [], render, (_, _) => Done(DeadlinesHtml()))
                { Help = "/deadlines — ближайшие сдачи", Say = "ближайшие сдачи — «какие дедлайны?», «что сдаём?»" },
            new("subjects", "Предметы", "предметы и кто их ведёт", all, anywhere, [], render,
                (c, _) => Done(SubjectsHtml(c.Place == Capabilities.Dm && c.Role != "member", c.Place == Capabilities.Dm && c.Role == "developer")))
                { Help = "/subjects — предметы и преподаватели", Say = "предметы и преподаватели — «кто ведёт матан?»" },
            new("roster", "Список группы", "список группы по номерам", all, anywhere, [], render,
                (c, _) => Done(RosterHtml(c.Place == Capabilities.Dm && c.Role != "member")))
                { Help = "/roster — список группы", Say = "список группы — «кто у нас в группе?»" },
            new("help", "Что я умею", "что умеет бот", all, anywhere, [], render, (c, _) => Done(Help(c.Role, c.Place))),

            new("web_link", "Веб-админка", "одноразовая ссылка на веб-админку", editors, dm, [], action, (c, _) => Done(H(WebLink(c.Actor))))
                { Section = "editor", Help = "/web — веб-админка (через VPN)", Say = "веб-админка (через VPN) — «дай ссылку на админку»" },
            new("edit_deadline", "Изменить дедлайн", "добавить, изменить, перенести или отменить сдачу (текст сообщения разберёт планировщик и покажет карточку)",
                editors, anywhere, [], action, async (c, ct) =>
                {
                    if (c.Text.StartsWith('/') && !config.Features.Router) await ManageDeadline(c.Message, c.Key, ct);
                    else await AdminDialog(c.Message, c.Key, c.Actor, c.Text, ct);
                    return null;
                }) { Section = "editor", Help = "<code>/deadline id | работа | YYYY-MM-DD | пара [| кабинет]</code> — дедлайн без ИИ\n/edit ID текст · /move ID дата [пара] · /cancel ID", Say = "дедлайны — «сиаод практика №7 в пятницу», «перенеси #12 на среду», «отмени #12»" },
            new("edit_registry", "Изменить реестр", "изменить расписание (разово или постоянно), список группы, предметы, звонки (планировщик покажет карточку)",
                editors, anywhere, [], action, async (c, ct) => { await AdminDialog(c.Message, c.Key, c.Actor, c.Text, ct); return null; }) { Section = "editor", Say = "расписание и список — «31-го матан лекция 6-й парой дистанционно», «Бутова отчислили»" },
            new("delete_deadline", "Удалить дедлайн", "удалить ошибочно созданный дедлайн вместе с его постами (по ID, с подтверждением кнопкой)",
                editors, anywhere, [new("id", "int", true, Note: "ID дедлайна, #N")], action, DeleteDeadlineCard)
                { Section = "editor", Slash = false, Help = "«удали дедлайн #12» — убрать ошибочный дедлайн вместе с постами (с подтверждением)", Say = "ошибочный дедлайн — «удали дедлайн #12» (уберу вместе с постами, после подтверждения)" },
            new("pin", "Закрепить", "закрепить или открепить в чате сообщение, на которое человек ответил своей просьбой", editors, anywhere,
                [new("unpin", "bool", Note: "true — открепить")], action, PinMessage)
                { Section = "editor", Slash = false, Say = "закреп — ответь на сообщение «закрепи» или «открепи»" },
            new("undo", "Откат", "откатить последнее изменение реестра (с подтверждением)", editors, anywhere, [], action,
                async (c, ct) => { await Undo(c.Message, c.Key, c.Actor, ct); return null; })
                { Section = "editor", Help = "/undo — откатить последнее изменение", Say = "откат — «откати последнее изменение»" },
            new("discard_drafts", "Убрать черновики", "выбросить неподтверждённые черновики дедлайна и реестра", editors, anywhere, [], action, (c, _) =>
                {
                    store.DiscardRegistryDraft(c.Actor);
                    store.Delete($"planner_question:{c.Actor}");
                    if (store.Draft(c.Actor) is { } d) store.Discard(c.Actor, d.Token);
                    return Done("Черновики убраны.");
                }) { Section = "editor", Help = "/discard — убрать черновики", Say = "черновики — «выкинь черновики»" },

            new("post_idea", "Пост в Воздуханство", "написать от бота сообщение в общий чат (превью перед отправкой)", dev, dm,
                [new("idea", "string", Note: "о чём написать")], action, async (c, ct) =>
                {
                    await PostPreview(c.Message, c.Key, c.Actor, c.Args.TryGetValue("idea", out var idea) ? (string)idea : null, ct);
                    return null;
                }) { Section = "developer", Help = "/post [идея] — я напишу в Воздуханство", Say = "пост от меня — «напиши в Воздуханство про …» (сначала покажу превью)" },
            new("where", "IDs", "технические ID группы, тем и версия реестра", dev, dm, [], render, (c, _) =>
                Done(H($"Твой ID: {c.Actor}\nРазрешённая группа: {config.GroupId}\nВоздуханство: {config.ChatTopicId}\nДедлайны: {config.DeadlinesTopicId}\nВерсия реестра: {store.RegistryVersion()}")))
                { Section = "developer", Help = "/where — IDs", Say = "технические ID — «покажи айдишники»" },
            new("media_add", "В медиатеку", "добавить присланный стикер или весь его набор в медиатеку", dev, dm,
                [new("scope", "enum", true, ["one", "set"], "one — этот стикер, set — весь набор")], action, AddMedia) { Section = "developer", Slash = false },
        ];
    }

    /// <summary>Help from the catalog: only what this role may do here.</summary>
    private string Help(string role, string place)
    {
        if (config.Features.Router) return SayHelp(role, place);
        var caps = Catalog.For(role, place).Where(c => c.Help is not null && c.Slash).ToList();
        string Lines(string section) => string.Join("\n", caps.Where(c => c.Section == section).Select(c => c.Help));
        var help = "👋 <b>Что я умею</b>\n\n🗓 <b>Расписание</b>\n" + Lines("schedule") + "\n\n📌 <b>Учёба</b>\n" + Lines("study") +
            "\n\n💬 А ещё можно просто спросить: «где матан?», «что завтра?». В Воздуханстве — через @упоминание или ответом на моё сообщение, в личке — как угодно.";
        if (Lines("editor") is { Length: > 0 } editor)
            help += "\n\n🛠 <b>Для редакторов</b>\nПиши обычным текстом — пойму, болтовня это, дедлайн или изменение расписания/списка, и покажу карточку перед сохранением.\n" +
                "<i>«31-го в субботу матан лекция 6-й парой дистанционно», «Бутова отчислили»</i>\n" + editor;
        if (Lines("developer") is { Length: > 0 } developer) help += "\n\n👑 <b>Только разработчик</b>\n" + developer;
        return help;
    }

    /// <summary>Help without commands: from the catalog, what this role may do here, each with phrases to ask it by.</summary>
    private string SayHelp(string role, string place)
    {
        var caps = Catalog.For(role, place).Where(c => c.Say is not null).ToList();
        string Lines(string section) => string.Join("\n", caps.Where(c => c.Section == section).Select(c => "• " + c.Say));
        var help = "👋 <b>Что я умею</b>\n\nКоманды не нужны — пиши обычными словами. " +
            (place == Capabilities.Group ? "Здесь — через @упоминание или ответом на моё сообщение." : "В личке — как угодно, в Воздуханстве — через @упоминание или ответом на моё сообщение.");
        foreach (var (section, title) in new[] { ("schedule", "🗓 <b>Расписание</b>"), ("study", "📌 <b>Учёба</b>"), ("editor", "🛠 <b>Изменения</b>"), ("developer", "👑 <b>Только разработчик</b>") })
            if (Lines(section) is { Length: > 0 } lines) help += "\n\n" + title + "\n" + lines;
        help += "\n\n📊 <b>Опросы</b>\n• опросы и викторины — «создай опрос: идём на пару или нет»";
        if (caps.Any(c => c.Section == "editor"))
            help += "\n\nПеред любым изменением покажу карточку с кнопками — сохраню только после подтверждения. Если что-то непонятно, переспрошу.";
        return help + "\n\nА ещё можно просто поболтать 🙂";
    }

    /// <summary>The DM answer to «/start» without commands: who I am and what I do, in prose, no command list.</summary>
    private static string Greeting(string role) =>
        "Привет! Я бот нашей группы: подскажу расписание с учётом чётных недель и замен, напомню о дедлайнах и могу просто поболтать." +
        (role is "head" or "admin" or "developer" ? " А ещё через меня можно менять дедлайны и расписание — тоже обычным текстом, перед сохранением покажу карточку." : "") +
        "\n\nКоманды не нужны — просто спроси, например «что завтра?» или «какие дедлайны?».";

    /// <summary>Slash commands stay until block 4, but run through the catalog.</summary>
    private async Task SlashCommand(JsonElement m, string key, long actor, string place, string command, string[] words, string text, CancellationToken ct)
    {
        var role = Catalog.Role(actor);
        var today = Today();
        (string? Id, object Args) call = command switch
        {
            "/today" => ("schedule_day", new { date = today.ToString("yyyy-MM-dd") }),
            "/tomorrow" => ("schedule_day", new { date = today.AddDays(1).ToString("yyyy-MM-dd") }),
            "/date" => ("schedule_day", new { date = words.Length > 1 ? words[1] : null }),
            "/week" => ("schedule_week", new { next = words.Length > 1 && (words[1].StartsWith("next", StringComparison.OrdinalIgnoreCase) || words[1].StartsWith("след", StringComparison.OrdinalIgnoreCase)) }),
            "/next" => ("next_lesson", new { }),
            "/deadlines" => ("deadlines_list", new { }),
            "/subjects" => ("subjects", new { }),
            "/roster" => ("roster", new { }),
            "/help" or "/start" => ("help", new { }),
            "/web" => ("web_link", new { }),
            "/post" => ("post_idea", new { idea = text.Length > 5 ? text[5..].Trim() : null }),
            "/undo" => ("undo", new { }),
            "/discard" => ("discard_drafts", new { }),
            "/where" => ("where", new { }),
            "/deadline" or "/edit" or "/move" or "/cancel" => ("edit_deadline", new { }),
            _ => (null, new { })
        };
        if (call.Id is null) { await Respond(m, key, $"Не знаю команду {H(command)} 🤔\n\n" + Help(role, place), ct, html: true); return; }
        var result = await Catalog.Execute(actor, place, call.Id, JsonSerializer.SerializeToElement(call.Args, Json.Options), m, key, text, ct);
        if (result.Ok) { if (result.Html is not null) await Respond(m, key, result.Html, ct, html: true); return; }
        var reply = result.Refusal == Refusal.Args
                ? call.Id == "schedule_day" ? "Формат: <code>/date 31.10</code> или <code>/date 2026-10-31</code>" : "Не понял параметры команды: " + H(result.Error)
            : place == Capabilities.Group ? "Это команда для лички со мной 🙂"
            : call.Id == "edit_deadline" ? "Дедлайны меняют только разработчик и староста. Я могу показать их через /deadlines."
            : $"Не знаю команду {H(command)} 🤔\n\n" + Help(role, place);
        await Respond(m, key, reply, ct, html: true);
    }

    /// <summary>A refused catalog call from the Router: polite, names nothing the person may not do.</summary>
    private static string RefusalText(CapabilityResult r) => r.Refusal switch
    {
        Refusal.Place => "Это только в личке со мной 🙂",
        Refusal.Args => "Не понял, что именно сделать — ничего не трогаю. Скажи подробнее 🙂",
        _ => "Это я сделать не могу 🙂"
    };

    /// <summary>Asks to confirm deleting a mistaken deadline (block 1, C2); the button carries the version it was shown at.</summary>
    private async Task<string?> PinMessage(CapabilityCall c, CancellationToken ct)
    {
        var unpin = c.Args.TryGetValue("unpin", out var u) && (bool)u;
        var target = c.Message.Field("reply_to_message");
        if (target.ValueKind != JsonValueKind.Object || target.Number("message_id") == c.Message.TopicId())
            return unpin ? "Ответь «открепи» на закреплённое сообщение." : "Ответь «закрепи» на сообщение, которое надо закрепить.";
        try { await telegram.Pin(c.Message.Field("chat").Number("id"), (int)target.Number("message_id"), !unpin, ct); }
        catch (TelegramError e) when (e.Code is 400 or 403) { return "Не вышло: у меня нет права закреплять сообщения в этом чате."; }
        store.SavePost(c.Key, 0, "");
        if (c.Message.Field("chat").Number("id") != config.GroupId) return unpin ? "Открепил." : "Закрепил.";
        try { await telegram.React(c.Message.Field("chat").Number("id"), (int)c.Message.Number("message_id"), "👌", ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        return null;
    }

    private async Task<string?> DeleteDeadlineCard(CapabilityCall c, CancellationToken ct)
    {
        var id = (int)c.Args["id"];
        var d = store.AllDeadlines().FirstOrDefault(x => x.Id == id && x.Status != "deleted");
        if (d is null) return $"Дедлайна #{id} нет (или он уже удалён). " + (config.Features.Router ? "Список — спроси «какие дедлайны?»." : "Список — /deadlines.");
        var posts = (store.Post("deadline:" + id) is null ? 0 : 1) + store.Posts($"change:{id}:").Count(p => !p.Key.Contains(":part:"));
        var text = $"🗑 <b>Удалить дедлайн #{d.Id}?</b>\n\n{H(Deadlines.Facts(config, d))}\n\n" +
            $"Он пропадёт из списков и сводок, его посты в «Дедлайнах» ({posts}) и будильники уберу. ID и история останутся.\n" +
            "<i>Если сдачу отменили по-настоящему — лучше попроси отменить: группа увидит отмену.</i>";
        await Respond(c.Message, c.Key, text, ct, Buttons($"ddel:{d.Id}:{d.Version}", $"dkeep:{d.Id}", "Удалить"), html: true);
        return null;
    }

    private async Task DeleteCallback(JsonElement cb, long actor, string kind, string value, CancellationToken ct)
    {
        var messageId = (int)cb.Field("message").Number("message_id");
        var chat = cb.Field("message").Field("chat").Number("id");
        var parts = value.Split(':');
        if (kind == "dkeep") { await SafeCallback(cb.Text("id")!, "Отменено", ct); await telegram.Edit(chat, messageId, "Дедлайн не трогаю.", ct); return; }
        if (parts.Length != 2 || !int.TryParse(parts[0], out var id) || !int.TryParse(parts[1], out var version)) return;
        await publicationGate.WaitAsync(ct);
        try
        {
            if (store.AllDeadlines().FirstOrDefault(x => x.Id == id) is not { Status: not "deleted" }) { await SafeCallback(cb.Text("id")!, "Карточка уже обработана.", ct); return; }
            await SafeCallback(cb.Text("id")!, "Удаляю…", ct);
            Deadline d;
            try { d = store.DeleteDeadline(actor, id, version, DateTimeOffset.UtcNow); }
            catch (InvalidOperationException e) { await telegram.Send(chat, cb.Field("message").TopicId(), "Не удалено: " + e.Message, ct); return; }
            await telegram.Edit(chat, messageId, $"🗑 Удалён дедлайн:\n{Deadlines.Facts(config, d)}\nЕго посты в «Дедлайнах» уберу в течение минуты.", ct);
        }
        finally { publicationGate.Release(); }
    }

    private string DeadlinesHtml()
    {
        var r = config.R; var today = Today();
        var rows = store.AllDeadlines().Where(x => x.Status == "active" && Deadlines.Ends(config, x) >= DateTimeOffset.UtcNow).ToArray();
        if (rows.Length == 0) return "⏰ <b>Дедлайны</b>\n\nПодтверждённых будущих сдач пока нет 🎉";
        return "⏰ <b>Ближайшие дедлайны</b>" + string.Concat(rows.Take(25).Select(x =>
        {
            var day = DateOnly.Parse(x.Date);
            var left = day.DayNumber - today.DayNumber;
            var when = left == 0 ? "сегодня" : left == 1 ? "завтра" : $"{Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()} {day:dd.MM}";
            return $"\n\n<b>{H(r.SubjectName(x.SubjectId))}</b> — {H(x.Work)}\n📅 {when}, {x.Period} пара ({x.Start}) · 🚪 <code>{H(x.Room)}</code>" +
                 $" <i>#{x.Id}</i>";
        }));
    }

    private string SubjectsHtml(bool editor, bool owner)
    {
        var r = config.R; var today = Today();
        if (r.Subjects.Length == 0) return "Предметов пока нет.";
        return "🎓 <b>Предметы</b>" + string.Concat(r.Subjects.Where(x => !x.Archived).Select(x =>
        {
            var lessons = r.Lessons.Where(l => l.SubjectId == x.Id && (l.Until is null || DateOnly.ParseExact(l.Until, "yyyy-MM-dd") >= today)).ToArray();
            var who = lessons.GroupBy(l => l.Kind).OrderBy(g => g.Key == "lecture" ? 0 : 1).Select(g =>
                $"{(g.Key == "lecture" ? "Лекции" : g.Key == "lab" ? "Лабы" : "Практики")}: " +
                string.Join(", ", g.Select(l => l.Teacher).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Select(H).DefaultIfEmpty("—")));
            return $"\n\n<b>{H(x.Name)}</b>" + (editor ? $" <code>{H(x.Id)}</code>" : "") +
                (lessons.Length > 0 ? "\n" + string.Join("\n", who) : "") +
                (owner && (x.Aliases ?? []).Length > 0 ? "\n🤫 сленг: " + H(string.Join(", ", x.Aliases!)) : "") +
                (owner && !string.IsNullOrWhiteSpace(x.AdminComment) ? "\n🤫 " + H(x.AdminComment) : "");
        }));
    }

    private string RosterHtml(bool editor)
    {
        var r = config.R;
        var active = r.Members.Where(x => x.Active).OrderBy(x => x.ListNumber == 0 ? int.MaxValue : x.ListNumber).ThenBy(x => x.FullName)
            .Select(x => $"{(x.ListNumber > 0 ? x.ListNumber + "." : "•")} {H(x.FullName)}" + (x.Role == "head" ? " ⭐" : "") +
                (editor ? (string.IsNullOrWhiteSpace(x.Username) ? "" : " @" + H(x.Username)) + (x.UserId is null ? " ❔" : "") : ""));
        var text = $"👥 <b>Список группы</b> · {r.Members.Count(x => x.Active)} чел.\n<blockquote expandable>" + string.Join("\n", active) + "</blockquote>";
        if (editor) text += "\n<i>⭐ староста" + (r.Members.Any(x => x.Active && x.UserId is null) ? " · ❔ нет Telegram ID — бот такому не отвечает" : "") + "</i>";
        var gone = r.Members.Where(x => !x.Active).ToArray();
        if (editor && gone.Length > 0) text += "\n\n<b>Неактивные</b>\n" + string.Join("\n", gone.Select(x => $"• {H(x.FullName)}" + (string.IsNullOrWhiteSpace(x.Note) ? "" : $" — <i>{H(x.Note)}</i>")));
        return text;
    }
}
