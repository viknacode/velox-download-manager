using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Velox.App.Services;

/// <summary>
/// Modo "native messaging host": o Chrome/Edge inicia o VeloxDM.exe e conversa por
/// stdin/stdout (mensagens JSON prefixadas com 4 bytes de tamanho). Cada mensagem é
/// encaminhada à instância do Velox em execução pelo named pipe; se o Velox não
/// estiver aberto, ele é iniciado.
/// </summary>
public static class NativeHost
{
    public const string PipeName = "VeloxDM_Bridge";
    private const int MaxMessage = 8 * 1024 * 1024;

    public static bool IsHostInvocation(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                      a.Equals("--native-host", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        while (true)
        {
            var message = ReadMessage(stdin);
            if (message == null) return 0; // Chrome fechou o canal

            string response;
            try
            {
                response = Forward(message);
            }
            catch (Exception ex)
            {
                response = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }

            WriteMessage(stdout, response);
        }
    }

    // ------------------------------------------------------------ framing

    private static string? ReadMessage(Stream stream)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header)) return null;
        int length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxMessage) return null;

        var body = new byte[length];
        if (!ReadExactly(stream, body)) return null;
        return Encoding.UTF8.GetString(body);
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private static void WriteMessage(Stream stream, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    // ------------------------------------------------------------ encaminhamento

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// O host foi iniciado pelo navegador (processo em primeiro plano), então pode
    /// transferir ao Velox o direito de trazer sua janela para a frente do Chrome.
    /// </summary>
    private static void GrantForeground()
    {
        try
        {
            var self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("VeloxDM"))
            {
                using (p)
                {
                    if (p.Id != self) AllowSetForegroundWindow(p.Id);
                }
            }
        }
        catch { }
    }

    private static string Forward(string json)
    {
        GrantForeground();
        var reply = TrySend(json, 800);
        if (reply != null) return reply;

        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString();
        }
        catch { }

        // consultas de estado não devem abrir o programa
        if (type is "ping")
            return JsonSerializer.Serialize(new { ok = false, running = false, error = "Velox não está em execução." });

        LaunchApp();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            GrantForeground();
            reply = TrySend(json, 1000);
            if (reply != null) return reply;
            Thread.Sleep(300);
        }

        return JsonSerializer.Serialize(new { ok = false, running = false, error = "Não foi possível iniciar o Velox." });
    }

    private static string? TrySend(string json, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            writer.WriteLine(json);
            return reader.ReadLine();
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void LaunchApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--from-browser",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
        }
        catch { }
    }
}
