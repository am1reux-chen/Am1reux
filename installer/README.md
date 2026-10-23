# Lexio Windows installer

The installer wraps the existing unpackaged, self-contained x64 publish output. It does not install .NET or Windows App SDK globally. The stable Inno Setup `AppId` allows later installers to update the same installation. The per-user install directory is separate from `%LOCALAPPDATA%\Lexio`; the installer contains no uninstall deletion rule for user data.

## Build

Publish to a new output directory so existing validated artifacts remain untouched:

```powershell
dotnet publish .\src\Lexico.App\Lexico.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishProfile=win-x64-self-contained `
  -p:PublishDir="artifacts\lexio-release-candidate\publish\"
```

Then compile with Inno Setup 6.7 or later, passing the absolute publish path:

```powershell
$versionDefine = '/DAppVersion=1.0.0'
ISCC.exe $versionDefine .\installer\Lexio.iss
```

For a separate validation or release candidate, the source directory can be overridden without replacing the default installer inputs:

```powershell
ISCC.exe '/DPublishSourceDir=C:\path\to\self-contained-publish' `
  /O"C:\path\to\separate-installer-output" `
  /FLexio-Setup-x64 .\installer\Lexio.iss
```

The installer is written to `artifacts\lexio-release-candidate\installer`. Use the actual application version for release builds. The version shown here is only an example.

## Install behavior

- Per-user install under `%LOCALAPPDATA%\Programs\Lexio`; no administrator permission is requested.
- Windows x64, Windows 10 build 19041 or later.
- Start Menu shortcut; optional desktop shortcut.
- Reusing the same `AppId` and destination upgrades an existing installation.
- Uninstall removes installed application files only. It preserves `%LOCALAPPDATA%\Lexio`, and the installer and Add/Remove Programs display name state that location.
- User data is never stored under the install directory.
