# Security Policy

## Supported versions

Weir is pre-1.0; only the latest released version receives security fixes.

| Version | Supported |
| ------- | --------- |
| latest  | yes       |
| older   | no        |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, use GitHub's private reporting:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability** (Privately report a vulnerability).
3. Describe the issue, affected version(s), and steps to reproduce.

We aim to acknowledge a report within a few days and will keep you updated on the fix and disclosure
timeline. Please give us reasonable time to address the issue before any public disclosure.

## Hardening notes

- API keys are stored only as a SHA-256 hash plus a short non-secret prefix; the plaintext is shown
  once at creation and never persisted.
- Admin passwords are stored with PBKDF2 (SHA-256). Set a stable `Weir:Jwt:SigningKey` in production
  so admin sessions survive restarts; an unset key is generated per process.
- Weir executes only the stored procedures / functions declared in its metadata; it never issues
  ad-hoc SQL. Object and schema names are quoted.
- Parameter values are never logged by default (telemetry carries metadata only).
- Keep data-plane connection strings out of source control; supply them via environment or a secret
  store.

Thank you for helping keep Weir and its users safe.
