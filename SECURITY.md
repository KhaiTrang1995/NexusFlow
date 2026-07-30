# Security Policy

## Reporting a vulnerability

**Do not open a public issue.** Report privately through GitHub Security
Advisories on this repository, or by email to the maintainers listed in
`CODEOWNERS`.

Please include: affected version, a description of the issue, reproduction steps
or a proof of concept, and the impact you believe it has.

## Response commitments

| Severity | Acknowledgement | Fix target |
|---|---|---|
| Critical — remote code execution, authentication bypass, cross-tenant data access | 24 hours | 48 hours |
| High — privilege escalation, data exposure, durable-state corruption | 48 hours | 7 days |
| Medium — denial of service, information disclosure without data access | 5 days | 30 days |
| Low — hardening opportunities | 10 days | next minor release |

We will keep you informed throughout, credit you in the advisory unless you
prefer otherwise, and coordinate disclosure timing with you.

## Supported versions

| Version | Supported |
|---|---|
| latest minor of the current major | ✅ |
| previous minor | ✅ security fixes only |
| older | ❌ |

Pre-1.0 previews receive fixes on the latest preview only.

## Scope

**In scope:** the FlowX runtime, compiler, SDK, CLI and first-party plugins —
in particular authorisation bypass at the capability boundary, cross-tenant
access, journal or fencing-token integrity, secret leakage into the manifest,
telemetry or journal, and unsafe defaults.

**Out of scope:** vulnerabilities in an application's own capability
implementations (see the known limitations in
[docs/15-Security.md §11](docs/15-Security.md#11-known-limitations)),
third-party plugins hosted elsewhere, and issues requiring an already-compromised
host.

## Security posture

The threat model, STRIDE analysis per trust boundary, and the CI security tests
are documented in [docs/15-Security.md](docs/15-Security.md).
