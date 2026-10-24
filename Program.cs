using Vozduhan.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Json.Options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
var telegram = new TelegramClient(new HttpClient { BaseAddress = new Uri("https://api.telegram.org/"), Timeout = TimeSpan.FromSeconds(65) }, telegramToken);
if (args.Contains("--discover"))
{
    if (string.IsNullOrWhiteSpace(telegramToken)) throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is missing");
    Console.WriteLine("Read-only ID discovery. Stop the service first. No text is logged, no replies are sent. Ctrl+C to exit.");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    long offset = 0;
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var updates = await telegram.Call("getUpdates", new { offset, timeout = 30, allowed_updates = new[] { "message" } }, stop.Token);
            foreach (var u in updates.EnumerateArray())
            {
                var m = u.Field("message");
                Console.WriteLine($"userId={m.Field("from").Number("id")} chatId={m.Field("chat").Number("id")} topicId={m.TopicId()}");
                offset = u.Number("update_id") + 1;
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    return;
}
var configPath = Environment.GetEnvironmentVariable("BOT_CONFIG_PATH") ?? "config/bot.json";
if (!File.Exists(configPath)) throw new InvalidOperationException("Copy config/bot.example.json to BOT_CONFIG_PATH and set real IDs, roster and timetable first");
var config = Json.Read<BotConfig>(File.ReadAllText(configPath));
var dataPath = Environment.GetEnvironmentVariable("BOT_DATA_PATH");
if (!string.IsNullOrWhiteSpace(dataPath)) config.DataPath = dataPath;
config.Validate();
if (args.Contains("--validate-config")) { Console.WriteLine("Configuration is valid. No Telegram/OpenRouter requests made."); return; }
if (args.Contains("--web-link"))
{
    // Works while the service is running and even when Telegram is unreachable.
    using var tool = new BotStore(config, tool: true);
    var token = tool.CreateWebToken(config.OwnerId, "login", TimeSpan.FromMinutes(10));
    Console.WriteLine($"{config.Web.PublicUrl.TrimEnd('/')}/login?t={token}");
    Console.WriteLine("One-time link for the owner, valid for 10 minutes.");
    return;
}
if (string.IsNullOrWhiteSpace(telegramToken)) throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is missing");
var store = new BotStore(config);
var ai = new OpenRouterClient(new HttpClient { BaseAddress = new Uri("https://openrouter.ai/"), Timeout = Timeout.InfiniteTimeSpan },
    config, Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(telegram);
builder.Services.AddSingleton(ai);
builder.Services.AddSingleton<BotWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotWorker>());
if (config.Web.Enabled) builder.Services.AddHostedService<AdminWeb>();
await builder.Build().RunAsync();
