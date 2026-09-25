# Security policy

## Supported versions

Security fixes are made on `main` and included in the next release. Use the latest release when you run Agent Fleet.

## Report a vulnerability

Use this repository's **Security > Report a vulnerability** form. Please do not open a public issue for a vulnerability or
include credentials, private network details, conversation data, or logs in a public report.

Include the affected version, operating system, deployment mode, steps to reproduce, and the security impact. Redact tokens,
passwords, private keys, machine names, addresses, file contents, and personal paths.

## Deployment boundary

Agent Fleet has no authentication and can read files and run configured tools with the account that starts it. Keep the web UI
on loopback and the backend on a trusted private network, restrict model endpoints to the hub, and never expose these services
to the internet. See [docs/security.md](docs/security.md) for the deployment controls.
