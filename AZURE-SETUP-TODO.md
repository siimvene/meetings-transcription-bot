# Azure Entra & Bot Setup — Remaining Steps

Must be done from a machine with access to tenant `7bae085e-3093-4c05-8334-7a5421e0af07`.

## 1. Grant Admin Consent for API Permissions

In the Azure portal, go to **Entra ID** > **App registrations** > **MeetingTranscriptionBot** (`dc397cb7-e087-4377-88d7-98bc6e4350f5`).

Go to **API permissions** > **Add a permission** > **Microsoft Graph** > **Application permissions** and add:
- `Calls.JoinGroupCall.All`
- `Calls.AccessMedia.All`

Then click **Grant admin consent for [your org]**.

Or use the consent URL (open in browser, sign in as admin):
```
https://login.microsoftonline.com/7bae085e-3093-4c05-8334-7a5421e0af07/adminconsent?client_id=dc397cb7-e087-4377-88d7-98bc6e4350f5
```

**Do NOT add Chat.ReadWrite.All** — chat uses RSC permissions declared in the Teams app manifest.

## 2. Create Azure Bot Resource (if not already created)

If no Azure Bot Service resource exists yet:

```bash
az bot create \
  --resource-group <your-rg> \
  --name smit-transcription-bot \
  --app-type SingleTenant \
  --appid dc397cb7-e087-4377-88d7-98bc6e4350f5 \
  --tenant-id 7bae085e-3093-4c05-8334-7a5421e0af07 \
  --endpoint "https://smit-transcription-bot.germanywestcentral.cloudapp.azure.com/api/messages" \
  --sku F0
```

Or via portal: **Create a resource** > **Azure Bot** > fill in App ID and messaging endpoint.

## 3. Configure Bot Channels

In the Azure Bot resource:

### Messaging endpoint
Set to: `https://smit-transcription-bot.germanywestcentral.cloudapp.azure.com/api/messages`

### Teams channel
1. Go to **Channels** > **Microsoft Teams**
2. Enable the Teams channel
3. Under **Calling** tab:
   - Enable calling
   - Set webhook URL to: `https://smit-transcription-bot.germanywestcentral.cloudapp.azure.com/api/calls`

## 4. Upload Teams App Package

File: `TranscriptionBot.zip` (in the repo root, already built with real App ID)

Upload via:
- **Teams Admin Center** (`https://admin.teams.microsoft.com`) > **Teams apps** > **Manage apps** > **Upload new app**
- Or sideload in Teams client for testing

## 5. Verify

After all steps:
1. Open Teams, start or schedule a meeting
2. Add "Transcription Bot" as a participant
3. Check the Windows server logs: `sc query MeetingsBot` and event viewer
4. The bot should auto-answer and start capturing audio
5. If the bot app is also installed in the meeting chat, you'll see the intro message

## Current Server Status

### Windows Server (20.52.56.64)
- Bot service: RUNNING (via NSSM)
- Health: `http://localhost:5000/health` returns OK
- Cert: Let's Encrypt, thumbprint `4439000E6B56651D584855CE01942B8B4C8E7504`

### B200 GPU Server (193.40.152.251)
- Gemma 4 31B: RUNNING on port 8000 (HTTPS via nginx on 443)
- WhisperLiveKit (general): RUNNING on port 8100
- WhisperLiveKit (Estonian): RUNNING on port 8101
- Docker containers: postgres, rabbitmq, ingestion, assembly, summarizer, translation-worker, api, web — all running
- LE cert: auto-renewing (shortlived profile, 7-day validity)
