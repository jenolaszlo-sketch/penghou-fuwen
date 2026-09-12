# Security policy

Penghou.Fuwen treats workflow source, persisted plans, provider output, and
artifact evidence as untrusted data. Please report suspected vulnerabilities
privately through GitHub's security-advisory feature for
[`jenolaszlo-sketch/penghou-fuwen`](https://github.com/jenolaszlo-sketch/penghou-fuwen/security/advisories/new).

Do not include credentials, private prompts, customer data, or exploit data
that is unnecessary to reproduce the issue. Preview releases receive fixes on
the newest preview line; older previews are not supported.

## Trust boundary

Fuwen compiles and validates descriptions of work. It is not a sandbox or an
authorization system. A host must provide trusted, immutable catalogues and
must authorize every descriptor, capability, resource handle, budget, model,
tool, and external side effect before admission. Source text cannot grant
itself capabilities.

Hosts should treat admission receipts as in-process authority, keep secrets out
of workflow values and diagnostic messages, use content-addressed immutable
artifacts, enforce provider time/cost/concurrency limits, and protect the Zhinu
and definition stores with normal operating-system and database controls.

See [the threat model](docs/threat-model.md) for the detailed design assumptions
and non-goals.
