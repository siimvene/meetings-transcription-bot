# Teams Meeting Transcription Bot

C# .NET 8 bot that joins Microsoft Teams meetings as an invited participant, captures per-participant audio, posts summaries to the meeting chat, and streams everything to the on-premise transcription server via gRPC.

## How It Works

1. User adds **Transcription Bot** as a participant in a Teams meeting
2. When the meeting starts, Teams calls the bot
3. Bot auto-answers, requests **unmixed audio** (separate stream per participant)
4. Identifies speakers by name via the Teams call roster (MSI to identity mapping)
5. Posts an intro message to the meeting chat
6. Streams 16kHz PCM audio per participant via gRPC to the GPU server
7. At meeting halftime, requests a summary from the GPU server and posts it to chat
8. At meeting end, fetches chat messages, posts the final summary to chat, and signals the GPU server to generate the full summary

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

## Azure Entra ID Permissions

Register as an Application (not Delegated). All require admin consent:

| Permission | Purpose |
|---|---|
| `Calls.JoinGroupCall.All` | Join meetings when invited |
| `Calls.AccessMedia.All` | Capture per-participant audio |
| `Chat.ReadWrite.All` | Read chat messages, post summaries to meeting chat |

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
│  ├─ PostToChatAsync()    chat messages        │
│  ├─ OnMidSummaryTimer()  halftime summary     │
│  ├─ FetchChatMessages()  read meeting chat    │
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
