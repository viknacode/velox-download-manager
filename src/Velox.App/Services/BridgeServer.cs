using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velox.App.Services;

/// <summary>Mensagem recebida da extensão do navegador.</summary>
public sealed class BrowserMessage
{
    public string? Type { get; set; }
    public string? Url { get; set; }
    public string? FinalUrl { get; set; }
    public string? Referrer { get; set; }
    public string? FileName { get; set; }
    public string? Mime { get; set; }
    public long? Size { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Source { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static BrowserMessage? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<BrowserMessage>(json, Options); }
        catch { return null; }
    }

    public static string Ok(object? extra = null) =>
        extra == null ? "{\"ok\":true}" : JsonSerializer.Serialize(extra);

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, running = true, error = message });
}

/// <summary>
/// Servidor de named pipe que recebe mensagens do host de native messaging
/// (uma linha JSON por conexão) e devolve uma linha JSON de resposta.
/// Restrito ao usuário atual.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, Task<string>> _handler;

    public BridgeServer(Func<string, Task<string>> handler)
    {
        _handler = handler;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = Create();
            }
            catch (Exception ex)
            {
                Log.Warn("Bridge: falha ao criar pipe — " + ex.Message);
                try { await Task.Delay(2000, _cts.Token); } catch { return; }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
                _ = HandleAsync(server);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("Bridge: " + ex.Message);
                server.Dispose();
                try { await Task.Delay(300, _cts.Token); } catch { return; }
            }
        }
    }

    private static NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user != null)
            security.AddAccessRule(new PipeAccessRule(user,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            NativeHost.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                var line = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(line)) return;

                string response;
                try
                {
                    response = await _handler(line);
                }
                catch (Exception ex)
                {
                    response = BrowserMessage.Error(ex.Message);
                }

                await writer.WriteLineAsync(response.AsMemory(), timeout.Token);
                try { pipe.WaitForPipeDrain(); } catch { }
            }
            catch (Exception ex)
            {
                Log.Warn("Bridge: erro ao atender conexão — " + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // desbloqueia o WaitForConnection pendente
        try
        {
            using var client = new NamedPipeClientStream(".", NativeHost.PipeName, PipeDirection.Out);
            client.Connect(200);
        }
        catch { }
        _cts.Dispose();
    }
}
