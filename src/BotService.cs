using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Graph;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Identity.Client;
using Microsoft.Skype.Bots.Media;
using MeetingsBot;
using MeetingsBot.Models;

/// <summary>
/// Core bot service — handles incoming calls from Teams, manages audio sessions,
/// and forwards per-participant PCM audio to the on-premise gRPC ingestion server.
///
/// Follows the pattern from Microsoft's PolicyRecordingBot sample:
/// https://github.com/microsoftgraph/microsoft-graph-comms-samples/tree/master/Samples/V1.0Samples/LocalMediaSamples/PolicyRecordingBot
///
/// Lifecycle:
///   1. InitializeAsync() — sets up Graph Communications client + media platform
///   2. Teams calls the webhook → HandleCallNotificationAsync() → routes to SDK
///   3. OnIncomingCallReceived() — auto-answers with unmixed audio config
///   4. OnParticipantsUpdated() — tracks MSI → participant identity
///   5. AudioHandler.OnAudioMediaReceived() — forwards PCM via gRPC
///   6. OnCallTerminated() — signals EndMeeting to ingestion server
/// </summary>
public class BotService
{
    private readonly BotOptions _botOptions;
    private readonly IngestionOptions _ingestionOptions;
    private readonly ConcurrentDictionary<string, MeetingSession> _activeMeetings = new();
    private readonly ConcurrentDictionary<string, AudioHandler> _audioHandlers = new();

    /// <summary>
    /// Stores Bot Framework conversation references keyed by chat thread ID.
    /// Captured when the bot is installed in a meeting chat (conversationUpdate event).
    /// Used for proactive messaging — no Chat.ReadWrite.All permission needed.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConversationReference> _conversationReferences = new();

    private ICommunicationsClient? _commsClient;
    private IMediaPlatform? _mediaPlatform;
    private GrpcForwarder? _grpcForwarder;
    private IBotFrameworkHttpAdapter? _botAdapter;
    private IConfidentialClientApplication? _msalApp;

    public BotService(BotOptions botOptions, IngestionOptions ingestionOptions)
    {
        _botOptions = botOptions;
        _ingestionOptions = ingestionOptions;
    }

    /// <summary>
    /// Initialize the Graph Communications SDK and media platform.
    /// Must be called once at application startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        Console.WriteLine($"[BotService] Initializing with App ID: {_botOptions.AppId}");
        Console.WriteLine($"[BotService] Webhook URL: {_botOptions.BaseUrl}/api/calls");
        Console.WriteLine($"[BotService] gRPC endpoint: {_ingestionOptions.GrpcEndpoint}");

        // 1. Initialize gRPC forwarder
        _grpcForwarder = new GrpcForwarder(_ingestionOptions);

        // 2. Initialize MSAL for Graph API token acquisition (used for reading chat via RSC)
        _msalApp = ConfidentialClientApplicationBuilder
            .Create(_botOptions.AppId)
            .WithClientSecret(_botOptions.AppSecret)
            .WithAuthority($"https://login.microsoftonline.com/{_botOptions.TenantId}")
            .Build();

        // 3. Initialize Bot Framework adapter for proactive messaging to meeting chats.
        // This replaces Graph API POST /chats/{id}/messages and eliminates Chat.ReadWrite.All.
        _botAdapter = new BotFrameworkHttpAdapter(
            new SimpleCredentialProvider(_botOptions.AppId, _botOptions.AppSecret));

        // 4. Set up Graph Communications auth provider
        var authProvider = new AuthenticationProvider(
            _botOptions.AppId,
            _botOptions.AppSecret,
            _botOptions.TenantId,
            _msalApp);

        // 5. Initialize the media platform for application-hosted media
        // TODO: In production, load a real certificate from _botOptions.CertificatePath.
        // The media platform requires a certificate for SRTP media encryption.
        // For development, you can create a self-signed cert:
        //   New-SelfSignedCertificate -Subject "CN=MeetingsBot" -CertStoreLocation "Cert:\CurrentUser\My"
        _mediaPlatform = CreateMediaPlatform();

        // 6. Build the communications client
        var builder = new CommunicationsClientBuilder("MeetingsBot", _botOptions.AppId);
        builder.SetAuthenticationProvider(authProvider);
        builder.SetNotificationUrl(new Uri($"{_botOptions.BaseUrl}/api/calls"));
        // TODO: Pass media platform settings to the builder.
        // The exact property name depends on the SDK version.
        // builder.SetMediaPlatformSettings(_mediaPlatform.Settings);
        builder.SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0"));

        _commsClient = builder.Build();

        // 7. Register event handlers
        _commsClient.Calls().OnIncoming += OnIncomingCallReceived;
        _commsClient.Calls().OnUpdated += OnCallUpdated;

        Console.WriteLine("[BotService] Initialization complete. Waiting for incoming calls...");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle incoming HTTP notification from Microsoft Graph.
    /// The Communications SDK processes the notification and triggers appropriate event handlers.
    /// </summary>
    public async Task HandleCallNotificationAsync(HttpContext context)
    {
        Console.WriteLine("[BotService] Received call notification");

        if (_commsClient == null)
        {
            Console.Error.WriteLine("[BotService] Communications client not initialized");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Bot not initialized");
            return;
        }

        try
        {
            // Read the request body
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            // Let the SDK process the notification — it will invoke our registered handlers
            // (OnIncomingCallReceived, OnCallUpdated, etc.) as appropriate.
            var response = await _commsClient.ProcessNotificationAsync(
                new HttpRequestMessage
                {
                    RequestUri = new Uri($"{_botOptions.BaseUrl}{context.Request.Path}"),
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                    Method = HttpMethod.Post
                });

            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content != null)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                await context.Response.WriteAsync(responseBody);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BotService] Error processing notification: {ex.Message}");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Internal error");
        }
    }

    /// <summary>
    /// Expose the Bot Framework adapter so Program.cs can wire up the /api/messages endpoint.
    /// </summary>
    public IBotFrameworkHttpAdapter BotAdapter => _botAdapter!;

    /// <summary>
    /// Handle incoming Bot Framework activities (conversationUpdate, message, installationUpdate).
    /// The key purpose is capturing the ConversationReference when the bot is installed in a
    /// meeting chat, enabling proactive messaging without Chat.ReadWrite.All.
    /// </summary>
    public async Task OnBotFrameworkActivityAsync(ITurnContext turnContext, CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;

        if (activity.Type == ActivityTypes.ConversationUpdate ||
            activity.Type == ActivityTypes.InstallationUpdate)
        {
            // Capture the conversation reference for this meeting chat.
            // This is the key to proactive messaging — we store the reference
            // when the bot is installed and reuse it to send summaries later.
            var conversationRef = activity.GetConversationReference();
            var chatId = conversationRef.Conversation?.Id ?? "";

            if (!string.IsNullOrEmpty(chatId))
            {
                _conversationReferences[chatId] = conversationRef;
                Console.WriteLine($"[BotService] Captured conversation reference for chat: {chatId}");

                // Mark any active meeting session with this chat as chat-enabled
                var session = _activeMeetings.Values.FirstOrDefault(s => s.ChatThreadId == chatId);
                if (session != null)
                {
                    session.ChatEnabled = true;
                    Console.WriteLine($"[BotService] Chat features enabled for meeting: {session.MeetingId}");
                }
            }

            if (activity.Type == ActivityTypes.ConversationUpdate &&
                activity.MembersAdded?.Any(m => m.Id?.Contains(_botOptions.AppId) == true) == true)
            {
                Console.WriteLine($"[BotService] Bot installed in meeting chat: {chatId}");
            }
        }
    }

    /// <summary>
    /// Called when an incoming call is received (Teams invites the bot to a meeting).
    /// Auto-answers the call with unmixed audio configuration.
    /// </summary>
    private void OnIncomingCallReceived(object? sender, CollectionEventArgs<ICall> e)
    {
        var call = e.AddedResources.FirstOrDefault();
        if (call == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var callId = call.Id;
                Console.WriteLine($"[BotService] Incoming call: {callId}");

                // Extract the organizer/inviter as the transcript owner
                var ownerAadId = "";
                var meetingTitle = "";

                // The person who invited the bot owns the transcript
                var source = call.Resource?.Source;
                if (source?.Identity?.User != null)
                {
                    ownerAadId = source.Identity.User.Id ?? "";
                    Console.WriteLine($"[BotService] Transcript owner (inviter): {ownerAadId}");
                }
                else if (call.Resource?.IncomingContext?.ObservedParticipantId != null)
                {
                    ownerAadId = call.Resource.IncomingContext.ObservedParticipantId;
                }

                // Try to extract meeting title from the call subject
                meetingTitle = call.Resource?.Subject ?? "Untitled Meeting";

                // Capture the chat thread ID for fetching chat messages later
                var chatThreadId = call.Resource?.ChatInfo?.ThreadId ?? "";
                if (!string.IsNullOrEmpty(chatThreadId))
                {
                    Console.WriteLine($"[BotService] Chat thread ID: {chatThreadId}");
                }

                // Create meeting session
                var session = new MeetingSession
                {
                    MeetingId = callId,
                    MeetingTitle = meetingTitle,
                    OwnerAadId = ownerAadId,
                    ChatThreadId = chatThreadId,
                    StartedAt = DateTime.UtcNow
                };
                _activeMeetings[callId] = session;

                // Configure unmixed audio for application-hosted media.
                // This tells Teams to send separate audio streams per participant
                // instead of a single mixed stream.
                var mediaConfig = new Microsoft.Graph.Models.AppHostedMediaConfig
                {
                    Blob = CreateMediaConfigBlob()
                };

                // Answer the call with unmixed audio
                await call.AnswerAsync(mediaConfig, acceptedModalities: new[] { Microsoft.Graph.Models.Modality.Audio });
                Console.WriteLine($"[BotService] Answered call: {callId}");

                // Register participant roster change handler for this call
                call.Participants.OnUpdated += (s, args) => OnParticipantsUpdated(callId, args);

                // Register call termination handler
                call.OnUpdated += (s, args) =>
                {
                    if (call.Resource?.State == Microsoft.Graph.Models.CallState.Terminated)
                    {
                        OnCallTerminated(callId);
                    }
                };

                // Set up audio handler for this call
                if (_grpcForwarder != null)
                {
                    var audioHandler = new AudioHandler(
                        callId, ownerAadId, meetingTitle, session, _grpcForwarder);
                    _audioHandlers[callId] = audioHandler;

                    // Subscribe to the audio socket from the media session
                    // TODO: The actual IAudioSocket reference comes from the media platform
                    // after the call is established. In the PolicyRecordingBot sample, this
                    // is obtained via the call's MediaSession.AudioSocket property.
                    // The exact API depends on how the media platform was configured.
                    var mediaSession = call.GetLocalMediaSession();
                    if (mediaSession?.AudioSocket != null)
                    {
                        audioHandler.Subscribe(mediaSession.AudioSocket);
                        Console.WriteLine($"[BotService] Audio handler subscribed for call: {callId}");
                    }
                }

                // Update recording status to show the recording indicator in Teams UI.
                try
                {
                    await call.UpdateRecordingStatusAsync(Microsoft.Graph.Models.RecordingStatus.Recording);
                    Console.WriteLine($"[BotService] Recording indicator set for call: {callId}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[BotService] Failed to set recording status: {ex.Message}");
                }

                // Check if bot is installed in this meeting chat (ConversationReference exists).
                // If yes, enable chat features. If not, the bot runs in voice-only mode.
                if (!string.IsNullOrEmpty(chatThreadId) && _conversationReferences.ContainsKey(chatThreadId))
                {
                    session.ChatEnabled = true;
                    Console.WriteLine($"[BotService] Chat features enabled (bot installed before call)");
                }
                else if (!string.IsNullOrEmpty(chatThreadId))
                {
                    Console.WriteLine($"[BotService] Voice-only mode — bot not installed in meeting chat. " +
                        "Transcript available via web UI only.");
                }

                // Post intro message to meeting chat (only if chat is available)
                if (session.ChatEnabled && !string.IsNullOrEmpty(chatThreadId))
                {
                    await PostToChatAsync(chatThreadId,
                        "📋 **Transkribeerimise bot liitus koosolekuga.** Salvestan ja transkribeerin seda koosolekut.\n\n" +
                        "• Vahearuanne postitatakse koosoleku poole peal.\n" +
                        "• Lõppkokkuvõte postitatakse koosoleku lõpus.\n" +
                        "• Täielik transkriptsioon on saadaval veebiportaalis pärast koosolekut.");

                    // Schedule a single halftime summary.
                    // Use scheduled meeting duration if available, otherwise default to 60 min
                    // (halftime = 30 min). The timer fires once (Timeout.InfiniteTimeSpan = no repeat).
                    var scheduledDuration = call.Resource?.ToneInfo?.SequenceId != null
                        ? TimeSpan.FromMinutes(60) // TODO: extract actual scheduled duration from meeting info
                        : TimeSpan.FromMinutes(60);
                    var halftime = TimeSpan.FromTicks(scheduledDuration.Ticks / 2);
                    session.ScheduledDuration = scheduledDuration;

                    session.MidSummaryTimer = new Timer(
                        async _ => await OnMidSummaryTimerAsync(callId),
                        null,
                        halftime,
                        Timeout.InfiniteTimeSpan);  // Fire once only

                    Console.WriteLine($"[BotService] Halftime summary scheduled at {halftime.TotalMinutes} min");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BotService] Error handling incoming call: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Called when the call state changes (ringing → established → terminated).
    /// </summary>
    private void OnCallUpdated(object? sender, CollectionEventArgs<ICall> e)
    {
        foreach (var call in e.AddedResources)
        {
            Console.WriteLine($"[BotService] Call updated: {call.Id}, state: {call.Resource?.State}");
        }
    }

    /// <summary>
    /// Called when participants join, leave, or update in the meeting.
    /// Updates the MSI → participant identity mapping used by AudioHandler.
    ///
    /// Each participant in a Teams call has a Media Source ID (MSI) assigned by the
    /// media platform. The unmixed audio buffers reference this MSI. We need the roster
    /// to map MSI → display name / AAD ID for the transcription pipeline.
    /// </summary>
    private void OnParticipantsUpdated(string callId, CollectionEventArgs<IParticipant> args)
    {
        if (!_activeMeetings.TryGetValue(callId, out var session))
            return;

        foreach (var participant in args.AddedResources.Concat(args.UpdatedResources))
        {
            var resource = participant.Resource;
            if (resource == null) continue;

            // Extract the media streams to find the participant's MSI
            var audioStream = resource.MediaStreams?
                .FirstOrDefault(s => s.MediaType == Microsoft.Graph.Models.Modality.Audio);
            if (audioStream == null) continue;

            // SourceId is the MSI for this participant's audio
            if (!int.TryParse(audioStream.SourceId, out int msi))
                continue;

            // Extract identity information
            var identity = resource.Info?.Identity;
            var displayName = identity?.User?.DisplayName
                ?? identity?.Application?.DisplayName
                ?? "Unknown";
            var aadUserId = identity?.User?.Id ?? "";

            // Detect Teams Room devices — they use a shared microphone and may need
            // Sortformer-based multi-speaker diarization on the server side.
            // Teams Room endpoints identify as "application" with specific endpoint types.
            bool isRoomDevice = resource.Info?.EndpointType?.ToString()
                ?.Contains("Room", StringComparison.OrdinalIgnoreCase) ?? false;

            var info = new ParticipantInfo
            {
                DisplayName = displayName,
                AadUserId = aadUserId,
                Email = "", // TODO: Resolve email via Graph API user lookup if needed
                IsRoomDevice = isRoomDevice,
                Msi = msi
            };

            session.Participants[msi] = info;
            Console.WriteLine($"[BotService] Participant mapped: MSI {msi} → {displayName} (AAD: {aadUserId}, Room: {isRoomDevice})");
        }

        // Remove participants who left
        foreach (var participant in args.RemovedResources)
        {
            var audioStream = participant.Resource?.MediaStreams?
                .FirstOrDefault(s => s.MediaType == Microsoft.Graph.Models.Modality.Audio);
            if (audioStream != null && int.TryParse(audioStream.SourceId, out int msi))
            {
                if (session.Participants.Remove(msi))
                {
                    Console.WriteLine($"[BotService] Participant removed: MSI {msi}");
                }
            }
        }
    }

    /// <summary>
    /// Called when the meeting call is terminated.
    /// Cleans up resources and signals the ingestion server to begin summarization.
    /// </summary>
    private void OnCallTerminated(string callId)
    {
        _ = Task.Run(async () =>
        {
            Console.WriteLine($"[BotService] Call terminated: {callId}");

            // Clean up audio handler
            if (_audioHandlers.TryRemove(callId, out var audioHandler))
            {
                audioHandler.Dispose();
            }

            // Signal end of meeting to ingestion server
            if (_activeMeetings.TryRemove(callId, out var session) && _grpcForwarder != null)
            {
                // Stop the mid-summary timer
                if (session.MidSummaryTimer != null)
                {
                    await session.MidSummaryTimer.DisposeAsync();
                }

                try
                {
                    // Fetch chat messages (only if bot was installed in the meeting chat)
                    var chatMessages = new List<(string sender, string text, string timestamp)>();
                    if (session.ChatEnabled && !string.IsNullOrEmpty(session.ChatThreadId))
                    {
                        chatMessages = await FetchChatMessagesAsync(session.ChatThreadId);
                        Console.WriteLine($"[BotService] Fetched {chatMessages.Count} chat messages");
                    }

                    // Signal end to the transcription pipeline — this triggers Gemma summarization
                    await _grpcForwarder.EndMeetingAsync(
                        session.MeetingId, session.OwnerAadId, chatMessages);
                    Console.WriteLine($"[BotService] EndMeeting sent for: {session.MeetingTitle}");

                    // Post final summary to chat (only if chat features are available)
                    if (session.ChatEnabled && !string.IsNullOrEmpty(session.ChatThreadId))
                    {
                        var summary = await RequestSummaryFromAssemblyAsync(session.MeetingId, "final");
                        if (!string.IsNullOrEmpty(summary))
                        {
                            var duration = DateTime.UtcNow - session.StartedAt;
                            await PostToChatAsync(session.ChatThreadId,
                                $"✅ **Koosolek lõppes** ({duration.Hours}h {duration.Minutes}min)\n\n{summary}");
                        }
                        else
                        {
                            await PostToChatAsync(session.ChatThreadId,
                                "✅ **Koosolek lõppes.** Kokkuvõtet genereeritakse, see on saadaval veebiportaalis.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[BotService] Voice-only meeting ended. Summary available via web UI.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[BotService] Error sending EndMeeting: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Fetch all chat messages from the meeting thread via Microsoft Graph API.
    /// Uses RSC permission ChatMessage.Read.Chat — scoped to the meeting where the bot is installed.
    /// No tenant-wide Chat.Read.All needed.
    /// </summary>
    private async Task<List<(string sender, string text, string timestamp)>> FetchChatMessagesAsync(string chatThreadId)
    {
        var messages = new List<(string sender, string text, string timestamp)>();

        try
        {
            using var httpClient = new HttpClient();

            // Acquire token via MSAL client credentials flow.
            // The RSC permission (ChatMessage.Read.Chat) is scoped to the meeting chat
            // where the bot is installed — declared in the Teams app manifest.
            if (_msalApp != null)
            {
                var tokenResult = await _msalApp.AcquireTokenForClient(
                    new[] { "https://graph.microsoft.com/.default" })
                    .ExecuteAsync();
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);
            }

            // Only fetch messages from when the bot joined (session start).
            // This is important for recurring meetings where the chat thread
            // contains history from previous occurrences.
            var sinceUtc = _activeMeetings.Values
                .FirstOrDefault(s => s.ChatThreadId == chatThreadId)?.StartedAt ?? DateTime.UtcNow.AddHours(-4);
            var sinceFilter = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var url = $"https://graph.microsoft.com/v1.0/chats/{chatThreadId}/messages?$top=200&$filter=createdDateTime ge {sinceFilter}";
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"[BotService] Failed to fetch chat messages: {response.StatusCode}");
                return messages;
            }

            var json = await response.Content.ReadAsStringAsync();
            // Parse the response — Graph returns { value: [ { from: { user: { displayName } }, body: { content }, createdDateTime } ] }
            // Using System.Text.Json for parsing
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var valueArray))
            {
                foreach (var msg in valueArray.EnumerateArray())
                {
                    // Skip system messages
                    var messageType = msg.TryGetProperty("messageType", out var mt)
                        ? mt.GetString() : "";
                    if (messageType != "message") continue;

                    var senderName = "";
                    if (msg.TryGetProperty("from", out var from) &&
                        from.TryGetProperty("user", out var user) &&
                        user.TryGetProperty("displayName", out var dn))
                    {
                        senderName = dn.GetString() ?? "Unknown";
                    }

                    var bodyContent = "";
                    if (msg.TryGetProperty("body", out var body) &&
                        body.TryGetProperty("content", out var content))
                    {
                        bodyContent = content.GetString() ?? "";
                        // Strip HTML tags (chat messages come as HTML)
                        bodyContent = System.Text.RegularExpressions.Regex.Replace(
                            bodyContent, "<[^>]+>", "").Trim();
                    }

                    var timestamp = msg.TryGetProperty("createdDateTime", out var ts)
                        ? ts.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(bodyContent))
                    {
                        messages.Add((senderName, bodyContent, timestamp));
                    }
                }
            }

            Console.WriteLine($"[BotService] Parsed {messages.Count} chat messages from thread {chatThreadId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BotService] Error fetching chat messages: {ex.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Post a message to the meeting chat via Bot Framework proactive messaging.
    /// Uses the stored ConversationReference — no Graph API permissions needed for sending.
    /// Falls back to constructing a reference from the chat thread ID if not yet captured.
    /// </summary>
    private async Task PostToChatAsync(string chatThreadId, string markdownMessage)
    {
        if (_botAdapter == null)
        {
            Console.Error.WriteLine("[BotService] Bot adapter not initialized");
            return;
        }

        try
        {
            // Look up the stored conversation reference, or build one from the chat thread ID
            if (!_conversationReferences.TryGetValue(chatThreadId, out var conversationRef))
            {
                // Fallback: construct a reference from the chat thread ID.
                // This works when the bot is installed in the meeting but the
                // conversationUpdate event hasn't been processed yet (e.g., race
                // condition between call join and bot install events).
                conversationRef = new ConversationReference
                {
                    Bot = new ChannelAccount { Id = $"28:{_botOptions.AppId}" },
                    Conversation = new ConversationAccount { Id = chatThreadId },
                    ServiceUrl = "https://smba.trafficmanager.net/emea/",
                    ChannelId = "msteams"
                };
                _conversationReferences[chatThreadId] = conversationRef;
                Console.WriteLine($"[BotService] Built fallback conversation reference for {chatThreadId}");
            }

            await ((BotFrameworkHttpAdapter)_botAdapter).ContinueConversationAsync(
                _botOptions.AppId,
                conversationRef,
                async (turnContext, cancellationToken) =>
                {
                    // Convert markdown bold (**text**) to HTML <b>text</b> for Teams rendering
                    var html = Regex.Replace(markdownMessage, @"\*\*(.+?)\*\*", "<b>$1</b>")
                        .Replace("\n", "<br/>");

                    var activity = MessageFactory.Text(string.Empty);
                    activity.TextFormat = "html";
                    activity.Text = html;

                    await turnContext.SendActivityAsync(activity, cancellationToken);
                },
                CancellationToken.None);

            Console.WriteLine($"[BotService] Posted message to chat {chatThreadId} via Bot Framework");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BotService] Error posting to chat: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggered periodically (first at 15min, then every 20min) to post a mid-meeting summary.
    /// Requests a partial summary from the assembly service and posts it to the meeting chat.
    /// </summary>
    private async Task OnMidSummaryTimerAsync(string callId)
    {
        if (!_activeMeetings.TryGetValue(callId, out var session))
            return;

        if (string.IsNullOrEmpty(session.ChatThreadId) || session.HalftimeSummaryPosted)
            return;

        session.HalftimeSummaryPosted = true;
        var elapsed = DateTime.UtcNow - session.StartedAt;

        try
        {
            Console.WriteLine($"[BotService] Generating halftime summary for {callId}");

            var summary = await RequestSummaryFromAssemblyAsync(session.MeetingId, "mid");

            if (!string.IsNullOrEmpty(summary))
            {
                await PostToChatAsync(session.ChatThreadId,
                    $"📝 **Vahearuanne** ({elapsed.Hours}h {elapsed.Minutes}min)\n\n{summary}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BotService] Halftime summary failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Request a summary from the assembly service for the current transcript so far.
    /// The assembly service fetches segments from PostgreSQL and calls Gemma via the summarizer.
    /// </summary>
    private async Task<string> RequestSummaryFromAssemblyAsync(string meetingId, string summaryType)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var url = $"{_ingestionOptions.GrpcEndpoint.Replace(":50051", ":8080")}/summarize-now";

            var body = JsonSerializer.Serialize(new { meeting_id = meetingId, type = summaryType });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("summary", out var s)
                    ? s.GetString() ?? "" : "";
            }

            Console.Error.WriteLine(
                $"[BotService] Summary request failed: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BotService] Error requesting summary: {ex.Message}");
        }

        return "";
    }

    /// <summary>
    /// Creates the media platform configuration for application-hosted media.
    /// Requires a publicly trusted certificate (Let's Encrypt via win-acme).
    /// The certificate must be in the Windows cert store for MTLS with Teams media relays.
    /// </summary>
    private IMediaPlatform CreateMediaPlatform()
    {
        // Resolve the certificate thumbprint — either from explicit config or from PFX file
        var thumbprint = _botOptions.CertificateThumbprint;

        if (string.IsNullOrEmpty(thumbprint) && !string.IsNullOrEmpty(_botOptions.CertificatePath))
        {
            // Load from PFX/PEM file to get the thumbprint
            var cert = new X509Certificate2(
                _botOptions.CertificatePath,
                _botOptions.CertificatePassword);
            thumbprint = cert.Thumbprint;
            Console.WriteLine($"[BotService] Loaded certificate from file, thumbprint: {thumbprint}");
        }

        if (string.IsNullOrEmpty(thumbprint))
        {
            Console.Error.WriteLine("[BotService] WARNING: No certificate configured — media platform cannot start.");
            Console.Error.WriteLine("[BotService] Set Bot:CertificateThumbprint or Bot:CertificatePath in appsettings.json.");
            Console.Error.WriteLine("[BotService] See INSTALLATION.md section 3.4 for win-acme setup.");
            return null!;
        }

        var serviceFqdn = new Uri(_botOptions.BaseUrl).Host;

        var instanceSettings = new MediaPlatformInstanceSettings
        {
            CertificateThumbprint = thumbprint,
            InstancePublicIPAddress = System.Net.IPAddress.Parse(
                _botOptions.MediaPublicAddress),
            InstancePublicPort = _botOptions.MediaPort,
            InstanceInternalPort = _botOptions.MediaPort,
            ServiceFqdn = serviceFqdn,
        };

        var settings = new MediaPlatformSettings
        {
            MediaPlatformInstanceSettings = instanceSettings,
            ApplicationId = _botOptions.AppId,
        };

        MediaPlatform.Initialize(settings);
        Console.WriteLine($"[BotService] Media platform initialized (FQDN: {serviceFqdn}, cert: {thumbprint[..8]}...)");

        // MediaPlatform.Initialize doesn't return an instance — use CreateMediaConfiguration
        // to verify it's working. The IMediaPlatform interface is satisfied by passing settings
        // to the CommunicationsClientBuilder instead.
        return null!;
    }

    /// <summary>
    /// Creates the media configuration blob for answering a call with unmixed audio.
    /// This JSON blob is sent to Teams in the AnswerAsync call.
    /// </summary>
    private static string CreateMediaConfigBlob()
    {
        // The blob configures the media session for the call.
        // For unmixed audio, we request receive-only audio with unmixed meeting audio enabled.
        // The exact format is dictated by the Graph Communications SDK.
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            // Placeholder — the SDK typically generates this blob internally
            // when AppHostedMediaConfig is used. The Blob property may be set to
            // a serialized MediaConfiguration object from the SDK.
            mediaConfiguration = new
            {
                audioConfiguration = new
                {
                    receiveUnmixedMeetingAudio = true,
                    format = "Pcm16K"
                }
            }
        });
    }
}

/// <summary>
/// Tracks an active meeting session with its participants and audio streams.
/// </summary>
public class MeetingSession
{
    public string MeetingId { get; set; } = "";
    public string MeetingTitle { get; set; } = "";
    public string OwnerAadId { get; set; } = "";
    public string ChatThreadId { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public Timer? MidSummaryTimer { get; set; }
    public TimeSpan ScheduledDuration { get; set; } = TimeSpan.FromMinutes(60);
    public bool HalftimeSummaryPosted { get; set; } = false;

    /// <summary>
    /// True when the bot app is installed in the meeting chat (not just invited to the call).
    /// When false, audio capture works but chat read/write is unavailable.
    /// </summary>
    public bool ChatEnabled { get; set; } = false;

    /// <summary>MSI (Media Source ID) -> Participant info mapping</summary>
    public Dictionary<int, ParticipantInfo> Participants { get; set; } = new();
}

public class ParticipantInfo
{
    public string DisplayName { get; set; } = "";
    public string AadUserId { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsRoomDevice { get; set; }
    public int Msi { get; set; }
}

/// <summary>
/// Authentication provider for Graph Communications SDK using client credentials via MSAL.
/// Acquires app-only tokens for outbound requests. Validates JWTs on inbound webhooks.
/// </summary>
public class AuthenticationProvider : IRequestAuthenticationProvider
{
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly string _tenantId;
    private readonly IConfidentialClientApplication _msalApp;

    // Microsoft's OIDC metadata for Bot Framework token validation.
    // Keys rotate periodically — ConfigurationManager handles refresh automatically.
    private Microsoft.IdentityModel.Protocols.ConfigurationManager<
        Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>? _configManager;

    private const string BotFrameworkOpenIdMetadata =
        "https://login.botframework.com/v1/.well-known/openidconfiguration";
    private const string GraphOpenIdMetadata =
        "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";

    public AuthenticationProvider(string appId, string appSecret, string tenantId, IConfidentialClientApplication msalApp)
    {
        _appId = appId;
        _appSecret = appSecret;
        _tenantId = tenantId;
        _msalApp = msalApp;
    }

    /// <summary>
    /// Authenticate an outbound request by adding a Bearer token.
    /// Uses MSAL client credentials flow (app-only, no user context).
    /// </summary>
    public async Task AuthenticateOutboundRequestAsync(HttpRequestMessage request, string tenantId)
    {
        var result = await _msalApp.AcquireTokenForClient(
            new[] { "https://graph.microsoft.com/.default" })
            .ExecuteAsync();
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
    }

    /// <summary>
    /// Validate an inbound request from Microsoft Graph (webhook callback).
    /// Verifies the JWT signature, audience, and issuer against Microsoft's signing keys.
    /// </summary>
    public async Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
    {
        try
        {
            // Extract Bearer token from Authorization header
            var authHeader = request.Headers.Authorization;
            if (authHeader == null || authHeader.Scheme != "Bearer" || string.IsNullOrEmpty(authHeader.Parameter))
            {
                Console.Error.WriteLine("[Auth] Missing or invalid Authorization header");
                return new RequestValidationResult { IsValid = false };
            }

            var token = authHeader.Parameter;

            // Initialize the OIDC configuration manager (caches and auto-refreshes signing keys)
            _configManager ??= new Microsoft.IdentityModel.Protocols.ConfigurationManager<
                Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                BotFrameworkOpenIdMetadata,
                new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever());

            var openIdConfig = await _configManager.GetConfigurationAsync(CancellationToken.None);

            // Also fetch Graph/Entra signing keys (Graph notifications may use either issuer)
            var graphConfigManager = new Microsoft.IdentityModel.Protocols.ConfigurationManager<
                Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
                GraphOpenIdMetadata,
                new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever());
            var graphConfig = await graphConfigManager.GetConfigurationAsync(CancellationToken.None);

            // Combine signing keys from both issuers
            var allKeys = openIdConfig.SigningKeys.Concat(graphConfig.SigningKeys).ToList();

            var validationParams = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = new[]
                {
                    "https://api.botframework.com",
                    $"https://sts.windows.net/{_tenantId}/",
                    $"https://login.microsoftonline.com/{_tenantId}/v2.0"
                },
                ValidateAudience = true,
                ValidAudience = _appId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                IssuerSigningKeys = allKeys
            };

            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            handler.ValidateToken(token, validationParams, out _);

            return new RequestValidationResult { IsValid = true };
        }
        catch (Microsoft.IdentityModel.Tokens.SecurityTokenException ex)
        {
            Console.Error.WriteLine($"[Auth] JWT validation failed: {ex.Message}");
            return new RequestValidationResult { IsValid = false };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Auth] Unexpected error during JWT validation: {ex.Message}");
            return new RequestValidationResult { IsValid = false };
        }
    }
}

/// <summary>
/// Creates a logger using the SDK's built-in factory.
/// The media platform requires an IGraphLogger instance.
/// </summary>
public static class GraphLoggerFactory
{
    public static IGraphLogger Create(string component)
    {
        // Use the SDK's built-in logger creation
        return new GraphLogger(component, redirectToTrace: true);
    }
}
