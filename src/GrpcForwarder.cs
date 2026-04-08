using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using AudioIngestion;
using MeetingsBot.Models;

namespace MeetingsBot;

/// <summary>
/// Manages gRPC streaming connections to the audio ingestion server.
/// Opens one StreamAudio call per participant and forwards PCM audio chunks.
/// Calls EndMeeting when the meeting terminates.
/// </summary>
public class GrpcForwarder : IAsyncDisposable
{
    private readonly IngestionOptions _options;
    private readonly GrpcChannel _channel;
    private readonly AudioIngestion.AudioIngestion.AudioIngestionClient _client;

    /// <summary>
    /// Active streams keyed by "{meetingId}:{participantId}".
    /// Each entry holds an open gRPC streaming call.
    /// </summary>
    private readonly ConcurrentDictionary<string, ParticipantStream> _streams = new();

    public GrpcForwarder(IngestionOptions options)
    {
        _options = options;
        _channel = GrpcChannel.ForAddress(options.GrpcEndpoint);
        _client = new AudioIngestion.AudioIngestion.AudioIngestionClient(_channel);
    }

    /// <summary>
    /// Send a single audio chunk for a participant. Opens the gRPC stream lazily
    /// on the first chunk for each participant.
    /// </summary>
    public async Task SendAudioChunkAsync(
        string meetingId,
        string participantId,
        string displayName,
        string ownerAadId,
        byte[] pcmData,
        long timestampMs,
        bool isRoomDevice,
        string email,
        string meetingTitle)
    {
        var key = $"{meetingId}:{participantId}";

        var stream = _streams.GetOrAdd(key, _ =>
        {
            var call = _client.StreamAudio();
            return new ParticipantStream
            {
                Call = call,
                IsFirstChunk = true
            };
        });

        var chunk = new AudioChunk
        {
            MeetingId = meetingId,
            ParticipantId = participantId,
            DisplayName = displayName,
            OwnerAadId = ownerAadId,
            PcmData = ByteString.CopyFrom(pcmData),
            TimestampMs = timestampMs,
            IsRoomDevice = isRoomDevice,
            Email = email
        };

        // Send meeting title only on the first chunk
        if (stream.IsFirstChunk)
        {
            chunk.MeetingTitle = meetingTitle;
            stream.IsFirstChunk = false;
        }

        try
        {
            await stream.Call.RequestStream.WriteAsync(chunk);
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"[GrpcForwarder] Error sending audio for {displayName} ({participantId}): {ex.Status}");
            // Remove the broken stream so a new one is opened on next chunk
            if (_streams.TryRemove(key, out var removed))
            {
                removed.Call.Dispose();
            }
        }
    }

    /// <summary>
    /// Close all open participant streams for a meeting and call EndMeeting RPC.
    /// </summary>
    public async Task EndMeetingAsync(
        string meetingId,
        string ownerAadId,
        List<(string sender, string text, string timestamp)>? chatMessages = null)
    {
        // Close all participant streams belonging to this meeting
        var keysToRemove = _streams.Keys.Where(k => k.StartsWith($"{meetingId}:")).ToList();
        foreach (var key in keysToRemove)
        {
            if (_streams.TryRemove(key, out var stream))
            {
                try
                {
                    await stream.Call.RequestStream.CompleteAsync();
                    var result = await stream.Call;
                    Console.WriteLine($"[GrpcForwarder] Stream closed for {key}: received {result.ChunksReceived} chunks");
                }
                catch (RpcException ex)
                {
                    Console.Error.WriteLine($"[GrpcForwarder] Error closing stream {key}: {ex.Status}");
                }
                finally
                {
                    stream.Call.Dispose();
                }
            }
        }

        // Build EndMeeting request with chat messages
        var request = new EndMeetingRequest
        {
            MeetingId = meetingId,
            OwnerAadId = ownerAadId
        };

        if (chatMessages != null)
        {
            foreach (var (sender, text, timestamp) in chatMessages)
            {
                request.ChatMessages.Add(new ChatMessage
                {
                    SenderName = sender,
                    Text = text,
                    Timestamp = timestamp
                });
            }
        }

        try
        {
            var response = await _client.EndMeetingAsync(request);
            Console.WriteLine($"[GrpcForwarder] EndMeeting response: ok={response.Ok}, message={response.Message}");
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"[GrpcForwarder] EndMeeting RPC failed: {ex.Status}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _streams)
        {
            try
            {
                await kvp.Value.Call.RequestStream.CompleteAsync();
            }
            catch { /* best effort */ }
            kvp.Value.Call.Dispose();
        }
        _streams.Clear();
        _channel.Dispose();
    }

    private class ParticipantStream
    {
        public AsyncClientStreamingCall<AudioChunk, StreamResult> Call { get; set; } = null!;
        public bool IsFirstChunk { get; set; }
    }
}
