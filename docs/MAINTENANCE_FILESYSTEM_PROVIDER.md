# Maintenance filesystem provider

CodexPro-Safe includes a source-available, Windows-only read boundary for a
future Routine Maintenance adapter. Its protocol is
`codexpro-maintenance-fs-v1`, served by the app-local
`CodexProSafe.DiagnosticHelper.exe --serve-maintenance-fs` process. It is a
separate mode from `--serve` and does not change `codexpro-diagnostic-v1`.

Consumers do not launch that external helper directly. The prior provisional
TypeScript verify-by-path-then-spawn contract was removed because replacement
could race the pathname check. A trusted caller instead compiles/adapts the
native sources in `tools/CodexProSafe.MaintenanceFsLauncher/` into its own
application package and starts its trusted launcher with the sole production
argument `--serve`.

The provider is local-only. It creates no listener, service, scheduled task,
startup entry, elevated process, connector route, MCP tool, or workspace root.
In particular, an arbitrary root must never be exposed through MCP.

## Binding and lifetime

The process starts unbound. Its first framed request must contain exactly:

```json
{"protocol":"codexpro-maintenance-fs-v1","operation":"bind_root","root":"C:\\exact\\absolute\\root"}
```

The root travels only in this private length-prefixed stdin frame. It is not an
argument, environment value, output, status field, or log value. Binding is
one-shot and immutable. A duplicate bind returns `already_bound`; another
operation before binding or malformed bootstrap terminates the helper
fail-closed. `close` or EOF ends the process and invalidates all state.

The helper accepts only canonical absolute drive paths on a local NTFS volume.
It rejects relative paths, UNC/network paths, device and extended namespaces,
alternate data streams, missing or non-directory roots, reparse points,
devices, redirected final paths, and volumes without the identity semantics
used by this implementation. The retained root handle—not a later pathname—is
the authority after binding.

## Framing and operations

Every message is a four-byte little-endian positive length followed by one
strict UTF-8 JSON object. Requests are at most 8 KiB; responses are at most
4 MiB. Duplicate/unknown fields, non-integer numbers, invalid UTF-8, malformed
JSON, invalid lengths, partial frames, and unknown operations fail closed.
Responses never include native exception text, Win32 messages, absolute paths,
file IDs, volume IDs, command lines, or environment values.

After `bind_root`, the only operations are:

- `handshake`: no additional fields; returns the protocol, `NTFS`, and the
  fixed maxima below.
- `walk`: requires `maxDepth`, `maxEntries`, `maxObservedBytes`,
  `maxResponseBytes`, and `maxDurationMs`. Each value may only lower its server
  maximum.
- `hash_file`: requires `entryId` and `maxBytes` (at most the hash maximum).
- `read_text_file`: requires `entryId` and `maxBytes` (at most the text
  maximum).
- `close`: no additional fields; acknowledges and exits.

The hard maxima are:

| Budget | Maximum |
|---|---:|
| Recursion depth | 64 |
| Returned/visited entries | 4,096 |
| Cumulative observed regular-file bytes | 4 GiB |
| Encoded response | 4 MiB |
| Walk duration | 5,000 ms |
| One hashed file | 64 MiB |
| One text file | 1 MiB |

`walk` returns `status`, `complete`, `limitation`, `returnedEntries`,
`observedFileBytes`, and `entries`. A successful walk is `ok`, `complete: true`,
and `limitation: none`. Exhaustion is `budget_exhausted`, `complete: false`, and
exactly one of `depth`, `entries`, `observed_bytes`, `response_bytes`, or
`duration`. Other failures are never marked complete.

Each entry contains only an opaque sequential `entryId`, a normalized
`/`-separated relative path, `file|directory|reparse|other`, regular-file byte
size, modified UTC time, and a fixed sanitized attribute string. Enumeration is
depth-first after sorting each directory ordinal-ignore-case with an ordinal
tie break. Reparse points are classified but never followed. Case-insensitive
namespace ambiguity fails the walk.

IDs do not encode paths or content. They refer only to immutable identity and
component-chain records from the latest walk in that process. A new walk,
failure, close, or exit invalidates earlier IDs.

## File access safety

`hash_file` and `read_text_file` accept only a current opaque ID for a captured
regular file. The helper reopens every ancestor from the retained root with
native handle-relative `NtCreateFile` calls, then verifies volume, 128-bit file
ID, type, attributes, reparse state, size, modified/change time, and link count
before and after the bounded streaming read. Reparse, non-disk, multi-link,
renamed, replaced, or changed objects return a fixed failure with no data.

Hashing returns a lowercase SHA-256 and byte count without content.
`read_text_file` additionally requires strict UTF-8 with no NUL and returns the
exact original bytes as base64 plus their SHA-256; line endings are not
normalized. The helper never writes, repairs, checkpoints, renames, locks
exclusively, or changes timestamps.

Fixed response statuses are `ok`, `invalid_request`, `not_bound`,
`already_bound`, `unsupported`, `unavailable`, `changed`, `budget_exhausted`,
`invalid_entry`, `too_large`, and `not_text`. The managed client treats any
schema/status inconsistency as failure.

## Trust, packaging, and ownership

The build emits the same app-local helper and public manifest. Existing fields
remain `protocolVersion`, `executable`, and `sha256`; the additive
`maintenanceFsProtocolVersion` is fixed to `codexpro-maintenance-fs-v1`.

The trust model has three explicit roles:

1. The consuming application and maintenance launcher compiled into that
   application's trusted package are the trusted caller package.
2. The external Manager manifest and helper remain mutable input until the
   launcher opens, validates, hashes, and locks them.
3. The inspected root is caller-selected policy and crosses only the private
   bootstrap pipe before the helper binds it once.

The launcher accepts one strict, bounded first frame under
`codexpro-maintenance-fs-launcher-v1`:

```json
{"protocol":"codexpro-maintenance-fs-launcher-v1","operation":"bootstrap","manifestPath":"C:\\exact\\CodexProSafe.DiagnosticHelper.json","expectedManifestSha256":"<lowercase-sha256>","expectedMaintenanceProtocol":"codexpro-maintenance-fs-v1","root":"C:\\exact\\root"}
```

It opens the package directory, manifest, and fixed helper basename with native
handle-relative operations; rejects reparse, non-disk, redirected, multi-link,
or identity-mismatched objects; hashes through retained handles; and denies
write/delete/rename while calling `CreateProcessW`. It verifies the child image
path and file identity, revalidates the locked files after creation, and keeps
all trust handles until the child and relay have ended. A kill-on-close job
terminates the helper if the launcher is killed or its parent disconnects.
After the private one-shot root bind, the launcher relays the existing framed
maintenance protocol without adding filesystem policy. Each child response has
a fixed deadline while the launcher concurrently monitors parent-pipe EOF and
queued-frame violations; timeout or disconnect terminates the job and releases
the package locks.

`src/windowsMaintenanceFsBoundary.ts` clearly separates
`trustedLauncherPath` from the external `manifestPath` and
`expectedManifestSha256`, sends those external values and the root only in the
bootstrap frame, launches only `--serve` with `shell: false` and a minimal
environment, preserves exact response validation and timeouts, and never falls
back to direct helper launch.

The exact reusable source set is `StrictJson.cs`, `PackageTrust.cs`,
`NativeChild.cs`, and `Program.cs` under
`tools/CodexProSafe.MaintenanceFsLauncher/`. A downstream adaptation must record
the source path, exact CodexPro-Safe commit, MIT license, every local
modification, and the tests used. Hashing an arbitrary launcher pathname just
before ordinary process creation is not an adequate trust root.

The future maintenance caller owns its root allowlist, policy, classification,
redaction, persistence, reporting, remediation, and scheduling. This repository
does not select roots or persist snapshots. This source task did not install or
activate the provider or launcher on a live Manager.

Rollback is additive: omit or disable the maintenance launcher and client.
The existing `--serve` fixed diagnostic behavior and its MCP profile remain
unchanged.
