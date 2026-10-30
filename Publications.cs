using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// Every self-initiated post goes through one table (publications): it is planned, its text is written PreviewLead before due
/// and shown to the developer with buttons (auto_preview), then sent at due unless the developer cancelled it. manual waits for ✅,
/// auto sends without a preview. Before sending, anti-dup rules may move or silence it; what it said about deadlines is logged.
/// </summary>
public sealed partial class BotWorker
{
    public const string AutoPreview = "auto_preview", ManualMode = "manual", AutoMode = "auto";
    public static readonly string[] PublicationKinds = ["summary", "morning", "spont", "eve", "wakeup", "post"];
    public static readonly string[] PublicationModes = [AutoPreview, ManualMode, AutoMode];
    public static readonly TimeSpan PreviewLead = TimeSpan.FromMinutes(30), ManualWait = TimeSpan.FromHours(2), EditWindow = TimeSpan.FromMinutes(30),
        MinGapAutopost = TimeSpan.FromMinutes(15), ReminderCooldown = TimeSpan.FromMinutes(90), RegenerateMin = TimeSpan.FromMinutes(5);
    public const string PublicationPrefix = "pub:", PublicationEdit = "publication_edit";
    private const int MaxPublicationAttempts = 5;
    private readonly SemaphoreSlim publicationsGate = new(1);

    private string ModeFor(string kind) => config.Features.PublicationModes.TryGetValue(kind, out var m) && PublicationModes.Contains(m) ? m : AutoPreview;

    /// <summary>The day's own events. roll: choose the day's random drops now if they are not chosen yet.</summary>
    internal IEnumerable<(string Key, string Kind, DateTimeOffset Due, int Topic)> DayEvents(DateOnly day, bool roll = true)
    {
        var a = config.Ambient;
        var ds = day.ToString("yyyy-MM-dd");
        DateTimeOffset At(string time) => config.Utc(day.ToDateTime(TimeOnly.Parse(time)));
        if (config.SummaryTopicId > 0) yield return ($"ambient:summary:{ds}", "summary", At(a.SummaryTime), config.SummaryTopicId);
        if (a.MorningTime.Length > 0) yield return ($"ambient:morning:{ds}", "morning", At(a.MorningTime), config.ChatTopicId);
        var drops = roll ? SpontaneousPlan(day) : store.Get($"ambient:plan:{ds}") is { } saved ? Json.Read<List<string>>(saved) : [];
        foreach (var (time, i) in drops.Select((t, i) => (t, i))) yield return ($"ambient:spont:{ds}:{i}", "spont", At(time), config.ChatTopicId);
        if (a.EveReminders) yield return ($"ambient:eve:{ds}", "eve", At(a.EveReminderTime), EveTopic);
    }
    private Publication EnsureWakeup(Wakeup w) =>
        store.EnsurePublication("wake:" + w.Id, w.Deadline == 0 ? "post" : "wakeup", w.Due, ReminderTopic, ModeFor(w.Deadline == 0 ? "post" : "wakeup"));

    /// <summary>The row of a key the admin acts on: created if the event or the wake-up exists, else the stored one (or null).</summary>
    internal Publication? EnsureKey(string key)
    {
        var parts = key.Split(':');
        if (parts[0] == "wake" && long.TryParse(parts.ElementAtOrDefault(1), out var id))
            return store.Wakeups().FirstOrDefault(w => w.Id == id && w.Status == "pending") is { } w ? EnsureWakeup(w) : store.Publication(key);
        if (parts.Length >= 3 && parts[0] == "ambient" && DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", out var day) &&
            DayEvents(day, roll: day == DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow))).FirstOrDefault(e => e.Key == key) is { Key: not null } e)
            return store.Publication(key) ?? store.EnsurePublication(e.Key, e.Kind, e.Due.ToUnixTimeSeconds(), e.Topic, ModeFor(e.Kind));
        return store.Publication(key);
    }

    internal async Task AmbientTick(DateTimeOffset utc, CancellationToken ct)
    {
        if (!config.Ambient.Enabled) return;
        await publicationsGate.WaitAsync(ct);
        try
        {
            var today = DateOnly.FromDateTime(config.LocalNow(utc));
            var days = DateOnly.FromDateTime(config.LocalNow(utc + PreviewLead)) > today ? new[] { today, today.AddDays(1) } : [today];
            var current = new HashSet<string>();
            foreach (var day in days)
                foreach (var e in DayEvents(day))
                {
                    current.Add(e.Key);
                    if (day == today || e.Due <= utc + PreviewLead) store.EnsurePublication(e.Key, e.Kind, e.Due.ToUnixTimeSeconds(), e.Topic, ModeFor(e.Kind));
                }
            foreach (var w in store.Wakeups().Where(w => w.Status == "pending")) EnsureWakeup(w);
            foreach (var p in store.ActivePublications())
            {
                // An event switched off in the settings (or a re-rolled drop) leaves the plan.
                if (p.Key.StartsWith("ambient:") && days.Any(d => p.Key.Contains(d.ToString("yyyy-MM-dd"))) && !current.Contains(p.Key))
                {
                    await Finish(p, "cancelled", "выключено в настройках", ct);
                    continue;
                }
                await Step(p, utc, ct);
            }
        }
        finally { publicationsGate.Release(); }
    }

    private async Task Step(Publication p, DateTimeOffset utc, CancellationToken ct)
    {
        var now = utc.ToUnixTimeSeconds();
        // A retry waits for its time, unless the post is already too late: then it is closed at once.
        if (p.NextTry > now && now - p.Due <= (p.Mode == ManualMode ? ManualWait : Late).TotalSeconds) return;
        try { await StepCore(p, utc, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var attempts = p.Attempts + 1;
            var note = "ошибка: " + FailureRu(e);
            log.LogWarning("Publication {Key} failed, attempt {Attempt} ({Type})", p.Key, attempts, Failure(e));
            store.RetryPublication(p.Key, attempts, attempts >= MaxPublicationAttempts ? 0 : now + attempts switch { 1 => 120, 2 => 300, 3 => 600, _ => 900 }, note);
            if (attempts >= MaxPublicationAttempts) await Finish(p, "failed", note, ct);
        }
    }

    private async Task StepCore(Publication p, DateTimeOffset utc, CancellationToken ct)
    {
        var now = utc.ToUnixTimeSeconds();
        if (Obsolete(p, utc) is { } why) { await Finish(p, "cancelled", why, ct); return; }
        if (p.Mode == ManualMode ? now - p.Due > ManualWait.TotalSeconds : now - p.Due > Late.TotalSeconds)
        {
            await Finish(p, "skipped", p.Mode == ManualMode ? "не подтверждено вовремя"
                : $"опоздало на {(now - p.Due) / 60} мин" + (p.Attempts > 0 ? $" после {p.Attempts} неудачных попыток" : ""), ct);
            return;
        }
        if (p.Kind == "eve" && DueOn(EventDay(p).AddDays(1)).Count == 0)
        {
            if (now >= p.Due) await Finish(p, "silent", "сдач на завтра нет", ct);
            return;
        }
        if (p.Mode != AutoMode)
        {
            if (p.Preview == 0 && now >= p.Due - PreviewLead.TotalSeconds) { await SendPreview(p, utc, ct); return; }
            if (p.Preview > 0 && p.Source == "ai" && p.Basis != Basis() && p.Due - now >= RegenerateMin.TotalSeconds)
            {
                await Regenerate(p, utc, "факты изменились", ct);
                return;
            }
            if (p.Mode == ManualMode) return;
        }
        if (now >= p.Due) await Deliver(p, utc, ct, antiDup: true);
    }

    /// <summary>Why a planned post makes no sense any more; null — it still does.</summary>
    private string? Obsolete(Publication p, DateTimeOffset utc)
    {
        if (!p.Key.StartsWith("wake:")) return null;
        var w = WakeupOf(p);
        if (w is null || w.Status != "pending") return "будильник убран";
        if (w.Deadline == 0) return null;
        return DeadlineOf(p) is { } d && Deadlines.Ends(config, d) >= utc ? null : "дедлайн отменён или уже прошёл";
    }
    private Wakeup? WakeupOf(Publication p) =>
        p.Key.StartsWith("wake:") && long.TryParse(p.Key[5..], out var id) ? store.Wakeups().FirstOrDefault(w => w.Id == id) : null;
    private Deadline? DeadlineOf(Publication p) =>
        WakeupOf(p) is { Deadline: > 0 } w ? store.AllDeadlines().FirstOrDefault(d => d.Id == w.Deadline && d.Status == "active") : null;
    private static DateOnly EventDay(Publication p) => DateOnly.ParseExact(p.Key.Split(':')[2], "yyyy-MM-dd");

    /// <summary>
    /// The text to post: the developer's own text always, an AI text while the facts it was written for hold (or once its sending began),
    /// otherwise a new one. null text — stay silent, with the reason.
    /// </summary>
    private async Task<(string? Text, string? Silent)> TextFor(Publication p, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && p.Text is not null)
        {
            if (p.Source == "manual" || p.Basis == Basis() || store.Posts("ambient_part:" + p.Key + ":").Count > 0)
                return p.Text == OpenRouterClient.Skip ? (null, "решено промолчать") : (p.Text, null);
            log.LogWarning("Publication {Key}: facts changed since its text was written, writing a new one", p.Key);
        }
        string? text, rejected = null;
        try { text = await Generate(p, ct, fresh ? p.Text : null); }
        catch (AiTextRejectedException e) { text = null; rejected = "текст не прошёл проверку: " + TextGuard.Explain(e.Reason); }
        // The summary and the evening reminder carry facts on their own: without a lead they still go out.
        if (text is null && p.Kind is "summary" or "eve") text = "";
        else if (rejected is not null) return (null, rejected);
        store.SetPublicationText(p.Key, text ?? OpenRouterClient.Skip, "ai", Basis());
        return text is null ? (null, "модель решила промолчать") : (text, null);
    }

    /// <summary>Generates the text of a planned post as of its due moment, strictly checked.</summary>
    private async Task<string?> Generate(Publication p, CancellationToken ct, string? previous = null)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(p.Due);
        var forTime = config.LocalNow(at).ToString("yyyy-MM-dd HH:mm");
        if (p.Kind is "wakeup" or "post")
        {
            var w = WakeupOf(p) ?? throw new InvalidOperationException("Будильник уже удалён.");
            if (w.Deadline == 0) return await GenerateAmbient("manual", new { idea = w.Reason, for_time = forTime, previous_variant = previous }, ct, strict: true, at: at, tag: p.Key);
            var d = DeadlineOf(p) ?? throw new InvalidOperationException("Дедлайн уже не активен.");
            return await GenerateAmbient("wakeup", WakeupExtra(w, d, at, previous), ct, strict: true, at: at, tag: p.Key);
        }
        var moment = new { for_date = EventDay(p).ToString("yyyy-MM-dd"), for_time = forTime, previous_variant = previous };
        return p.Kind switch
        {
            "summary" => await GenerateAmbient("summary", moment, ct, strict: true, at: at, tag: p.Key),
            "morning" => await GenerateAmbient("morning", moment, ct, split: true, strict: true, at: at, tag: p.Key),
            "eve" => await GenerateAmbient("eve", moment, ct, strict: true, at: at, tag: p.Key),
            _ => await GenerateAmbient("spontaneous", moment, ct, split: true, strict: true, at: at, tag: p.Key)
        };
    }

    /// <summary>Sends the post now. antiDup: the 15-minute gap and the reminder cooldown may move it or silence it instead.</summary>
    private async Task Deliver(Publication p, DateTimeOffset utc, CancellationToken ct, bool antiDup)
    {
        var now = utc.ToUnixTimeSeconds();
        string Hm(long t) => config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(t)).ToString("HH:mm");
        if (antiDup)
        {
            if (store.LastSelfPost(now) is { } last && now - last < MinGapAutopost.TotalSeconds)
            {
                var next = last + (long)MinGapAutopost.TotalSeconds;
                if (next - p.FirstDue > Late.TotalSeconds) await Finish(p, "skipped", $"слишком часто: последний пост в {Hm(last)}", ct);
                else store.MovePublicationDue(p.Key, next, $"сдвинуто на {Hm(next)}: в {Hm(last)} уже был пост");
                return;
            }
            if (p.Kind == "wakeup" && DeadlineOf(p) is { } d && store.LastFullMention(d.Id) is { } said && now - said < ReminderCooldown.TotalSeconds)
            {
                var next = said + (long)ReminderCooldown.TotalSeconds;
                var start = config.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start))).ToUnixTimeSeconds();
                if (next <= start - 15 * 60) store.MovePublicationDue(p.Key, next, $"сдвинуто на {Hm(next)}: уже напоминал в {Hm(said)}");
                else await Finish(p, "silent", $"уже напоминал в {Hm(said)}", ct);
                return;
            }
        }
        var (text, silent) = await TextFor(p, ct);
        if (text is null) { await Finish(p, "silent", silent, ct); return; }
        switch (p.Kind)
        {
            case "summary": await MorningSummary(EventDay(p), ct, text); break;
            case "eve": await EveReminder(EventDay(p).AddDays(1), ct, text); break;
            case "morning": await PostAmbient("morning", null, ct, text, p.Topic, p.Key); break;
            case "spont": await PostAmbient("spontaneous", null, ct, text, p.Topic, p.Key); break;
            default: await PostAmbient(p.Kind == "post" ? "manual" : "wakeup", null, ct, text, p.Topic, p.Key); break;
        }
        Mentions(p, text, now);
        await Finish(p, "sent", null, ct, $"✅ Отправлено в {Hm(now)}.", now);
    }

    /// <summary>
    /// Logs the deadlines this post was about. full: a wake-up's deadline, the evening reminder's and the morning summary's
    /// submissions of the day; brief: deadlines only named in passing. Cards and change news never come here.
    /// </summary>
    private void Mentions(Publication p, string text, long now)
    {
        var active = store.AllDeadlines().Where(d => d.Status == "active").ToList();
        List<Deadline> full = p.Kind switch
        {
            "wakeup" => DeadlineOf(p) is { } d ? [d] : [],
            "eve" => DueOn(EventDay(p).AddDays(1)),
            "summary" => DueOn(EventDay(p)),
            _ => []
        };
        foreach (var d in full) store.AddMention(d.Id, p.Key, "full", now);
        foreach (var d in active.Where(d => full.All(f => f.Id != d.Id) && Named(text, d))) store.AddMention(d.Id, p.Key, "brief", now);
    }
    private bool Named(string text, Deadline d)
    {
        var s = config.R.Subjects.FirstOrDefault(x => x.Id == d.SubjectId);
        var names = new[] { s?.Name, s?.ShortName }.Concat(s?.Aliases ?? []).Where(n => n is { Length: >= 3 });
        return names.Any(n => text.Contains(n!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Closes an active post with a final status; the developer's preview loses its buttons, a wake-up mirrors the result.</summary>
    private async Task Finish(Publication p, string status, string? note, CancellationToken ct, string? previewText = null, long? at = null)
    {
        if (!store.FinishPublication(p.Key, status, note, at)) return;
        if (WakeupOf(p) is { Status: "pending" } w)
            store.SetWakeup(w.Id, status switch
            {
                "sent" => "fired", "silent" => "silent", "skipped" => "skipped", "failed" => "failed",
                _ => note == "дедлайн отменён или уже прошёл" ? "obsolete" : "deleted"
            }, note: note);
        await ClosePreview(p, previewText ?? status switch
        {
            "cancelled" => "🚫 Не отправлю" + (note is null ? "." : $": {note}."),
            "silent" => "🤐 Промолчал" + (note is null ? "." : $": {note}."),
            "skipped" => "⏭ Пропущено" + (note is null ? "." : $": {note}."),
            _ => "⚠️ Не отправилось" + (note is null ? "." : $": {note}.")
        }, ct);
    }
    private async Task ClosePreview(Publication p, string text, CancellationToken ct)
    {
        if (p.Preview <= 0) return;
        try { await telegram.Edit(config.OwnerId, p.Preview, $"{text}\n({Title(p)})", ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { log.LogInformation("Preview {Key} not updated: {Error}", p.Key, Failure(e)); }
    }

    // ---------- Preview in the developer's DM ----------

    private string Title(Publication p)
    {
        var when = config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(p.Due)).ToString("dd.MM HH:mm");
        var what = p.Kind switch
        {
            "summary" => "утро в «Сводку»", "morning" => "утро в Воздуханство", "spont" => "вброс в Воздуханство", "eve" => "напоминание на завтра",
            "wakeup" => "будильник к дедлайну", _ => "отложенный пост"
        };
        return $"{what}, {when}";
    }
    private static object PreviewButtons(long id) => new
    {
        inline_keyboard = new[]
        {
            new[] { new { text = "✏️ Править", callback_data = $"{PublicationPrefix}e:{id}" }, new { text = "🔄 Другой вариант", callback_data = $"{PublicationPrefix}r:{id}" } },
            new[] { new { text = "🚫 Не отправлять", callback_data = $"{PublicationPrefix}c:{id}" }, new { text = "✅ Отправить сейчас", callback_data = $"{PublicationPrefix}s:{id}" } }
        }
    };
    /// <summary>The preview exactly as it will look in the group; the buttons sit under its last part. Returns that message.</summary>
    private async Task<int> PreviewMessage(Publication p, CancellationToken ct)
    {
        var due = DateTimeOffset.FromUnixTimeSeconds(p.Due);
        var head = p.Mode == ManualMode
            ? $"✋ <b>{System.Net.WebUtility.HtmlEncode(Title(p))}</b> — уйдёт только после ✅ (до {config.LocalNow(due + ManualWait):HH:mm})"
            : $"👀 <b>{System.Net.WebUtility.HtmlEncode(Title(p))}</b> — уйдёт в {config.LocalNow(due):HH:mm}, если не остановишь";
        var parts = TelegramHtml.Split(head + "\n\n" + (Preview(p.Key, p.Text ?? "") ?? "")).ToList();
        var last = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var keyboard = i == parts.Count - 1 ? PreviewButtons(p.Id) : null;
            JsonElement sent;
            try { sent = await telegram.Send(config.OwnerId, 0, parts[i], ct, keyboard, html: true); }
            catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("parse", StringComparison.OrdinalIgnoreCase))
            { sent = await telegram.Send(config.OwnerId, 0, TelegramHtml.Plain(parts[i]), ct, keyboard); }
            last = (int)sent.Number("message_id");
        }
        return last;
    }
    private async Task SendPreview(Publication p, DateTimeOffset utc, CancellationToken ct)
    {
        var (text, silent) = await TextFor(p, ct);
        if (text is null) { await Finish(p, "silent", silent, ct); return; }
        p = store.Publication(p.Key)!;
        int message;
        // No DM with the developer must not stop the post itself: it goes at due without a preview.
        try { message = await PreviewMessage(p, ct); }
        catch (TelegramError e) { log.LogWarning("Preview {Key} not delivered: {Error}", p.Key, Failure(e)); message = -1; }
        store.SetPublicationPreview(p.Key, message);
        store.MovePublication(p.Key, "planned", "ready");
    }
    /// <summary>A new text (🔄 or changed facts): the old preview says it was replaced, a new one comes.</summary>
    private async Task Regenerate(Publication p, DateTimeOffset utc, string? why, CancellationToken ct)
    {
        var (text, silent) = await TextFor(p, ct, fresh: true);
        if (text is null) { await Finish(p, "silent", silent, ct); return; }
        await Replace(p, "🔄 Заменено" + (why is null ? " новым вариантом." : $": {why}."), ct);
    }
    private async Task Replace(Publication p, string oldPreview, CancellationToken ct)
    {
        await ClosePreview(p, oldPreview, ct);
        var fresh = store.Publication(p.Key)!;
        if (!fresh.Active) return;
        store.SetPublicationPreview(p.Key, await PreviewMessage(fresh, ct));
        store.MovePublication(p.Key, "planned", "ready");
    }

    /// <summary>The developer's buttons under a preview. Anyone else's press, or a press on a replaced preview, does nothing.</summary>
    private async Task PublicationCallback(JsonElement cb, long actor, CancellationToken ct)
    {
        var callback = cb.Text("id") ?? "";
        var chat = cb.Field("message").Field("chat").Number("id");
        var messageId = (int)cb.Field("message").Number("message_id");
        var parts = (cb.Text("data") ?? "").Split(':');
        if (actor != config.OwnerId || chat != actor || parts.Length != 3 || !long.TryParse(parts[2], out var id)) return;
        await publicationsGate.WaitAsync(ct);
        try
        {
            var p = store.Publication(id);
            if (p is null || !p.Active || p.Preview != messageId) { await SafeCallback(callback, "Уже не актуально", ct); return; }
            switch (parts[1])
            {
                case "c":
                    await SafeCallback(callback, "Не отправлю", ct);
                    await Finish(p, "cancelled", "отменил разработчик", ct, "🚫 Не отправлю.");
                    return;
                case "s":
                    await SafeCallback(callback, "Отправляю…", ct);
                    try { await Deliver(p, DateTimeOffset.UtcNow, ct, antiDup: false); }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        log.LogWarning("Publication {Key} not sent on request ({Type})", p.Key, Failure(e));
                        await telegram.Send(actor, 0, "Не отправилось: " + FailureRu(e) + ". Попробую сам в назначенное время.", ct);
                    }
                    return;
                case "r":
                    await SafeCallback(callback, "Пишу заново…", ct);
                    try { await Regenerate(p, DateTimeOffset.UtcNow, null, ct); }
                    catch (Exception e) when (!ct.IsCancellationRequested) { await telegram.Send(actor, 0, "Не получилось: " + FailureRu(e) + ". Нажми ещё раз.", ct); }
                    return;
                case "e":
                    var (slot, replaced) = store.OpenSlot(actor, actor, 0, PublicationEdit, Json.Write(new { id }), "[]", "Новый текст публикации",
                        "pubedit:" + callback, "{}", DateTimeOffset.UtcNow, EditWindow);
                    if (replaced is not null) await DropButtons(replaced, ct);
                    await SafeCallback(callback, "Жду текст", ct);
                    await telegram.Send(actor, 0, $"✏️ Пришли новый текст следующим сообщением (в течение {EditWindow.TotalMinutes:0} минут) — уйдёт именно он." +
                        (p.Kind is "summary" or "eve" ? " Это вступление: расписание и сдачи бот добавит ниже сам." : "") + "\n«отмена» — оставить как было.", ct);
                    return;
            }
        }
        finally { publicationsGate.Release(); }
    }

    /// <summary>The developer's next DM after ✏️: it becomes the text, a new preview follows.</summary>
    private async Task PublicationEditText(JsonElement m, string key, PendingSlot slot, string text, CancellationToken ct)
    {
        await publicationsGate.WaitAsync(ct);
        try
        {
            var p = slot.KnownArgs.Field("id").ValueKind == JsonValueKind.Number ? store.Publication(slot.KnownArgs.Field("id").GetInt64()) : null;
            var trimmed = text.Trim();
            if (trimmed.ToLowerInvariant() is "отмена" or "не надо" or "отбой")
            {
                store.CloseSlot(slot.Id, "cancelled");
                await Respond(m, key, "Оставил как было.", ct); return;
            }
            if (p is null || !p.Active)
            {
                store.CloseSlot(slot.Id, "closed");
                await Respond(m, key, "Эта публикация уже ушла или отменена — текст не применён.", ct); return;
            }
            var max = p.Kind is "summary" or "eve" ? OpenRouterClient.LeadMax : OpenRouterClient.TextMax;
            if (trimmed.Length == 0 || trimmed.Length > max) { await Respond(m, key, $"Нужен текст до {max} символов — пришли ещё раз.", ct); return; }
            store.CloseSlot(slot.Id, "answered");
            store.SetPublicationText(p.Key, trimmed, "manual", Basis());
            await Replace(p, "✏️ Заменено твоим текстом.", ct);
            store.SavePost(key, store.Publication(p.Key)!.Preview, "");
        }
        finally { publicationsGate.Release(); }
    }

    // ---------- The web admin's actions on a planned post ----------

    /// <summary>Writes the text now (🔥 in the web admin). A spontaneous drop may decide to stay silent.</summary>
    internal async Task<Publication> Warm(string key, CancellationToken ct)
    {
        await publicationsGate.WaitAsync(ct);
        try
        {
            var p = EnsureKey(key) ?? throw new InvalidOperationException("Неизвестное событие.");
            if (!p.Active) throw new InvalidOperationException("Это событие уже прошло.");
            if (p.Kind == "eve" && DueOn(EventDay(p).AddDays(1)).Count == 0) throw new InvalidOperationException("На следующий день сдач нет — напоминания не будет.");
            var text = await Generate(p, ct);
            if (text is null && p.Kind != "spont") throw new InvalidOperationException("Нейронка не ответила — попробуй ещё раз.");
            store.SetPublicationText(p.Key, text ?? OpenRouterClient.Skip, "ai", Basis());
            if (p.Preview > 0) await Replace(p, "🔄 Заменено текстом из веб-админки.", ct);
            return store.Publication(p.Key)!;
        }
        finally { publicationsGate.Release(); }
    }
    /// <summary>save — the developer's own text; clear — forget the text (written anew later); cancel / restore — leave or return to the plan.</summary>
    internal async Task<Publication> AdminPublication(string key, string action, string? text, CancellationToken ct)
    {
        await publicationsGate.WaitAsync(ct);
        try
        {
            var p = EnsureKey(key) ?? throw new InvalidOperationException("Неизвестное событие.");
            switch (action)
            {
                case "restore":
                    if (!store.RestorePublication(p.Key)) throw new InvalidOperationException("Вернуть можно только отменённое.");
                    break;
                case "cancel":
                    if (!p.Active) throw new InvalidOperationException("Уже отправлено или пропущено.");
                    await Finish(p, "cancelled", "отменено в веб-админке", ct);
                    break;
                case "save":
                    if (!p.Active) throw new InvalidOperationException("Уже отправлено или пропущено.");
                    store.SetPublicationText(p.Key, text, "manual", Basis());
                    if (p.Preview > 0) await Replace(p, "✏️ Заменено текстом из веб-админки.", ct);
                    break;
                default:
                    if (!p.Active) throw new InvalidOperationException("Уже отправлено или пропущено.");
                    store.SetPublicationText(p.Key, null, null, null);
                    if (p.Preview != 0)
                    {
                        await ClosePreview(p, "↺ Текст сброшен — напишу заново.", ct);
                        store.SetPublicationPreview(p.Key, 0);
                    }
                    break;
            }
            return store.Publication(p.Key)!;
        }
        finally { publicationsGate.Release(); }
    }
}
