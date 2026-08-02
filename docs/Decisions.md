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

## Where retained code and a spec conflict, the spec wins and the retained code moves

2026-08-02, prework.

Rejected: escalating the conflict as a possible defect in the specs or the brief. `Brief-Vault-Access-Build.md` says a retained component that looks wrong should be raised because a misread requirement is the likelier explanation, which reads as a presumption in favour of the retained code. That presumption is right for a component whose subject the specs do not cover, and wrong here: these conflicts were anticipated, and the specs are the specification.

The two that closed it. The op-class floor is checked in `ScriptToolDispatcher`, in the MCP front-end, while `Spec-Vault-Access-Design.md:What The Layer Owns` puts it in the shared execute path that also takes the mutex lock - so a CLI built from the retained shape would ship with neither. And the vault-membership check cannot reach a compiled tool: the native branch returns before it, and its predicate is typed on `ScriptTool`. Every tool this build adds is compiled, so the check the brief lists as retained and correct would cover none of them.

Both are approved changes to retained code rather than escalations.

## The scope resolver has no absent-scope case

2026-08-02, prework.

Rejected: leaving the null-map branch in `ResolveVaultScopeRoots` alone. It became unreachable when `ResolvePath` and `ResolveVault` were deleted, so keeping it cost nothing and touched no retained code - the safe-looking option. It lost on what it was rather than what it did. An argument meaning "no authority was supplied" answering `Unscoped` is a fail-open default, sitting in the check every path-touching operation routes through. `Spec-Auth-Requirements.md:What The Layer May Assume` fails closed on a missing scope definition, and `Spec-Frontmatter-Substrate.md:Write Authority Classification` says no authority context at all is not a valid state.

The parameter is now required, so the shape cannot be reached by a caller who forgot rather than decided. A caller meaning the whole vault passes a map holding `VaultScope.Unscoped` - the same discipline `VaultScope` already applies to the empty root set.

Answers `Spec-Auth-Requirements.md:What The Layer Must Do`.
