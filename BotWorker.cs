using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Vozduhan.AI;

public sealed class BotWorker(ILogger<BotWorker> log) : BackgroundService
{
    private static readonly HttpClient OpenRouter = new() { BaseAddress = new Uri("https://openrouter.ai/"), Timeout = TimeSpan.FromSeconds(15) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is missing");

        var bot = new TelegramBotClient(token);
        var me = await bot.GetMe(stoppingToken);
        log.LogInformation("Started @{Name}", me.Username);
        await CheckOpenRouter(stoppingToken);
        await bot.DeleteWebhook(cancellationToken: stoppingToken);

        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(offset: offset, limit: 100, timeout: 30,
                    allowedUpdates: [UpdateType.Message], cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    // Do not repeat already handled updates after a transient send error.
                    offset = update.Id + 1;
                    var message = update.Message;
                    if (message?.Text is null) continue;
                    var command = message.Text.Split(' ', 2)[0].Split('@')[0];
                    if (command != "/start") continue;
                    await bot.SendMessage(message.Chat.Id, "Привет! Я Vozduhan.AI 👋", cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Telegram polling error; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task CheckOpenRouter(CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            log.LogWarning("OPENROUTER_API_KEY is missing; Telegram /start still works");
            return;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await OpenRouter.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                log.LogInformation("OpenRouter API key verified");
            else
                log.LogWarning("OpenRouter verification returned HTTP {Status}", (int)response.StatusCode);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "OpenRouter unreachable; Telegram /start remains available");
        }
    }
}
