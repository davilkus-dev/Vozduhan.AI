using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>One answer to a clarification: the button text and the args it adds to the known ones.</summary>
public sealed record RouteOption(string Label, JsonElement? Args);

/// <summary>
/// A question the bot asked one person in one chat (Router clarify): which capability, the args already known, the options as buttons.
/// State: active → done (a button) | answered (text filled it) | closed (text went elsewhere) | cancelled | replaced | expired.
/// </summary>
public sealed record PendingSlot(long Id, long Actor, long Chat, int Topic, string Capability, string Args, string Options, string Question,
    long Created, long Expires, string State, string Source, string Payload, int Message, int? Choice)
{
    public JsonElement KnownArgs => JsonSerializer.Deserialize<JsonElement>(Args);
    public RouteOption[] Choices => Json.Read<RouteOption[]>(Options) ?? [];
}

public sealed partial class BotStore
{
    private const string SlotColumns = "id,actor,chat,topic,capability,args,options,question,created,expires,state,source,payload,message,choice";
    private static PendingSlot ReadSlot(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetInt64(9), r.GetString(10), r.GetString(11), r.GetString(12),
        r.GetInt32(13), r.IsDBNull(14) ? null : r.GetInt32(14));

    /// <summary>
    /// Opens the person's question in this chat; the active one there is replaced (and returned, to take its buttons away).
    /// The same source message gives the same slot, so a replay after a restart asks nothing new.
    /// </summary>
    public (PendingSlot Slot, PendingSlot? Replaced) OpenSlot(long actor, long chat, int topic, string capability, string args, string options,
        string question, string source, string payload, DateTimeOffset now, TimeSpan life)
    {
        lock (gate)
        {
            if (Query($"SELECT {SlotColumns} FROM pending_slots WHERE source=$s", ReadSlot, ("s", source)).FirstOrDefault() is { } same) return (same, null);
            using var tx = db.BeginTransaction();
            var old = Query($"SELECT {SlotColumns} FROM pending_slots WHERE actor=$a AND chat=$c AND state='active' ORDER BY id DESC", ReadSlot,
                ("a", actor), ("c", chat)).FirstOrDefault();
            Exec("UPDATE pending_slots SET state='replaced' WHERE actor=$a AND chat=$c AND state='active'", ("a", actor), ("c", chat));
            Exec("""
                INSERT INTO pending_slots(actor,chat,topic,capability,args,options,question,created,expires,state,source,payload)
                VALUES($a,$c,$t,$cap,$args,$o,$q,$now,$exp,'active',$s,$p)
                """, ("a", actor), ("c", chat), ("t", topic), ("cap", capability), ("args", args), ("o", options), ("q", question),
                ("now", now.ToUnixTimeSeconds()), ("exp", now.Add(life).ToUnixTimeSeconds()), ("s", source), ("p", payload));
            var id = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
            tx.Commit();
            return (Query($"SELECT {SlotColumns} FROM pending_slots WHERE id=$id", ReadSlot, ("id", id)).Single(), old);
        }
    }
    public PendingSlot? Slot(long id)
    {
        lock (gate) return Query($"SELECT {SlotColumns} FROM pending_slots WHERE id=$id", ReadSlot, ("id", id)).FirstOrDefault();
    }
    /// <summary>The person's unexpired open question in this chat, if any.</summary>
    public PendingSlot? ActiveSlot(long actor, long chat, DateTimeOffset now)
    {
        lock (gate) return Query($"SELECT {SlotColumns} FROM pending_slots WHERE actor=$a AND chat=$c AND state='active' AND expires>$now ORDER BY id DESC LIMIT 1",
            ReadSlot, ("a", actor), ("c", chat), ("now", now.ToUnixTimeSeconds())).FirstOrDefault();
    }
    public void SlotMessage(long id, int message) { lock (gate) Exec("UPDATE pending_slots SET message=$m WHERE id=$id", ("m", message), ("id", id)); }
    /// <summary>Closes an active slot; false if it was not active any more (pressed twice, replaced, cancelled).</summary>
    public bool CloseSlot(long id, string state, int? choice = null)
    {
        lock (gate) return Exec("UPDATE pending_slots SET state=$s, choice=$c WHERE id=$id AND state='active'", ("s", state), ("c", choice), ("id", id)) == 1;
    }
}

public sealed partial class BotWorker
{
    public const string SlotPrefix = "slot:";
    public const int SlotOptionsMax = 4, SlotLabelMax = 40;
    public static TimeSpan SlotLife = TimeSpan.FromMinutes(10);
    public const string SlotStale = "Вопрос устарел";

    /// <summary>Router clarify: the question; for a capability also a slot, with one button per option and «Отмена».</summary>
    private async Task Clarify(JsonElement m, string key, long actor, RouteDecision d, CancellationToken ct, string cancel = "Отмена")
    {
        if (d.Capability is null) { await Respond(m, key, d.Question!, ct); return; }
        var options = d.Options ?? [];
        var (slot, replaced) = store.OpenSlot(actor, m.Field("chat").Number("id"), m.TopicId(), d.Capability, Json.Write(Capabilities.Merge(d.Args, null)),
            Json.Write(options), d.Question!, key, m.GetRawText(), DateTimeOffset.UtcNow, SlotLife);
        if (replaced is not null) await DropButtons(replaced, ct);
        object? keyboard = options.Count == 0 ? null : new
        {
            inline_keyboard = options.Select((o, i) => new[] { new { text = o.Label, callback_data = $"{SlotPrefix}{slot.Id}:{i}" } })
                .Append([new { text = cancel, callback_data = $"{SlotPrefix}{slot.Id}:x" }]).ToArray()
        };
        await Respond(m, key, d.Question!, ct, keyboard);
        if (store.Post(key) is { MessageId: > 0 } sent) store.SlotMessage(slot.Id, sent.MessageId);
    }

    /// <summary>The question stays, its buttons go: it was replaced or answered by text.</summary>
    private async Task DropButtons(PendingSlot slot, CancellationToken ct)
    {
        if (slot.Message <= 0) return;
        try { await telegram.Call("editMessageReplyMarkup", new { chat_id = slot.Chat, message_id = slot.Message }, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { log.LogInformation("Clarification buttons not removed: {Error}", Failure(e)); }
    }

    /// <summary>
    /// A clarification button: only its author, only while it is active and fresh. The choice runs through the catalog
    /// (rights checked now), the result is sent as usual and the question becomes «✓ label».
    /// </summary>
    private async Task SlotCallback(JsonElement cb, long actor, CancellationToken ct)
    {
        var callback = cb.Text("id") ?? "";
        var chat = cb.Field("message").Field("chat").Number("id");
        var messageId = (int)cb.Field("message").Number("message_id");
        var parts = (cb.Text("data") ?? "").Split(':');
        var slot = parts.Length == 3 && long.TryParse(parts[1], out var slotId) ? store.Slot(slotId) : null;
        if (slot is null || slot.Chat != chat) { await SafeCallback(callback, SlotStale, ct); return; }
        if (slot.Actor != actor) { await SafeCallback(callback, "Это не твой вопрос", ct); return; }
        var key = $"slot:{slot.Id}";
        var options = slot.Choices;
        // A retried update whose result was not delivered yet goes on; anything else needs an active, fresh slot.
        var resume = slot.State == "done" && slot.Choice?.ToString() == parts[2] && store.Post(key) is null;
        if (!resume)
        {
            if (slot.State != "active" || slot.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await SafeCallback(callback, SlotStale, ct);
                if (slot.State == "active" && store.CloseSlot(slot.Id, "expired")) await telegram.Edit(chat, messageId, slot.Question, ct);
                return;
            }
            if (parts[2] == "x")
            {
                if (!store.CloseSlot(slot.Id, "cancelled")) { await SafeCallback(callback, SlotStale, ct); return; }
                await SafeCallback(callback, "Отменено", ct);
                await telegram.Edit(chat, messageId, "Отменено.", ct);
                return;
            }
            if (!int.TryParse(parts[2], out var n) || n < 0 || n >= options.Length || !store.CloseSlot(slot.Id, "done", n))
            { await SafeCallback(callback, SlotStale, ct); return; }
            await SafeCallback(callback, "✓", ct);
        }
        var option = options[int.Parse(parts[2])];
        await telegram.Edit(chat, messageId, "✓ " + option.Label, ct);
        var source = JsonSerializer.Deserialize<JsonElement>(slot.Payload);
        var text = (source.Text("text") ?? source.Text("caption") ?? "") + "\n" + option.Label;
        var place = chat == actor ? Capabilities.Dm : Capabilities.Group;
        var result = await Catalog.Execute(actor, place, slot.Capability, Capabilities.Merge(slot.KnownArgs, option.Args), source, key, text, ct);
        if (!result.Ok) await Respond(source, key, RefusalText(result), ct, html: true);
        else if (result.Html is not null) await Respond(source, key, result.Html, ct, html: true);
        else if (store.Post(key) is null) store.SavePost(key, 0, "");
    }

    private bool commandsCleared;
    private long commandsRetryAt;
    /// <summary>
    /// Router on means no commands: the menu is cleared once at start and once each time the flag is switched on.
    /// Switching it off restores nothing (old commands still work when typed).
    /// </summary>
    internal async Task SyncCommands(CancellationToken ct)
    {
        if (!config.Features.Router) { commandsCleared = false; return; }
        if (commandsCleared || Environment.TickCount64 < commandsRetryAt) return;
        try
        {
            foreach (var scope in new object[] { new { type = "default" }, new { type = "all_private_chats" }, new { type = "all_group_chats" },
                         new { type = "chat", chat_id = config.GroupId } })
                await telegram.Call("deleteMyCommands", new { scope }, ct);
            commandsCleared = true;
            log.LogInformation("Bot commands removed (no-commands mode)");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            commandsRetryAt = Environment.TickCount64 + 300_000;
            log.LogWarning("deleteMyCommands failed ({Type}); retry in 5 minutes", Failure(e));
        }
    }
}
