# Build Decisions

In-flight judgment calls made during the build where the specs and design left a small gap and the obvious solution was taken rather than stopping. A review queue for the operator: each entry says what was decided and why it follows from what is written. An entry that turns out to be a real design decision migrates to its design document's `What This Closes` or a comment at the code site, and comes out of here. Ratified entries the code already documents can simply be deleted.

## Core access layer, slice: path types and VaultAccess

- **Refusal order is malformed → perimeter → scope.** The perimeter binds "whatever its scope" (`Design-Core-Access-Layer.md:Refusals`), so it must be decided before scope; text that is not a path at all cannot be classified by either. Caller-visible only in which refusal a doubly-bad path gets.
- **`Malformed` carries a reason string** taken from `ScopePath.Normalize`'s exception message (parameter suffix stripped). Oracle-safe: the message derives entirely from the caller's own text.
- **`Admits` throws on a candidate from another vault** rather than answering false. A cross-vault candidate is a layer defect, not a scope answer — same posture as `Grant.ScopeIn` on a missing vault (fail as on an upstream defect, never interpret as permission).
- **`Unavailable` resolutions canonicalize the name too**; only `Unknown` echoes the caller's spelling, it being the only spelling that exists. The spec's canonical-name rule speaks of resolution generally, and a skipped vault is configured, so a canonical spelling exists.
- **`VaultPath.TryCreate` is public.** Containment is not admission — a `VaultPath` reaches no operation without passing `VaultAccess` — and the census/enumeration slices need the producer.
