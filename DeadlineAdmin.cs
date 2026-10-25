using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

public sealed partial class BotWorker
{
    /// <summary>Editors' private messages: the planner decides between conversation, deadline and registry change.</summary>
    private async Task AdminDialog(JsonElement m, string key, long actor, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) { await ChatGuarded(m, key, true, actor, ct); return; }
        PlanResult plan;
        var pending = store.Get($"planner_question:{actor}");
        var request = pending is null ? text : pending + "\nУточнение: " + text;
        try { plan = await ai.Plan(request, conversation.PlannerState(actor, store.Draft(actor), store.RegistryDraft(actor)), Tag(m), ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Planner failed: {Error}", Failure(e));
            await Respond(m, key, "Не смог разобрать сообщение через ИИ — ничего не изменено. Попробуй ещё раз, используй команды " +
                "/deadline, /move, /cancel, /today или веб-админку (/web) — они работают без нейронки.", ct);
            return;
        }
        if (plan.Intent == "registry" && plan.Operations.Length == 0 && plan.Question is not null)
            store.Set($"planner_question:{actor}", Conversation.Limit(request, 4000));
        else store.Delete($"planner_question:{actor}");
        switch (plan.Intent)
        {
            case "deadline":
                await ManageDeadline(m, key, ct, plan.Deadline ?? new ParsedDeadline(Question: plan.Question)); return;
            case "registry":
                await RegistryRequest(m, key, actor, text, plan, ct); return;
            case "post" when actor == config.OwnerId:
                await PostPreview(m, key, actor, plan.Idea ?? text, ct); return;
            default:
                await ChatGuarded(m, key, true, actor, ct); return;
        }
    }

    private async Task RegistryRequest(JsonElement m, string key, long actor, string text, PlanResult plan, CancellationToken ct)
    {
        if (plan.Operations.Length == 0)
        {
            await Respond(m, key, plan.Question ?? "Не понял, что именно поменять. Опиши подробнее: что, на какую дату, какая пара.", ct); return;
        }
        List<string> lines;
        try { lines = RegistryOps.ApplyAll(config.R, plan.Operations, config.OwnerId, actor == config.OwnerId).Lines; }
        catch (InvalidOperationException e)
        {
            await Respond(m, key, "Так не получится: " + e.Message + (plan.Question is null ? "" : "\n" + plan.Question) + "\nНичего не изменено.", ct); return;
        }
        var previous = store.RegistryDraft(actor);
        var draft = new RegistryDraft(Guid.NewGuid().ToString("N")[..16], actor, store.RegistryVersion(), "ops",
            plan.Operations.Select(o => o.GetRawText()).ToArray(), lines.ToArray(),
            Conversation.Limit((previous is null ? "" : previous.Source + "\n") + text, 4000));
        store.SaveRegistryDraft(draft);
        var preview = "🛠 Изменения реестра:\n" + string.Join("\n", lines) +
            (plan.Question is null ? "" : "\n\n" + Conversation.Limit(plan.Question, 300)) + "\n\nПрименить? Можно ответить уточнением — карточка пересоберётся.";
        await Respond(m, key, Conversation.Limit(preview, 3900), ct, Buttons("rok:" + draft.Token, "rno:" + draft.Token, "Применить"));
    }

    private static object Buttons(string yes, string no, string yesText) => new
    {
        inline_keyboard = new[] { new[] { new { text = yesText, callback_data = yes }, new { text = "Не надо", callback_data = no } } }
    };

    private async Task Undo(JsonElement m, string key, long actor, CancellationToken ct)
    {
        var previous = store.PreviousRegistryVersion();
        var target = previous is null ? null : store.RegistryAt(previous.Value);
        if (target is null) { await Respond(m, key, "Откатывать нечего: это первая версия реестра.", ct); return; }
        var lines = RegistryOps.Diff(config.R, target);
        if (lines.Count == 0) { await Respond(m, key, "Предыдущая версия совпадает с текущей.", ct); return; }
        if (actor != config.OwnerId && lines.Any(RegistryOps.IsSecret))
        {
            await Respond(m, key, "Последнее изменение сделал разработчик в своих настройках — откатить его может только он.", ct); return;
        }
        var current = store.RegistryVersion();
        var draft = new RegistryDraft(Guid.NewGuid().ToString("N")[..16], actor, current, "restore", [], lines.ToArray(), "/undo", previous);
        store.SaveRegistryDraft(draft);
        await Respond(m, key, Conversation.Limit($"↩️ Откат реестра: версия {current} → {previous}\n" + string.Join("\n", lines), 3900), ct,
            Buttons("rok:" + draft.Token, "rno:" + draft.Token, "Откатить"));
    }

    private string WebLink(long actor)
    {
        if (!config.Web.Enabled) return "Веб-админка выключена в серверном конфиге (web.enabled).";
        var token = store.CreateWebToken(actor, "login", TimeSpan.FromMinutes(30));
        return $"{config.Web.PublicUrl.TrimEnd('/')}/login?t={token}\n\nСсылка одноразовая, действует 30 минут: открой и нажми «Войти». Открывай на устройстве, подключённом к VPN.";
    }

    /// <summary>The other editor learns about every registry change made by the owner or the head.</summary>
    internal async Task NotifyEditors(long actor, int version, IEnumerable<string> lines, string where, CancellationToken ct)
    {
        var who = actor == config.OwnerId ? "Разработчик" : "Староста " + (config.FindMember(actor)?.ShortName ?? "");
        var editors = config.R.Members.Where(x => x.Active && x.Role == "head" && x.UserId is not null).Select(x => x.UserId!.Value)
            .Append(config.OwnerId).Distinct().Where(x => x != actor);
        foreach (var editor in editors)
            try
            {
                var visible = RegistryOps.ForViewer(lines, editor == config.OwnerId);
                if (visible.Count == 0) continue; // owner-only change: the head does not even learn it happened
                await telegram.Send(editor, 0, Conversation.Limit($"ℹ️ {who.Trim()} изменил реестр ({where}, версия {version}):\n" + string.Join("\n", visible), 3900), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogInformation("Editor notification skipped: {Error}", Failure(e)); }
    }

    private async Task ManageDeadline(JsonElement m, string responseKey, CancellationToken ct, ParsedDeadline? preParsed = null)
    {
        var actor = m.Field("from").Number("id");
        var text = m.Text("text") ?? m.Text("caption") ?? "";
        var command = text.StartsWith('/') ? text.Split(' ', '\n')[0].Split('@')[0].ToLowerInvariant() : "";
        var previous = store.Draft(actor);
        if (command == "/discard")
        {
            if (previous is not null) store.Discard(actor, previous.Token);
            await Respond(m, responseKey, "Черновики убраны.", ct); return;
        }
        try
        {
            ParsedDeadline parsed;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (command == "/deadline")
            {
                var parts = text[(text.IndexOf(' ') + 1)..].Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length is not (4 or 5) || !int.TryParse(parts[3], out var period)) throw new InvalidOperationException("Формат: /deadline subjectId | работа | YYYY-MM-DD | пара [| кабинет]");
                parsed = new("create", null, parts[0], parts[1], parts[2], period, Room: parts.Length == 5 ? parts[4] : null); previous = null;
            }
            else if (command == "/move")
            {
                if (words.Length is < 3 or > 4 || !int.TryParse(words[1], out var id) ||
                    (words.Length == 4 && !int.TryParse(words[3], out _))) throw new InvalidOperationException("Формат: /move ID YYYY-MM-DD [пара]");
                parsed = new("update", id, Date: words[2], Period: words.Length == 4 ? int.Parse(words[3]) : null); previous = null;
            }
            else if (command == "/cancel")
            {
                if (words.Length != 2 || !int.TryParse(words[1], out var id)) throw new InvalidOperationException("Формат: /cancel ID");
                parsed = new("cancel", id); previous = null;
            }
            else
            {
                if (command == "/edit")
                {
                    if (words.Length < 3 || !int.TryParse(words[1], out _)) throw new InvalidOperationException("Формат: /edit ID что меняем");
                    previous = null;
                }
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Для дедлайна напиши текстом предмет, работу и дату.");
                parsed = command != "/edit" && preParsed is not null ? preParsed : await ai.Parse(text, previous, store.AllDeadlines(), ct);
                if (command == "/edit") parsed = parsed with { Action = "update", TargetId = int.Parse(words[1]) };
            }
            var draft = Deadlines.Resolve(config, parsed, previous, store.AllDeadlines(), actor,
                Conversation.Limit((previous is null ? "" : previous.Source + "\n") + text, 4000), DateTimeOffset.UtcNow);
            if (draft.Missing.Length == 0)
            {
                draft = draft with { Intro = await DeadlineIntro(draft, ct), MorningIntro = "Сегодня сдаём:" };
            }
            store.SaveDraft(draft);
            // Ready draft: show exactly what will appear in «Дедлайны». Incomplete one: the plain checklist of what is missing.
            var preview = draft.Missing.Length == 0
                ? Deadlines.DraftCard(config, draft) +
                  (string.IsNullOrWhiteSpace(draft.Note) ? "" : $"\n\n<i>Комментарий для бота (в пост дословно не попадёт): {System.Net.WebUtility.HtmlEncode(draft.Note)}</i>") +
                  "\n\nПубликую только после подтверждения. Можно поправить сообщением."
                : System.Net.WebUtility.HtmlEncode(Deadlines.Preview(config, draft));
            if (!string.IsNullOrWhiteSpace(parsed.Question) && draft.Missing.Length > 0) preview += "\n" + System.Net.WebUtility.HtmlEncode(Conversation.Limit(parsed.Question, 300));
            var keyboard = draft.Missing.Length == 0
                ? Buttons("save:" + draft.Token, "drop:" + draft.Token, draft.Action == "cancel" ? "Подтвердить отмену" : "Сохранить и опубликовать")
                : null;
            await Respond(m, responseKey, preview, ct, keyboard, html: true);
        }
        catch (TelegramError) { throw; }
        catch (InvalidOperationException e) { await Respond(m, responseKey, e.Message, ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await Respond(m, responseKey, "Не смог разобрать сообщение через ИИ. Черновик не опубликован. Попробуй ещё раз или используй /deadline, /move, /cancel — они работают без OpenRouter.", ct);
        }
    }

    private async Task ProcessCallback(JsonElement cb, CancellationToken ct)
    {
        var actor = cb.Field("from").Number("id");
        if (!config.CanEdit(actor) || cb.Field("message").Field("chat").Number("id") != actor) return;
        var data = (cb.Text("data") ?? "").Split(':', 2);
        if (data.Length == 2 && data[0] is "apost" or "achat" or "aredo" or "ano")
        {
            if (actor == config.OwnerId) await PostCallback(cb, data[0], data[1], ct);
            return;
        }
        if (data.Length != 2 || data[0] is not ("save" or "drop" or "rok" or "rno")) return;
        var messageId = (int)cb.Field("message").Number("message_id");
        if (data[0] is "rok" or "rno")
        {
            if (store.RegistryDraft(actor, data[1]) is null) { await SafeCallback(cb.Text("id")!, "Карточка уже обработана или заменена.", ct); return; }
            if (data[0] == "rno")
            {
                store.DiscardRegistryDraft(actor, data[1]);
                await SafeCallback(cb.Text("id")!, "Отменено", ct);
                await telegram.Edit(actor, messageId, "Изменения не применены.", ct); return;
            }
            await SafeCallback(cb.Text("id")!, "Применяю…", ct);
            (int Version, List<string> Lines) result;
            try { result = store.ConfirmRegistryDraft(actor, data[1]); }
            catch (InvalidOperationException e) { await telegram.Send(actor, 0, "Не применено: " + e.Message, ct); return; }
            await telegram.Edit(actor, messageId, Conversation.Limit($"✅ Применено, версия реестра {result.Version}:\n" + string.Join("\n", result.Lines), 3900), ct);
            await NotifyEditors(actor, result.Version, result.Lines, "Telegram", ct);
            return;
        }
        var draft = store.Draft(actor, data[1]);
        if (draft is null) { await SafeCallback(cb.Text("id")!, "Карточка уже обработана или заменена.", ct); return; }
        if (data[0] == "drop")
        {
            store.Discard(actor, data[1]);
            await SafeCallback(cb.Text("id")!, "Отменено", ct);
            await telegram.Edit(actor, messageId, "Черновик убран.", ct); return;
        }
        await SafeCallback(cb.Text("id")!, "Сохраняю…", ct);
        await publicationGate.WaitAsync(ct);
        try
        {
            Deadline d;
            try { d = store.Confirm(actor, data[1], DateTimeOffset.UtcNow); }
            catch (InvalidOperationException e) { await telegram.Send(actor, 0, e.Message, ct); return; }
            await telegram.Edit(actor, messageId,
                $"✅ Сохранено в БД. Публикация поставлена в очередь.\n\n{Deadlines.Facts(config, d)}" +
                (d.Status == "cancelled" ? "\nСтатус: отменено." : ""), ct);
        }
        finally { publicationGate.Release(); }
    }
    private async Task SafeCallback(string id, string text, CancellationToken ct)
    {
        try { await telegram.Callback(id, text, ct); }
        catch (TelegramError e) when (e.Code == 400) { }
    }
}
