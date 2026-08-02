# Decisions

Architectural decisions made during this build, one entry each, newest last. Permanent - this file accumulates and is never cleared.

Record a decision when a fork closes, meaning something was rejected. A choice with no alternative is a design fact and belongs in the code, not here.

Keep the prose clear and concise. Avoid overly complex sentence structures.

Entry shape:

```
## <The decision, as a claim>

Date, and the phase it landed in.
What was rejected, and why it lost.
Which spec section it answers, where one applies.
```

See `Brief-Vault-Access-Build.md:Working Files`.

## The unused half of the retained `VaultAccess` surface is deleted rather than built on

2026-08-02, prework.

Rejected: keeping `ResolvePath` and `ResolveVault` as the seam the new resolved-path type is built over. Neither has a production caller and no spec asks for them, so what they would contribute is the previous implementation's shape - a vault path as a bare string, with scope as a separate check every caller must remember to make. That is the shape the resolved-path type exists to replace, and inheriting it is the outcome this build was restarted to avoid. Their tests go with them.

`IsPathInReadScope` stays. `Spec-Auth-Requirements.md:What The Layer Must Do` puts the read-floor union on the retained check rather than on the layer, which makes it a spec-named seam rather than leftover.

Answers `Spec-Vault-Access-Design.md:Load-Bearing Semantics`.
