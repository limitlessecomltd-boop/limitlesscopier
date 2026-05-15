; ============================================================================
; Limitless Trade Copier — Windows installer script (Inno Setup)
; ============================================================================
; This script tells Inno Setup how to wrap the published application folder
; into a single user-facing installer EXE.
;
; What the resulting installer does:
;   1. Welcome / license / install-path wizard pages
;   2. Copies all files from `..\LTC.App\bin\Release\net8.0-windows\win-x64\publish\`
;      into "C:\Program Files\Limitless Trade Copier\" (default; user can change)
;   3. Creates a Start Menu shortcut and (optionally) a desktop shortcut
;   4. Registers in Windows Add/Remove Programs so users can uninstall cleanly
;   5. Offers a "Launch Limitless Trade Copier now" checkbox at the end
;
; Build dependency:
;   Inno Setup 6+ (free, open-source). Get it from https://jrsoftware.org/isdl.php
;   The script is run as: iscc.exe limitless-installer.iss
;   The build-installer.ps1 wrapper handles publish + iscc invocation in one go.
;
; The published .NET app is SELF-CONTAINED — it includes the full .NET 8
; runtime in its own folder. This means the installer is ~150MB but the user
; never sees a "please install .NET" prompt. Tradeoff worth it for ease of
; install on cold-start machines.
; ============================================================================

#define MyAppName "Limitless Trade Copier"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Limitless"
#define MyAppURL "https://limitlesscopier.com"
#define MyAppExeName "LimitlessTradeCopier.exe"
#define MyAppId "{{C7B1F4D2-9E4A-4B6E-A8F1-3D2F1E5C7A9B}"
; ^ unique GUID identifying this product in the registry. Used for upgrade
;   detection — bumping the version with the same AppId means future installers
;   will automatically replace the old install rather than installing alongside.

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\{#MyAppName}
; {autopf} = Program Files (x64) on 64-bit Windows; auto-resolves correctly.

DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE.txt
; ^ shown on the license-acceptance page. We ship a permissive LICENSE.txt
;   alongside this script (see installer/LICENSE.txt).

OutputDir=Output
OutputBaseFilename=LimitlessTradeCopier-Setup-{#MyAppVersion}
; The final installer EXE name: e.g. LimitlessTradeCopier-Setup-1.0.0.exe

SetupIconFile=..\LTC.App\Assets\ltc-icon.ico
; The icon Windows shows for the installer EXE itself in File Explorer.

UninstallDisplayIcon={app}\{#MyAppExeName}
; ^ icon shown in Add/Remove Programs.

Compression=lzma2/ultra64
SolidCompression=yes
; LZMA2 ultra produces a ~25% smaller installer than default. Slow to build
; (~60s on a fast machine for our 150MB payload) but worth it once.

WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
; ^ blocks installation on 32-bit Windows. Our publish target is win-x64
;   (since mtapi.mt5.dll is x86-or-x64 only and most retail Windows is x64)
;   so trying to install on 32-bit Windows would fail anyway. Better to fail
;   early with a friendly message than to install and crash on launch.

PrivilegesRequired=admin
; Installing into Program Files needs admin elevation. The user gets the
; standard UAC prompt on first run of the installer.

MinVersion=10.0
; Windows 10 or newer. .NET 8 requires Win 10 1809+.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Optional desktop icon checkbox on the Setup Tasks page.
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; ====================================================================
; Pull EVERY file from the published app folder into the install dir.
;
; Wildcard `*` with `recursesubdirs` and `createallsubdirs` ensures the
; .NET runtime DLLs in subfolders (runtimes\win-x64\native\, etc.) are
; preserved. Without recursesubdirs the publish layout would flatten and
; the runtime wouldn't load.
;
; The publish folder contains roughly:
;   LimitlessTradeCopier.exe         our app
;   LimitlessTradeCopier.dll         our app (managed entrypoint)
;   *.dll                            ~200 .NET 8 runtime DLLs (self-contained)
;   mtapi.mt5.dll                    broker connector
;   mt5api.dll                       broker connector (renamed copy)
;   Fonts\*.ttf                      DM Sans / DM Mono / Bebas Neue
;   Assets\ltc-icon.ico              app icon
;   Themes\*.xaml                    palette files (compiled into BAML
;                                    inside our DLL but XAML source is also
;                                    written for tooling)
; ====================================================================
Source: "..\LTC.App\bin\Release\net8.0-windows\win-x64\publish\*"; \
    DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; Start Menu shortcut (always created)
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    IconFilename: "{app}\Assets\ltc-icon.ico"
; Desktop shortcut (only if user ticked the checkbox in Tasks)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Tasks: desktopicon; IconFilename: "{app}\Assets\ltc-icon.ico"

[Run]
; Optional "Launch app now" checkbox on the Finish page.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Wipe the entire install dir on uninstall — including any leftover log files
; the app wrote to its install dir (the app actually logs to %LOCALAPPDATA%
; not Program Files, so this is mostly defensive).
Type: filesandordirs; Name: "{app}"

; NOTE: We deliberately do NOT delete %LOCALAPPDATA%\LimitlessTradeCopier on
; uninstall. That folder contains the user's encrypted account credentials
; (DPAPI) and saved theme preference. If the user reinstalls, they'd hate to
; lose those. Standard Windows app etiquette: leave user data alone.
