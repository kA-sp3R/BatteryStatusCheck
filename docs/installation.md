# Installation and updates

The per-user installer is an additional distribution option. Portable framework-dependent and self-contained ZIPs remain available. The installer contains the same inspected self-contained payload, including its runtime, notices, source-bound package manifest and payload hashes.

## Install for the current user

Run `DeviceBatteryStatus-<version>-win-x64-setup.exe` deliberately. Setup uses `%LOCALAPPDATA%\Programs\DeviceBatteryStatus` by default, creates current-user Start menu shortcuts and registers its uninstaller in the current user's Windows Apps list. It does not require administrator privileges or install a driver, service or scheduled task.

Setup does not launch the application or enable Windows startup. Start the installed application from its shortcut when ready. Its battery monitoring remains read-only, and manual polling-rate controls still start disabled.

Close a running copy through its tray menu before installation, upgrade or uninstall. The installer checks the application's mutex and refuses to replace a running copy. It does not force-close the application. An update started through the app verifies its staged installer and waits until orderly application shutdown releases that mutex before launching setup.

The application remains unsigned. SHA-256 checksums and the GitHub release asset digest establish consistency with the selected repository release; they do not provide an Authenticode publisher signature.

## Upgrades and saved data

Preferences, recoverable settings backups and diagnostic logs live separately under `%LOCALAPPDATA%\DeviceBatteryStatus`. The installer does not rewrite or delete this directory. A portable copy and an installed copy running as the same Windows user therefore use the same saved preferences.

During an installed upgrade, setup replaces its owned application files and removes obsolete files listed by the previous installer. Unowned files in the installation directory are preserved. The installer does not reset preferences, choose new tray devices, apply polling settings or register startup automatically.

An existing startup command remains unchanged during installation or upgrade. Uninstall removes that value only when it exactly addresses this installed executable with `--startup`. A command for a portable or different installed copy is preserved. When switching from portable to installed use, disable startup in the old copy before enabling it in the installed copy.

## Update preferences

- **Notify only** is the default. Checks can announce an available stable release, but do not download or install it.
- **Check manually** performs a check only after the user requests one.
- **Off** disables the application's update-network requests.

Downloading and installing are separate deliberate actions. Download verification checks the expected repository, release version, asset name and size, GitHub-provided SHA-256 digest and sibling checksum before producing an installable receipt. The installer is checked again immediately before execution and held against concurrent modification during process startup. Failed verification does not run an installer.

Portable users may continue downloading complete portable ZIPs instead of installing. An application update does not silently overwrite arbitrary portable folders. The application does not store GitHub credentials; a private repository's unauthenticated release feed may be unavailable, in which case the release page can be opened in a browser for manual access.

## Uninstall

Exit the application, then use Windows Settings → Apps or the Start menu's uninstall shortcut. Uninstall removes the installer-owned payload, owned shortcuts, current-user uninstall registration and only its exact owned startup command. It leaves saved preferences, backups, diagnostics and unowned files intact.

If desired, remove `%LOCALAPPDATA%\DeviceBatteryStatus` manually after exiting all copies to delete saved data. Portable copies are removed by deleting their extracted folders after disabling their startup registration and exiting them.

## Build and smoke evidence

The Windows installer job takes the already inspected self-contained ZIP:

```powershell
./.github/scripts/build-installer.ps1 -PackagePath artifacts/installer-input/DeviceBatteryStatus-1.1.0-win-x64-self-contained.zip -CreateUpgradeFixture
./.github/scripts/test-installer.ps1 -InstallerPath artifacts/installer/DeviceBatteryStatus-1.1.0-win-x64-setup.exe
```

`build-installer.ps1` requires the Windows GitHub Actions environment and an existing Inno Setup 6 compiler, version 6.3 or later. It does not download or execute an unverified compiler. The optional `-CompilerVersion` argument pins an exact reviewed version; otherwise compiler version and SHA-256 are recorded in `installer-release.json`. A changed runner tool can be assessed from that provenance.

The smoke script runs only on an isolated Windows runner user profile. It silently installs a controlled prior-installer fixture, verifies every application payload hash and per-user registration, confirms that a held app mutex blocks replacement, upgrades to the current installer, checks byte-for-byte settings/backup/log preservation and obsolete-file cleanup, then uninstalls. A second cycle checks that a foreign startup command survives. The prior fixture uses the current application bytes and different installer metadata; it tests installer lifecycle rather than compatibility with an actual older application binary.

The JSON smoke report records each check, process exits, exact source/run/attempt and installer digest. Installer execution is separate from application runtime: these installer checks never launch the application or contact physical hardware. Actual results belong in [verification](verification.md); interactive desktop/UAC behavior and physical hardware remain separate verification work.
