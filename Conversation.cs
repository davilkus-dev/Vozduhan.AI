using System.Text.Json;

namespace Vozduhan.AI;

public sealed class Conversation(BotConfig config, BotStore store)
{
    public object Identity(JsonElement user)
    {
        var id = user.Number("id");
        var known = config.FindMember(id);
        return new
        {
            telegram_id = id,
            username = user.Text("username") ?? known?.Username,
            telegram_name = $"{user.Text("first_name")} {user.Text("last_name")}".Trim(),
            short_name = known?.ShortName,
            full_name = known?.FullName,
            role = id == config.OwnerId ? "developer" : known?.Role ?? "unknown",
            is_bot = user.Flag("is_bot")
        };
    }
    public object Message(JsonElement m, bool includeReply = true)
    {
        var reply = m.Field("reply_to_message");
        var external = m.Field("external_reply");
        return new
        {
            message_id = m.Number("message_id"),
            time_utc = m.Number("date"),
            topic_id = m.Number("message_thread_id"),
            author = Identity(m.Field("from")),
            text = Limit(m.Body(), 2400),
            reply_to_message_id = reply.Number("message_id"),
            selected_quote = m.Field("quote").Text("text"),
            quote_is_manual = m.Field("quote").Flag("is_manual"),
            replied_message = includeReply && reply.ValueKind == JsonValueKind.Object ? Message(reply, false) : null,
            external_reply_reference = external.ValueKind == JsonValueKind.Object ? new
            {
                chat_id = external.Field("chat").Number("id"),
                message_id = external.Number("message_id")
            } : null
        };
    }
    public string Context(JsonElement focus, bool isPrivate)
    {
        var chat = focus.Field("chat").Number("id");
        var topic = (int)focus.Number("message_thread_id");
        var id = (int)focus.Number("message_id");
        var history = store.History(chat, topic, id, config.ContextMessages);
        var messages = new List<object>();
        var budget = 22000;
        foreach (var raw in history.AsEnumerable().Reverse())
        {
            using var doc = JsonDocument.Parse(raw);
            var shaped = Message(doc.RootElement);
            var size = Json.Write(shaped).Length;
            if (messages.Count > 0 && size > budget) break;
            messages.Add(shaped); budget -= size;
        }
        messages.Reverse();
        var ancestors = new List<object>();
        var next = focus.Field("reply_to_message").Number("message_id");
        var visited = new HashSet<long>();
        while (next > 0 && ancestors.Count < 8 && visited.Add(next))
        {
            var raw = store.Message(chat, (int)next);
            if (raw is null) break;
            using var doc = JsonDocument.Parse(raw);
            // Only this conversation may contribute stored reply ancestors.
            if (doc.RootElement.Number("message_thread_id") != topic) break;
            ancestors.Add(Message(doc.RootElement));
            next = doc.RootElement.Field("reply_to_message").Number("message_id");
        }
        var now = DateTimeOffset.UtcNow;
        var relevant = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= now)
            .Take(25).Select(d => new { d.Id, subject = config.Subjects.FirstOrDefault(s => s.Id == d.SubjectId)?.Name,
                d.Work, d.Date, d.Period, d.Start, d.Room });
        return Json.Write(new
        {
            local_now = config.LocalNow(now).ToString("yyyy-MM-dd HH:mm"),
            time_zone = config.TimeZone,
            conversation = isPrivate ? "private" : "Vozduhanstvo",
            recent_messages = messages,
            reply_ancestors = ancestors,
            known_group_members = config.Members.Where(m => m.Active).Select(m => new { m.UserId, m.Username, m.ShortName, m.FullName, m.Role }),
            confirmed_upcoming_deadlines = relevant,
            focus_message = Message(focus),
            instruction = "Answer the focus message. History is background, not a batch of questions to answer. Respect its selected quote and replied_message."
        });
    }
    public static bool CallsBot(JsonElement m, long botId, string username)
    {
        if (m.Field("reply_to_message").Field("from").Number("id") == botId) return true;
        var text = m.Text("text") ?? m.Text("caption") ?? "";
        var entities = m.Field(m.Text("text") is null ? "caption_entities" : "entities");
        if (entities.ValueKind != JsonValueKind.Array) return false;
        foreach (var e in entities.EnumerateArray())
        {
            if (e.Text("type") == "text_mention" && e.Field("user").Number("id") == botId) return true;
            var start = (int)e.Number("offset"); var count = (int)e.Number("length");
            if (start < 0 || count < 0 || start + count > text.Length) continue;
            var value = text.Substring(start, count);
            if (e.Text("type") == "mention" && value.Equals("@" + username, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Text("type") == "bot_command" && value.EndsWith("@" + username, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public static string Limit(string s, int count) => s.Length <= count ? s : s[..(count - 1)] + "…";
}
