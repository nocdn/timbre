using System.Net.WebSockets;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

internal static class RealtimeWebSocketConnector
{
    public static async Task<TSession> ConnectAsync<TSession>(
        Uri endpoint,
        Action<ClientWebSocket> configureWebSocket,
        Func<ClientWebSocket, TSession> createSession,
        Func<TSession, CancellationToken, Task> initializeSessionAsync,
        string timeoutMessage,
        string failureMessagePrefix,
        Func<string> startingLogMessage,
        Func<string> establishedLogMessage,
        Action<ClientWebSocket, Exception>? logFailure,
        CancellationToken cancellationToken)
        where TSession : IRealtimeTranscriptionSession
    {
        var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        configureWebSocket(webSocket);

        TSession? session = default;

        try
        {
            DiagnosticsLogger.Info(startingLogMessage());
            await webSocket.ConnectAsync(endpoint, cancellationToken);

            session = createSession(webSocket);
            await initializeSessionAsync(session, cancellationToken);
            DiagnosticsLogger.Info(establishedLogMessage());
            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeFailedSessionAsync(session, webSocket);
            throw new TranscriptionException(timeoutMessage, true);
        }
        catch (Exception exception)
        {
            logFailure?.Invoke(webSocket, exception);
            await DisposeFailedSessionAsync(session, webSocket);

            throw exception as TranscriptionException
                ?? new TranscriptionException($"{failureMessagePrefix}: {exception.Message}", true, null, exception);
        }
    }

    private static async Task DisposeFailedSessionAsync<TSession>(TSession? session, ClientWebSocket webSocket)
        where TSession : IRealtimeTranscriptionSession
    {
        if (session is not null)
        {
            await session.DisposeAsync();
            return;
        }

        webSocket.Dispose();
    }
}
