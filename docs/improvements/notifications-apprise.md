# Notifications — Apprise

- **CLI:** `apprise` is a command-line tool supporting 80+ notification services (Slack, Discord, Telegram, email, etc.). Original Python ARM invokes it via subprocess.
- **Config key:** `Apprise` / `APPRISE` already exist in `ArmSettings` and `ConfigSnapshot`.
- **Implementation:** ~30 lines in `NotificationService` — call `apprise -b "body" -t "title"` via `CliProcessRunner`.
- **Settings UI:** Same as Pushover — needs editable form on Apprise tab.
