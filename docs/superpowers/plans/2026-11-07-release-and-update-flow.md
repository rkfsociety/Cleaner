# Release and update flow implementation plan

## 1. Establish versioned releases

- Add the initial numeric version `0.0.1` to `Cleaner.csproj`.
- Update `.github/workflows/build.yml` to expose and validate the project version, package the executable and SHA-256 file, and upload the artifact only for pushes to `main`.
- Add a separate least-privilege release job. It creates a release when the numeric version is higher than all existing matching tags, uploads both assets, then deletes only older matching releases and tags.

## 2. Add in-app update support

- Add a service to read the latest release, validate its tag/assets, download the executable and checksum, and verify SHA-256 before installation.
- Add a per-user installer service that stages the verified executable under `%LOCALAPPDATA%`, creates a Start Menu shortcut through a hidden helper, waits for the app to exit, and safely replaces/starts the installed copy.
- Add a Settings action to check and install updates, plus a one-time install action for portable users. Keep network access user-initiated and use the existing embedded dialog host.

## 3. Document and verify

- Document how to bump versions and the release/update behavior in `README.md`.
- Review the final diff and staged diff; run `git diff --check` and a Release build. Do not run tests unless requested.
- Commit only files changed for this task. Do not push.
