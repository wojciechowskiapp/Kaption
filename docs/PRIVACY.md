# Privacy

A short summary for people reading the source. The policy that applies is the one on the website, [kaption.one/privacy](https://kaption.one/privacy) (in Polish: [kaption.one/pl/privacy](https://kaption.one/pl/privacy/)). If this page and the website ever differ, the website is right.

Data controller: Michał Wojciechowski DEV, ul. Wałowa 3a/10, 37-450 Stalowa Wola, Poland, <contact@kaption.one>.

## What stays on your computer

- The app reads the part of the screen you chose and recognises the text on your PC. Screen images never leave it.
- `%APPDATA%\Kaption` holds your settings, the activation (protected with Windows DPAPI), the subtitle packs (encrypted for this machine) and `app.log`. We can't see these files unless you send them to us.
- The app does not read game memory or game files, record keystrokes, or talk to the game's servers.

## What the app sends

- **Sign-in and activation.** You sign in with Google or Discord in your browser. When the app is activated on a PC, we store the Windows computer name and SHA-256 hashes of three hardware identifiers, computed on your PC.
- **The hourly check.** About once an hour the app confirms the activation and sends how many seconds the subtitles ran since the last check. That number is kept only for people who joined through a referral.
- **Subtitle pack downloads.** Each download is logged with the account, device, file, time and a hash of the IP address with a salt that changes daily. The entries are deleted after 90 days.
- **Crash reports, only if you turn them on.** They are off by default; the switch is in Pomoc › Prywatność (Help › Privacy). A report has the error, recent error-level log lines (which can include a short piece of game text), the app and system versions and a random activation ID, with no email and no screen image. Sentry stores them in Germany (Frankfurt) and deletes them after 30 days. App versions before 2.5.8 still send them to GlitchTip, which keeps them for 90 days, until we close that account on 9 December 2026.
- **Feedback you send** from the app. It is deleted from our database after 90 days.
- **Your Genshin UID, only if you save it** for the "Co dziś farmić" card. The app then reads that UID's public showcase through our server (the same read as the "Mój build" page) at most once a day and keeps the result, names and talent levels only, on your PC. "Usuń UID i dane" removes both.
- **Public game data and icons** come straight from gitlab.com, enka.network, yatta.moe and nanoka.cc. Those servers see your IP address and get nothing else from us.

## The website

- Two cookies, both for signing in. No ads and no Google Analytics.
- Our own visit statistics keep nothing in your browser for good, and the entries are deleted after 90 days. Cloudflare Web Analytics runs without cookies.
- The "Mój build" ("My build") page asks Enka.Network for the public character showcase of the UID you type. The result is cached briefly and is never stored in a database or linked to an account.

## Your rights

You can ask to see, correct, export or delete your data, object to processing, or withdraw consent at any time: write to <contact@kaption.one> from the email address on your account. Deleting the account also removes your devices, downloads, referrals and feedback. In Poland you can complain to UODO, the data protection authority; elsewhere in the EU, to your national authority.

## Checking the code

Calls to our server go through `GI-Subtitles/Services/Network/KaptionApiClient.cs`, crash reports through `Services/Observability/CrashReportingService.cs`, game data downloads through `Services/Translation/GameDataUpdateService.cs` and character icons through `Services/Characters/CharacterImageCache.cs`. Searching for `HttpClient` and `SendAsync` finds every outbound request.
