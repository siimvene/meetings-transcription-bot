using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MeetingsBot.Models;

var builder = WebApplication.CreateBuilder(args);

// Bind strongly typed configuration
builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));

// Register bot services
builder.Services.AddSingleton<BotOptions>(sp =>
{
    var opts = new BotOptions();
    builder.Configuration.GetSection(BotOptions.SectionName).Bind(opts);
    return opts;
});
builder.Services.AddSingleton<IngestionOptions>(sp =>
{
    var opts = new IngestionOptions();
    builder.Configuration.GetSection(IngestionOptions.SectionName).Bind(opts);
    return opts;
});
builder.Services.AddSingleton<BotService>();
builder.Services.AddControllers();

var app = builder.Build();

// Initialize bot on startup
var botService = app.Services.GetRequiredService<BotService>();
await botService.InitializeAsync();

app.MapControllers();

// Webhook endpoint for Graph Communications SDK notifications.
// Teams sends POST requests here when calls are incoming, participants change, etc.
app.MapPost("/api/calls", async (HttpContext context) =>
{
    await botService.HandleCallNotificationAsync(context);
});

// Bot Framework messaging endpoint.
// Teams sends conversationUpdate/installationUpdate activities here when the bot
// is added to a meeting chat. We capture the ConversationReference for proactive messaging.
app.MapPost("/api/messages", async (HttpContext context) =>
{
    await botService.BotAdapter.ProcessAsync(
        context.Request,
        context.Response,
        new BotCallbackHandler(botService));
});

// Health check endpoint for monitoring
app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", timestamp = System.DateTime.UtcNow }));

app.Run();

/// <summary>
/// Bridges the Bot Framework adapter to BotService.OnBotFrameworkActivityAsync.
/// </summary>
internal class BotCallbackHandler : Microsoft.Bot.Builder.IBot
{
    private readonly BotService _botService;

    public BotCallbackHandler(BotService botService)
    {
        _botService = botService;
    }

    public Task OnTurnAsync(Microsoft.Bot.Builder.ITurnContext turnContext, System.Threading.CancellationToken cancellationToken)
    {
        return _botService.OnBotFrameworkActivityAsync(turnContext, cancellationToken);
    }
}
