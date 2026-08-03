# Plan

The working plan for this build, seeded from the planning session that produced this bundle. It is this build's own plan, held by the agent and the operator together, and it is expected to change.

## How to use it

- **Read it at the start of a session** and say which step is in play before working. Read `Brief-Vault-Access-Build.md` first if the session has not already.
- **Work one step at a time**, in order, unless the operator redirects. The sequence is deliberate.
- **Check a step off when its work is done and verified**, not when it is written.
- **Keep it current.** Expand a step into sub-steps as it comes into focus, add steps the build discovers, drop steps that turn out to be unnecessary, and re-sequence when the order proves wrong. A step that no longer describes the work is worse than no step.
- **Sweep detail out once it has a home.** A step may carry inputs - a finding, a constraint, something noticed early - while nothing else can hold them yet. Working that step is when they migrate into the design document, the decision, or the code that now owns them, and come out of here. This file holds what was completed and what is still ahead; a detail left behind after its real home exists is a second copy nobody updates.
- **Comments describe the code as it is, never how it got there.** A comment written during the build says what is true now and why, and carries no history of the build itself - no "this used to", "no longer", "moved here from", "a later batch", "as of this step". The reasoning is worth keeping and the chronology is not: it is stale the moment the next step lands, and a reader cannot tell a stale narration from a live constraint. Where a change makes an existing comment describe a world that no longer exists, rewrite it in the same commit rather than leaving it for a sweep. Git history holds the chronology; the code holds the present tense.
- **Propose plan changes rather than making them silently.** The operator is the other holder of this plan; a re-sequencing or a dropped step is a decision they should see.
- **This plan does not overrule the specs.** If working a step surfaces a contradiction with them, that is an escalation under the rules in `../CLAUDE.md`, not a plan edit. The plan says what to do next; the specs say what is correct.

Its neighbours do different jobs: the design documents under `design/` record what was chosen, `Questions.md` carries what the bundle cannot answer, `Sidequests.md` holds agreed work that does not block the build. This file only tracks what is left to do.

## Prework

Runs before any build work begins.

- [x] Carry the spec bundle in - seven specs into `docs/spec/`, the brief in `docs/` - vault frontmatter stripped and cross-references rewritten as file links
- [x] Verify from inside this repo that the knowledge vault is unreachable - no MCP registration, no additional-directory grant, no path out
- [x] Flush out this plan with the operator before any build work begins. What is below is a seed: the phases, their order, what each produces, and where a phase ends are settled together rather than assumed. Bring proposals rather than questions
- [x] Sweep the retained code before design reads it - comments instructing an agent to mirror the deleted PowerShell implementation, citations to documents this repo cannot reach, rationale that is no longer true, and test cases whose subject was the deleted tooling. Rewrite to keep the reasoning and drop the pointer; no behaviour changes, no renaming, no restructuring. Dead code is listed rather than removed, since some of it is spec-named

## Design

Design runs ahead of the code it governs and lands in its own documents under `docs/design/`, split into logical areas rather than accumulated in one. The specs stay the requirement; a design doc says how this build meets it. A design that lands as code is a design nobody reviewed.

The phase opens with two steps that touch code rather than documents. They are groundwork, not design landing early: they relocate code that is already correct and decide nothing the design decides. Their point is that design should reason about the repository as it will be, and that `VaultShared`'s boundaries should be compile errors before anything is designed against them rather than a rule someone remembers.

**A design step is done when its document is written and says what it settles.** The rest of the plan's steps check themselves - the code compiles, the suite is green - and a document has no such test, so review is a step of its own at the end of the phase rather than a gate on each. Bring each document and say what it settles as it lands; the operator reads it then or at the review, and nothing is built on the phase until the review step is done.

- [x] Scaffold the projects - `VaultCli`, `VaultShared.Tests`, `VaultCli.Tests` - so `VaultShared`'s two hard boundaries (no web framework, no `Microsoft.PowerShell.SDK`) are enforced by the compiler from the first file, rather than being a claim about a library nothing consumes
- [x] Migrate the unambiguous half of the retained shared code into `VaultShared` - `VaultScope` and the scope primitives, vault resolution, path policy, the op-class registry, the grant model, and `NativeToolArguments`. Moves, not redesigns; a behaviour change on the way is a separate proposal
- [x] Define the design-document convention, before the first one is written. It lands in `docs/design/README.md`, pointed at from `Brief-Vault-Access-Build.md:Working Files`
- [x] Design the core access layer, resolved-path type first - every later choice depends on whether an out-of-scope path is representable
- [x] Design the frontmatter surface - the block grammar and facet split, the operation vocabulary, outcome-based authority classification, the validator, and the registry's loading and failure behaviour. Ahead of the tool vocabulary, because `edit`'s argument surface is a projection of the operation vocabulary and its advertised schema is generated from the declaration
- [x] Design the tool vocabulary as one surface: argument shapes, return tiers, error vocabulary, the preview convention, the write-class envelope. The largest single task here, and its own phase
- [x] Design the front-end projection - one tool declaration, two front-ends, and what each may vary, and the shared execute path that checks the op-class floor, takes the mutex lock, and constructs the per-call access object
- [ ] Operator review of the design, as one pass over the four documents together. The phase's own check: each document was written against its area, and this is where they are read as one design and either signed off or sent back. Nothing in Build starts until it is done
  - Read as a set rather than one at a time. Most of what a review can catch here is a seam - a subject two documents each think the other owns, a type one names and another contradicts, a deferral pointing at a document that no longer takes it - and none of those are visible from inside a single document
  - A document sent back is revised and re-read at the same bar. Where the review changes a decision that a later document built on, the later one is revised in the same pass

## Build

**The specs and the design documents are both inputs to every step, and neither restates the other.** A design document says what shape this build takes where the specs left the shape open. Where a spec already bridges to implementation, no design document repeats it. A subject missing from a design document is therefore not a gap; it is a subject the specs settled. Work each step from its area's design document together with the spec sections that document's coverage index names. Where the two disagree, the specs win and the design document is the defect.

- [ ] Build the core access layer
  - Input: `PathPolicy.IsPathInReadScope` comes out with this step. The design computes a call's effective read scope once, when the access object is constructed, rather than per candidate - a whole-vault enumeration is exactly where rebuilding the union per path costs. `EffectiveReadScope` stays and is what computes it
  - Input: `VaultConfig.Resolve` and `VaultConfig.PathPolicyFor` come out too, and their tests follow the behaviour into the layer. Resolution answers a three-case result and produces the layer's own vault type, so configuration holds which vaults exist and the layer decides what a usable one is
- [ ] Build the frontmatter surface - the substrate's single write path and the registry's loader, asserted by the conformance suite against a synthetic declaration
  - `OpClassRegistry.IsValid` has no production caller and is not leftover: the load-time cross-file check this step owes - an op class the op-class configuration does not define - is a membership query, and that is the member shaped for it
  - Retained `PathPolicyParser` refuses `mutation_requires` and `rule` at load today, and retained `VaultConfigLoader` refuses `frontmatter_registry` the same way. All three are accepted and carried here, an approved change to retained code: the write path consults policy beside the authority assertion, and move and delete consult it at their own validate phases. The refusals are guards over specified-but-unbuilt surfaces, not dead config - each is lifted only when its consumer lands
  - Build the failure behaviour with the load, not after it. A missing or unparseable declaration fails closed on frontmatter writes, leaves reads, move and delete untouched, keeps `edit` advertised with its variant branches simply absent, and surfaces through health state. Nothing on the happy path forces any of it
- [ ] Build the tool infrastructure - the declaration both front-ends project from, the shared execute path that checks the op-class floor and takes the mutex lock, and the transport-neutral result and error representation
  - The three retained natives serialize camelCase results - `isFatallyDegraded`, `scopeRoots`, `requiredOpClass` - and the design pins one convention in both directions, so they convert to snake_case here with the neutral result they return through. An approved change to retained code, and a breaking one for any existing caller of `health_check`, `mint_token` or `request_elevation`. `NativeToolSchemaTests` pins part of what moves
  - The floor check moves here out of `ScriptToolDispatcher`, and the vault-membership check is widened to reach compiled tools - today its predicate is typed on `ScriptTool` and the native branch returns before it. Both are approved changes to retained code
- [ ] Build `read`, `glob`, and `grep`
  - Restore the skipped read probe in `e2e-smoke.ps1`, written against `read`'s own argument surface rather than the deleted script's. The two vault-gate probes beside it still pass, since that gate fires on the body peek before dispatch
  - The markdown restriction on note enumeration filters the engine's results and is never appended to the caller's pattern. `notes/**` with `.md` appended becomes `notes/**.md`, which ripgrep accepts and degrades to a single `*`, silently dropping nested notes. A positive `--iglob` also overrides `--type`, so the restriction cannot ride that flag either. This moves to a comment at the filter site when the code lands
- [ ] Build `edit` over the frontmatter surface
  - Test fixture capability is parked here waiting for the first mutating tool, and reads as dead until then: `ProgramHostFixtureBase.AssertMutationSandboxed` and its `CopyDirectory`, `TestVaultCleanGuard`, and the `vaultMcpVaultsJsonPath` parameter of `ConfigureEnvironment`. One trap in restoring them - `AssertMutationSandboxed` asserts on `VAULT_MCP_VAULTS_JSON`, which no fixture sets any more, so it throws until a fixture passes that parameter
  - Restore the skipped `ToolCallNamingInScopeVault_IsNotRejectedByTheGate` once a compiled tool takes a vault argument. It asserts the gate's admitting side, which cannot be told from a missing tool while none exists
  - `BearerGrantMiddleware` logs every call's full arguments verbatim to an unrotated file. Harmless for the read tools; `edit` and `create` carry note bodies and frontmatter operations, so every write's content would land in `logs/`. Propose the middleware change before the first write tool ships, not after
- [ ] Verify the read-class scope floor end to end. This is the earliest point both halves can be proved: the check needs a write refused as well as a read admitted, so it waits for the first mutating tool rather than the first read. The floor is retained code with unit coverage and no end-to-end proof: `test-vault` declares `Resources/Vault/Guides` `always_readable` in `vaults.json`, so a grant scoped to `Projects/Widget` alone must read and glob both guide docs, must be refused writing either, and must reach nothing else outside its roots. A failure here is the floor's, not the tool's - it means an operation reached the scope check on a path that should have been unioned in, or a write path consulted the floor when it must not
- [ ] Build wikilink maintenance as one shared area - repair on relocation, pinning on creation, the census on removal - over the layer's single grammar and ranking rule. Ahead of the three tools that consume it, so it is not an artifact of whichever was built first
- [ ] Build `create`, `move`, and `delete`
- [ ] Build the MCP front-end as a projection of the tool declarations
- [ ] Build the CLI front-end from the same declarations
- [ ] Sweep the code for comments narrating the build rather than describing the code. Every comment should read as a statement about the current design, with the reasoning kept and the chronology dropped. Rewrite in place; do not simply delete, since most of these carry a real constraint wrapped in a stale story
  - Distinct from the prework sweep, which removed pointers to the deleted PowerShell implementation and to documents this repo cannot reach. This one is about the *history* narrated in comments that are otherwise accurate
  - Two sources feed it. The retained code carries the previous build's work-sequencing - `a later batch`, `this batch`, `the new check this batch adds`, `the health-state redesign exists to eliminate` - which describes an order of work nobody can now observe. This build adds its own each time something moves
  - The shapes to look for: `no longer`, `used to`, `previously`, `formerly`, `originally`, `a later batch`, `this batch`, `moved here from`, and a comment naming a type by its old home. A grep finds most of them; the ones it misses are comments that stayed literally true while their subject moved out from under them
  - Leave alone what is genuinely about runtime sequence rather than build sequence - a re-scan retracting a problem it reported earlier is a live behaviour, not a story about the build
- [ ] Audit test coverage against the specs, then commit the suite
  - Known gaps going in: `ScopePath.Normalize` has no direct test and is reached only through `VaultScope.Admits`; `health_check`'s reporting of skipped vaults is unasserted, the fixture having none; and the non-loopback path is covered at unit level only, the host-level `RemoteIpHeader` override existing but unused by any test
  - The committed fixture has no duplicate basenames, so `Spec-Obsidian-Link-Behavior.md`'s ranking rule cannot be exercised against it. `TestVaultFixture` injects `A/Dupe.md`, `B/Dupe.md` and a dot-directory into its temp copy, which is where those cases live and is why they read as unused

Tests are built with the functionality they cover and split into files by logical area. Coverage derives from the specs and there is nothing else to derive it from.

## Handoff

These run outside this repo and are listed so the build knows where it ends.

- [ ] Semantic harvest, in its own session against the preserved old repo, once the suite is committed
- [ ] Reconcile the harvest against the committed suite - every gap is a case to add and a spec to amend
- [ ] Cutover: this build's files replace the canonical repo's wholesale, at a cut date
