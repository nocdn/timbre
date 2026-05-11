using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using timbre.Models;

namespace timbre.Services;

internal sealed record WebSocketTextMessage(
    string? Text,
    WebSocketMessageType MessageType,
    WebSocketCloseStatus? CloseStatus,
    string? CloseStatusDescription)
{
    public bool IsClose => MessageType == WebSocketMessageType.Close;

    public bool HasText => MessageType == WebSocketMessageType.Text && !string.IsNullOrEmpty(Text);
}

internal static class WebSocketUtilities
{
    public static async Task SendJsonAsync<TMessage>(
        ClientWebSocket webSocket,
        SemaphoreSlim sendLock,
        TMessage message,
        JsonSerializerOptions serializerOptions,
        string closedConnectionMessage,
        string sendFailureMessage,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, serializerOptions);
        await SendTextPayloadAsync(
            webSocket,
            sendLock,
            payload,
            closedConnectionMessage,
            sendFailureMessage,
            cancellationToken);
    }

    public static async Task SendTextPayloadAsync(
        ClientWebSocket webSocket,
        SemaphoreSlim sendLock,
        byte[] payload,
        string closedConnectionMessage,
        string sendFailureMessage,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);

        try
        {
            if (webSocket.State != WebSocketState.Open)
            {
                throw new TranscriptionException(closedConnectionMessage, true);
            }

            await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException exception)
        {
            throw new TranscriptionException(sendFailureMessage, true, null, exception);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public static async Task<WebSocketTextMessage> ReceiveTextMessageAsync(
        ClientWebSocket webSocket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var messageStream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new WebSocketTextMessage(
                    null,
                    result.MessageType,
                    result.CloseStatus,
                    result.CloseStatusDescription);
            }

            messageStream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var text = result.MessageType == WebSocketMessageType.Text && messageStream.Length > 0
            ? Encoding.UTF8.GetString(messageStream.ToArray())
            : null;

        return new WebSocketTextMessage(
            text,
            result.MessageType,
            result.CloseStatus,
            result.CloseStatusDescription);
    }
}
