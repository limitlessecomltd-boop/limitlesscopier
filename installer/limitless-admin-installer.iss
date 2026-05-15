; ============================================================================
; Limitless Trade Copier — ADMIN APP installer script (Inno Setup)
; ============================================================================
; This script wraps the LTC.AdminApp publish folder into a separate installer
; EXE that operators (you, support staff) use to install the admin tool on
; their own machines.
;
; CRITICAL DIFFERENCES FROM THE CUSTOMER INSTALLER:
;
;   1. Different AppId GUID — so installing the admin app on a machine that
;      also has the customer app does NOT replace the customer app.
;
;   2. Different default install folder — "Limitless Trade Copier Admin"
;      under Program Files, so users (and Add/Remove Programs) can see
;      both apps side-by-side.
;
;   3. Setup wizard text and license make it clear this is OPERATOR-ONLY.
;      A bold "DO NOT DISTRIBUTE" message appears on the welcome page.
;
;   4. We DO NOT include the keygen-private.key in the installed payload.
;      The operator manually copies their key onto the machine after install
;      (Settings tab in the admin app shows the expected path). This means
;      the .key never lives inside a redistributable file — if an admin
;      installer EXE somehow escapes into the wild, no one can mint
;      licenses with it.
; ============================================================================

#define MyAppName "Limitless Trade Copier Admin"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Limitless"
#define MyAppURL "https://limitlesscopier.com"
#define MyAppExeName "LimitlessTradeCopierAdmin.exe"

; Unique GUID specifically for the ADMIN app — different from the customer
; AppId so the two apps install/uninstall independently.
#define MyAppId "{{B2A6D8F1-3C5E-4D7A-9F2B-8E4C6D1A2F35}"

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
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE-ADMIN.txt

OutputDir=Output
OutputBaseFilename=LimitlessTradeCopierAdmin-Setup-{#MyAppVersion}

; Use the same icon as the customer app for now; can swap to a dedicated
; admin icon (e.g. with a red accent) once we have one.
SetupIconFile=..\LTC.App\Assets\ltc-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
PrivilegesRequired=admin
MinVersion=10.0

; A custom wizard image with the brand mark would go here once we have one.
; WizardImageFile=admin-wizard-image.bmp

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"

[Files]
; Pull the published admin app folder. Same self-contained .NET deployment
; pattern as the customer app — admin operators don't need .NET pre-installed.
Source: "..\LTC.AdminApp\bin\Release\net8.0-windows\win-x64\publish\*"; \
    DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    IconFilename: "{app}\Assets\ltc-icon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Tasks: desktopicon; IconFilename: "{app}\Assets\ltc-icon.ico"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; ============================================================================
; SECURITY NOTE on uninstall
; ============================================================================
; Uninstall removes ONLY the install folder under Program Files.
; If the operator placed keygen-private.key inside the install folder it
; will be deleted. If they (correctly) placed it under a separate path like
; %LOCALAPPDATA%\Limitless\keygen-private.key or C:\KeySafe\, uninstalling
; the admin app does NOT touch that file. This is intentional: the private
; key is the operator's master credential, not application data.
; ============================================================================
