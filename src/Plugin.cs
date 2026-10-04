using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Collections.Concurrent;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using XUnity.AutoTranslator.Plugin.Core;

namespace IKAG.LiveTranslationPatcher;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("gravydevsupreme.xunity.autotranslator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "tw.iknowaguy.livetranslationpatcher";
    public const string PluginName = "I Know a Guy XUnity Assistant";
    public const string PluginVersion = "1.3.0";

    internal static ManualLogSource PluginLog { get; private set; }
    internal static ConfigEntry<bool> CaptureMissingText { get; private set; }
    internal static ConfigEntry<string> LogoTextureName { get; private set; }
    internal static ConfigEntry<bool> EnableTranslationUpdater { get; private set; }
    internal static ConfigEntry<string> TranslationManifestUrl { get; private set; }
    internal static ConfigEntry<float> UpdateCheckIntervalHours { get; private set; }

    public override void Load()
    {
        PluginLog = Log;
        CaptureMissingText = Config.Bind("General", "CaptureMissingText", false,
            "Legacy option retained for compatibility. The XUnity assistant does not scan or capture text.");
        LogoTextureName = Config.Bind("Logo", "TargetTextureName", "LIBRARY LOGO",
            "Exact Texture2D name that receives the embedded Traditional Chinese logo.");
        EnableTranslationUpdater = Config.Bind("Updater", "Enabled", true,
            "Check the translation manifest and install verified translation updates.");
        TranslationManifestUrl = Config.Bind("Updater", "ManifestUrl",
            "https://raw.githubusercontent.com/XoF-eLtTiL/I-Know-a-Guy-zh-TW/main/manifest.json",
            "HTTPS URL of the translation manifest.");
        UpdateCheckIntervalHours = Config.Bind("Updater", "CheckIntervalHours", 24.0f,
            "Hours between update checks. Minimum is one hour.");

        ClassInjector.RegisterTypeInIl2Cpp<XUnityAssistantBehaviour>();
        AddComponent<XUnityAssistantBehaviour>();
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo($"{PluginName} {PluginVersion} loaded; text replacement is delegated to XUnity.");
    }
}

public sealed class XUnityAssistantBehaviour : MonoBehaviour
{
    internal static XUnityAssistantBehaviour Instance { get; private set; }
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly HashSet<int> _patchedTextureIds = new();
    private byte[] _logoBytes;
    private int _lastSceneHandle;
    private int _logoPasses;
    private float _nextLogoPass;
    private float _nextUpdateCheck;
    private bool _updateRunning;
    private bool _loggedFirstTranslation;

    public XUnityAssistantBehaviour(IntPtr pointer) : base(pointer) { }

    private void Start()
    {
        Instance = this;
        _logoBytes = LoadEmbeddedLogo();
        _lastSceneHandle = SceneManager.GetActiveScene().handle;
        RequestLogoPasses(4);
        _nextUpdateCheck = Time.unscaledTime + 2.0f;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception exception) { Plugin.PluginLog.LogWarning($"Queued action failed: {exception.Message}"); }
        }

        var now = Time.unscaledTime;
        var scene = SceneManager.GetActiveScene().handle;
        if (scene != _lastSceneHandle)
        {
            _lastSceneHandle = scene;
            _patchedTextureIds.Clear();
            RequestLogoPasses(4);
        }

        if (_logoPasses > 0 && now >= _nextLogoPass)
        {
            PatchLogoTextures();
            _logoPasses--;
            _nextLogoPass = now + 0.5f;
        }

        if (Plugin.EnableTranslationUpdater.Value && !_updateRunning && now >= _nextUpdateCheck)
        {
            _nextUpdateCheck = now + Math.Max(1.0f, Plugin.UpdateCheckIntervalHours.Value) * 3600.0f;
            _updateRunning = true;
            _ = TranslationUpdater.CheckAsync(message => _mainThreadActions.Enqueue(() =>
            {
                _updateRunning = false;
                if (message.StartsWith(TranslationUpdater.ReloadPrefix, StringComparison.Ordinal))
                {
                    TranslationUpdater.ReloadXUnity();
                    message = message[TranslationUpdater.ReloadPrefix.Length..];
                }
                if (!string.IsNullOrEmpty(message)) Plugin.PluginLog.LogInfo(message);
            }));
        }
    }

    private void RequestLogoPasses(int passes)
    {
        _logoPasses = Math.Max(_logoPasses, passes);
        _nextLogoPass = Time.unscaledTime + 0.1f;
    }

    internal bool TryTranslateWithXUnity(string source, string framework, out string translated)
    {
        translated = source;
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            if (!AutoTranslator.Default.TryTranslate(source, out translated) ||
                string.IsNullOrEmpty(translated) || string.Equals(source, translated, StringComparison.Ordinal))
                return false;
            if (!_loggedFirstTranslation)
            {
                _loggedFirstTranslation = true;
                Plugin.PluginLog.LogInfo($"XUnity assistant active via {framework} (first source: '{OneLine(source)}').");
            }
            return true;
        }
        catch (Exception exception)
        {
            Plugin.PluginLog.LogWarning($"XUnity translation bridge failed: {exception.Message}");
            return false;
        }
    }

    internal void Enqueue(Action action) => _mainThreadActions.Enqueue(action);

    private static byte[] LoadEmbeddedLogo()
    {
        const string resourceName = "IKAG.LiveTranslationPatcher.IKAG_zhTW_Logo.png";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return Array.Empty<byte>();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string OneLine(string value)
    {
        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return value.Length <= 80 ? value : value[..80] + "...";
    }

    private void PatchLogoTextures()
    {
        if (_logoBytes is null || _logoBytes.Length == 0) return;
        try
        {
            foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (texture is null || !string.Equals(texture.name, Plugin.LogoTextureName.Value, StringComparison.Ordinal)) continue;
                var id = texture.GetInstanceID();
                if (_patchedTextureIds.Contains(id)) continue;
                if (ImageConversion.LoadImage(texture, new Il2CppStructArray<byte>(_logoBytes), false))
                {
                    _patchedTextureIds.Add(id);
                    Plugin.PluginLog.LogInfo($"Replaced Texture2D '{texture.name}' ({texture.width}x{texture.height}, id {id}).");
                }
            }
        }
        catch (Exception exception) { Plugin.PluginLog.LogWarning($"Logo replacement failed: {exception.Message}"); }
    }
}

internal static class TextPatchHelper
{
    [ThreadStatic] private static bool _applying;

    internal static void Apply(TMP_Text instance, string framework)
    {
        if (_applying || instance is null) return;
        var watcher = XUnityAssistantBehaviour.Instance;
        if (watcher is null || !watcher.TryTranslateWithXUnity(instance.text, framework, out var translated)) return;
        try { _applying = true; instance.text = translated; }
        finally { _applying = false; }
    }

    internal static void Apply(UnityEngine.UI.Text instance)
    {
        if (_applying || instance is null) return;
        var watcher = XUnityAssistantBehaviour.Instance;
        if (watcher is null || !watcher.TryTranslateWithXUnity(instance.text, "Unity UI event", out var translated)) return;
        try { _applying = true; instance.text = translated; }
        finally { _applying = false; }
    }

    internal static void Apply(TextElement instance)
    {
        if (_applying || instance is null) return;
        var watcher = XUnityAssistantBehaviour.Instance;
        if (watcher is null || !watcher.TryTranslateWithXUnity(instance.text, "UI Toolkit event", out var translated)) return;
        try { _applying = true; instance.text = translated; }
        finally { _applying = false; }
    }
}

[HarmonyPatch(typeof(TMP_Text), "set_text")]
internal static class TmpTextSetterPatch { private static void Postfix(TMP_Text __instance) => TextPatchHelper.Apply(__instance, "TMP text event"); }

[HarmonyPatch]
internal static class TmpTextMutationPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(TMP_Text))
        .Where(method => (method.Name == "SetText" || method.Name == "SetCharArray") && method.GetParameters().Length > 0);
    private static void Postfix(TMP_Text __instance) => TextPatchHelper.Apply(__instance, "TMP mutation event");
}

[HarmonyPatch]
internal static class TmpGeometryDirtyPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var ugui = AccessTools.DeclaredMethod(typeof(TextMeshProUGUI), "SetVerticesDirty");
        if (ugui is not null) yield return ugui;
        var world = AccessTools.DeclaredMethod(typeof(TextMeshPro), "SetVerticesDirty");
        if (world is not null) yield return world;
    }
    private static void Postfix(TMP_Text __instance) => TextPatchHelper.Apply(__instance, "TMP geometry event");
}

[HarmonyPatch(typeof(UnityEngine.UI.Text), "set_text")]
internal static class UnityUiTextSetterPatch { private static void Postfix(UnityEngine.UI.Text __instance) => TextPatchHelper.Apply(__instance); }

[HarmonyPatch(typeof(TextElement), "set_text")]
internal static class UiToolkitTextSetterPatch { private static void Postfix(TextElement __instance) => TextPatchHelper.Apply(__instance); }
