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
        foreach (var d in all.Where(d => (d.Status == "active" && Deadlines.Ends(config, d) >= now) ||
                     (store.Post("deadline:" + d.Id) is { } p && p.Text != Deadlines.Card(config, d))))
        {
            try { await UpsertPost("deadline:" + d.Id, config.DeadlinesTopicId, Deadlines.Card(config, d), ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Deadline {Id} failed: {Error}", d.Id, Failure(e));
                await NotifyOwner("Не получается опубликовать карточку дедлайна. Запись сохранена; проверь журнал и права бота.", ct);
            }
        }
        var urgent = all.Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= now &&
                DateOnly.Parse(d.Date) <= DateOnly.FromDateTime(config.LocalNow(now)).AddDays(config.PinDays))
            .OrderBy(d => d.Date).ThenBy(d => d.Start).ThenBy(d => d.Id).Take(config.MaxPins).Select(d => "deadline:" + d.Id).ToHashSet();
        foreach (var p in store.Posts("deadline:"))
        {
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
    private async Task UpsertPost(string key, int topic, string text, CancellationToken ct)
    {
        var p = store.Post(key);
        var probeKey = "post_probe:" + key;
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (p?.Text == text && long.TryParse(store.Get(probeKey), out var lastProbe) && currentTime - lastProbe < 300) return;
        if (p is not null)
        {
            try
            {
                await telegram.Edit(config.GroupId, p.MessageId, text, ct);
                store.SavePost(key, p.MessageId, text); store.Set(probeKey, currentTime.ToString()); return;
            }
            catch (TelegramError e) when (e.MissingMessage) { store.ForgetPost(key); }
        }
        var sent = await telegram.Send(config.GroupId, topic, text, ct);
        store.SavePost(key, (int)sent.Number("message_id"), text);
        store.Set(probeKey, currentTime.ToString());
        store.Remember(config.GroupId, topic, (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
    }
    public static IEnumerable<string> SplitPost(string text)
    {
        while (text.Length > 3800)
        {
            var at = text.LastIndexOf('\n', 3700);
            if (at < 1000) at = 3700;
            yield return text[..at]; text = text[at..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}
