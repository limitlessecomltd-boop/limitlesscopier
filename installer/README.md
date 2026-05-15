# Building the Limitless Trade Copier installers

This folder contains everything needed to wrap the apps into single
user-facing installer EXEs. There are TWO installers:

| Installer                               | Audience                | Output filename                                   |
|-----------------------------------------|-------------------------|---------------------------------------------------|
| **Customer installer**                  | Paying customers        | `LimitlessTradeCopier-Setup-1.0.0.exe`            |
| **Admin installer** (operator-only)     | You + support staff     | `LimitlessTradeCopierAdmin-Setup-0.1.0.exe`       |

**Never ship the admin installer to customers.** It contains the tool
that issues licenses; if it leaks together with the Ed25519 private key,
anyone could mint valid licenses for the customer app and every existing
license would have to be re-issued under a new keypair.

## TL;DR — build both at once

```powershell
.\installer\build-all-installers.ps1
```

This publishes both apps in Release/win-x64/self-contained mode, then
runs Inno Setup against both `.iss` scripts. Total time ~3-4 minutes on
a fast machine. Outputs land in `installer\Output\`.

To build just one:

```powershell
.\installer\build-all-installers.ps1 -OnlyCustomer
.\installer\build-all-installers.ps1 -OnlyAdmin
```

To rerun Inno Setup only (after editing `.iss` files):

```powershell
.\installer\build-all-installers.ps1 -SkipPublish
```

---

# Customer installer (`limitless-installer.iss`)

## What you get when you ship

A single file named like:

```
LimitlessTradeCopier-Setup-1.0.0.exe   (~150 MB)
```

When the user runs it:

1. UAC prompt (normal — installer writes to Program Files)
2. License page (the LICENSE.txt in this folder)
3. Install location prompt (defaults to `C:\Program Files\Limitless Trade Copier`)
4. Optional desktop shortcut checkbox
5. Install progress bar (a few seconds)
6. Finish page with optional "Launch now" checkbox

After install:

- App is in Program Files
- Start Menu shortcut: "Limitless Trade Copier"
- Desktop shortcut (if user ticked the box)
- Listed in Windows Settings → Apps for clean uninstall
- User data stays in `%LOCALAPPDATA%\LimitlessTradeCopier\` (encrypted creds, theme prefs, SQLite DB) and is preserved across reinstalls

## Why ~150 MB?

The installer is **self-contained** — it bundles the full .NET 8 runtime
inside. Tradeoff:

| Approach              | Installer size | User experience |
|-----------------------|---------------:|-----------------|
| Self-contained        |        ~150 MB | Just works on any clean Windows |
| Framework-dependent   |          ~3 MB | User must install .NET 8 first or app crashes |

We chose self-contained. For a paid product, friction-free install matters
more than 150 MB of extra download.

## Files in this folder

```
build-installer.ps1      # PowerShell build script (publish + Inno Setup)
limitless-installer.iss  # Inno Setup script (defines the wizard)
LICENSE.txt              # EULA shown on the license page
Output/                  # Where the finished .exe lands (gitignored)
```

## First-time setup on the build machine

Two installs needed once on whatever machine you build from:

### 1. .NET 8 SDK
Download from https://dotnet.microsoft.com/download/dotnet/8.0
Pick "SDK 8.0.x" for Windows x64. ~250 MB. Reboot not required.

Verify:
```powershell
dotnet --version
# Should print 8.0.something
```

### 2. Inno Setup 6
Download from https://jrsoftware.org/isdl.php
Pick the **unicode** build (default). ~6 MB. Free, open-source.
Default install location is `C:\Program Files (x86)\Inno Setup 6\`.

Verify:
```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe"
# Should print Inno Setup version banner and exit
```

You don't need anything else. No Visual Studio, no Wix, no Chocolatey.

## Building the installer

From a regular PowerShell window opened in the repo root:

```powershell
.\installer\build-installer.ps1
```

What that does:

1. **Publishes the app** — runs `dotnet publish` in Release / win-x64 /
   self-contained mode. Produces a folder of ~150MB at
   `LTC.App\bin\Release\net8.0-windows\win-x64\publish\` containing the
   app + the bundled .NET 8 runtime + mtapi.mt5.dll + fonts + icon.

2. **Runs Inno Setup** — invokes `iscc.exe` on `limitless-installer.iss`,
   which compresses that publish folder (LZMA2/ultra) into the final EXE.

3. **Reports the result** — prints the path and size of the produced
   installer.

Total build time: roughly **45-90 seconds** on a modern laptop.

The output you ship is at:
```
installer\Output\LimitlessTradeCopier-Setup-1.0.0.exe
```

## Quick-iterate flow

Once you've built once, if you're tweaking ONLY the installer .iss script
(e.g. changing the wizard text, adding a shortcut, etc.) you can skip the
slow publish step:

```powershell
.\installer\build-installer.ps1 -SkipPublish
```

This just re-runs Inno Setup against the previous publish output.
~5-10 seconds.

## Releasing a new version

1. Bump the version in two places:
   - `LTC.App\LTC.App.csproj`     →  `<Version>1.1.0</Version>`
   - `installer\limitless-installer.iss`  →  `#define MyAppVersion "1.1.0"`

2. Re-run `build-installer.ps1`. Output filename includes the version, so
   you'll get `LimitlessTradeCopier-Setup-1.1.0.exe`.

3. Users with the old version installed will see the new version replace
   their existing install (the AppId GUID in the .iss file is what makes
   that work — keep it the same forever).

## Distributing the installer

Just upload the single .exe wherever you distribute. Common options:

- **Direct download from limitlesscopier.com** — host on your web server,
  link from the landing page CTA. Make sure the link uses HTTPS.
- **GitHub Releases** — create a release on the project's GitHub repo,
  attach the .exe as a binary asset. Free CDN bandwidth, version history
  built-in.
- **S3 / Cloudflare R2** — for high-volume distribution. ~$0.02/GB egress.

## Code signing (optional but recommended for production)

Right now the installer is **unsigned**. Windows will show a SmartScreen
warning ("Windows protected your PC...") on first run. Users have to click
"More info" → "Run anyway" to proceed.

To get rid of that warning, sign the EXE with a code-signing certificate:

1. Buy a code signing cert from DigiCert / Sectigo / SSL.com (~$100-300/yr,
   or ~$70/yr for an OV cert; EV certs are pricier but bypass SmartScreen
   immediately rather than waiting for reputation to build up).

2. After building, sign both the app's main EXE and the installer EXE:

   ```powershell
   signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 `
       /a /n "Your Company Name" `
       "installer\Output\LimitlessTradeCopier-Setup-1.0.0.exe"
   ```

3. Or set up signing inside the .iss file via `SignTool` directives if you
   want automated signing on every build.

Skip this for the initial launch and beta. Add it before you start running
ads — SmartScreen warnings absolutely tank conversion rates from cold
traffic.

## Troubleshooting

### "dotnet publish failed"
Usually a .NET SDK version mismatch. Run `dotnet --version` and make sure
it prints 8.0.something. If it prints 7.x or 6.x, install the .NET 8 SDK.

### "Inno Setup not found"
The build script auto-detects `iscc.exe` in the standard install
locations. If you installed Inno Setup somewhere unusual, pass the path
explicitly:
```powershell
.\installer\build-installer.ps1 -InnoSetupPath "D:\Tools\InnoSetup\iscc.exe"
```

### "mtapi.mt5.dll is MISSING from the publish output"
The build script catches this case explicitly. Means the `<None Include>`
entry in `LTC.App.csproj` got removed or the file at `lib\mtapi.mt5.dll`
isn't there. Check both.

### Installer runs but app won't launch
Most common cause: anti-virus is quarantining the unsigned EXE. Whitelist
the install folder OR sign the binaries (see "Code signing" above).

Second most common: someone reinstalled .NET on the build machine and
something got corrupted. Run `dotnet --info` and confirm 8.0 SDK is healthy,
then `dotnet build` first, then re-run the installer build.

### Installer is bigger/smaller than expected
~150 MB ± 20 MB is normal. If it's drastically smaller (~5 MB), the publish
likely ran in framework-dependent mode by mistake — check the
`<SelfContained>` property in `LTC.App.csproj`.

---

# Admin installer (`limitless-admin-installer.iss`)

Same structure as the customer installer but with these critical differences:

- **Different `AppId` GUID** — installs alongside the customer app under a
  separate Program Files folder. Both apps appear independently in Windows
  Settings → Apps so you can uninstall either without touching the other.
- **Install folder**: `C:\Program Files\Limitless Trade Copier Admin\`
- **Output filename**: `LimitlessTradeCopierAdmin-Setup-0.1.0.exe`
- **License page** (`LICENSE-ADMIN.txt`) is an operator-only warning, not a
  customer EULA. It makes clear that this installer is internal use only.
- **The Ed25519 private key (`keygen-private.key`) is NOT bundled.** The
  operator copies it manually onto their machine after install. This means
  if the admin installer .exe accidentally gets distributed, no one can use
  it to mint licenses.

## Operator setup checklist (after running the admin installer)

1. Install completes — admin app is in `C:\Program Files\Limitless Trade Copier Admin\`
2. Copy your `keygen-private.key` to a location of your choice. Recommended:

       %LOCALAPPDATA%\Limitless\keygen-private.key

   Reasons NOT to put it in the install folder: a future uninstall would
   delete it, and Program Files is a more discoverable / scannable location.
3. Launch the admin app from the Start Menu.
4. In the Mint tab → Settings (future), or by editing `appsettings.json`,
   point the "Private key path" to wherever you placed the file.
5. Mint a test license bound to your own machine's fingerprint to confirm
   the keypair pairing works.

## Security reminders

- Never email the `.key` file, even to yourself.
- Never commit it to source control.
- Never install the admin app on a shared computer.
- If the key is ever exposed, generate a new keypair, update the embedded
  public key in the customer app, ship a new customer build, and re-issue
  every active customer's license. There is no graceful in-place rotation.
