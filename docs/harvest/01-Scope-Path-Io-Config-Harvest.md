# Harvest: Scope, path resolution, I/O, and configuration

Sources:
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\Scope.Tests.ps1` (127 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\Io.Tests.ps1` (123 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\Config.Tests.ps1` (36 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\SharedCases.Tests.ps1` (131 lines) — the driver for both JSON case files, so it carries this area's path and scope semantics
- `C:\Repos\vault-server\tests\cases\path-cases.json` (10 lines, 8 rows)
- `C:\Repos\vault-server\tests\cases\scope-cases.json` (21 lines, 19 rows)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\TestHelpers.ps1` (47 lines) — fixture

Read the implementation for:
- `Public/Config.ps1` — to state what "valid"/"invalid" means in `path-cases.json`, which asserts
  only "resolves to a rooted path" vs "throws" and names no rule.
- `Public/Scope.ps1` and `Private/ScopeHelpers.ps1` — to state the normalization rule behind the
  `outcome: invalid` rows, and to separate the `.` gate from ordinary containment.
- `Public/Io.ps1` — to confirm the order in which a note operation applies vault resolution, scope
  resolution, scope check, and path resolution.

## Summary

This area is the containment boundary: vault name resolution (`Resolve-VaultRoot`), vault-relative
path resolution (`Resolve-VaultPath`), scope containment (`Test-VaultPathInScope`,
`Resolve-VaultScopeRoots`, `Assert-VaultOperationAllowed`), the op-class floor gate
(`Assert-VaultOpClass`), and note read/write with its missing-error classifier.

Things a reconciler should know before reading the cases:

- **Two independent refusal layers.** Path resolution refuses paths that leave the *vault root*.
  Scope containment refuses paths that leave the *grant's scope roots* inside a vault. They are
  separately tested and separately escapable; a suite that covers only one has a hole. Both share
  the same normalization vocabulary (backslash folded to forward slash, `.` dropped, `..` popped,
  rooted paths refused) but they are two code paths with two error messages.
- **Normalization is by path *segment*, never string prefix.** Rows exist purely to pin that
  (`SCP-025`, `SCP-026`, and on the path side `SCP-018`), and getting it wrong is a silent scope
  escape rather than a test failure elsewhere.
- **`$null` vs empty is load-bearing in two places and means opposite things in each.** For scope
  roots: `$null` = no grant in play = unscoped; empty array = an explicit "full access" entry =
  also unscoped. For op classes: `$null` = no injected host context = degrade **open**; empty array
  = an authenticated grant holding nothing = **deny everything**. The old suite pins the op-class
  pair explicitly because they are easy to collapse.
- **An unscopeable query errors rather than degrading.** One malformed scope root makes the whole
  check throw even when a different root would have matched (`SCP-035`). This is deliberate.
- **The unscoped short-circuit precedes path validation.** With empty scope roots,
  `Test-VaultPathInScope` returns true without normalizing the path at all, so a path that would
  throw under a scoped grant returns `true` under an unscoped one. Asserted only indirectly, and it
  cuts against `SCP-035`'s "unscopeable queries error" posture.
- **Per-vault isolation is asserted from four directions** (`SCP-036`, `SCP-038`, `SCP-042`,
  `SCP-058`): a root configured for vault A must never restrict vault B, and a non-null grant map
  missing an entry for the target vault is a hard invariant violation, not "unscoped".
- **Vaults can be configured-but-unavailable.** A missing root directory is skipped per entry, the
  load still succeeds (even if *every* vault is missing, and even if the default is the missing
  one), and resolution of that name fails as "currently unavailable" — a state distinct from
  "unknown vault". This is a whole third outcome beyond resolve/refuse.
- The fixture (`TestHelpers.ps1`) gives every module suite a temp root with `test` and `scratch`
  copies of `test-vault` and a `vaults.json` naming `test` default; the config suite mutates that
  config and restores it. `scope-cases.json` names vaults `primary` and `test` that need not exist —
  those rows exercise map lookup, not disk.
- **Config coverage looked thin.** `Config.Tests.ps1` is 36 lines and asserts five things; several
  load-time refusals in the implementation (missing `default_vault`, missing `vaults` object, a
  vault with an empty root path, config file not found, the `VAULT_MCP_VAULTS_JSON` environment
  override of the default config path) are **not asserted by any test in this area**. I have not
  written cases for them, since the harvest is of asserted behavior, but a reconciler should know
  the absence is the old suite's gap and not evidence the rules did not exist.

## Cases

### SCP-001 — Omitted vault name resolves to the configured default vault

- **Behavior:** An operation that does not name a vault is served by the configuration's declared
  default vault.
- **Trigger:** Vault resolution with no vault name supplied.
- **Kind:** `semantic`
- **Source:** `Config.Tests.ps1:14` — `falls back to the default vault`
- **Note:** The fixture declares `test` as default. An all-whitespace vault name is treated the same
  as omitted by the implementation; no test asserts that.

### SCP-002 — An explicitly named vault resolves to that vault

- **Behavior:** Naming a configured vault resolves to that vault rather than the default.
- **Trigger:** Vault resolution with `scratch` supplied while `test` is the default.
- **Kind:** `semantic`
- **Source:** `Config.Tests.ps1:18` — `resolves an explicit vault`

### SCP-003 — Vault names match case-insensitively and resolve to the canonical spelling

- **Behavior:** A vault name is matched without regard to case, and what comes back is the name as
  spelled in the configuration, not as spelled by the caller.
- **Trigger:** Requesting vault `TEST` when the configuration spells it `test`.
- **Kind:** `semantic`
- **Source:** `Config.Tests.ps1:22` — `returns the canonical name for case-insensitive input`
- **Note:** Asserted with an exact-string comparison, so the canonicalization is the point, not just
  the successful match. Downstream matters: scope-root lookup and error messages use the resolved
  canonical name.

### SCP-004 — An unconfigured vault name is refused as unknown

- **Behavior:** Requesting a vault that appears nowhere in the configuration is refused, and the
  refusal identifies it as an unknown vault.
- **Trigger:** Requesting vault `nope`.
- **Kind:** `semantic`
- **Source:** `Config.Tests.ps1:26` — `throws on an unknown vault`
- **Note:** The implementation's message also lists the configured vault names. Only the "Unknown
  vault" wording is asserted. Deliberately distinct from SCP-009 — see that case.

### SCP-005 — A default vault naming no configured vault is refused at load time

- **Behavior:** A configuration whose declared default names a vault that is not among the
  configured vaults at all is rejected when the configuration is loaded, not when first used.
- **Trigger:** Loading a configuration with `default_vault = 'b'` and only vault `a` configured.
- **Kind:** `semantic`
- **Source:** `Config.Tests.ps1:30` — `throws on a config whose default is not a configured vault`
- **Note:** Contrast SCP-010: a default that *is* configured but whose root directory is missing
  loads fine. "Not configured" and "configured but unavailable" are different failures at different
  times.

### SCP-006 — A configured vault whose root directory is missing is skipped, not fatal

- **Behavior:** A vault whose root directory does not exist is dropped from the resolvable set and
  recorded separately with a reason, while the configuration loads successfully and every other
  vault keeps working.
- **Trigger:** Loading a configuration with one existing root and one non-existent root.
- **Kind:** `semantic`
- **Source:** `SharedCases.Tests.ps1:40` — `skips a configured vault whose root does not exist,
  without failing the load`
- **Note:** Four things are pinned: the good vault is resolvable, the bad one is absent from the
  resolvable set, the bad one is present in a separate skipped record, and the recorded reason
  mentions that the root does not exist. The skipped name is still a *configured* name — that is
  what makes SCP-009 possible.

### SCP-007 — Resolving a skipped vault fails as "currently unavailable", distinct from unknown

- **Behavior:** A vault that was configured but skipped for a missing root fails resolution with a
  distinguishable "configured but currently unavailable" outcome naming the reason — a different
  outcome from an entirely unconfigured name.
- **Trigger:** Resolving skipped vault `bad`, then resolving unconfigured vault `nope`.
- **Kind:** `semantic`
- **Source:** `SharedCases.Tests.ps1:55` — `fails resolving a skipped vault as "currently
  unavailable", distinct from unknown`
- **Note:** The distinction is asserted in **both** directions: the unavailable message must not say
  "Unknown vault", and the unknown message must not say "currently unavailable". A single collapsed
  error type fails this case. This is the strongest statement in the area that there are three
  vault-resolution outcomes, not two.

### SCP-008 — A configuration loads even when the default vault's own root is missing

- **Behavior:** A configuration whose declared default vault has a missing root directory still
  loads successfully; only an attempt to resolve the default fails, as unavailable.
- **Trigger:** Loading a configuration with `default_vault = 'bad'` where `bad`'s root does not
  exist, then resolving with no vault named.
- **Kind:** `semantic`
- **Source:** `SharedCases.Tests.ps1:75` — `loads successfully when default_vault's root is missing,
  but resolving the default fails as unavailable`

### SCP-009 — A configuration loads even when every vault root is missing

- **Behavior:** A configuration in which no vault root exists still loads: the resolvable set is
  empty, every vault is recorded as skipped, and resolution fails as unavailable rather than as a
  load error.
- **Trigger:** Loading a configuration whose two vaults both point at non-existent directories.
- **Kind:** `semantic`
- **Source:** `SharedCases.Tests.ps1:84` — `loads successfully even when every vault root is
  missing`
- **Note:** Counts are pinned exactly (zero resolvable, two skipped). This is the degenerate end of
  the skip rule and the case most likely to be missing from a spec-derived suite, because "an empty
  vault set is a valid loaded configuration" is a counter-intuitive rule.

### SCP-010 — A simple vault-relative path resolves to an absolute path

- **Behavior:** An ordinary forward-slash vault-relative path resolves to an absolute path under the
  vault root.
- **Trigger:** `Inbox/Note-Loose-Thought.md`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:2` via `SharedCases.Tests.ps1:98` — `simple relative
  path`
- **Note:** The assertion is only that the result is rooted; the JSON does not assert where it
  points. Read the implementation to state the containment rule — see SCP-014 through SCP-017.

### SCP-011 — Backslash separators are accepted in a vault-relative path

- **Behavior:** A vault-relative path written with backslash separators is accepted and resolves
  identically to the forward-slash spelling.
- **Trigger:** `Inbox\Note-Loose-Thought.md`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:3` — `backslash separators`
- **Note:** Paired with SCP-025, which pins the same tolerance in scope containment. A
  reimplementation on a platform where backslash is a legal filename character has a real decision
  to make here; the old behavior is that backslash is always a separator.

### SCP-012 — A `..` segment that stays inside the vault is accepted

- **Behavior:** A relative path containing `..` resolves normally as long as the result still lies
  within the vault root; interior `..` is not itself a refusal.
- **Trigger:** `Projects/../Test-Vault.md`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:4` — `dot-dot that stays inside the vault`
- **Note:** The rule is about the destination, not the spelling. A suite that refuses any path
  containing `..` is stricter than the old behavior and fails this case.

### SCP-013 — A path need not exist to resolve

- **Behavior:** Path resolution does not require the target to exist; a path to a note not yet
  written resolves successfully.
- **Trigger:** `Not/Yet/Written.md`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:5` — `nonexistent relative path still resolves`
- **Note:** Necessary for creates. Distinct from SCP-053, where a *write* to a resolvable path whose
  parent directory is missing is nonetheless refused — resolution and writability are separate
  questions.

### SCP-014 — A rooted (absolute) path is refused

- **Behavior:** An absolute path is refused; every path into the vault must be vault-relative.
- **Trigger:** `C:/Windows/win.ini`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:6` — `rooted path rejected`
- **Note:** The refusal is on rootedness itself, tested before any containment comparison — an
  absolute path that happens to point *inside* the vault root is still refused. That stronger
  reading comes from the implementation, not from this row.

### SCP-015 — A single leading `..` escape is refused

- **Behavior:** A path that steps above the vault root is refused.
- **Trigger:** `../outside.md`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:7` — `single dot-dot escape rejected`

### SCP-016 — A `..` escape reached through a descent is refused

- **Behavior:** A path that descends and then climbs back above the vault root is refused; the check
  is on where the path lands after normalization, not on its leading segment.
- **Trigger:** `Projects/../../outside.md`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:8` — `nested dot-dot escape rejected`
- **Note:** With SCP-015 and SCP-012 this fixes the rule as post-normalization containment. A naive
  "starts with `..`" check passes SCP-015 and fails here — this is the row that catches it.

### SCP-017 — An empty or whitespace-only path is refused

- **Behavior:** A path that is empty or consists only of whitespace is refused rather than treated
  as the vault root.
- **Trigger:** `"   "`.
- **Kind:** `semantic`
- **Source:** `tests/cases/path-cases.json:9` — `whitespace-only path rejected`
- **Note:** The implementation refuses null/empty by the same test, so "" is refused too, though the
  JSON only carries the whitespace row.

### SCP-018 — Containment is measured against the vault root with a separator boundary

- **Behavior:** A resolved path counts as inside the vault only if it is the vault root itself or
  lies beneath it at a directory boundary; a sibling directory whose name merely begins with the
  vault root's name is outside.
- **Trigger:** Any resolution; the boundary is what SCP-015/SCP-016 fail against.
- **Kind:** `semantic`
- **Source:** `Public/Config.ps1:128` (implementation — read to disambiguate the "valid"/"invalid"
  rows above, which assert only throw-or-not)
- **Note:** **No test row exercises the sibling-vault-root case directly** (a root of `C:\vault` and
  a path resolving into `C:\vault-other`). The implementation guards it explicitly with a trailing
  separator; the old suite does not. Harvested because it is the same class of hole the scope-side
  rows SCP-021/SCP-022 exist to close, and its absence on the path side looks like an oversight in
  the old suite rather than an intended difference.

### SCP-019 — Comparison against the vault root is case-insensitive

- **Behavior:** The containment comparison between a resolved path and the vault root ignores case.
- **Trigger:** Any resolution where the caller's casing differs from the stored root's.
- **Kind:** `artifact`
- **Source:** `Public/Config.ps1:129` (implementation)
- **Note:** Marked `artifact` because it is a Windows-filesystem assumption baked into a string
  comparison, not a stated vault contract, and no test asserts it. It matters to a reconciler
  because on a case-sensitive filesystem the same rule would refuse valid paths — or, worse, a
  case-sensitive comparison on Windows would refuse paths the old system accepted.

### SCP-020 — An empty scope-root set means unscoped: every path is in scope

- **Behavior:** A grant with no scope roots for a vault has full access to it; every path in that
  vault is in scope.
- **Trigger:** Path `Areas/Personal/Health.md`, scope roots `[]`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:2` — `unscoped grant allows anything`
- **Note:** The implementation short-circuits *before* normalizing the path, so under an unscoped
  grant even a malformed path reports in-scope. Not asserted; a reimplementation that validates
  first would refuse where the old one allowed. Flagging because it interacts with SCP-035.

### SCP-021 — A path beneath a single-segment scope root is in scope

- **Behavior:** A path lying under a one-segment scope root is in scope.
- **Trigger:** Path `Projects/Widget/Widget.md`, root `Projects`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:3` — `path directly under a single-segment root`

### SCP-022 — A path beneath a multi-segment scope root is in scope

- **Behavior:** A scope root of more than one segment matches paths beneath it at any depth.
- **Trigger:** Path `Projects/Vault-MCP-Server/Corpus/Server-Structure.md`, root
  `Projects/Vault-MCP-Server`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:4` — `path directly under a multi-segment root`

### SCP-023 — A path exactly one segment below the scope root is in scope

- **Behavior:** A file sitting immediately inside the scope root is in scope; containment is not
  restricted to deeper descendants.
- **Trigger:** Path `Projects/Vault-MCP-Server/Tasks.md`, root `Projects/Vault-MCP-Server`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:5` — `path equal to the root plus one segment`
- **Note:** The implementation also admits a path *equal* to the root (equal segment counts, all
  matching), so the scope root folder itself is in scope. No row asserts that; flagged because
  enumeration gating may depend on it.

### SCP-024 — A path under a different top-level folder is out of scope

- **Behavior:** A path sharing no leading segment with any scope root is out of scope.
- **Trigger:** Path `Areas/Personal/Health.md`, root `Projects/Vault-MCP-Server`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:6` — `path under a different top-level folder is out of
  scope`

### SCP-025 — A sibling folder whose name string-prefixes the scope root is out of scope

- **Behavior:** Containment is matched segment by segment, not by string prefix: a sibling folder
  whose name begins with the scope root's last segment is out of scope.
- **Trigger:** Path `Projects/Vault-MCP-Server-Old/Notes.md`, root `Projects/Vault-MCP-Server`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:7` — `sibling folder sharing a string prefix is out of
  scope`
- **Note:** This is the single highest-consequence row in the file. A string-prefix implementation
  passes every other in-scope row and leaks here. Pair with SCP-026.

### SCP-026 — A note whose name string-prefixes the scope root is out of scope

- **Behavior:** A *file* whose name begins with the scope root's final segment, sitting beside the
  scope root rather than inside it, is out of scope.
- **Trigger:** Path `Projects/Vault-MCP-Server.md`, root `Projects/Vault-MCP-Server`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:8` — `note whose name string-prefixes the root but sits
  outside it is out of scope`
- **Note:** Distinct from SCP-025 in that the extension (`.md`) is the only difference from the root
  itself — a prefix check that appends a separator before comparing still catches this one, but a
  raw `StartsWith` does not.

### SCP-027 — A path matching any one of several scope roots is in scope

- **Behavior:** Multiple scope roots are disjunctive: matching any one of them puts the path in
  scope.
- **Trigger:** Path `Areas/Homelab/Servers.md`, roots `["Projects/Vault-MCP-Server",
  "Areas/Homelab"]`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:9` — `path matches when any of several roots contains
  it`
- **Note:** The matching root is the second in the list, so ordering is covered too.

### SCP-028 — A path matching none of several scope roots is out of scope

- **Behavior:** A path outside every configured scope root is out of scope.
- **Trigger:** Path `Areas/Personal/Health.md`, roots `["Projects/Vault-MCP-Server",
  "Areas/Homelab"]`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:10` — `path matches none of several roots`

### SCP-029 — Scope segment matching is case-insensitive

- **Behavior:** A path whose segments differ from the scope root only in case is in scope.
- **Trigger:** Path `projects/WIDGET/widget.md`, root `Projects/Widget`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:11` — `case-insensitive segment match`
- **Note:** Every segment differs in case, including the filename. Case-insensitivity is asserted as
  a *containment* rule here, and separately as a *vault-name* rule (SCP-003) and an *op-class* rule
  (SCP-049). The normalization itself is case-*preserving* — only the comparison ignores case.

### SCP-030 — A backslash-separated path is normalized identically for scope matching

- **Behavior:** A candidate path written with backslashes is normalized to the same segments as the
  forward-slash spelling before scope matching.
- **Trigger:** Path `Projects\Widget\Widget.md`, root `Projects/Widget`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:12` — `backslash-separated path normalizes the same as
  forward-slash`

### SCP-031 — A scope root containing an interior `..` that stays inside the vault normalizes cleanly

- **Behavior:** Scope roots are normalized like paths: a root written with a `..` that resolves back
  inside the vault matches as its normalized form.
- **Trigger:** Path `Projects/Widget/Widget.md`, root `Projects/Foo/../Widget`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:13` — `root with a trailing dot-dot that stays inside
  the vault normalizes cleanly`
- **Note:** The scope root is subjected to the same normalization as the candidate path — this is
  the row that says so.

### SCP-032 — A scope root that escapes the vault makes the query unscopeable, not merely unmatched

- **Behavior:** A scope root that climbs above the vault root is not a root that matches nothing; it
  is malformed, and the containment query fails.
- **Trigger:** Path `Projects/Widget/Widget.md`, root `../outside`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:14` — `root that escapes the vault is unscopeable`
- **Note:** Refusing-by-error rather than returning false is deliberate. A suite that expects
  `outOfScope` here has weakened the boundary into a silent deny, which hides a misconfigured grant.

### SCP-033 — A rooted (absolute) scope root makes the query unscopeable

- **Behavior:** An absolute path used as a scope root is malformed and the containment query fails.
- **Trigger:** Path `Projects/Widget/Widget.md`, root `C:/Windows`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:15` — `rooted root path is unscopeable`

### SCP-034 — An empty-string scope root makes the query unscopeable

- **Behavior:** An empty string as a scope root is malformed and the query fails — it does not mean
  "the vault root" and does not mean "unscoped".
- **Trigger:** Path `Projects/Widget/Widget.md`, root `[""]`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:16` — `empty-string root is unscopeable`
- **Note:** Sharp contrast with SCP-020, where an *empty list* means fully unscoped. A list
  containing an empty string is the opposite outcome from an empty list. Also note the
  implementation refuses a root that normalizes away to nothing (e.g. `"."` or `"Projects/.."`) as
  "resolves to the vault root" — asserted for `""` only.

### SCP-035 — One malformed scope root fails the whole query even when another root would match

- **Behavior:** Every scope root is normalized before any match is attempted, so a single malformed
  root makes the containment query fail regardless of whether a well-formed root would have admitted
  the path.
- **Trigger:** Path `Projects/Widget/Widget.md`, roots `["Projects/Widget", "../outside"]` — the
  first root matches the path.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:17` — `one malformed root among otherwise-matching roots
  still errors`
- **Note:** The most implementation-constraining row in the file: it forbids the natural
  short-circuit of returning true on the first matching root. A lazy implementation passes every
  other row and fails only here. Likely to be missing from a spec-derived suite, since the rule only
  becomes visible when you think about evaluation order.

### SCP-036 — An explicit empty scope entry for a vault means unscoped for that vault

- **Behavior:** A vault whose grant entry is an explicit empty set has full access to that vault,
  even though another vault in the same grant is scoped.
- **Trigger:** Vault `test`, path `Areas/Personal/Health.md`, map `{ primary: ["Projects"], test: []
  }`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:18` — `an explicit empty scope entry for a vault means
  fully unscoped, even though another vault has a scope root configured`

### SCP-037 — A scope root restricts the vault it is keyed to

- **Behavior:** The control case for isolation: a scope root configured for a vault does restrict
  paths in that same vault.
- **Trigger:** Vault `primary`, path `Areas/Personal/Health.md`, map `{ primary: ["Projects"] }`.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:19` — `a scope root configured for one vault still
  restricts that same vault directly`
- **Note:** Present so that SCP-036 and SCP-038 cannot be satisfied by an implementation that
  ignores scope roots entirely.

### SCP-038 — A scope root for one vault does not leak into another vault's own scope

- **Behavior:** Only the entry keyed to the target vault is consulted; a root configured for a
  different vault neither grants nor extends access in this one.
- **Trigger:** Vault `test`, path `Projects/Widget/Widget.md`, map `{ primary: ["Projects/Widget"],
  test: ["Areas/Homelab"] }` — the path would be in scope under `primary`'s root.
- **Kind:** `semantic`
- **Source:** `tests/cases/scope-cases.json:20` — `a scope root configured for one vault does not
  leak into a second vault's own (different) scope root`

### SCP-039 — A null grant scope map means unscoped for every vault

- **Behavior:** The absence of any grant scope map at all means no restriction is in play; the
  resolved root list for any vault is empty.
- **Trigger:** Resolving scope roots for vault `test` from a null map.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:14` — `returns an empty array when ScopeRoots is null`
- **Note:** Represents a direct, no-grant caller. Distinct from SCP-040, where a map exists but
  omits the vault.

### SCP-040 — A non-null grant map with no entry for the target vault is an invariant violation

- **Behavior:** When a grant scope map exists but has no entry for the vault being accessed, that is
  a configuration invariant violation and fails loudly — it is **not** treated as unscoped and not
  treated as fully denied.
- **Trigger:** Resolving scope roots for vault `test` from map `{ scratch: ["Projects/Widget"] }`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:18` — `throws when a non-null map has no entry for the vault`
- **Note:** The invariant is that every vault a grant can access carries an explicit entry, with an
  empty set meaning full access. Silently treating a missing entry as unscoped is a privilege
  escalation and silently treating it as fully scoped-out is a denial — this case forbids both.
  Strong candidate for being missing from a spec-derived suite, since it asserts a state that "should
  be impossible" for a well-formed grant.

### SCP-041 — Scope-map lookup is by vault name, case-insensitively

- **Behavior:** The grant's per-vault map is keyed by vault name and looked up without regard to
  case; the roots come back as a flat list for that vault.
- **Trigger:** Resolving scope roots for vault `TEST` from map `{ test: ["Projects/Widget"] }`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:22` — `returns the flat root list for the matching vault, matched
  case-insensitively`

### SCP-042 — Resolving one vault's roots yields only that vault's roots

- **Behavior:** Resolving a grant map for a vault returns exactly that vault's roots and no other
  vault's, asserted both by equality and by explicit non-containment.
- **Trigger:** Resolving vault `test` from map `{ test: ["Projects/Widget"], scratch:
  ["Areas/Homelab"] }`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:26` — `does not leak a different vault's roots into the resolved
  vault`

### SCP-043 — An in-scope path passes the pre-operation gate

- **Behavior:** The gate applied before a read, write, or enumeration admits a path lying under the
  grant's scope roots.
- **Trigger:** Path `Projects/Widget/Widget.md` with roots `["Projects/Widget"]`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:34` — `allows an in-scope path`

### SCP-044 — An out-of-scope path is refused at the pre-operation gate

- **Behavior:** The pre-operation gate refuses a path outside the grant's scope roots, and the
  refusal identifies it as being outside the grant's scope (as opposed to a path-escape or an
  unknown-vault refusal).
- **Trigger:** Path `Resources/Gizmo-Reference.md` with roots `["Projects/Widget"]`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:38` — `throws on an out-of-scope path`
- **Note:** The implementation's message names the vault as well as the path.

### SCP-045 — The `.` enumeration-gate path is always permitted, even under a scoped grant

- **Behavior:** The literal path `.` gates the listing operation itself rather than any one note and
  is always admitted, even when the grant is scoped; per-note scope filtering happens after listing
  instead.
- **Trigger:** Gating path `.` with roots `["Projects/Widget"]`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:42` — `always allows the "." enumeration-gate path, even when scoped`
- **Note:** Necessary because normalization refuses `.` outright (it resolves to the vault root — see
  SCP-034's note), so without this special case a scoped grant could not enumerate at all. This is a
  designed carve-out in the containment boundary and the kind of thing a redesign is likely to spell
  differently or omit; a reconciler should check that whatever replaces it cannot be reached with a
  path that is not a listing.

### SCP-046 — An empty scope-root list makes the pre-operation gate a no-op

- **Behavior:** With no scope roots for the vault, the pre-operation gate admits any path.
- **Trigger:** Path `Resources/Gizmo-Reference.md` with roots `[]`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:46` — `allows anything when ScopeRoots is empty (unscoped)`

### SCP-047 — An operation passes the op-class gate when the grant holds the required class

- **Behavior:** An operation requiring an elevated capability proceeds when the calling grant's
  capability set contains it.
- **Trigger:** Granted classes `['content:read','content:write']`, required `content:write`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:64` — `passes when the injected granted set holds the required op
  class`
- **Note:** The gate exists so a tool admitted on a lower capability floor can still require a
  higher class on a branch that mutates — enforcement in-script rather than at tool-listing time.

### SCP-048 — Lacking the required op class refuses with a stable `insufficient_capability` code

- **Behavior:** An operation whose required capability the grant does not hold is refused with a
  structured error carrying the stable code `insufficient_capability` and a human-facing message
  naming the missing class.
- **Trigger:** Granted classes `['content:read']`, required `content:write`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:69` — `denies with an insufficient_capability JSON error when the
  injected set lacks the class`
- **Note:** The test deliberately parses the payload and branches on the code rather than
  substring-matching, so the code is the contract and the message is not. The spelling of the code
  is the portable part.

### SCP-049 — Absent host capability context degrades open

- **Behavior:** When no capability context was supplied at all — a standalone run, or a direct call
  with no host — the capability gate does not fire and the operation proceeds.
- **Trigger:** No injected capability set whatsoever; required `content:write`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:80` — `degrades OPEN when no host context was injected at all (absent
  global)`
- **Note:** Deliberate openness, mirroring how a null scope map means "no restriction in play"
  (SCP-039). Whether this survives a redesign is a real question — it is a fail-open on the
  containment boundary — but the old behavior is unambiguous and the test comments defend it at
  length. Harvested as `semantic` because it is a contract about how a no-host caller is treated,
  not a PowerShell detail.

### SCP-050 — An injected-but-empty capability set denies everything

- **Behavior:** A capability set that was supplied but is empty is a real, authenticated grant that
  holds nothing, and it must deny every required class — the opposite outcome from absent context.
- **Trigger:** Injected capability set `[]`, required `content:write`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:92` — `DENIES on an injected-but-empty set (authenticated "holds
  nothing", distinct from absent)`
- **Note:** Pairs with SCP-049 and is the reason that case is stated separately. An implementation
  that cannot distinguish "no context" from "empty context" — for instance by normalizing both to an
  empty collection — either fails this case or fails SCP-049. Very likely to be missing from a
  spec-derived suite.

### SCP-051 — A capability refusal carries no data member when none was supplied

- **Behavior:** A capability refusal raised without supplemental context contains exactly a code and
  a message and no data member at all.
- **Trigger:** Granted `['content:read']`, required `content:write`, no supplemental data.
- **Kind:** `artifact`
- **Source:** `Scope.Tests.ps1:100` — `omits data entirely when -Data is not supplied, byte-for-byte
  as before it existed`
- **Note:** Asserted byte-for-byte against a literal JSON string, so it pins message wording and
  member order as well as the absence of `data`. Marked `artifact` for the exact serialization; the
  portable part — that an absent data member is omitted rather than emitted as null — is semantic
  and worth keeping.

### SCP-052 — A capability refusal can carry structured retry material

- **Behavior:** A capability refusal can carry a structured data payload attached at the point of
  refusal — for a batched operation, which item failed and which class it needed.
- **Trigger:** Required `agentic:write` with data `{ opIndex: 2, requiredClass: 'agentic:write' }`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:109` — `attaches structured data to the refusal when -Data is
  supplied`
- **Note:** The comment states the reason it must be attachable at the throw: the error convention
  forbids a caller catching this refusal and re-shaping it to add context. That constraint is a
  cross-cutting rule a reconciler should chase into the error-handling area.

### SCP-053 — Op-class names match case-insensitively

- **Behavior:** Capability class names are compared without regard to case.
- **Trigger:** Granted `['content:write']`, required `CONTENT:WRITE`.
- **Kind:** `semantic`
- **Source:** `Scope.Tests.ps1:118` — `matches op classes case-insensitively (by design)`
- **Note:** The comment states this is a system-wide rule the capability registry also follows, and
  that a change to case-sensitive matching must fail here first.

### SCP-054 — A note reads by vault-relative path

- **Behavior:** A note is read in full by its vault-relative path, returning its content.
- **Trigger:** Reading `Resources/Gizmo-Reference.md`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:14` — `reads a note by vault-relative path`
- **Note:** Asserted by matching a sentinel word (`quorvium`) present in the fixture note. Content
  matching against that sentinel recurs across this area's read cases.

### SCP-055 — Reading a missing note fails

- **Behavior:** Reading a note that does not exist fails rather than returning empty content.
- **Trigger:** Reading `Missing.md`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:18` — `throws on a missing note`
- **Note:** No error code or message is asserted — only that it fails. Contrast SCP-058, which
  classifies such a failure for skip-missing purposes.

### SCP-056 — A read of an in-scope note under a scoped grant succeeds

- **Behavior:** A read proceeds normally when the note lies under the grant's scope roots for that
  vault.
- **Trigger:** Reading `Projects/Widget/Tasks.md` with map `{ test: ["Projects/Widget"] }`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:22` — `reads an in-scope note`
- **Note:** Exercises the full composition — vault resolution, scope-map resolution, gate, path
  resolution — through the read operation rather than through the primitives.

### SCP-057 — A read of an out-of-scope note is refused

- **Behavior:** A read of a note outside the grant's scope roots is refused.
- **Trigger:** Reading `Resources/Gizmo-Reference.md` with map `{ test: ["Projects/Widget"] }`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:26` — `throws on an out-of-scope note`
- **Note:** The scope check runs on the caller-supplied relative path *before* path resolution, so a
  path both out of scope and malformed is refused as out-of-scope. Not asserted; from the
  implementation.

### SCP-058 — A scope root on another vault does not restrict the vault being read

- **Behavior:** Reading from a vault whose grant entry is empty succeeds even though a different
  vault in the same grant carries a scope root.
- **Trigger:** Reading `Resources/Gizmo-Reference.md` from vault `scratch` with map `{ scratch: [],
  test: ["Projects/Widget"] }`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:30` — `a scope root configured for a different vault does not restrict
  this vault`
- **Note:** The end-to-end form of SCP-036/SCP-038, asserted through a real read rather than the
  containment primitive.

### SCP-059 — A note is written as UTF-8 with no byte-order mark

- **Behavior:** Written note content is encoded UTF-8 and the file begins with the content's own
  first bytes, never a byte-order mark.
- **Trigger:** Writing a note and inspecting its first three bytes.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:36` — `writes UTF-8 without a BOM`
- **Note:** Asserted at the byte level (`EF BB BF` must not lead). Matters because a BOM corrupts
  frontmatter parsing and shows up in diffs.

### SCP-060 — A write into an existing directory succeeds

- **Behavior:** Writing a note into a directory that already exists creates the file.
- **Trigger:** Writing `Projects/Widget/Inbox/Note-Existing-Dir.md`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:43` — `writes into an existing directory`
- **Note:** The control case for SCP-061 — it establishes that the refusal there is about the
  missing parent and not about nesting depth.

### SCP-061 — A write whose parent directory is missing is refused, creating nothing

- **Behavior:** A write never brings a directory into existence: when the target's parent directory
  does not exist the write is refused, and no part of the missing path is created as a side effect.
- **Trigger:** Writing `New-Folder/Nested/Note-Deep.md` where `New-Folder` does not exist.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:48` — `refuses a write whose parent directory does not exist, creating
  nothing`
- **Note:** Two assertions: the write fails, **and** `New-Folder` still does not exist afterward. The
  no-side-effect half is the load-bearing one — an implementation that creates directories and then
  fails passes the first half.

### SCP-062 — The missing-parent refusal is coded `parent_not_found` and names the parent vault-relative

- **Behavior:** The missing-parent refusal carries the stable code `parent_not_found`, names the
  missing parent directory in its message, and carries structured data giving both the missing
  parent and the path that was attempted — all expressed vault-relative with forward slashes, never
  as an absolute path leaking the vault root.
- **Trigger:** Writing `New-Folder/Nested/Note-Deep.md`; expected parent `New-Folder/Nested`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:53` — `raises parent_not_found naming the missing parent vault-relative`
- **Note:** The code and the named parent are described as the contract a create tool maps straight
  through. Note the parent named is `New-Folder/Nested` — the *immediate* parent — even though the
  outermost missing directory is `New-Folder`. See SCP-063. The forward-slash and vault-relative
  requirements are separately valuable: the old implementation strips the absolute root and
  re-slashes deliberately.

### SCP-063 — The immediate parent is named even when only the leaf directory is missing

- **Behavior:** The missing-parent refusal always names the target's immediate parent, whether that
  is the only missing directory or the innermost of several.
- **Trigger:** Writing `Projects/Widget/Absent/Note-Leaf.md` where `Projects/Widget` exists but
  `Absent` does not; expected parent `Projects/Widget/Absent`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:68` — `names the immediate parent even when only the leaf directory is
  missing`
- **Note:** With SCP-062 this fixes the rule as "always the immediate parent", never "the outermost
  missing ancestor" and never "the deepest existing ancestor". Two cases are needed to pin it
  because either single case is satisfiable by the wrong rule.

### SCP-064 — A file missing at read time classifies as nothing-to-read

- **Behavior:** A read failure caused by the file not existing is classified as "there was nothing to
  read" — a skippable condition — rather than a real failure.
- **Trigger:** Reading a path with no file at it.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:88` — `treats a file missing at read time as nothing-to-read`
- **Note:** This classifier is the shared definition behind skip-missing in every
  enumerate-then-read operation, so one definition governs them all. Its whole purpose is the race
  between enumeration and read.

### SCP-065 — A missing parent directory at read time classifies as nothing-to-read

- **Behavior:** A read failure caused by a missing *directory* on the path, not just a missing file,
  is also classified as nothing-to-read.
- **Trigger:** Reading `no-such-dir/note.md` under the temp root.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:93` — `treats a missing parent directory as nothing-to-read`

### SCP-066 — A not-found surfaced from a wrapped call is still classified as nothing-to-read

- **Behavior:** A not-found failure that arrives wrapped inside another exception — as happens when
  the read goes through a platform API rather than the shell's own file cmdlet — is unwrapped and
  still classified as nothing-to-read.
- **Trigger:** Reading a non-existent file through a direct .NET call, producing a
  `MethodInvocationException` wrapping `FileNotFoundException`.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:98` — `unwraps a not-found raised from a .NET call, not just a cmdlet`
- **Note:** Marked `semantic` despite naming PowerShell exception types, because the portable rule is
  "inspect the whole causal chain, not the outermost error". The test says an outer-type-only check
  would hard-fail the whole operation. The wrapper type itself is an artifact; the rule is not.

### SCP-067 — A sharing violation is a real failure, not nothing-to-read

- **Behavior:** A read that fails because the file is locked by another process is a genuine failure
  and must not be silently skipped.
- **Trigger:** Reading a file held open with no sharing permitted.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:106` — `does NOT treat a sharing violation as nothing-to-read`
- **Note:** The negative half of the classifier matters more than the positive half: over-broad
  classification turns real I/O failures into silently skipped notes, which looks like a
  successfully completed operation over an incomplete vault.

### SCP-068 — Access-denied is a real failure even though it arrives wrapped

- **Behavior:** An access-denied failure is a genuine failure, even when it arrives wrapped in the
  same outer exception type that a wrapped not-found does.
- **Trigger:** Reading a directory as if it were a file through a direct .NET call.
- **Kind:** `semantic`
- **Source:** `Io.Tests.ps1:117` — `does NOT treat access-denied as nothing-to-read, even though it
  is also wrapped`
- **Note:** Explicitly guards SCP-066 against over-broadening — the unwrapping must inspect inner
  *types*, not merely conclude "wrapped, therefore missing". Together SCP-066 and SCP-068 are a pair
  neither of which is safe alone.

### SCP-069 — Fixture: two vault copies, injected hidden and duplicate notes, and a default vault

- **Behavior:** The module suites run against a temp root holding two independent copies of the
  sample vault named `test` and `scratch`, into which `.obsidian/junk.md`, `A/Dupe.md` and
  `B/Dupe.md` are injected, with a configuration naming `test` as default.
- **Trigger:** Suite setup, before any test in this area.
- **Kind:** `fixture`
- **Source:** `TestHelpers.ps1:17` — `Initialize-VaultAccessTestFixture`
- **Note:** `scratch` exists so mutating tests do not disturb `test`; the writes in SCP-059 through
  SCP-063 all target it. `.obsidian/junk.md` and the two `Dupe.md` files shape enumeration and
  link-resolution cases in other areas, not this one. The whole root is deleted afterward. Two
  scope-case rows name a vault `primary` that the fixture never creates — those rows exercise
  grant-map lookup only and touch no disk.

### SCP-070 — Fixture: configuration mutation is restored after each config test

- **Behavior:** Tests that swap in an alternative vault configuration restore the suite's main
  configuration afterward, so a mutated configuration cannot leak into later tests or files.
- **Trigger:** Any test loading an alternative configuration.
- **Kind:** `fixture`
- **Source:** `SharedCases.Tests.ps1:36` — `AfterEach`; `Config.Tests.ps1:34` (inline restore)
- **Note:** Worth recording because the configuration is process-global state in the old design, and
  the two files restore it by different means — a shared `AfterEach` in one, an inline call at the
  end of the `It` in the other. The inline form does not restore if the assertion above it fails.
  Suspected latent fragility rather than an asserted behavior; harvested per the "flag, do not drop"
  rule.

### SCP-071 — Fixture: injected host capability context is always cleared between tests

- **Behavior:** The ambient capability context that the host injects is cleared after every test that
  sets it, so injected authority can never leak into a later test, block, or file.
- **Trigger:** Any test setting the ambient capability set.
- **Kind:** `fixture`
- **Source:** `Scope.Tests.ps1:60` — `AfterEach`
- **Note:** Load-bearing for SCP-049, which asserts the absent-context posture and would pass
  spuriously if a previous test's context survived. The test explicitly re-clears rather than
  relying on ordering, which says the authors considered this a real hazard.
