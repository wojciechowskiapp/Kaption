# Privacy Policy

This document mirrors the public privacy policy published at
<https://kaption.one/privacy> (and <https://kaption.one/pl/privacy> for
the Polish version). If the two ever drift, the version on the
landing page is the authoritative one.

*Last updated: 2026-05-18.*

---

## Data controller

**Michał Wojciechowski DEV**
ul. Wałowa 3a/10, 37-450 Stalowa Wola, Poland
NIP: 9452272167 · REGON: 525109472
Contact: <contact@kaption.one>

We have not appointed a separate Data Protection Officer — for now, all
privacy matters go to the address above.

## Short version

If you sign up, we store your email, name, and avatar (from Google or
Discord). If you buy a plan, LemonSqueezy handles the payment — we only
see what they tell us (tier, expiry, order ID). The desktop app runs
OCR on your screen **locally**; screenshots and dialogue text never
leave your machine. We don't use Google Analytics, we don't run ads,
and we don't sell anything. The only cookies we set are the ones that
keep you logged in.

## What we collect

For each category: **what** · **why (legal basis under Art. 6(1) GDPR)**
· **where it lives**.

### Waitlist email

Email address if you joined the waitlist.
*Legal basis:* consent — Art. 6(1)(a).
*Stored in:* Supabase.

### OAuth profile (Google / Discord)

Name, email address, avatar URL, and the provider-side user ID. We
never see your password.
*Legal basis:* contract — Art. 6(1)(b) (to let you log in and manage
your license).
*Stored in:* Cloudflare D1.

### License purchase metadata

Order ID, tier (30/180 days), purchase and expiry dates. Card data is
handled end-to-end by LemonSqueezy — we never see it.
*Legal basis:* contract — Art. 6(1)(b); plus legal obligation for
invoicing/accounting — Art. 6(1)(c).
*Stored in:* Cloudflare D1 + LemonSqueezy.

### Session cookies

An HttpOnly JWT cookie (`kaption_session`) after sign-in and a
non-HttpOnly hint cookie (`kaption_signed_in`) so the page knows
whether to ask the server about you.
*Legal basis:* legitimate interest — Art. 6(1)(f) (keeping you logged
in).
*Stored in:* your browser.

### Device registration

Up to two machines per account: a friendly device name (e.g. your
Windows hostname) and a hashed device fingerprint.
*Legal basis:* contract — Art. 6(1)(b) (to enforce the two-device cap).
*Stored in:* Cloudflare D1.

### Feedback submissions

Free-text message you send through the Setup Wizard plus the account
it came from. Rate-limited to 5 per IP per day.
*Legal basis:* legitimate interest — Art. 6(1)(f) (improving the
product).
*Stored in:* Cloudflare D1.

### Vote fingerprint

A random hash generated in your browser when you vote for a language.
Not linked to your email, IP, or account — it just blocks duplicate
votes.
*Legal basis:* legitimate interest — Art. 6(1)(f).
*Stored in:* Supabase + your browser's localStorage.

### Opt-in crash reports

Sent only if you ticked the box on the first-run EULA dialog. Contains
a stack trace, OS version, app version, and a machine fingerprint — no
personal files, no dialogue text, no screenshots. You can turn it off
anytime in Settings.
*Legal basis:* consent — Art. 6(1)(a).
*Stored in:* GlitchTip.

## What we do NOT collect

- Screen contents. OCR runs locally on your machine — no screenshots,
  no dialogue text, no game footage is ever uploaded.
- Card numbers. Payments go through LemonSqueezy end-to-end; our
  servers only receive webhook events.
- No Google Analytics, Facebook Pixel, Hotjar, ad trackers, or
  behavioural profiling of any kind.
- Your game account credentials. The app never touches the game
  client.

## Cookies and local storage

We set exactly two cookies, both strictly necessary:

- `kaption_session` — HttpOnly JWT that keeps you logged in across
  pages. Set only after you sign in via Google or Discord.
- `kaption_signed_in` — a non-HttpOnly flag so the page knows whether
  to call `/api/me`. Saves round-trips for anonymous visitors.

Both fall under the "strictly necessary" carve-out in the ePrivacy
Directive (Art. 5(3)) — authentication cookies needed to deliver a
service the user explicitly requested do not require a consent banner.
That's why we don't show one. If you sign out or clear your cookies,
both are removed.

Separately, the site uses your browser's `localStorage` to remember a
few preferences between visits:

- Whether you joined the waitlist (so the form shows a confirmation
  instead of asking again).
- Your preferred site language.
- Your vote choice and vote fingerprint (duplicate-vote prevention).
- First-run flags (to avoid showing welcome banners twice) and, if you
  arrived via a referral or attribution link, the code that brought
  you in.

localStorage entries are first-party only and never shared with third
parties. You can clear them at any time via your browser settings.

## Third parties (processors)

The following providers process data on our behalf under Art. 28 GDPR
data-processing agreements.

- **Cloudflare** — hosts the landing page, the `api.kaption.one`
  Worker, and the D1 database (accounts, devices, feedback). May log
  IP addresses and request headers for abuse protection.
  <https://www.cloudflare.com/privacypolicy/>
- **Supabase** — hosts the legacy waitlist and language-vote tables.
  <https://supabase.com/privacy>
- **LemonSqueezy** — our merchant of record. Handles checkout, card
  processing, tax, and invoicing. We receive webhook events (order
  ID, tier, expiry) — never card data.
  <https://www.lemonsqueezy.com/privacy>
- **Google (OAuth)** — only if you choose "Sign in with Google".
  Returns your name, email, and avatar URL — no passwords, no
  contacts, no Drive access. <https://policies.google.com/privacy>
- **Discord (OAuth)** — only if you choose "Sign in with Discord".
  Returns your Discord username, email, and avatar URL.
  <https://discord.com/privacy>
- **GlitchTip** — receives crash reports **only if you opt in** during
  first-run. <https://glitchtip.com/privacy>
- **YouTube** — the homepage shows a static YouTube thumbnail for the
  demo video. YouTube's own cookies are only set if you actually click
  the thumbnail and start playback. <https://policies.google.com/privacy>

## Data retention

- **Waitlist emails** — kept until you ask to be removed.
- **Account & device data** — kept until you request deletion.
- **Purchase and invoicing records** — 5 years after the end of the
  tax year, as required by Polish accounting law (Art. 74 Ordynacja
  podatkowa).
- **Session logs** — 30 days or less.
- **Crash reports** — 90 days, then purged automatically.
- **Feedback submissions** — 12 months, unless we need them longer to
  diagnose a specific issue you reported.
- **Anonymous votes** — indefinitely (no personal data attached).

## Your rights under GDPR

Because we store personal data, EU and UK users have the following
rights:

- **Art. 15** — right of access. Ask what we have on you.
- **Art. 16** — right to rectification. Ask us to fix anything
  inaccurate.
- **Art. 17** — right to erasure ("right to be forgotten").
- **Art. 18** — right to restriction of processing.
- **Art. 20** — right to data portability. Get your data in a
  machine-readable format.
- **Art. 21** — right to object to processing based on legitimate
  interests.
- **Art. 7(3)** — right to withdraw consent at any time (e.g. for
  crash reporting).
- **Art. 77** — right to lodge a complaint with a supervisory
  authority.

To exercise any of these, write to <contact@kaption.one>. We'll reply
within 30 days (GDPR's default deadline).

## International data transfers

Some of our processors (Cloudflare, LemonSqueezy, GlitchTip) are based
in the United States or operate globally. When data leaves the EEA,
transfers rely on the EU Standard Contractual Clauses and — where
available — the EU–US Data Privacy Framework. Cloudflare gives us
regional-routing controls and we pick EU regions wherever a choice
exists.

## Children

Kaption is not aimed at children under 16. Under Art. 8 GDPR, EU users
below 16 need parental consent to use online services that rely on
consent as the legal basis. If you believe a child has given us data
without that consent, email us and we'll delete it.

## Changes to this policy

When we update this policy, we bump the date at the top. Material
changes get flagged on the home page for at least two weeks — we won't
email you for minor edits.

## Supervisory authority

If you're in Poland and think we've mishandled your data, you can
complain to:

**Prezes Urzędu Ochrony Danych Osobowych (PUODO)**
ul. Stawki 2, 00-193 Warszawa, Poland
<https://uodo.gov.pl/>

EU users outside Poland can file with their national data-protection
authority instead.

## Source-code transparency

You are reading the source. Search the repository for `HttpClient`,
`SendAsync`, `KaptionApiClient`, `CrashReportingService`, and
`MainWindow.OCRTimer` to see every outbound call site and every place
a request payload is constructed.

---

*Authoritative versions: <https://kaption.one/privacy> (English) and
<https://kaption.one/pl/privacy> (Polish). This file is the
source-repo mirror.*
