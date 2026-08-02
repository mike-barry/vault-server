# Auth Requirements

What the rebuilt vault access layer owes the authorization model, and what that model owes it back. **The model itself is retained code and is not specified here** - grants, tokens, bindings, scope primitives, minting, and elevation all survive the rewrite intact, and the source is the authority on how they work. Restating them in a doc would create a second source of truth that drifts, so this doc carries only the obligations across the seam.

The companion [Spec-Vault-Access-Design](Spec-Vault-Access-Design.md) states the guarantees the layer owes its callers.

## Not Open For Redesign

The grant model, the op-class vocabulary and its resource-and-verb shape, path scope, the mint attenuation rules, and the elevation approval path are all fixed. A change to any of them is a separate piece of work with its own reasoning, not something to fold into this build.

**How scope is structured is read from source, never from here.** This doc names what the layer may rely on, not what the model looks like. A description of the shape in a doc is a second copy that drifts from the code the moment either moves.

The C# scope primitives - segment normalization, containment, and the per-vault root resolution - are retained along with the rest, because the auth code consumes them directly. The rebuilt layer calls them; it does not reimplement them, and it must not fork their semantics.

## What The Layer May Assume

These hold because the retained code guarantees them. Where one appears violated, the correct response is to fail as on an upstream defect, never to interpret it as permission.

- **A grant that exists is well-formed** - every part of it canonical and fully resolved when the grant is built, needing no interpretation at enforcement time, with expiry an absolute instant.
- **Every vault a grant can reach has scope defined.** A missing definition is a construction defect, not a permission question. Fail closed.
- **A malformed scope element cannot reach enforcement.** If one appears anyway, the whole check fails - it does not skip that element and answer from the rest.
- **A raw credential never reaches an operation.** Principals are identified by a derived, non-reversible value.
- **Scope never subtracts.** A path in scope has its whole subtree in scope. An operation on a folder therefore never meets a child its grant cannot reach: scope either admits the folder or refuses it.

## What The Layer Must Do

- **Route every path-touching operation through the retained scope check.** No entry point opts out. This is the layer's own obligation and the one thing the retained code cannot enforce for it.
- **Route read-class operations through the read-scope check**, whose effective scope is the grant's own roots unioned with the vault's always-readable roots ([Spec-Tool-Capabilities: Path Policy](Spec-Tool-Capabilities.md#path-policy)). The union is the retained check's, not the layer's to apply, and it reaches reads alone - a write is scope-checked against the grant's roots and nothing wider. Enumeration runs over the same effective scope a containment check uses, so an always-readable path needs no case of its own.
- **Wikilink maintenance is the one exception, and it is not scope-bound.** A move is scope-checked at its source and destination, and a creation at its target. The wikilink pass that follows runs over every note in the vault, because the links it must correct can be anywhere. No scope check runs on that pass, since one would imply a limit that is not there. It carries an authority of its own instead ([Spec-Tool-Capabilities: Authority](Spec-Tool-Capabilities.md#authority)).
- **Take authority as a handed-in parameter, never ambient and never caller-settable.** Not readable from a global, an environment value, or a request argument. A caller-supplied scope argument is a forged grant, and the strongest defense is that no such argument exists on any public surface.
- **Assert mid-operation capability keys where the required authority depends on what the work turned out to touch**, rather than on what was requested. The floor is checked before the operation runs; a key is asserted once the outcome is known.
- **Refuse without an oracle.** An out-of-scope refusal must not disclose whether the target exists, and a refusal payload carries only the caller's own coordinates - never the grant's classes, vaults, or roots, and never the filtered subset that violated a check, which is the same disclosure paid in instalments.
- **Filter silently, gate loudly.** An enumeration returns the in-scope subset without saying what was withheld; a named out-of-scope path errors. Consequently the gate on an enumeration operation itself must pass even under a narrow scope - the operation is admitted and its results filtered.

## Both Front-Ends Are Granted

**Every invocation carries a grant, and there is no authority-free context.** The CLI presents a named grant from configuration exactly as an MCP client presents its own, so one enforcement path serves both and nothing degrades open. Configuration that will not load fails the CLI rather than admitting it unrestricted.

**Elevation is available at either front-end.** At the CLI the operator is the approver. That is a guard against an unintended edit rather than a boundary, since an operator who can run the CLI can also edit the grant it uses.
