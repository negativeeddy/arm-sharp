# Notifications — Pushover

- **API:** `POST https://api.pushover.net/1/messages.json` with `token` (app key), `user` (user key), `message`, `title`, `sound`, etc.
- **Config keys:** `PoUserKey` / `PO_USER_KEY` already exist in `ArmSettings` and `ConfigSnapshot`, mapped from YAML. Missing: a `PoAppToken` key for the application token.
- **Implementation:** ~20 lines in `NotificationService` — `SendPushoverAsync(client, appToken, userKey, title, body, ct)`.
- **Settings UI:** Apprise tab currently read-only; would need editable form fields.
