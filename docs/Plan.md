# Plan

The working plan for this build, seeded from the planning session that produced this bundle. It is this build's own plan, held by the agent and the operator together, and it is expected to change.

## How to use it

- **Read it at the start of a session** and say which step is in play before working. Read `Brief-Vault-Access-Build.md` first if the session has not already.
- **Work one step at a time**, in order, unless the operator redirects. The sequence is deliberate.
- **Check a step off when its work is done and verified**, not when it is written.
- **Keep it current.** Expand a step into sub-steps as it comes into focus, add steps the build discovers, drop steps that turn out to be unnecessary, and re-sequence when the order proves wrong. A step that no longer describes the work is worse than no step.
- **Sweep detail out once it has a home.** A step may carry inputs - a finding, a constraint, something noticed early - while nothing else can hold them yet. Working that step is when they migrate into the design document, the decision, or the code that now owns them, and come out of here. This file holds what was completed and what is still ahead; a detail left behind after its real home exists is a second copy nobody updates.
- **Propose plan changes rather than making them silently.** The operator is the other holder of this plan; a re-sequencing or a dropped step is a decision they should see.
- **This plan does not overrule the specs.** If working a step surfaces a contradiction with them, that is an escalation under the rules in `../CLAUDE.md`, not a plan edit. The plan says what to do next; the specs say what is correct.

Its neighbours do different jobs: `Decisions.md` records what was chosen and what lost, `Questions.md` carries what the bundle cannot answer. This file only tracks what is left to do.

## Prework

Runs before any build work begins.

- [x] Carry the spec bundle in - seven specs into `docs/spec/`, the brief in `docs/` - vault frontmatter stripped and cross-references rewritten as file links
- [x] Verify from inside this repo that the knowledge vault is unreachable - no MCP registration, no additional-directory grant, no path out
- [x] Flush out this plan with the operator before any build work begins. What is below is a seed: the phases, their order, what each produces, and where a phase ends are settled together rather than assumed. Bring proposals rather than questions
- [ ] Sweep the retained code before design reads it - comments instructing an agent to mirror the deleted PowerShell implementation, citations to documents this repo cannot reach, rationale that is no longer true, and test cases whose subject was the deleted tooling. Rewrite to keep the reasoning and drop the pointer; no behaviour changes, no renaming, no restructuring. Dead code is listed rather than removed, since some of it is spec-named

## Design

Design runs ahead of the code it governs and lands in its own documents under `docs/design/`, split into logical areas rather than accumulated in one. The specs stay the requirement; a design doc says how this build meets it. A design that lands as code is a design nobody reviewed.

- [ ] Design the core access layer, resolved-path type first - every later choice depends on whether an out-of-scope path is representable
  - Input: the retained scope primitives are called, not reimplemented. `NormalizeScopeSegments` and `IsPathInScope` have live callers; `IsPathInReadScope` has none yet and is not leftover - it is the read path's own entry point, and the layer routes reads through it rather than applying the always-readable union itself
- [ ] Design the frontmatter surface - the block grammar and facet split, the operation vocabulary, outcome-based authority classification, the validator, and the registry's loading and failure behaviour. Ahead of the tool vocabulary, because `edit`'s argument surface is a projection of the operation vocabulary and its advertised schema is generated from the declaration
- [ ] Design the tool vocabulary as one surface: argument shapes, return tiers, error vocabulary, the preview convention, the write-class envelope. The largest single task here, and its own phase
- [ ] Design the front-end projection - one tool declaration, two front-ends, and what each may vary
  - Input: the argument half already exists as retained code. `NativeToolArguments` makes one record per tool both the binding target and the source of the advertised schema, is `System.Text.Json` throughout with no MCP type in it, and already pins `snake_case` on the wire - so it reaches `VaultShared` as a move rather than a rewrite
  - Input: `CallToolResult` is the only transport-shaped part of the retained `INativeTool`. Where the neutral result converts, and whether the retained interface is adapted or changed, is the design's call - the retained tools (health, mint, elevation) implement it directly today

## Build

- [ ] Scaffold the projects - `VaultCli`, `VaultShared.Tests`, `VaultCli.Tests` - before any new code is written, so `VaultShared`'s two hard boundaries (no web framework, no `Microsoft.PowerShell.SDK`) are compile-enforced from the first file rather than checked at the end
- [ ] Migrate the retained shared code into `VaultShared` - the scope primitives, vault resolution, the op-class registry, the grant model, and the compiled-tool surface. Moves, not redesigns; a behaviour change on the way is a separate proposal
- [ ] Build the core access layer
- [ ] Build the frontmatter surface - the substrate's single write path and the registry's loader, asserted by the conformance suite against a synthetic declaration
  - Retained `PathPolicy` refuses `mutation_requires` and `rule` at load today. Both are accepted and carried here, an approved change to retained code: the write path consults policy beside the authority assertion, and move and delete consult it at their own validate phases
  - Build the failure behaviour with the load, not after it. A missing or unparseable declaration fails closed on frontmatter writes, leaves reads, move and delete untouched, keeps `edit` advertised with its variant branches simply absent, and surfaces through health state. Nothing on the happy path forces any of it
- [ ] Build the tool infrastructure - the declaration both front-ends project from, the shared execute path that checks the op-class floor and takes the mutex lock, and the transport-neutral result and error representation
- [ ] Build `read`, `glob`, and `grep`
- [ ] Build `edit` over the frontmatter surface
- [ ] Verify the read-class scope floor end to end. This is the earliest point both halves can be proved: the check needs a write refused as well as a read admitted, so it waits for the first mutating tool rather than the first read. The floor is retained code with unit coverage and no end-to-end proof: `test-vault` declares `Resources/Vault/Guides` `always_readable` in `vaults.json`, so a grant scoped to `Projects/Widget` alone must read and glob both guide docs, must be refused writing either, and must reach nothing else outside its roots. A failure here is the floor's, not the tool's - it means an operation reached the scope check on a path that should have been unioned in, or a write path consulted the floor when it must not
- [ ] Build wikilink maintenance as one shared area - repair on relocation, pinning on creation, the census on removal - over the layer's single grammar and ranking rule. Ahead of the three tools that consume it, so it is not an artifact of whichever was built first
- [ ] Build `create`, `move`, and `delete`
- [ ] Build the MCP front-end as a projection of the tool declarations
- [ ] Build the CLI front-end from the same declarations
- [ ] Audit test coverage against the specs, then commit the suite

Tests are built with the functionality they cover and split into files by logical area. Coverage derives from the specs and there is nothing else to derive it from.

## Handoff

These run outside this repo and are listed so the build knows where it ends.

- [ ] Semantic harvest, in its own session against the preserved old repo, once the suite is committed
- [ ] Reconcile the harvest against the committed suite - every gap is a case to add and a spec to amend
- [ ] Cutover: this build's files replace the canonical repo's wholesale, at a cut date
