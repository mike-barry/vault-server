# Reconciliation: Scope, path resolution, I/O, configuration

Harvest source: `01-Scope-Path-Io-Config-Harvest.md` (71 cases)

Implementation read:
- `src/VaultShared/Scope/ScopePath.cs`, `VaultScope.cs`, `PathPolicy.cs`
- `src/VaultShared/Access/VaultPath.cs`, `PathResolution.cs`, `AdmittedPath.cs`, `Vault.cs`,
  `VaultResolution.cs`, `VaultAccess.cs`, `VaultWidePass.cs`, `VaultFiles.cs`, `VaultSearch.cs`,
  `AuthorityRefusal.cs`
- `src/VaultShared/Configuration/VaultConfigLoader.cs`, `OpClassRegistry.cs`
- `src/VaultShared/Auth/Grant.cs`, `GrantFileLoader.cs`, `GrantStore.cs`
- `src/VaultShared/Tools/ToolRefusalEnvelope.cs`, `MutationSupport.cs`
- `src/VaultMcpServer/Tools/MintTokenTool.cs`, `ElevationRequestTool.cs` (op-class canonicalization)

Tests read: `tests/VaultShared.Tests/ScopePathTests.cs`, `VaultScopeTests.cs`, `VaultPathTests.cs`,
`VaultAccessTests.cs`, `VaultResolutionTests.cs`, `VaultConfigTests.cs`,
`VaultFilesReadWriteTests.cs`, `GrantTests.cs`; `tests/VaultMcpServer.Tests/ScopeFloorDispatchTests.cs`

Old source consulted (to resolve harvest ambiguity only): `C:\Repos\vault-server\powershell\VaultAccess\Public\Config.ps1`,
`Private/ScopeHelpers.ps1`

Design/spec consulted: `docs/design/Design-Core-Access-Layer.md` (§ "What This Closes", the
vault-wide pass entry at line 172)

## Summary

Cases: 71. MATCH 58 · IMPROVED 8 · N/A 5 · REGRESSION 0 · GAP 0 · UNRESOLVED 0

**Nothing in this area is a regression.** Every shape of path the old system refused is still
refused, and in several places the refusal is now unavoidable rather than checked.

`ScopePath.Normalize` is a segment-for-segment port of the old `ConvertTo-VaultScopeSegments`,
including the escape-pop, the empty-segment drop, the rooted refusal and the resolves-to-root
refusal. The important change is that the old system had *two* normalization vocabularies — one for
scope containment and a separate `GetFullPath`-plus-string-prefix comparison for path resolution —
and the rewrite has one. The old path-resolution side was the weaker of the two (it accepted a path
resolving to the vault root, and relied on a hand-written trailing-separator guard for the
sibling-directory case, `SCP-018`). Collapsing both onto the scope grammar makes path resolution
strictly stricter than it was, and makes the sibling-root class of escape unrepresentable rather
than guarded.

I checked `SCP-035` specifically, since it forbids a first-match short-circuit.
**The new implementation does not short-circuit.** `VaultScope.RootedScope.NormalizedRoots`
(`src/VaultShared/Scope/VaultScope.cs:89`) materializes with `.ToArray()`, so every root is
normalized before `Admits` runs its `Any(...)`, and the failure is deliberately not cached so a
partly-resolved scope can never answer. `VaultScopeTests.ScopeCases` carries the exact harvest row
("one malformed root among otherwise-matching roots still errors"). Belt-and-braces: a malformed
root is now also a *load-time* refusal in `GrantFileLoader.ReadScopeRoots`, so the runtime throw is
the second line of defence rather than the only one.

Three refusal classes that the old system enforced by convention are now enforced by construction:
an empty scope-root set is not constructible (`VaultScope.Of` throws; whole-vault access has exactly
one spelling), a grant with no entry for the target vault throws rather than being read as
permission (`Grant.ScopeIn`), and an absolute path can never be composed because a `VaultPath` is
held as segments and joined onto the root.

Two things for the operator to look at, neither a finding:

1. The one place the new design is *wider* than the old is `VaultWidePass`
   (`src/VaultShared/Access/VaultWidePass.cs`) — unscoped enumerate, read and **write** over a whole
   vault, for wikilink maintenance. The old system had no counterpart because it did no link
   maintenance, so no harvest case bears on it. It is recorded in `Design-Core-Access-Layer.md:172`
   and confined to a separately-obtained object. Flagging it only because this reconciliation is
   the containment boundary and it is the boundary's one deliberate hole.
2. The scope-check ordering flipped (`SCP-057`). The old system checked scope on the caller's raw
   relative path *before* path resolution, so a path that was both malformed and out of scope was
   refused as out-of-scope. The new `VaultAccess.Resolve` refuses malformed first, then the
   dot-segment perimeter, then scope. This discloses nothing — malformedness is a property of the
   caller's own text — but it is a visible change in which refusal a caller gets.

## Findings

None. No `REGRESSION`, `GAP` or `UNRESOLVED` case in this area.

## Present in code, unasserted by the suite

- **`SCP-041`** — scope-map lookup by vault name is case-insensitive (`Grant.ScopeIn` compares
  `OrdinalIgnoreCase`), but no test resolves a scope entry under a differently-cased vault name.
  `VaultScopeTests` always spells the vault exactly as the map key.
- **`SCP-053`** — op-class matching is case-insensitive, but only because every producer builds
  `Grant.OpClasses` as an `OrdinalIgnoreCase` set and every entry point canonicalizes against
  `OpClassRegistry` first. `Grant.Allows` is a bare `OpClasses.Contains`, so the property belongs to
  the producers rather than to the type or the comparison, and no test pins it at the gate. Not a
  behavioural difference — every production producer is correct — but it is a weaker guarantee than
  the old case-insensitive comparison in the gate itself.
- **`SCP-063`** — the missing-parent refusal names the *immediate* parent even when only the leaf
  directory is missing. `VaultPath.ParentValue` is unconditional so the rule cannot vary, but every
  missing-parent test uses a path where two directories are missing (the `SCP-062` shape). The
  old suite needed both cases because either alone is satisfiable by the wrong rule.
- **`SCP-067`** — a sharing violation is a real failure rather than nothing-to-read. The code has
  it (`VaultFiles.Read` catches `FileNotFoundException`/`DirectoryNotFoundException` first, then
  `IOException` → `Failed`), but no test holds a file open with no sharing. The nearest test,
  `Read_ThereAndSomethingWentWrongIsTheOtherAnswer`, is the access-denied shape (`SCP-068`).
- **`SCP-023` note** — a path *equal* to a scope root is admitted (the root folder itself is in
  scope). `VaultScope.Admits` allows equal segment counts, and `VaultAccess.Beneath` depends on it,
  but the row asserting equality-to-root is only covered indirectly through `Beneath`.

## Dismissed

- **SCP-018** — the old string-prefix-plus-trailing-separator containment check against the vault
  root has no counterpart — `VaultPath` holds segments and composes onto the root, so landing in a
  sibling directory is unrepresentable rather than guarded. The old suite had no row for this;
  the new design cannot fail it.
- **SCP-019** — the case-insensitive comparison against the vault root is gone because there is no
  string comparison against the root to make. Removes the Windows-filesystem assumption the harvest
  marked `artifact`; nothing on a case-sensitive filesystem now behaves differently.
- **SCP-020** — an empty scope-root set meaning "unscoped" is not representable: `VaultScope.Of([])`
  throws and whole-vault access is `VaultScope.Unscoped` (config spells it `["*"]`). The old
  short-circuit-before-normalizing is also gone: `VaultAccess.Resolve` refuses malformed text before
  consulting scope, so a malformed path is refused under an unscoped grant too — which closes the
  hole the harvest flagged as cutting against SCP-035.
- **SCP-036** — an explicit *empty* scope entry meaning "unscoped for that vault" is replaced by an
  explicit `VaultScope.Unscoped` entry; `GrantFileLoader` refuses an empty list at load with a
  message naming `["*"]` as the only spelling. Same authority, but it can no longer be arrived at by
  dropping an entry or filtering a list to nothing.
- **SCP-039** — a null grant scope map meaning "unscoped for every vault" is gone: `Grant.ScopeRoots`
  is always fully populated (enforced by `GrantFileLoader` and `GrantStore.Mint`), and there is no
  no-grant caller. This is the scope-side twin of the SCP-049 fail-open and is dropped for the same
  reason.
- **SCP-045** — the `.` enumeration-gate carve-out has no counterpart because enumeration takes the
  vault as its operand rather than a gating path (`VaultAccess.EnumerateNotes`/`EnumerateFiles` take
  a glob, never a path). `.` is refused everywhere as a path, and the harvest's worry — that the
  carve-out could be reached with something that is not a listing — cannot arise because the
  carve-out does not exist.
- **SCP-046** — the empty-scope-roots no-op gate follows SCP-036: `VaultScope.Unscoped` admits
  everything, and an empty root set cannot be constructed to reach the same outcome by accident.
- **SCP-049** — the fail-open on an absent host capability context is dropped. Ruled an improvement
  by the operator; a session with no recognized grant degrades to `GrantStore.MinimumGrant`, which
  holds no op classes and no vaults.
- **SCP-057** — the scope check no longer runs before path resolution: `VaultAccess.Resolve` refuses
  malformed, then dot-segment, then scope. A path both malformed and out of scope is now reported
  malformed rather than out-of-scope. No disclosure, since malformedness is decidable from the
  caller's own text, and the refusal still carries only the path the caller wrote.
- **SCP-066** — unwrapping a not-found from inside a `MethodInvocationException` has no counterpart:
  the .NET file APIs raise `FileNotFoundException`/`DirectoryNotFoundException` directly, so there
  is no causal chain to walk. The rule the harvest called portable ("inspect the chain, not the
  outermost error") is satisfied because the chain is one link long.
- **SCP-069** — the two-vault-copy fixture is a PowerShell-suite arrangement. The rewrite's
  equivalents (`TestVaultFixture`, `ScratchVault`, `ProgramHostFixtureBase`) provide the same
  isolation per test class.
- **SCP-070** — configuration-restore-after-mutation has no counterpart: `VaultConfigLoader.Load`
  returns a value rather than setting process-global state, so there is nothing to restore and the
  latent fragility the harvest flagged (an inline restore that does not run if the assertion above
  it fails) cannot occur.
- **SCP-071** — clearing the injected ambient capability context between tests has no counterpart:
  there is no ambient capability carrier. A `VaultAccess` is constructed with its grant, and
  `VaultAccess` documents that no member may take a caller-supplied scope.
