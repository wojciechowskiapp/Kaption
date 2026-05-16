# Privacy

The full privacy policy is at [kaption.one/privacy](https://kaption.one/privacy).
This file is the source-repo pointer for security researchers, auditors,
and contributors who want a one-glance summary of what the desktop
client touches.

## What the desktop client sends out

- **Activation + licence heartbeat.** Once you sign in with Google or
  Discord OAuth, the client stores a session JWT and refreshes it
  hourly against `api.kaption.one`. The heartbeat carries the device
  identifier (a per-install random GUID), the session expiry, and an
  active-OCR-seconds counter for usage analytics.
- **Translation-pack downloads.** When a new pack version is published,
  the client downloads the encrypted `.gisub-dist` file from
  `files.kaption.one` over HTTPS.
- **Update checks.** Velopack polls `releases.stable.json` from the
  same files host on every launch.
- **Optional crash reports.** Off by default. You opt in on first
  launch (or in Settings). When enabled, exceptions are sent to a
  self-hosted GlitchTip instance with PII minimisation rules in
  `CrashReportingService.cs`.

## What the desktop client never sends

- Screen captures. OCR runs entirely on your machine. Pixels never
  leave the device.
- Game saves, character data, account names from the game itself.
- Window contents from anything other than the game window region you
  configured.
- Keystrokes outside Kaption's own configured hotkeys.

## Data controller (GDPR)

For EU/UK GDPR purposes the data controller is **Michał Wojciechowski**
(operating as Kaption, kaption.one), Poland.

Contact for data-subject requests, including access, rectification,
erasure, and portability:

- **Email:** [contact@kaption.one](mailto:contact@kaption.one)

## Source-code transparency

You are reading the source. Search the repository for `HttpClient`,
`SendAsync`, `KaptionApiClient`, `CrashReportingService`, and
`MainWindow.OCRTimer` to see every outbound call site and every place
a request payload is constructed.

---

*Last updated: 2026-05-16. The authoritative version is at
[kaption.one/privacy](https://kaption.one/privacy).*
