<!--
By submitting a pull request, you agree your contribution is licensed under both AGPL-3.0 and the Kaption
commercial-licence option (inbound = outbound). See CONTRIBUTING.md.
Security issues: do NOT open a PR. Follow SECURITY.md (private advisory or email).
Title: `type(scope): what changes, in plain words`, at most 72 characters, e.g.
`feat(desktop): active game codes on the Start tab`. No brackets, lists or review jargon; check it with
`python tools/review/pr_title.py "<title>"`.
Layout: the top part is for a person and must read in under a minute (plain words, no file paths or test
counts). Everything an agent or reviewer needs goes inside the <details> block below it.
-->

## Summary

<!-- Two or three sentences: what changes for players, the owner or the team, and why. No jargon. -->

## Screenshots

<!-- What it looks like now, or before and after, for anything a person can see (site, app, Grafana, a PR
     comment). Delete this section when nothing is visible. Upload with `python tools/review/pr_shots.py`. -->

## Owner steps

<!-- Only what the owner has to do, as numbered clicks or commands. Write "None" if nothing. -->

<details>
<summary>Details for reviewers and agents</summary>

### What it's about

<!-- The problem or the ask, with links to the issue, the owner's request or the earlier PR. -->

### How it's implemented

<!-- The approach in a few bullets: the main files or modules, the key decision, anything replaced or removed. -->

### Risks

<!-- What could go wrong for players, data, cost or operations, and how it is contained (tests, a setting, a
     rollback). Say "None known" only after looking. -->

### What it needs

<!-- Deploy order, migrations applied by hand, secrets or config changes, VPS steps, follow-ups. -->

### Verification

<!-- Exact commands and their counts (pass / fail / skip), rehearsal or live checks. -->

### Reviews

Tier: A / B / C
<!-- Keep one letter on the line above (the merge gate reads it): A no review (plans, docs, spike results, test-only),
     B gpt-6.1-sol only (ordinary low-risk code), C Fable 5.2 and sol with Fable's last word (deploy and release
     scripts, CI and merge gates, auth and secrets, D1 migrations, money, production data rewrites, player notices at
     scale, the desktop release, the autopilot gate). Then who reviewed it and the verdicts, with links to the PR
     comments; each review names the head it read ("head: <sha>"). Outside contributors: leave this for the
     maintainers. -->

</details>
