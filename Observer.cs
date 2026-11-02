using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// H2: joining a lively conversation in Воздуханство nobody called the bot into. Every condition is checked by code;
/// a cheap model (kind "observer") only decides whether and how to join, the answer goes as a reply to the chosen message.
/// </summary>
public sealed partial class BotWorker
{
    public const int ObserverRecent = 15, ObserverLively = 4, ObserverLivelyPeople = 2;
    public static readonly TimeSpan ObserverWindow = TimeSpan.FromMinutes(10), ObserverCallGap = TimeSpan.FromMinutes(10);
    private static readonly Regex ObserverStop = new(@"бот\W{0,3}молчи|не\s+лезь", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EditorChange = new(@"перенес|перенос|добав|измен|поменя|отмен|удал|сдвин|замен|исправ|отчисл|убер|постав", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A group message not addressed to the bot. Returns true when the observer model was asked.</summary>
    internal async Task<bool> Observe(JsonElement m, DateTimeOffset utc, CancellationToken ct)
    {
        var f = config.Features;
        var chat = m.Field("chat").Number("id"); var topic = m.TopicId();
        if (!f.Observer || chat != config.GroupId || topic != config.ChatTopicId) return false;
        var local = config.LocalNow(utc);
        var today = DateOnly.FromDateTime(local);
        var day = today.ToString("yyyy-MM-dd");
        var time = TimeOnly.FromDateTime(local);
        if (time < TimeOnly.Parse(config.Ambient.ActiveFrom) || time > TimeOnly.Parse(config.Ambient.ActiveTo) || store.Get("observer:quiet:" + day) is not null) return false;
        bool Bot(JsonElement x) => x.Field("from").Flag("is_bot") || x.Field("from").Number("id") == botId;
        string Said(JsonElement x) => x.Text("text") ?? x.Text("caption") ?? "";
        var recent = store.History(chat, topic, int.MaxValue, ObserverRecent).Select(raw => JsonSerializer.Deserialize<JsonElement>(raw)).ToList();
        if (recent.Any(x => !Bot(x) && ObserverStop.IsMatch(Said(x)))) { store.Set("observer:quiet:" + day, "1"); return false; }
        var now = utc.ToUnixTimeSeconds();
        var lively = recent.Where(x => !Bot(x) && x.Number("date") >= now - (long)ObserverWindow.TotalSeconds).ToList();
        if (lively.Count < ObserverLively || lively.Select(x => x.Field("from").Number("id")).Distinct().Count() < ObserverLivelyPeople) return false;
        if (store.LastBotMessage(chat, topic, botId) is { } said && now - said < f.ObserverMinGapMinutes * 60L) return false;
        if (int.TryParse(store.Get("observer:count:" + day), out var joined) && joined >= f.ObserverPerDay) return false;
        if (long.TryParse(store.Get("observer:call:" + topic), out var called) && now - called < ObserverCallGap.TotalSeconds) return false;
        store.Set("observer:call:" + topic, now.ToString());

        var caps = Catalog.For("member", Capabilities.Group).Where(c => c.Mode == Capabilities.Render).ToList();
        string Who(JsonElement x) => Bot(x) ? "бот" : config.FindMember(x.Field("from").Number("id"))?.ShortName ?? x.Field("from").Text("first_name") ?? "кто-то";
        var input = Json.Write(new
        {
            now = local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + ", " + Schedule.DayNames[Schedule.Dow(today)],
            calendar = Enumerable.Range(0, 7).Select(i => today.AddDays(i)).Select((d, i) => $"{d:yyyy-MM-dd} {Schedule.DayShort[Schedule.Dow(d)].ToLowerInvariant()}" +
                (i switch { 0 => " (сегодня)", 1 => " (завтра)", _ => "" })),
            group = config.R.Members.Where(x => x.Active).Select(x => new { short_name = x.ShortName, username = x.Username, role = x.UserId == config.OwnerId ? "developer" : x.Role }),
            messages = recent.Select(x => new
            {
                message_id = x.Number("message_id"), author = Who(x),
                text = Conversation.Limit(MediaDigest.Text(x, store).Replace('\n', ' '), 300),
                reply_to = x.Field("reply_to_message").Number("message_id") is > 0 and var to ? to : (long?)null
            }),
            capabilities = caps.Select(c => c.Id + (c.Args.Length > 0 ? "(" + string.Join("; ", c.Args.Select(a => a.Name + ": " + a.Type + (a.Required ? ", обязательно" : ""))) + ")" : "") + " — " + c.Description)
        });
        JsonElement root;
        try { using var doc = JsonDocument.Parse(await ai.Observe(input, $"observe:{chat}:{m.Number("message_id")}", ct)); root = doc.RootElement.Clone(); }
        catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning("Observer failed ({Type})", Failure(e)); return true; }
        if (root.ValueKind != JsonValueKind.Object || root.Field("join").ValueKind != JsonValueKind.True) return true;
        var target = recent.FirstOrDefault(x => x.Number("message_id") == root.Field("reply_to").TryNumber() && !Bot(x));
        if (target.ValueKind != JsonValueKind.Object) return true;
        var author = target.Field("from").Number("id");
        if (config.CanEdit(author) && EditorChange.IsMatch(Said(target))) return true;
        var key = $"observe:{chat}:{target.Number("message_id")}";
        if (store.Post(key) is not null) return true;
        switch (root.Text("mode"))
        {
            case "render":
                var cap = caps.FirstOrDefault(c => c.Id == root.Text("capability"));
                if (cap is null || !Capabilities.TryArgs(cap, root.Field("args"), today, out var values, out _)) return true;
                var html = await cap.Handler(new(author, "member", Capabilities.Group, values, target, key, Said(target)), ct);
                if (html is null) return true;
                store.Set("observer:count:" + day, (joined + 1).ToString());
                await Respond(target, key, html, ct, html: true);
                return true;
            case "writer":
                store.Set("observer:count:" + day, (joined + 1).ToString());
                await Chat(target, key, false, ct, author);
                return true;
        }
        return true;
    }
}
