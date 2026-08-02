# Plan

The working plan for this build, seeded from the planning session that produced this bundle. It is this build's own plan, held by the agent and the operator together, and it is expected to change.

## How to use it

- **Read it at the start of a session** and say which step is in play before working. Read `Brief-Vault-Access-Build.md` first if the session has not already.
- **Work one step at a time**, in order, unless the operator redirects. The sequence is deliberate.
- **Check a step off when its work is done and verified**, not when it is written.
- **Keep it current.** Expand a step into sub-steps as it comes into focus, add steps the build discovers, drop steps that turn out to be unnecessary, and re-sequence when the order proves wrong. A step that no longer describes the work is worse than no step.
- **Propose plan changes rather than making them silently.** The operator is the other holder of this plan; a re-sequencing or a dropped step is a decision they should see.
- **This plan does not overrule the specs.** If working a step surfaces a contradiction with them, that is an escalation under the rules in `../CLAUDE.md`, not a plan edit. The plan says what to do next; the specs say what is correct.

Its neighbours do different jobs: `Decisions.md` records what was chosen and what lost, `Questions.md` carries what the bundle cannot answer. This file only tracks what is left to do.

## Prework

Runs before any build work begins.

- [x] Carry the spec bundle into `spec/` - the brief and seven specs, vault frontmatter stripped and cross-references rewritten as file links
- [ ] Land the remaining grant-model prework: the shared read-root floor, the two-case scope-roots shape, and the maximality rule on rows carrying `*`
- [ ] Verify from inside this repo that the knowledge vault is unreachable - no MCP registration, no additional-directory grant, no path out

## Build

- [ ] Build the core access layer, resolved-path type first - every later choice depends on whether an out-of-scope path is representable
- [ ] Design the tool vocabulary as one surface: argument shapes, return tiers, error vocabulary, the preview convention, the write-class envelope. The largest single task here, and its own phase
- [ ] Build the tool layer over the access layer, with the transport-neutral result and error representation
- [ ] Build the MCP front-end as a projection of the tool declarations
- [ ] Build the CLI front-end from the same declarations
- [ ] Audit test coverage against the specs, then commit the suite

Tests are built with the functionality they cover and split into files by logical area. Coverage derives from the specs and there is nothing else to derive it from.

## Handoff

These run outside this repo and are listed so the build knows where it ends.

- [ ] Semantic harvest, in its own session against the preserved old repo, once the suite is committed
- [ ] Reconcile the harvest against the committed suite - every gap is a case to add and a spec to amend
- [ ] Cutover: this build's files replace the canonical repo's wholesale, at a cut date
