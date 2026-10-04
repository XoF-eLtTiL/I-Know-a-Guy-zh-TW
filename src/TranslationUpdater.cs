using BepInEx;
using HarmonyLib;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using XUnity.AutoTranslator.Plugin.Core;

namespace IKAG.LiveTranslationPatcher;

internal static class TranslationUpdater
{
    internal const string ReloadPrefix = "RELOAD_XUNITY:";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "MainTable_Fixed_zh-TW.txt",
        "MainTable_Regex_zh-TW.txt",
    };

    internal static async Task CheckAsync(Action<string> completed)
    {
        try
        {
            var manifestUrl = Plugin.TranslationManifestUrl.Value?.Trim();
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("ManifestUrl must be an absolute HTTPS URL.");

            var manifestBytes = await Client.GetByteArrayAsync(uri).ConfigureAwait(false);
            if (manifestBytes.Length > 256 * 1024) throw new InvalidDataException("Manifest is too large.");
            var manifest = JsonSerializer.Deserialize<TranslationManifest>(manifestBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest?.Files is null || manifest.Files.Count == 0)
                throw new InvalidDataException("Manifest does not contain translation files.");

            var directory = Path.Combine(Paths.BepInExRootPath, "Translation", "zh-TW", "Text");
            Directory.CreateDirectory(directory);
            var installed = 0;
            foreach (var file in manifest.Files)
            {
                ValidateEntry(file);
                var target = Path.Combine(directory, file.Path);
                var currentHash = File.Exists(target) ? Sha256(File.ReadAllBytes(target)) : string.Empty;
                if (currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) && (file.AcceptedPreviousSha256 is null ||
                    !file.AcceptedPreviousSha256.Contains(currentHash, StringComparer.OrdinalIgnoreCase)))
                {
                    Plugin.PluginLog.LogWarning($"Skipped update for locally modified file: {file.Path}");
                    continue;
                }

                var bytes = await Client.GetByteArrayAsync(file.Url).ConfigureAwait(false);
                if (bytes.LongLength != file.Size || !Sha256(bytes).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Size or SHA-256 mismatch for {file.Path}.");
                ValidateTranslationText(bytes, file.Path);

                var temporary = target + ".download";
                var backup = target + ".before-update";
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(target)) File.Copy(target, backup, true);
                File.Move(temporary, target, true);
                installed++;
            }

            if (installed > 0)
            {
                completed($"{ReloadPrefix}Installed translation update {manifest.Version}: {installed} file(s); XUnity reloaded.");
            }
            else completed($"Translation files are current ({manifest.Version}).");
        }
        catch (Exception exception)
        {
            completed($"Translation update check failed; keeping local files: {exception.Message}");
        }
    }

    private static void ValidateEntry(TranslationFile file)
    {
        if (file is null || !AllowedFiles.Contains(file.Path) || Path.GetFileName(file.Path) != file.Path)
            throw new InvalidDataException("Manifest contains a disallowed path.");
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Invalid HTTPS URL for {file.Path}.");
        if (file.Size <= 0 || file.Size > 8 * 1024 * 1024)
            throw new InvalidDataException($"Invalid size for {file.Path}.");
        if (string.IsNullOrWhiteSpace(file.Sha256) || file.Sha256.Length != 64)
            throw new InvalidDataException($"Invalid SHA-256 for {file.Path}.");
    }

    private static void ValidateTranslationText(byte[] bytes, string name)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (text.IndexOf('\0') >= 0 || text.Contains('\uFFFD'))
            throw new InvalidDataException($"Invalid UTF-8 translation data in {name}.");
        var mappings = text.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line) &&
            !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        if (mappings < 10) throw new InvalidDataException($"Translation file is unexpectedly small: {name}.");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static void ReloadXUnity()
    {
        var translator = AutoTranslator.Default;
        var method = AccessTools.Method(translator.GetType(), "ReloadTranslations");
        if (method is null) throw new MissingMethodException("XUnity ReloadTranslations was not found.");
        method.Invoke(translator, null);
    }

    private sealed class TranslationManifest
    {
        public string Version { get; set; } = "unknown";
        public List<TranslationFile> Files { get; set; } = new();
    }

    private sealed class TranslationFile
    {
        public string Path { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public List<string> AcceptedPreviousSha256 { get; set; } = new();
    }
}
