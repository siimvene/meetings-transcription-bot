using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;

namespace MeetingsBot;

/// <summary>
/// Handles per-call audio media events. Receives unmixed audio buffers from
/// the Teams media platform and forwards each participant's PCM data via gRPC.
///
/// Teams delivers unmixed audio as 20ms frames at 16kHz, 16-bit mono PCM (640 bytes each),
/// with 50 frames/second per active speaker. Each buffer is tagged with an ActiveSpeakerId
/// (MSI) to identify which participant produced the audio.
///
/// Reference: PolicyRecordingBot BotMediaStream
/// https://github.com/microsoftgraph/microsoft-graph-comms-samples
/// </summary>
public class AudioHandler : IDisposable
{
    private readonly string _meetingId;
    private readonly string _ownerAadId;
    private readonly string _meetingTitle;
    private readonly MeetingSession _session;
    private readonly GrpcForwarder _grpcForwarder;
    private readonly DateTime _meetingStartTime;
    private bool _disposed;

    public AudioHandler(
        string meetingId,
        string ownerAadId,
        string meetingTitle,
        MeetingSession session,
        GrpcForwarder grpcForwarder)
    {
        _meetingId = meetingId;
        _ownerAadId = ownerAadId;
        _meetingTitle = meetingTitle;
        _session = session;
        _grpcForwarder = grpcForwarder;
        _meetingStartTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Subscribes to audio events on the given audio socket.
    /// Call this after the call is established and the media session is ready.
    /// </summary>
    public void Subscribe(IAudioSocket audioSocket)
    {
        if (audioSocket == null) throw new ArgumentNullException(nameof(audioSocket));
        audioSocket.AudioMediaReceived += OnAudioMediaReceived;
    }

    /// <summary>
    /// Callback invoked by the media platform when an unmixed audio buffer arrives.
    /// Each buffer contains PCM data from a single participant identified by ActiveSpeakerId (MSI).
    ///
    /// Teams unmixed audio format:
    /// - Sample rate: 16,000 Hz
    /// - Bit depth: 16-bit signed
    /// - Channels: 1 (mono)
    /// - Frame duration: 20ms
    /// - Frame size: 640 bytes (16000 * 2 * 0.020)
    /// </summary>
    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            var buffer = e.Buffer;

            // In unmixed audio mode, the buffer contains UnmixedAudioBuffers —
            // one per active speaker. Each has an ActiveSpeakerId (MSI).
            // The MSI maps to participant identity via the call roster.
            if (buffer.UnmixedAudioBuffers == null || buffer.UnmixedAudioBuffers.Length == 0)
            {
                buffer.Dispose();
                return;
            }

            foreach (var unmixedBuffer in buffer.UnmixedAudioBuffers)
            {
                int msi = (int)unmixedBuffer.ActiveSpeakerId;
                ProcessUnmixedBuffer(unmixedBuffer, msi);
            }

            buffer.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AudioHandler] Error processing audio: {ex.Message}");
        }
    }

    private void ProcessUnmixedBuffer(UnmixedAudioBuffer unmixedBuffer, int msi)
    {
        try
        {

            // Skip if we haven't mapped this MSI to a participant yet
            if (!_session.Participants.TryGetValue(msi, out var participant))
            {
                // This can happen briefly when a participant joins but roster update hasn't arrived yet.
                // The audio will be lost, but roster updates are frequent so this is a short window.
                return;
            }

            // Extract the raw PCM bytes from the unmixed buffer
            byte[] pcmData = new byte[unmixedBuffer.Length];
            Marshal.Copy(unmixedBuffer.Data, pcmData, 0, (int)unmixedBuffer.Length);

            // Calculate timestamp relative to meeting start
            long timestampMs = (long)(DateTime.UtcNow - _meetingStartTime).TotalMilliseconds;

            // Fire-and-forget: forward to gRPC ingestion server.
            // We don't await here because OnAudioMediaReceived is called at 50 fps per participant
            // and we must not block the media platform's audio pipeline.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _grpcForwarder.SendAudioChunkAsync(
                        meetingId: _meetingId,
                        participantId: participant.AadUserId.Length > 0 ? participant.AadUserId : msi.ToString(),
                        displayName: participant.DisplayName,
                        ownerAadId: _ownerAadId,
                        pcmData: pcmData,
                        timestampMs: timestampMs,
                        isRoomDevice: participant.IsRoomDevice,
                        email: participant.Email,
                        meetingTitle: _meetingTitle);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AudioHandler] Error forwarding audio for {participant.DisplayName}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AudioHandler] Error in ProcessUnmixedBuffer: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Audio socket event unsubscription happens when the call is terminated
        // and the media platform tears down the socket.
    }
}
