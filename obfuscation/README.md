# Obfuscation

This folder configures Obfuscar to scramble the customer-app binaries
before they ship. The goal is to make casual decompilation (dnSpy /
ILSpy / dotPeek) unproductive enough that pirates move on.

**This is deterrence, not encryption.** A determined attacker WILL
defeat it. The realistic target is the 95% of people who'd otherwise
post a cracked binary on a forum within a week.

## Files

| File             | What it is                                                              |
|------------------|-------------------------------------------------------------------------|
| `Obfuscar.xml`   | Obfuscar configuration — which DLLs to obfuscate, which types to skip   |
| `Output\`        | Generated; obfuscated DLLs land here before being copied back to publish |

## Build pipeline

The wrapper script at repo root (`build-obfuscated.ps1`) does it all:

```powershell
cd C:\Users\Administrator\LimitlessTradeCopier
.\build-obfuscated.ps1
```

That runs in three stages:

1. **Publish** the customer app with `dotnet publish -c Release`
2. **Obfuscate** the three of-interest DLLs in-place inside the publish
   folder (`LimitlessTradeCopier.dll`, `LTC.Core.dll`, `LTC.Persistence.dll`)
3. **Installer** — wrap the now-obfuscated publish folder with Inno Setup

Skips for iteration:

```powershell
# Already have a publish — just re-obfuscate and re-pack:
.\build-obfuscated.ps1 -SkipPublish

# Obfuscate but don't bother making an installer (for manual testing):
.\build-obfuscated.ps1 -SkipInstaller

# Sanity test - skip obfuscation completely to confirm the non-obfuscated
# build works first:
.\build-obfuscated.ps1 -SkipObfuscate
```

## What gets obfuscated

| DLL                          | Status        | Reason                                                |
|------------------------------|---------------|-------------------------------------------------------|
| `LimitlessTradeCopier.dll`   | ✅ Obfuscated | Main app — license dialog, fingerprint, gating logic |
| `LTC.Core.dll`               | ✅ Obfuscated | Licensing brain — token codec, signature verification |
| `LTC.Persistence.dll`        | ✅ Obfuscated | DPAPI calls, SQLite schema                            |
| `mtapi.mt5.dll`              | ❌ Untouched  | Third-party broker DLL; signed; mustn't change       |
| `CommunityToolkit.Mvvm.dll`  | ❌ Untouched  | NuGet binary; no value in obfuscating                |
| `Serilog.*.dll`              | ❌ Untouched  | Same                                                  |
| `Microsoft.*.dll`, `System.*.dll` | ❌ Untouched | .NET runtime; never obfuscate                   |

## What's protected (qualitatively)

After obfuscation, decompiling `LimitlessTradeCopier.dll` in dnSpy shows:

- Private/internal methods renamed to `a`, `b`, `c`, …
- String literals (including the embedded Ed25519 public key) replaced
  with calls to a decoder function — grepping the binary for `"KILODO..."`
  no longer finds the key
- `[SuppressIldasm]` attribute on every type — well-behaved decompilers
  refuse to open the file (dnSpy ignores it; ILSpy respects it)

## What's still visible

- Public class/method names referenced from XAML — `MainShell`,
  `AddAccountDialog`, all view models, all converters. These MUST stay
  visible or the app will crash at startup with a XAML parse exception.
- All `[ObservableProperty]` properties and data-bound types.
- The structure of WPF resources, themes, fonts.

## When obfuscation breaks something

If after running `build-obfuscated.ps1 -SkipInstaller` you launch the EXE
and it crashes or misbehaves:

1. Find the exception details. Most common:
   - `XamlParseException`: a type referenced from XAML got renamed.
     The message names the type — go add a `<SkipType name="LTC.App.Xxx" />`
     entry in `Obfuscar.xml`.
   - `MethodNotFoundException`: an event handler XAML references got
     renamed. Add the containing class to the skip list.
   - `MissingMemberException` on a property: data binding broke.
     Make sure the property's containing class has `skipProperties="true"`.
2. Re-run `build-obfuscated.ps1 -SkipPublish` (faster — keeps the publish)
3. Verify the fix; rinse and repeat until the app launches and works.

## Going further — additional protections that are NOT here

Obfuscar doesn't do these. If you want them later, the upgrade path is:

| Protection           | What it adds                                          | Tool that does it       |
|----------------------|-------------------------------------------------------|-------------------------|
| Control-flow obfuscation | Spaghetti-jumps so decompiled code is unreadable | Eazfuscator, ConfuserEx |
| Anti-debug           | Crash if a debugger is attached                       | Eazfuscator, ConfuserEx |
| Anti-tamper          | Crash if the DLL bytes have been edited               | Eazfuscator, ConfuserEx |
| Packing              | Wrap the assembly in a runtime-decrypted shell        | ConfuserEx              |
| Strong native protection | Compile to native code via .NET AOT             | Built into .NET 8       |

The single biggest UX win that's NOT obfuscation-related is a **code
signing certificate** ($70-300/yr). Unsigned installers trigger Windows
SmartScreen warnings on every customer install — that hurts conversion
rates way more than the absence of obfuscation hurts anti-piracy.
