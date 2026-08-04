# Maintenance filesystem provider

CodexPro-Safe includes a source-available, Windows-only read boundary for a
future Routine Maintenance adapter. Its protocol is
`codexpro-maintenance-fs-v1`, served by the app-local
`CodexProSafe.DiagnosticHelper.exe --serve-maintenance-fs` process. It is a
separate mode from `--serve` and does not change `codexpro-diagnostic-v1`.

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
`src/windowsMaintenanceFsBoundary.ts` verifies the helper bytes before launch,
uses only `--serve-maintenance-fs` with `shell: false` and a minimal environment,
binds the root in the first private frame, serializes requests, validates exact
responses, enforces timeouts, and terminates on failure.

The future maintenance caller owns its root allowlist, policy, classification,
redaction, persistence, reporting, remediation, and scheduling. This repository
does not select roots or persist snapshots. This source task did not install or
activate the provider on a live Manager.

Rollback is additive: omit or disable the maintenance launch mode and client.
The existing `--serve` fixed diagnostic behavior and its MCP profile remain
unchanged.
