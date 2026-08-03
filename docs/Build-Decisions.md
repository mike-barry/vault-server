# Build Decisions

In-flight judgment calls made during the build where the specs and design left a small gap and the obvious solution was taken rather than stopping. A review queue for the operator: each entry says what was decided and why it follows from what is written. An entry that turns out to be a real design decision migrates to its design document's `What This Closes` or a comment at the code site, and comes out of here. Ratified entries the code already documents can simply be deleted.

## Core access layer, slice: path types and VaultAccess

- **Refusal order is malformed → perimeter → scope.** The perimeter binds "whatever its scope" (`Design-Core-Access-Layer.md:Refusals`), so it must be decided before scope; text that is not a path at all cannot be classified by either. Caller-visible only in which refusal a doubly-bad path gets.
- **`Malformed` carries a reason string** taken from `ScopePath.Normalize`'s exception message (parameter suffix stripped). Oracle-safe: the message derives entirely from the caller's own text.
- **`Admits` throws on a candidate from another vault** rather than answering false. A cross-vault candidate is a layer defect, not a scope answer — same posture as `Grant.ScopeIn` on a missing vault (fail as on an upstream defect, never interpret as permission).
- **`Unavailable` resolutions canonicalize the name too**; only `Unknown` echoes the caller's spelling, it being the only spelling that exists. The spec's canonical-name rule speaks of resolution generally, and a skipped vault is configured, so a canonical spelling exists.
- **`VaultPath.TryCreate` is public.** Containment is not admission — a `VaultPath` reaches no operation without passing `VaultAccess` — and the census/enumeration slices need the producer.

## Core access layer, slice: the file primitives

- **The primitives are static members on `VaultFiles`, not members on `VaultAccess`.** A primitive's authority is the type of the path it is handed, so there is no per-call state to hold; enumeration and search sit on `VaultAccess` because they must be *filtered* by a scope, which these are not. It also leaves the vault-wide pass free to reach the same mechanics from its own object rather than through the scoped one.
- **`Replace` refuses a target that is not there**, from `FileMode.Truncate` rather than a probe. The specs say "replace an existing note's content wholesale" and make creation the other form; a replace that created would make the two forms one, and only the creating form checks the grammar.
- **Folder creation establishes its parent with a check.** The one probe in the slice, and unavoidable: the platform offers no non-recursive directory creation, so without it the call would materialize every missing folder above the target. The window is a parent deleted between the check and the create. Every other refusal here comes from the attempt.
- **A folder already there is not an error.** Directory creation is idempotent on every platform and cannot report the difference without a second probe, and no spec asks for the refusal.
- **A missing parent for a path directly under the root is `Failed`, not `MissingParent`.** The root is a vault rather than a path and has no vault-relative rendering; a root that is not there is what vault resolution answers `Unavailable` for.
- **Move passes `overwrite: true` only when the destination is the source under another spelling.** .NET's non-overwrite move pre-checks the destination on Unix, so a case-only rename would otherwise refuse its own target on a case-insensitive filesystem. No other occupant is ever replaced.
- **Move relocates a file; a folder source answers `SourceMissing`.** Each operation states what it expects and fails on the other, and the folder relocation the tool spec composes needs a decision this slice does not have to make. Flagged rather than assumed — see the report.
- **Removing a file is an open with delete-on-close.** `File.Delete` succeeds silently on a file that was never there, so it cannot produce the classification removal owes; the open fails when nothing is there and the entry is unlinked as the handle closes.
- **"Already exists" and "not empty" are recognized from the platform's error code.** Both arrive as a plain `IOException`, shared with sharing conflicts and device errors, so the exception type alone would collapse a refusal the specs require to be distinguishable.
- **A failure reason renders every location it names vault-relative.** The platform puts the absolute path in its message, and a reason carrying one would take an absolute path out of the layer.
- **One `WriteOutcome` across replacing, creating and folder creation.** Their refusals are the same refusals; each operation's documentation says which cases it produces. Three near-identical hierarchies buy exhaustiveness at the cost of the reader.
- **LF normalization happens in the write primitive.** `Spec-Frontmatter-Substrate.md:Preservation Invariants` normalizes "on entry, not on the way out", but that governs the substrate's own before-and-after guard; the composing seam is where the design states terminators, and stating them anywhere else makes the rule one every caller must remember.
- **The note extension is matched ordinally.** `.MD` is not a note name: this grammar is checked only on a name being brought into existence, where one spelling is the whole point.
- **`VaultPath.Absolute` and `ParentValue` are internal.** The primitives compose root and segments; nothing outside the layer sees an absolute path.
