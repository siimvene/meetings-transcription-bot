using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

// Webhook endpoint for Graph notifications.
// Teams sends POST requests here when calls are incoming, participants change, etc.
app.MapPost("/api/calls", async (HttpContext context) =>
{
    await botService.HandleCallNotificationAsync(context);
});

// Health check endpoint for monitoring
app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", timestamp = System.DateTime.UtcNow }));

app.Run();
