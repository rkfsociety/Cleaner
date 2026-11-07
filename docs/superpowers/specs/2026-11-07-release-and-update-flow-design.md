# Cleaner release and update flow

## Goal

Publish a Windows single-file release automatically when `Cleaner.csproj` has a newer version, retain only the newest versioned release/tag, and let users check for and install updates from Cleaner.

## Decisions

- `Cleaner.csproj` is the single version source. The initial version is `0.0.1`; release tags use the canonical form `v0.0.1` without unnecessary zero padding.
- Versions have three non-negative numeric components. Compare each component numerically, so `0.0.10` is newer than `0.0.9`.
- The first successful push to `main` creates the initial release. Later pushes create a release only when the project version is higher than every existing `vMAJOR.MINOR.PATCH` tag.
- The release contains `Cleaner.exe` and its SHA-256 file. Only after both assets upload successfully does the workflow delete older releases and matching version tags. Tags outside this version format are never deleted.
- Pull requests build and test with read-only repository contents permission; only the release job on a push to `main` receives `contents: write`.
- Update checking is manual in Settings. The app downloads the latest public GitHub release, checks its SHA-256, asks before installing, and installs to `%LOCALAPPDATA%\Programs\Cleaner`. A Start Menu shortcut launches that stable copy. Portable users can install their current copy from Settings even when no newer release exists.
- The app retains its existing settings, history, and logs under `%LOCALAPPDATA%\Cleaner`.

## User flow

1. The user chooses **Проверить обновления** in Settings.
2. Cleaner compares the latest release tag with its assembly version. If newer, it asks before downloading and installing.
3. Cleaner downloads the executable and checksum, verifies the hash, and starts a hidden installer helper. The helper waits for Cleaner to exit, replaces the installed executable, creates the Start Menu shortcut, and launches the installed copy.
4. If there is no newer release, a portable user can choose **Установить в профиль пользователя** to copy the current executable into the same install location and create the shortcut.

## Failure handling

- Invalid or duplicate version tags, version downgrades, missing assets, checksum mismatch, download errors, or installation errors must not silently install an unverified executable.
- If replacement fails, the helper restores the prior installed executable when available and records an error under `%LOCALAPPDATA%\Cleaner`.
- Release cleanup runs only after the new release and both assets are present. Cleanup is limited to prior numeric version tags and their releases.

## Scope

Changes cover the project version, GitHub Actions release workflow, updater/install services, Settings UI, installer helper, and README. No installer package, background polling, auto-update, or external production dependency is introduced.
