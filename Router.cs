using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// What to do with one message. Mode: render | action | writer | clarify | react_only, plus «unavailable» (AI down, no keyword matched).
/// Source: router (the model), keywords (AI down, matched by code), fallback (invalid twice: behave as before the Router).
/// </summary>
public sealed record RouteDecision(string? Reaction, string Mode, string? Capability, JsonElement? Args, string? Question, string Reason, string Source)
{
    /// <summary>Clarify only: up to four answers as buttons; each, merged with Args, is a complete call of Capability.</summary>
    public IReadOnlyList<RouteOption>? Options { get; init; }
}

public sealed partial class BotWorker
{
    public const int RouteQuestionMax = 300, RouteRecent = 6, RouteRecentChars = 200;
    public static readonly string[] RouteModes = ["render", "action", "writer", "clarify", "react_only"];
    private static readonly TimeSpan RouteKeep = TimeSpan.FromDays(2);

    private static string Emoji(string s) => s.Replace("️", "").Trim();
    /// <summary>config.Reactions that Telegram really accepts.</summary>
    private string[] RouterReactions() => config.Reactions.Select(Emoji).Where(TelegramClient.ReactionEmoji.Contains).Distinct().ToArray();

    /// <summary>One message with the Router on: decide (one cheap call), react at once, then the chosen mode.</summary>
    private async Task Routed(JsonElement m, string key, long actor, bool isPrivate, string? joined, bool addressed, CancellationToken ct)
    {
        var chat = m.Field("chat").Number("id"); var id = (int)m.Number("message_id");
        var place = isPrivate ? Capabilities.Dm : Capabilities.Group;
        var editor = isPrivate && config.CanEdit(actor);
        var text = m.Text("text") ?? m.Text("caption") ?? "";
        var full = joined is null ? text : joined + "\n" + text;
        // A Writer answer generated before a restart is delivered as is.
        if (store.Post("answer:" + key) is not null) { await ChatGuarded(m, key, isPrivate, actor, ct); return; }
        var stored = StoredRoute(chat, id);
        if (stored is null && !editor && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - m.Number("date") > 600) return;
        var decision = stored;
        if (decision is null)
        {
            // The person's open question in this chat goes to the Router with the message: a text answer may fill it.
            var slot = store.ActiveSlot(actor, chat, DateTimeOffset.UtcNow) is { } open && open.Topic == m.TopicId() ? open : null;
            await digest.Prepare(Related(m), "digest", ct);
            decision = await Route(m, actor, place, joined, full, slot, ct);
            var fills = slot is not null && decision.Capability == slot.Capability && decision.Mode is "render" or "action" or "clarify";
            // Not addressed to the bot (only let through for the open question) and not an answer to it: stay out of the conversation.
            if (!addressed && !fills) decision = new(null, "ignore", null, null, null, "не боту и не ответ на вопрос", decision.Source);
            store.Set(RouteKey(chat, id), Json.Write(new { decision, expires = DateTimeOffset.UtcNow.Add(RouteKeep).ToUnixTimeSeconds() }));
            if (slot is not null && decision.Mode != "clarify" && store.CloseSlot(slot.Id, fills ? "answered" : "closed")) await DropButtons(slot, ct);
        }
        if (decision.Reaction is { } emoji)
            try { await telegram.React(chat, id, emoji, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        switch (decision.Mode)
        {
            case "render" or "action":
                var result = await Catalog.Execute(actor, place, decision.Capability ?? "", decision.Args ?? default, m, key, full, ct);
                if (!result.Ok) await Respond(m, key, RefusalText(result), ct, html: true);
                else if (result.Html is not null) await Respond(m, key, result.Html, ct, html: true);
                return;
            case "clarify":
                await Clarify(m, key, actor, decision, ct); return;
            case "react_only" or "ignore":
                store.SavePost(key, 0, ""); return;
            case "unavailable":
                // No AI and no commands: an editor's change goes through the web admin; schedule and deadlines were already tried by keywords.
                await Respond(m, key, editor ? NoAiEdit(actor) : NoAiAnswer, ct);
                return;
            default:
                // Invalid twice: exactly the behaviour before the Router (editors' planner, everybody else's chat).
                if (decision.Source == "fallback" && editor) { await AdminDialog(m, key, actor, full, ct); return; }
                await ChatGuarded(m, key, isPrivate, actor, ct); return;
        }
    }

    private static string RouteKey(long chat, int id) => $"route:{chat}:{id}";
    private RouteDecision? StoredRoute(long chat, int id)
    {
        if (store.Get(RouteKey(chat, id)) is not { } raw) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(raw).Field("decision").Deserialize<RouteDecision>(Json.Options); }
        catch (JsonException) { return null; }
    }

    private async Task<RouteDecision> Route(JsonElement m, long actor, string place, string? joined, string full, PendingSlot? slot, CancellationToken ct)
    {
        var role = Catalog.Role(actor);
        var allowed = Catalog.For(role, place);
        var reactions = RouterReactions();
        var input = RouterInput(m, actor, role, place, joined, allowed, reactions, slot);
        var today = Today();
        string? note = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var callId = Guid.NewGuid().ToString("N");
            string answer;
            try { answer = await ai.Route(note is null ? input : input + "\n\n" + note, Tag(m), callId, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested && e is InvalidDataException or JsonException)
            {
                log.LogWarning("Router answer rejected ({Type}), attempt {Attempt}", e.GetType().Name, attempt);
                note = "Прошлый ответ отклонён: это не JSON-объект. Верни только JSON по схеме.";
                continue;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Router unavailable: {Error}", Failure(e));
                return Keywords(full, place == Capabilities.Dm && config.CanEdit(actor)) ?? new(null, "unavailable", null, null, null, "OpenRouter недоступен", "keywords");
            }
            if (ParseRoute(answer, allowed, reactions, today, out var error) is { } decision) return decision;
            try { ai.DiscardUsage?.Invoke(callId, "rejected:route"); } catch { }
            log.LogWarning("Router answer rejected: {Error}, attempt {Attempt}", error, attempt);
            note = $"Прошлый ответ отклонён: {error}. Исправь и верни только JSON по схеме.";
        }
        return new(null, "writer", null, null, null, "ответ маршрутизатора дважды не прошёл проверку", "fallback");
    }

    /// <summary>The model's JSON checked by code: enum, capability allowed for this role and place, its args, question length. Bad emoji is dropped.</summary>
    public static RouteDecision? ParseRoute(string json, IReadOnlyList<Capability> allowed, IReadOnlyCollection<string> reactions, DateOnly today, out string? error)
    {
        error = null;
        JsonElement root;
        try { using var doc = JsonDocument.Parse(json); root = doc.RootElement.Clone(); }
        catch (JsonException) { error = "это не JSON"; return null; }
        if (root.ValueKind != JsonValueKind.Object) { error = "нужен JSON-объект"; return null; }
        string? Str(string name) => root.Field(name).ValueKind == JsonValueKind.String ? root.Field(name).GetString()!.Trim() : null;
        var mode = Str("mode");
        if (mode is null || !RouteModes.Contains(mode)) { error = "mode должен быть одним из: " + string.Join(", ", RouteModes); return null; }
        var reaction = Str("reaction") is { } r && reactions.Contains(Emoji(r)) ? Emoji(r) : null;
        string? capability = null; JsonElement? args = null; string? question = null; List<RouteOption>? options = null;
        if (mode is "render" or "action")
        {
            var cap = allowed.FirstOrDefault(c => c.Id == Str("capability"));
            if (cap is null) { error = $"capability «{Str("capability")}» нет в списке capabilities"; return null; }
            if (!Capabilities.TryArgs(cap, root.Field("args"), today, out _, out var argError)) { error = "args: " + argError; return null; }
            (capability, mode) = (cap.Id, cap.Mode);
            args = root.Field("args").ValueKind == JsonValueKind.Object ? root.Field("args").Clone() : null;
        }
        else if (mode == "clarify")
        {
            question = Str("question");
            if (string.IsNullOrEmpty(question) || question.Length > RouteQuestionMax || TextGuard.Check(question, "", RouteQuestionMax) is not null)
            { error = $"для clarify нужен короткий вопрос по-русски до {RouteQuestionMax} символов"; return null; }
            if (!ParseOptions(root, allowed, today, out capability, out args, out options, out error)) return null;
        }
        else if (mode == "react_only" && reaction is null)
            reaction = reactions.Contains("👍") ? "👍" : reactions.FirstOrDefault(); // never silently ignore a person
        return new(reaction, mode, capability, args, question, Conversation.Limit(Str("reason") ?? "", 200), "router") { Options = options };
    }

    /// <summary>
    /// Clarify for an action: capability from the allowed list, the known args (may be incomplete), up to four options —
    /// a short label each, and with the known args a complete, valid call. No capability — a plain question (no options then).
    /// </summary>
    private static bool ParseOptions(JsonElement root, IReadOnlyList<Capability> allowed, DateOnly today,
        out string? capability, out JsonElement? args, out List<RouteOption>? options, out string? error)
    {
        capability = null; args = null; options = null; error = null;
        var id = root.Field("capability").ValueKind == JsonValueKind.String ? root.Field("capability").GetString()!.Trim() : null;
        var list = root.Field("options");
        var hasOptions = list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;
        if (list.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Array)) { error = "options должен быть массивом"; return false; }
        if (string.IsNullOrEmpty(id))
        {
            if (hasOptions) { error = "для options нужна capability"; return false; }
            return true;
        }
        var cap = allowed.FirstOrDefault(c => c.Id == id);
        if (cap is null) { error = $"capability «{id}» нет в списке capabilities"; return false; }
        var known = root.Field("args");
        if (!Capabilities.TryArgs(cap, known, today, out _, out var argError, partial: true)) { error = "args: " + argError; return false; }
        capability = cap.Id;
        args = known.ValueKind == JsonValueKind.Object ? known.Clone() : null;
        if (!hasOptions) return true;
        if (list.GetArrayLength() > SlotOptionsMax) { error = $"options — не больше {SlotOptionsMax}"; return false; }
        options = [];
        var i = 0;
        foreach (var o in list.EnumerateArray())
        {
            var label = o.ValueKind == JsonValueKind.Object && o.Field("label").ValueKind == JsonValueKind.String ? o.Field("label").GetString()!.Trim() : "";
            if (label.Length is 0 or > SlotLabelMax || TextGuard.Check(label, "", SlotLabelMax) is not null)
            { error = $"options[{i}].label — короткая подпись кнопки по-русски до {SlotLabelMax} символов"; return false; }
            var extra = o.Field("args");
            if (extra.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) { error = $"options[{i}].args должен быть объектом"; return false; }
            if (!Capabilities.TryArgs(cap, Capabilities.Merge(args, extra), today, out _, out argError)) { error = $"options[{i}].args: {argError}"; return false; }
            options.Add(new(label, extra.ValueKind == JsonValueKind.Object ? extra.Clone() : null));
            i++;
        }
        if (options.Select(x => x.Label).Distinct().Count() != options.Count) { error = "у options одинаковые подписи"; return false; }
        return true;
    }

    /// <summary>
    /// AI is down: the obvious schedule and deadline questions are answered by code (old command words too).
    /// An editor's request to change something is not answered with a list: it gets the web admin link instead (null here).
    /// </summary>
    private RouteDecision? Keywords(string text, bool editor = false)
    {
        var t = text.ToLowerInvariant().Replace('ё', 'е');
        var today = Today();
        RouteDecision R(string id, object? args = null) =>
            new(null, Capabilities.Render, id, args is null ? null : JsonSerializer.SerializeToElement(args, Json.Options), null, "ИИ недоступен, ключевые слова", "keywords");
        if (editor && Regex.IsMatch(t, @"перенес|перенос|добав|измен|поменя|отмен|удал|сдвин|замен|исправ|отчисл|убер|постав")) return null;
        switch (Regex.Match(t, @"^/([a-z]+)").Groups[1].Value)
        {
            case "today": return R("schedule_day", new { date = today.ToString("yyyy-MM-dd") });
            case "tomorrow": return R("schedule_day", new { date = today.AddDays(1).ToString("yyyy-MM-dd") });
            case "next": return R("next_lesson");
            case "week": return R("schedule_week", new { next = t.Contains("next") || t.Contains("след") });
            case "deadlines": return R("deadlines_list");
        }
        if (Regex.IsMatch(t, @"дедлайн|сдач|сдават")) return R("deadlines_list");
        if (Regex.IsMatch(t, @"следующ\w*\s+пар|пар\w*\s+следующ|какая\s+(?:сейчас\s+)?пара")) return R("next_lesson");
        if (t.Contains("недел")) return R("schedule_week", new { next = t.Contains("следующ") });
        if (t.Contains("послезавтра")) return R("schedule_day", new { date = today.AddDays(2).ToString("yyyy-MM-dd") });
        if (t.Contains("завтра")) return R("schedule_day", new { date = today.AddDays(1).ToString("yyyy-MM-dd") });
        if (t.Contains("сегодня")) return R("schedule_day", new { date = today.ToString("yyyy-MM-dd") });
        return null;
    }

    /// <summary>A compact input: the message, what it replies to, six short lines of history, role and place, a calendar, the filtered catalog.</summary>
    private string RouterInput(JsonElement m, long actor, string role, string place, string? joined, IReadOnlyList<Capability> allowed, string[] reactions,
        PendingSlot? slot = null)
    {
        var r = config.R;
        var local = config.LocalNow(DateTimeOffset.UtcNow);
        var today = DateOnly.FromDateTime(local);
        var chat = m.Field("chat").Number("id"); var id = (int)m.Number("message_id");
        string Who(JsonElement from) => from.Number("id") == botId ? "бот" : config.FindMember(from.Number("id"))?.ShortName ?? from.Text("first_name") ?? "кто-то";
        string Week(DateOnly d) => Schedule.InTerm(r, d) ? $"{Schedule.Week(r, d)}-я неделя, {Schedule.ParityRu(Schedule.Parity(Schedule.Week(r, d)))}" : "вне семестра";
        var recent = store.History(chat, m.TopicId(), id - 1, RouteRecent).Select(raw =>
        {
            var x = JsonSerializer.Deserialize<JsonElement>(raw);
            return $"{Who(x.Field("from"))}: {Conversation.Limit(MediaDigest.Text(x, store).Replace('\n', ' '), RouteRecentChars)}";
        });
        var reply = m.Field("reply_to_message");
        var pending = new List<string>();
        if (place == Capabilities.Dm && config.CanEdit(actor))
        {
            if (store.Draft(actor) is not null) pending.Add("черновик дедлайна ждёт подтверждения или правки");
            if (store.RegistryDraft(actor) is not null) pending.Add("черновик изменения реестра ждёт подтверждения или правки");
            if (store.Get($"planner_question:{actor}") is not null) pending.Add("планировщик задал уточняющий вопрос и ждёт ответа");
        }
        static string Arg(CapabilityArg a) => a.Name + ": " + (a.Type == "enum" ? string.Join("|", a.Options ?? []) : a.Type) + (a.Required ? ", обязательно" : "") + (a.Note is null ? "" : $" ({a.Note})");
        return Json.Write(new
        {
            role, place = place == Capabilities.Dm ? "личка с ботом" : "Воздуханство (общий чат группы)",
            now = local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + ", " + Schedule.DayNames[Schedule.Dow(today)] + ", " + Week(today),
            calendar = Enumerable.Range(0, 14).Select(i => today.AddDays(i)).Select((d, i) =>
                $"{d:yyyy-MM-dd} {Schedule.DayShort[Schedule.Dow(d)].ToLowerInvariant()}" + (i switch { 0 => " (сегодня)", 1 => " (завтра)", 2 => " (послезавтра)", _ => "" }) +
                (d.DayOfWeek == DayOfWeek.Monday || i == 0 ? ", " + Week(d) : "")),
            pending = pending.Count > 0 ? pending : null,
            pending_question = slot is null ? null : new
            {
                question = slot.Question, capability = slot.Capability, args = slot.KnownArgs,
                options = slot.Choices.Select(o => new { label = o.Label, args = o.Args })
            },
            recent,
            replied_to = reply.ValueKind == JsonValueKind.Object ? $"{Who(reply.Field("from"))}: {Conversation.Limit(MediaDigest.Text(reply, store), 300)}" : null,
            message = new { author = Who(m.Field("from")), text = Conversation.Limit((joined is null ? "" : joined + "\n") + MediaDigest.Text(m, store), 1500) },
            capabilities = allowed.Select(c => $"{c.Id} [{c.Mode}]" + (c.Args.Length > 0 ? "(" + string.Join("; ", c.Args.Select(Arg)) + ")" : "") + " — " + c.Description),
            allowed_reactions = reactions
        });
    }
}
