using System.Net.Http.Json;
using System.Text.Json;

namespace Vozduhan.AI;

public static class Fields
{
    public static JsonElement Field(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string? Text(this JsonElement e, string key) => e.Field(key).ValueKind == JsonValueKind.String ? e.Field(key).GetString() : null;
    public static long Number(this JsonElement e, string key) => e.Field(key).TryNumber();
    public static long TryNumber(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? n : 0;
    public static bool Flag(this JsonElement e, string key) => e.Field(key).ValueKind == JsonValueKind.True;
    public static int TopicId(this JsonElement e)
    {
        var id = (int)e.Number("message_thread_id");
        return id > 0 ? id : e.Field("chat").Text("type") is "group" or "supergroup" ? 1 : 0;
    }
    public static string Body(this JsonElement e) => e.Text("text") ?? e.Text("caption") ??
        (e.Field("photo").ValueKind == JsonValueKind.Array ? "[фото]" :
        e.Field("sticker").ValueKind == JsonValueKind.Object ? "[стикер]" :
        e.Field("voice").ValueKind == JsonValueKind.Object ? "[голосовое сообщение]" :
        e.Field("document").ValueKind == JsonValueKind.Object ? $"[файл: {e.Field("document").Text("file_name")}]" : "[нет текста]");
}

public sealed class TelegramError(string method, int code, string description, int retryAfter = 0)
    : Exception($"Telegram {method}: {code} {description}")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
    public string Description { get; } = description;
    public int RetryAfter { get; } = retryAfter;
    public bool MissingMessage => Code == 400 && (Description.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) ||
        Description.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase));
}

public sealed class TelegramClient(HttpClient http, string token)
{
    private long blockedUntil;

    public async Task<JsonElement> Call(string method, object data, CancellationToken ct)
    {
        var remaining = Interlocked.Read(ref blockedUntil) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining > 0) throw new TelegramError(method, 429, "Local Telegram rate-limit cooldown", (int)Math.Ceiling(remaining / 1000d));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(method == "getUpdates" ? 65 : 25));
        using var response = await http.PostAsJsonAsync($"./bot{token}/{method}", data, Json.Options, timeout.Token);
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token); }
        catch (JsonException)
        {
            if (!response.IsSuccessStatusCode)
                throw new TelegramError(method, (int)response.StatusCode, "Non-JSON HTTP error", 5);
            throw new HttpRequestException("Telegram returned invalid JSON");
        }
        using (document)
        {
            var root = document.RootElement;
            if (!root.Flag("ok"))
            {
                var code = (int)root.Number("error_code");
                if (code == 0) code = response.IsSuccessStatusCode ? 502 : (int)response.StatusCode;
                var retry = (int)root.Field("parameters").Number("retry_after");
                if (code == 429)
                {
                    retry = Math.Max(1, retry);
                    var until = DateTimeOffset.UtcNow.AddSeconds(retry).ToUnixTimeMilliseconds();
                    long old;
                    do { old = Interlocked.Read(ref blockedUntil); if (old >= until) break; }
                    while (Interlocked.CompareExchange(ref blockedUntil, until, old) != old);
                }
                throw new TelegramError(method, code, root.Text("description") ?? "Unknown API error", retry);
            }
            return root.Field("result").Clone();
        }
    }
    public Task<JsonElement> Send(long chat, int topic, string text, CancellationToken ct, object? keyboard = null, int? reply = null) =>
        Call("sendMessage", new
        {
            chat_id = chat, message_thread_id = topic > 1 ? (int?)topic : null, text,
            reply_markup = keyboard,
            reply_parameters = reply.HasValue ? new { message_id = reply.Value, allow_sending_without_reply = true } : null,
            link_preview_options = new { is_disabled = true }
        }, ct);
    public async Task Edit(long chat, int id, string text, CancellationToken ct, object? keyboard = null)
    {
        try { await Call("editMessageText", new { chat_id = chat, message_id = id, text, reply_markup = keyboard,
            link_preview_options = new { is_disabled = true } }, ct); }
        catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) { }
    }
    public Task<JsonElement> Delete(long chat, int id, CancellationToken ct) => Call("deleteMessage", new { chat_id = chat, message_id = id }, ct);
    public Task<JsonElement> Callback(string id, string text, CancellationToken ct) => Call("answerCallbackQuery", new { callback_query_id = id, text, show_alert = false }, ct);
    public Task<JsonElement> Pin(long chat, int id, bool pin, CancellationToken ct) => Call(pin ? "pinChatMessage" : "unpinChatMessage",
        new { chat_id = chat, message_id = id, disable_notification = true }, ct);
    public Task<JsonElement> React(long chat, int id, string emoji, CancellationToken ct) => Call("setMessageReaction",
        new { chat_id = chat, message_id = id, reaction = new[] { new { type = "emoji", emoji } } }, ct);
}
