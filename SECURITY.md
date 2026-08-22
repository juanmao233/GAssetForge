# Security Policy

## Supported versions

Until the first stable release, security fixes target the latest `main` branch only. Old portable
builds are not supported once a newer build is published.

## Reporting a vulnerability

Do not open a public issue for vulnerabilities, leaked credentials, path traversal, unsafe image
parsing, MCP authentication bypasses, or Agent tool-scope escapes.

Use GitHub private vulnerability reporting through the repository's **Security → Report a
vulnerability** page. If private reporting is not enabled yet, contact the repository owner through
the private contact method listed on their GitHub profile and include:

- affected commit or version;
- reproduction steps and impact;
- whether user files, tokens, prompts, or generated assets may be exposed;
- a minimal proof of concept without real secrets or proprietary artwork.

Allow the maintainer reasonable time to investigate before public disclosure.

## Security assumptions

- The HTTP MCP server is loopback-only and must not be exposed through a proxy, port forward, or
  firewall rule.
- Bearer tokens are process-scoped, high-entropy values and must not be logged or persisted.
- Project and generation paths must remain below their validated roots and reject reparse points.
- Image inputs are untrusted; file size, decoded size, format structure, and Alpha constraints must
  remain fail-closed.
- Agent output is untrusted until the local harness validates and atomically commits it.
