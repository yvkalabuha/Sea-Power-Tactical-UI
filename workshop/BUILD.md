# Building Sea Power Tactical UI from source

This archive holds the corresponding source of `SeaPowerTacticalUI.dll` (GPL-3.0 with the section 7 permission, see `LICENSE`).

Layout: `SeaPowerTacticalUI/` (mod code, `SeaPowerTacticalUI.csproj`, `lang/`) and `Common/` (shared code compiled into the same DLL).

Needs: .NET SDK 8, an installed Sea Power (only its `Managed` DLLs and `BepInEx/core` are read, nothing is copied), AnchorChain.dll (Workshop 3380210757).

```
dotnet build SeaPowerTacticalUI/SeaPowerTacticalUI.csproj -c Release \
  -p:GameDir="<path to Sea Power>" -p:AnchorChainDll="<path to AnchorChain.dll>"
```

The result is `out/SeaPowerTacticalUI.dll`. Put it with `lang/`, `_info.ini` and the texts into one mod folder.

The Workshop package also holds `loader/SeaPowerTacticalUI.Loader.dll`, a small BepInEx plugin that starts the mod from the Workshop folder. Its source is `SeaPowerTacticalUI.Loader/`; AnchorChain is not needed for it:

```
dotnet build SeaPowerTacticalUI.Loader/SeaPowerTacticalUI.Loader.csproj -c Release -p:GameDir="<path to Sea Power>"
```

The result is `out-loader/SeaPowerTacticalUI.Loader.dll`.
