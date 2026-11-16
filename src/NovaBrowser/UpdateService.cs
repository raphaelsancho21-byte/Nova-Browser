using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace NovaBrowser;

internal sealed record BrowserUpdate(Version Version, string DownloadUrl, string ChecksumUrl, string Tag);

internal static class UpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/raphaelsancho21-byte/Nova-Browser/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NovaBrowser-Updater/0.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static async Task<BrowserUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ReleasesApi, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var releaseVersion)) return null;
        var installed = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        if (releaseVersion <= installed) return null;

        string? exe = null, sha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == "NovaBrowser.exe") exe = url;
            if (name == "NovaBrowser.exe.sha256") sha = url;
        }
        if (exe is null || sha is null ||
            !IsTrustedReleaseAsset(exe, tag) || !IsTrustedReleaseAsset(sha, tag))
            return null;
        return new BrowserUpdate(releaseVersion, exe, sha, tag);
    }

    private static bool IsTrustedReleaseAsset(string url, string tag)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.AbsolutePath.StartsWith(
            "/raphaelsancho21-byte/Nova-Browser/releases/download/" +
            Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal);
    }

    internal static async Task DownloadAndInstallAsync(BrowserUpdate update, Action<string> report)
    {
        string currentExe = Environment.ProcessPath ??
            throw new InvalidOperationException("Não foi possível localizar o executável instalado.");
        if (!File.Exists(currentExe)) throw new FileNotFoundException("Executável não encontrado.", currentExe);
        if (Path.GetFileName(currentExe) != "NovaBrowser.exe")
            throw new InvalidOperationException("Atualização disponível somente para NovaBrowser.exe.");

        // Do not silently replace binaries inside a protected directory.
        var directory = Path.GetDirectoryName(currentExe)!;
        var writeTest = Path.Combine(directory, ".nova-write-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(writeTest, "test"); File.Delete(writeTest); }
        catch (Exception ex) { throw new IOException(
            "A pasta atual é protegida. Instale o Nova Browser em uma pasta com permissão de escrita.", ex); }

        string staging = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaBrowser", "Updates", update.Version.ToString());
        Directory.CreateDirectory(staging);
        string stagedExe = Path.Combine(staging, "NovaBrowser.exe");
        string downloadedExe = stagedExe + ".download";
        string checksumPath = Path.Combine(staging, "NovaBrowser.exe.sha256");

        report("Baixando atualização " + update.Tag + "…");
        using (var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(downloadedExe);
            await source.CopyToAsync(target);
        }

        report("Verificando integridade do arquivo…");
        var checksum = await Client.GetStringAsync(update.ChecksumUrl);
        var expected = checksum.Trim().Split(' ', '\t', '\r', '\n',
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("O Release não possui um SHA-256 válido.");
        var bytes = await File.ReadAllBytesAsync(downloadedExe);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A integridade do download falhou; atualização cancelada.");
        File.Move(downloadedExe, stagedExe, true);
        await File.WriteAllTextAsync(checksumPath, expected);
        report("Atualização verificada. Reiniciando…");

        // A separate Windows process waits for the browser to exit before replacing the .exe.
        // The update is triggered only after explicit user confirmation.
        string helper = Path.Combine(staging, "apply-update.ps1");
        const string script = """
param(
 [Parameter(Mandatory=$true)][int]$ParentPid,
 [Parameter(Mandatory=$true)][string]$NewExe,
 [Parameter(Mandatory=$true)][string]$TargetExe,
 [Parameter(Mandatory=$true)][string]$ExpectedSha
)
$ErrorActionPreference = 'Stop'
try {
 Wait-Process -Id $ParentPid -ErrorAction SilentlyContinue -Timeout 60
 $hash = (Get-FileHash -LiteralPath $NewExe -Algorithm SHA256).Hash
 if ($hash -ne $ExpectedSha) { throw 'SHA256 mismatch' }
 $backup = "$TargetExe.bak"
 if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
 Move-Item -LiteralPath $TargetExe -Destination $backup -Force
 try { Copy-Item -LiteralPath $NewExe -Destination $TargetExe -Force }
 catch {
  Move-Item -LiteralPath $backup -Destination $TargetExe -Force
  throw
 }
 Start-Process -FilePath $TargetExe
} catch {
 Add-Content -LiteralPath "$env:LOCALAPPDATA\NovaBrowser\update-error.log" -Value $_
}
""";
        await File.WriteAllTextAsync(helper, script, new UTF8Encoding(false));

        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = staging
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(helper);
        info.ArgumentList.Add("-ParentPid");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add("-NewExe");
        info.ArgumentList.Add(stagedExe);
        info.ArgumentList.Add("-TargetExe");
        info.ArgumentList.Add(currentExe);
        info.ArgumentList.Add("-ExpectedSha");
        info.ArgumentList.Add(expected);
        if (Process.Start(info) is null)
            throw new InvalidOperationException("Não foi possível iniciar o atualizador.");
        Application.Current.Shutdown();
    }
}
