using System.Text.Json;

namespace Vozduhan.AI;

public sealed partial class BotWorker
{
    private async Task ManageDeadline(JsonElement m, string responseKey, CancellationToken ct)
    {
        var actor = m.Field("from").Number("id");
        var text = m.Text("text") ?? m.Text("caption") ?? "";
        var command = text.Split(' ', '\n')[0].Split('@')[0].ToLowerInvariant();
        var previous = store.Draft(actor);
        if (command == "/discard")
        {
            if (previous is not null) store.Discard(actor, previous.Token);
            await Respond(m, responseKey, "Черновик убран.", ct); return;
        }
        try
        {
            ParsedDeadline parsed;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (command == "/deadline")
            {
                var parts = text[(text.IndexOf(' ') + 1)..].Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length != 4 || !int.TryParse(parts[3], out var period)) throw new InvalidOperationException("Формат: /deadline subjectId | работа | YYYY-MM-DD | пара");
                parsed = new("create", null, parts[0], parts[1], parts[2], period); previous = null;
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
                parsed = await ai.Parse(text, previous, store.AllDeadlines(), ct);
                if (command == "/edit") parsed = parsed with { Action = "update", TargetId = int.Parse(words[1]) };
            }
            var draft = Deadlines.Resolve(config, parsed, previous, store.AllDeadlines(), actor,
                Conversation.Limit((previous is null ? "" : previous.Source + "\n") + text, 4000), DateTimeOffset.UtcNow);
            if (draft.Missing.Length == 0)
            {
                var intros = command is "/deadline" or "/move" or "/cancel"
                    ? (News: draft.Action == "create" ? "Гайс, держим в голове:" : "Гайс, по сдаче новости.", Morning: "Гайс, сегодня у нас:")
                    : await ai.Intros(draft.Action, ct);
                draft = draft with { Intro = intros.News, MorningIntro = intros.Morning };
            }
            store.SaveDraft(draft);
            var preview = Deadlines.Preview(config, draft);
            if (!string.IsNullOrWhiteSpace(parsed.Question) && draft.Missing.Length > 0) preview += "\n" + Conversation.Limit(parsed.Question, 300);
            var keyboard = draft.Missing.Length == 0 ? new
            {
                inline_keyboard = new[] { new[] {
                    new { text = draft.Action == "cancel" ? "Подтвердить отмену" : "Сохранить и опубликовать", callback_data = "save:" + draft.Token },
                    new { text = "Не надо", callback_data = "drop:" + draft.Token }
                } }
            } : null;
            await Respond(m, responseKey, preview, ct, keyboard);
        }
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
        if (data.Length != 2 || data[0] is not ("save" or "drop")) return;
        var draft = store.Draft(actor, data[1]);
        if (draft is null) { await SafeCallback(cb.Text("id")!, "Карточка уже обработана или заменена.", ct); return; }
        if (data[0] == "drop")
        {
            store.Discard(actor, data[1]);
            await SafeCallback(cb.Text("id")!, "Отменено", ct);
            await telegram.Edit(actor, (int)cb.Field("message").Number("message_id"), "Черновик убран.", ct); return;
        }
        await SafeCallback(cb.Text("id")!, "Сохраняю…", ct);
        await publicationGate.WaitAsync(ct);
        try
        {
            Deadline d;
            try { d = store.Confirm(actor, data[1], DateTimeOffset.UtcNow); }
            catch (InvalidOperationException e) { await telegram.Send(actor, 0, e.Message, ct); return; }
            await telegram.Edit(actor, (int)cb.Field("message").Number("message_id"),
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
