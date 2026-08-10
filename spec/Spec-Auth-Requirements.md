# Auth Requirements

What the rebuilt vault access layer owes the authorization model, and what that model owes it back. **The model itself is retained code and is not specified here** - grants, tokens, bindings, scope primitives, minting, and elevation all survive the rewrite intact, and the source is the authority on how they work. Restating them in a doc would create a second source of truth that drifts, so this doc carries only the obligations across the seam.

The companion [[Spec-Vault-Access-Design]] states the guarantees the layer owes its callers.

## Not Open For Redesign

The grant model, the op-class vocabulary and its resource-and-verb shape, path scope as a set of per-vault folder roots, the mint attenuation rules, and the elevation approval path are all fixed. A change to any of them is a separate piece of work with its own reasoning, not something to fold into this build.

The C# scope primitives - segment normalization, containment, and the per-vault root resolution - are retained along with the rest, because the auth code consumes them directly. The rebuilt layer calls them; it does not reimplement them, and it must not fork their semantics.

## Delegated Reach

Where a grant row declares a dimension as "everything," it is **delegating that dimension's reach to the configuration that owns it** - permanently, and approved once at the row rather than at each expansion. The row's effective authority therefore tracks the configured set as that set changes, which is the property such a row exists to have. Delegation is to the configured set and not to some healthy subset of it, so a vault whose root was unresolvable at load is still within reach of a row that delegates.

**A row may delegate only if it is maximal on every dimension.** A row expressing any real limit states all of its sets explicitly. Delegation on a partially-authorized row is authority nobody approved, arriving whenever the configuration next changes, and scope follows it in unscoped.

## What The Layer May Assume

These hold because the retained code guarantees them. Where one appears violated, the correct response is to fail as on an upstream defect, never to interpret it as permission.

- **A grant that exists is well-formed** - scope map present, roots already canonical, op-class names canonical, wildcards already expanded, expiry an absolute instant.
- **Every vault a grant can reach has a scope entry.** A missing entry is a construction defect, not a permission question. Fail closed.
- **A malformed root cannot reach enforcement**, because roots are canonicalized when the grant is built. If one appears anyway, the whole check fails - it does not skip that root and answer from the rest.
- **A raw credential never reaches an operation.** Principals are identified by a derived, non-reversible value.

## What The Layer Must Do

- **Route every path-touching operation through the retained scope check.** No entry point opts out. This is the layer's own obligation and the one thing the retained code cannot enforce for it.
- **Take authority as a handed-in parameter, never ambient and never caller-settable.** Not readable from a global, an environment value, or a request argument. A caller-supplied scope argument is a forged grant, and the strongest defense is that no such argument exists on any public surface.
- **Assert mid-operation capability keys where the required authority depends on what the work turned out to touch**, rather than on what was requested. The floor is checked before the operation runs; a key is asserted once the outcome is known.
- **Refuse without an oracle.** An out-of-scope refusal must not disclose whether the target exists, and a refusal payload carries only the caller's own coordinates - never the grant's classes, vaults, or roots, and never the filtered subset that violated a check, which is the same disclosure paid in instalments.
- **Filter silently, gate loudly.** An enumeration returns the in-scope subset without saying what was withheld; a named out-of-scope path errors. Consequently the gate on an enumeration operation itself must pass even under a narrow scope - the operation is admitted and its results filtered.

## The Ungranted Path

**No authority context at all means no grant is in play, and is unrestricted within the vault.** This is the CLI and recovery case, and it is distinct from a grant that happens to be unrestricted. It must stay distinct: it is the property that keeps the vault operable with nothing running, and it must not become a partially-enforced state. Capability keys degrade open under the same condition and for the same reason - a half-enforced recovery path is worse than an unenforced one.

This is a deliberate decision rather than an accident of having nothing to check, and it should be stated wherever it is user-visible.
