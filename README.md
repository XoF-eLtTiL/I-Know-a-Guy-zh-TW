# I Know a Guy XUnity Assistant

`IKAG.LiveTranslationPatcher` is a BepInEx IL2CPP helper for the Traditional Chinese localization of **I Know a Guy: Shady Life Simulator**.

## What changed in 1.3.0

- Text replacement is delegated to `XUnity.AutoTranslator.Plugin.Core.AutoTranslator.Default`.
- No periodic TMP/UI Toolkit object-tree scan and no private Fixed/Regex matching engine.
- Harmony hooks only forward text mutation events that XUnity 5.6.2 misses on this Unity/TMP build.
- Translation updates use an HTTPS manifest, SHA-256 and exact-size validation, atomic replacement and automatic XUnity reload.
- Locally modified managed translation files are not overwritten unless their hash is explicitly accepted by the manifest.
- The embedded Traditional Chinese `LIBRARY LOGO` replacement remains enabled.

## Build

The project targets .NET 6 and references the installed BepInEx, Unity interop and XUnity assemblies. Update the local `HintPath` values in the project file when building elsewhere.

```powershell
dotnet build .\IKAG.LiveTranslationPatcher.csproj -c Release
```

Install the built DLL under:

`BepInEx/plugins/IKAG.LiveTranslationPatcher/`

Translations are loaded from:

`BepInEx/Translation/zh-TW/Text/`

## Updater

The default manifest is:

`https://raw.githubusercontent.com/XoF-eLtTiL/I-Know-a-Guy-zh-TW/main/manifest.json`

Only `MainTable_Fixed_zh-TW.txt` and `MainTable_Regex_zh-TW.txt` are accepted. A failed download or validation keeps the current local files.
