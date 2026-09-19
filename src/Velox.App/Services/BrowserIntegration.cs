using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Velox.App.Services;

/// <summary>
/// Registra o VeloxDM.exe como "native messaging host" para navegadores Chromium
/// (Chrome, Edge, Brave, Vivaldi, Opera, Chromium) e localiza a pasta da extensão.
/// </summary>
public static class BrowserIntegration
{
    public const string HostName = "com.velox.dm";

    /// <summary>ID fixo da extensão (derivado da chave pública em browser-extension/manifest.json).</summary>
    public const string ExtensionId = "ednjdkmgoihpnopanpflbledhfkkmagh";

    private static readonly (string Label, string Key)[] Browsers =
    {
        ("Google Chrome", @"Software\Google\Chrome\NativeMessagingHosts"),
        ("Microsoft Edge", @"Software\Microsoft\Edge\NativeMessagingHosts"),
        ("Chromium", @"Software\Chromium\NativeMessagingHosts"),
        ("Brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"),
        ("Vivaldi", @"Software\Vivaldi\NativeMessagingHosts"),
        ("Opera", @"Software\Opera Software\NativeMessagingHosts"),
    };

    public static string ExtensionDirectory => Path.Combine(AppContext.BaseDirectory, "extension");

    public static string ManifestPath(string dataDirectory) => Path.Combine(dataDirectory, HostName + ".json");

    public static string? CurrentExePath => Environment.ProcessPath;

    /// <summary>Escreve o manifesto do host e as chaves de registro (HKCU — não precisa de admin).</summary>
    public static bool Register(string dataDirectory, out string error)
    {
        error = "";
        try
        {
            var exe = CurrentExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                error = "Não foi possível determinar o caminho do executável.";
                return false;
            }

            var manifestPath = ManifestPath(dataDirectory);
            var manifest = new
            {
                name = HostName,
                description = "Velox Download Manager — ponte com o navegador",
                path = exe,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
            };

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != json)
                File.WriteAllText(manifestPath, json);

            foreach (var (_, key) in Browsers)
            {
                using var reg = Registry.CurrentUser.CreateSubKey(key + "\\" + HostName);
                if (reg?.GetValue("") as string != manifestPath)
                    reg?.SetValue("", manifestPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsRegistered(string dataDirectory)
    {
        try
        {
            var manifestPath = ManifestPath(dataDirectory);
            if (!File.Exists(manifestPath)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (!string.Equals(path, CurrentExePath, StringComparison.OrdinalIgnoreCase)) return false;

            using var reg = Registry.CurrentUser.OpenSubKey(Browsers[0].Key + "\\" + HostName);
            return string.Equals(reg?.GetValue("") as string, manifestPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Unregister(string dataDirectory)
    {
        foreach (var (_, key) in Browsers)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(key + "\\" + HostName, throwOnMissingSubKey: false); } catch { }
        }
        try { File.Delete(ManifestPath(dataDirectory)); } catch { }
    }

    public static bool ExtensionFilesPresent => File.Exists(Path.Combine(ExtensionDirectory, "manifest.json"));

    public static void OpenExtensionFolder()
    {
        try
        {
            if (Directory.Exists(ExtensionDirectory))
                Process.Start("explorer.exe", $"\"{ExtensionDirectory}\"");
        }
        catch { }
    }

    /// <summary>Abre a página de extensões do navegador (chrome://extensions ou edge://extensions).</summary>
    public static bool OpenExtensionsPage(string browserExe, string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(browserExe, url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
