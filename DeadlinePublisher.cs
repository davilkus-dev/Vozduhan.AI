using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

public sealed partial class BotWorker
{
    private async Task PublishLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await publicationGate.WaitAsync(ct);
                try { await Publish(ct); }
                finally { publicationGate.Release(); }
                store.Maintain(DateTimeOffset.UtcNow);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Deadline publication failed ({Type})", Failure(e));
                await NotifyOwner("Не получается обновить публикации дедлайнов. Проверь права бота в тестовой группе. Записи сохранены, отправка повторится.", ct);
                await Task.Delay(TimeSpan.FromSeconds(e is TelegramError t ? Math.Max(10, t.RetryAfter) : 10), ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
    private async Task Publish(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var all = store.AllDeadlines();
        foreach (var d in all.Where(d => d.Status != "deleted" && ((d.Status == "active" && Deadlines.Ends(config, d) >= now) ||
                     (store.Post("deadline:" + d.Id) is { } p && p.Text != Deadlines.Card(config, d, now)))))
        {
            // A cancelled card is only edited in place; if people deleted it, it is not re-posted (the change news is enough).
            try { await UpsertPost("deadline:" + d.Id, config.DeadlinesTopicId, Deadlines.Card(config, d, now), ct, recreate: d.Status == "active" && Deadlines.Ends(config, d) >= now); }
            catch (TelegramError e) when (e.Code == 429) { log.LogInformation("Deadline cards wait for the Telegram rate limit ({Seconds}s)", e.RetryAfter); break; }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Deadline {Id} failed: {Error}", d.Id, Failure(e));
                await NotifyOwner("Не получается опубликовать карточку дедлайна. Запись сохранена; проверь журнал и права бота.", ct);
            }
        }
        foreach (var d in all.Where(d => d.Status == "deleted"))
            foreach (var p in store.Posts($"change:{d.Id}:").Prepend(store.Post("deadline:" + d.Id)).OfType<SavedPost>())
            {
                try { await RemovePost(p, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("Removing {Key} failed: {Error}", p.Key, Failure(e));
                    await NotifyOwner("Не получается убрать посты удалённого дедлайна. Повторю позже; проверь право удаления сообщений.", ct);
                }
            }
        var urgent = all.Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= now &&
                DateOnly.Parse(d.Date) <= DateOnly.FromDateTime(config.LocalNow(now)).AddDays(config.PinDays))
            .OrderBy(d => d.Date).ThenBy(d => d.Start).ThenBy(d => d.Id).Take(config.MaxPins).Select(d => "deadline:" + d.Id).ToHashSet();
        foreach (var p in store.Posts("deadline:"))
        {
            if (store.Get($"deleted_msg:{config.GroupId}:{p.MessageId}") is not null) continue;
            var want = urgent.Contains(p.Key);
            if (p.Pinned == want) continue;
            try { await telegram.Pin(config.GroupId, p.MessageId, want, ct); store.Pinned(p.Key, want); }
            catch (TelegramError e) when (e.Code == 400 && !want && e.Description.Contains("not found", StringComparison.OrdinalIgnoreCase))
            { store.Pinned(p.Key, false); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Pin {Key} failed: {Error}", p.Key, Failure(e));
                await NotifyOwner("Не получается изменить закрепление. Проверь право закрепления сообщений; публикации продолжаются.", ct);
            }
        }
        foreach (var d in store.Due(now))
        {
            if (!store.CurrentDelivery(d)) continue;
            try
            {
                var parts = SplitPost(d.Text).ToArray();
                for (var i = 0; i < parts.Length; i++)
                    await UpsertPost(i == 0 ? d.Key : d.Key + ":part:" + i, d.TopicId, parts[i], ct);
                foreach (var old in store.Posts(d.Key + ":part:"))
                {
                    if (!int.TryParse(old.Key.Split(':')[^1], out var index) || index < parts.Length) continue;
                    try { await telegram.Delete(config.GroupId, old.MessageId, ct); }
                    catch (TelegramError e) when (e.Code == 400) { await telegram.Edit(config.GroupId, old.MessageId, "Этот фрагмент больше не актуален. См. обновлённый пост выше.", ct); }
                    store.ForgetPost(old.Key);
                }
                store.Delivered(d);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                store.DeliveryFailed(d, e is TelegramError t ? t.RetryAfter : 0);
                log.LogWarning("Scheduled deadline post failed ({Type})", Failure(e));
            }
        }
    }
    private async Task UpsertPost(string key, int topic, string text, CancellationToken ct, bool recreate = true)
    {
        var p = store.Post(key);
        if (p is not null && store.Get($"deleted_msg:{config.GroupId}:{p.MessageId}") is not null) return;
        var probeKey = "post_probe:" + key;
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (p?.Text == text && long.TryParse(store.Get(probeKey), out var lastProbe) && currentTime - lastProbe < 300) return;
        var manualKey = p is null ? null : $"manual_msg:{config.GroupId}:{p.MessageId}";
        if (manualKey is not null && store.Get(manualKey) is not null)
        {
            if (p!.Text == text) return; // edited by hand and nothing changed in the data: leave it alone
            store.Delete(manualKey);      // the deadline itself changed: the fresh card wins
        }
        if (p is not null)
        {
            try
            {
                await telegram.Edit(config.GroupId, p.MessageId, text, ct, html: true);
                store.SavePost(key, p.MessageId, text); store.Set(probeKey, currentTime.ToString()); return;
            }
            catch (TelegramError e) when (e.MissingMessage) { store.ForgetPost(key); if (!recreate) return; }
        }
        else if (!recreate) return;
        var sent = await telegram.Send(config.GroupId, topic, text, ct, html: true);
        store.SavePost(key, (int)sent.Number("message_id"), text);
        store.Set(probeKey, currentTime.ToString());
        store.Remember(config.GroupId, topic, (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
    }
    private async Task RemovePost(SavedPost p, CancellationToken ct)
    {
        if (store.Get($"deleted_msg:{config.GroupId}:{p.MessageId}") is null)
            try { await telegram.Delete(config.GroupId, p.MessageId, ct); }
            catch (TelegramError e) when (e.Code == 400)
            {
                try { await telegram.Edit(config.GroupId, p.MessageId, "🗑 Запись удалена как ошибочная.", ct); }
                catch (TelegramError m) when (m.MissingMessage) { store.ForgetPost(p.Key); return; }
                try { await telegram.Pin(config.GroupId, p.MessageId, false, ct); }
                catch (TelegramError u) when (u.Code == 400) { }
            }
        store.ForgetPost(p.Key);
    }
    public static IEnumerable<string> SplitPost(string text) => TelegramHtml.Split(text);
}
