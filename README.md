# Teams Meeting Transcription Bot

C# .NET 8 bot that joins Microsoft Teams meetings as an invited participant, captures per-participant audio, posts summaries to the meeting chat, and streams everything to the on-premise transcription server via gRPC.

## How It Works

The bot supports two modes depending on how the user adds it:

### Full mode (install app in meeting)

User adds **Transcription Bot** from the Teams app catalog into a meeting. This installs the app in the meeting chat and invites the bot to the call.

1. Bot auto-answers, requests **unmixed audio** (separate stream per participant)
2. Identifies speakers by name via the Teams call roster
3. Posts an intro message to the meeting chat
4. Streams 16kHz PCM audio per participant via gRPC to the GPU server
5. At meeting halftime, posts an interim summary to chat
6. At meeting end, fetches chat messages for richer context, posts the final summary to chat
7. Full transcript and summary available in the web portal

### Voice-only mode (invite only)

User adds the bot as a meeting participant without installing the Teams app. Audio capture works, chat features don't.

1. Bot auto-answers and captures unmixed audio as above
2. No chat messages (intro, halftime, final summary) are posted
3. Chat messages from the meeting are not included in the summary context
4. Transcript and summary available only via the web portal

## Meeting Chat Messages

| When | Message (Estonian) |
|---|---|
| **Join** | "Transkribeerimise bot liitus koosolekuga..." |
| **Halftime** | "Vahearuanne" + summary in detected meeting language |
| **End** | "Koosolek lõppes" + final summary in detected meeting language |

## Prerequisites

- Windows Server 2022 or 2025 (Core edition is sufficient, headless)
- .NET 8 SDK
- Public IP with these ports open:
  - TCP 443 — HTTPS (Graph webhook)
  - UDP 3478-3481 — STUN/TURN (Teams media relays)
  - UDP 49152-53247 — Media relay port range
- TLS certificate (Let's Encrypt via win-acme)
- Network path to the GPU server (for gRPC on port 50051)

## Permissions

### Azure Entra ID (Application, admin consent)

| Permission | Purpose |
|---|---|
| `Calls.JoinGroupCall.All` | Join meetings when invited |
| `Calls.AccessMedia.All` | Capture per-participant audio |

### Teams App Manifest (RSC, no admin consent)

These are granted per-meeting when the bot is installed — no tenant-wide access needed:

| Permission | Type | Purpose |
|---|---|---|
| `ChatMessage.Read.Chat` | Application | Read chat messages from the meeting the bot is in |
| `OnlineMeeting.ReadBasic.Chat` | Application | Read meeting metadata |

### Chat messaging

Summaries are posted to meeting chat via **Bot Framework proactive messaging** (`ContinueConversationAsync`),
not Graph API. This eliminates the need for `Chat.ReadWrite.All` entirely.

## Configuration

`appsettings.json`:

```json
{
  "Bot": {
    "AppId": "<Azure AD Application ID>",
    "AppSecret": "<Client secret value>",
    "TenantId": "<Directory (tenant) ID>",
    "BaseUrl": "https://<server-hostname>",
    "MediaPlatformInstanceId": "<unique GUID>",
    "CertificatePath": "C:\\certs\\<hostname>-chain.pem",
    "CertificatePassword": "",
    "MediaPublicAddress": "<server-public-ip>",
    "MediaPort": 8445
  },
  "Ingestion": {
    "GrpcEndpoint": "http://<gpu-server-ip>:50051"
  }
}
```

## Architecture

```
Teams Meeting
    │
    │ SRTP (unmixed audio per participant)
    ▼
┌──────────────────────────────────────────────┐
│ This Bot (Windows Server)                     │
│                                               │
│  BotService                                   │
│  ├─ OnIncomingCall()     auto-answer          │
│  ├─ OnParticipantsUpdated()  roster tracking  │
│  ├─ PostToChatAsync()    Bot Framework proact. │
│  ├─ OnMidSummaryTimer()  halftime summary     │
│  ├─ FetchChatMessages()  Graph RSC-scoped     │
│  └─ OnCallTerminated()   final summary        │
│                                               │
│  AudioHandler                                 │
│  └─ OnAudioReceived()    50 fps per speaker   │
│     └─ unmixed buffers → PCM per participant  │
│                                               │
│  GrpcForwarder                                │
│  ├─ SendAudioChunk()     stream to GPU server │
│  └─ EndMeeting()         signal + chat msgs   │
└──────────────────────────────────────────────┘
         │ gRPC
         ▼
    GPU Server (transcription pipeline)
```

## Speaker Identification

- **Remote participants** (individual devices): deterministic — the bot gets a separate audio stream per participant tagged with their Media Source ID, which maps to their Teams identity
- **Room devices** (shared conference room mic): the bot gets one mixed stream; Sortformer on the GPU server separates speakers as "Room Speaker 1/2/..."

## Build & Run

```bash
# Build
dotnet restore
dotnet build

# Run (testing)
dotnet run --project src/

# Publish + install as Windows Service (production)
dotnet publish src/ -c Release -o C:\bot\publish
sc create MeetingsBot binPath="C:\bot\publish\MeetingsBot.exe" start=auto
sc start MeetingsBot
```

## Teams App Package

The `TranscriptionBot.zip` contains the Teams app manifest and icons. Upload it via:
- **Teams Admin Center** > Teams apps > Manage apps > Upload new app (org-wide)
- Or sideload in Teams for testing

To customize, edit `teams-app/manifest.json` and repackage:
```bash
cd teams-app && zip -j ../TranscriptionBot.zip manifest.json icon-outline.png icon-color.png
```

## Based On

Microsoft's [PolicyRecordingBot sample](https://github.com/microsoftgraph/microsoft-graph-comms-samples/tree/master/Samples/V1.0Samples/LocalMediaSamples/PolicyRecordingBot) with modifications for invite-based joining, gRPC audio forwarding, meeting chat interaction, and mid-meeting summarization.

## Full Installation Guide

See [INSTALLATION.md](../meetings-transcription/INSTALLATION.md) in the main repository for the complete setup guide covering Azure Entra ID, GPU server, Windows server, and Teams deployment.
