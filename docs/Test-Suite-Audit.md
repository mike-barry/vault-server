# Test Suite Audit

A standalone job for one agent to orchestrate. Everything needed is in this file or reachable from
it. Delete this file when the job is done; nothing links to it and nothing depends on it.

## What this is

The suite is 1576 tests and every one of them passes. That is not the same as every one of them
earning its place. A test that asserts nothing, that pins an implementation detail, that sorts away
the property it looks like it is checking, or that duplicates its neighbour, still shows green and
still has to be read, understood and kept true by everyone who comes after.

Audit the existing tests, cut or repair what does not earn its place, and then write the guide that
stops the same shapes coming back.

## Read first

- `../CLAUDE.md` - the constraints that bind every change in this repository.
- `../conventions/Code-Comments.md` - the writing standard. It governs C# comments, and its register
  is the house style for anything you write.
- `Plan.md` - where the build stands and what is still owed. Say which step you are in when you
  start.

Two constraints from `CLAUDE.md` bear directly on this job and are easy to trip over:

- **Test method names are documentation.** They are not rewritten, however long or oddly phrased. If
  a name is actively wrong - it says the test asserts something it does not - that is a finding to
  raise, not an edit to make quietly.
- **String literals are wire contract.** Refusal messages, error codes, tool `Description` strings
  and schema text are caller-visible and pinned deliberately. A test asserting one is doing its job.

A third: **this repository has no access to the knowledge vault and must not seek one.** Do not look
for documentation of the system being replaced.

## The suite as it stands

Re-verify rather than trust these numbers:

- `dotnet build --no-incremental` - 0 warnings, 0 errors
- `dotnet test` - 1576 passing: 1212 `VaultShared`, 305 `VaultMcpServer`, 59 `VaultCli`

Three projects under `tests/`:

- `VaultShared.Tests` - the access layer, the frontmatter surface, the links layer and all eight
  tools. Subdirectories `Frontmatter/`, `Links/` and `Tools/`, plus the access-layer files at the
  root. Shared fixtures include `ScratchVault.cs`, `TestVaultFixture.cs`, `FixedTimeProvider.cs`,
  `RecordingProblemSink.cs`, `TestOpClasses.cs`.
- `VaultMcpServer.Tests` - dispatch, auth middleware, the script-tool framework, host wiring.
  Fixtures include `DeclaredToolHarness.cs`, `ProgramHostFixtureBase.cs`, `ProgramHostTestCollection.cs`.
- `VaultCli.Tests` - argument binding, the command tree, rendering, fail-closed behaviour.

## Sequencing

This is `Plan.md` step 1 and nothing is queued ahead of it. The code review and the comment sweep
follow it and read what it leaves behind, so leave the suite in a state you would hand to a reviewer.

## What you are looking for

These are the shapes that pass forever while guarding nothing. The list is a starting point, not a
closed set - a shape you find that is not here belongs in the guide at the end.

- **An assertion that cannot fail.** Asserting a value against itself, asserting a freshly
  constructed object has the values just passed to its constructor, asserting a collection is not
  null right after it was built.
- **A property sorted away.** `SearchToolTests` defines `Lines(string text) => [.. text.Split('\n').Order()]`
  and every glob and grep assertion goes through it, so a change that started sorting results would
  satisfy the whole file. This is the known instance. Look for others: any helper that normalizes,
  orders, trims or lowercases on the way to an assertion is a candidate, and each one has to be
  judged on whether the normalization is the point or is hiding the point.
- **Containment where an exact set is meant.** `Assert.Contains` over a result set passes when the
  set grows. Where the test's name or intent says "and nothing else", containment does not assert it.
- **Duplicates.** Two tests pinning one property. Both have to be read and both have to be kept true,
  and the second buys nothing. Keep the clearer one and cut the other, or widen one to cover both
  cases if they differ slightly.
- **Implementation detail rather than behaviour.** A test that breaks on a rename or a refactor that
  changes no observable behaviour is a maintenance cost with no guard behind it.
- **Ambient or non-deterministic arrangement.** A test reading the system clock, the working
  directory, an environment variable, or depending on filesystem timestamp granularity or on
  enumeration order that is not guaranteed. These pass until they do not.
- **Scaffolding out of proportion.** Forty lines of arrange for one assertion that a five-line
  fixture would reach.
- **A test whose name promises more than its body delivers.** Raise these; do not rename them.
- **Tests that would not fail if the production code were deleted.** The strongest single check
  available: for a sample of tests, ask what would still pass if the member under test returned a
  default or threw.

## The bar for cutting

**Be conservative. The default is keep.** A green test that is merely unexciting is not junk. Cut
only where you can state what is wrong with it, and prefer repair to removal.

Every test gets one of four outcomes:

1. **Keep** - it asserts a real property and it does so honestly. Most tests. No change.
2. **Strengthen** - the property is real but the assertion is weak: containment where an exact set is
   meant, a normalizing helper hiding the thing, a missing assertion on a second field. Fix the
   assertion in place. This is the outcome to prefer whenever it is available.
3. **Merge** - two or more tests pin one property. Keep the clearest, widen it if the others cover a
   case it does not, and remove the rest.
4. **Cut** - it asserts nothing that can fail, or it pins an implementation detail, or it is ambient
   or non-deterministic. State which.

Do not cut a test because you cannot see why it exists. Read the production code it drives first. A
test that looks pointless is more often a test whose point you have not found, and the retained code
in this repository is mostly correct. Where you genuinely cannot tell, keep it and list it for the
operator.

Behaviour does not change. This job touches `tests/` only. If the audit turns up what looks like a
defect in `src/`, stop that thread and raise it - do not fix it here.

## How to run it

Orchestrate with workflows rather than reading 1576 tests yourself, and run the sweep in three
phases. The phase structure below is the shape of the job; settle the details with the operator when
you start rather than treating this as fixed.

**Phase 1 - review and change.** One agent per area. Split by test file or by cohesive group of
files, sized so each agent can read its files whole along with the production code they drive.
`VaultShared.Tests` is large enough to want several agents; `Frontmatter/`, `Links/`, `Tools/` and
the access-layer root files are natural seams. Each agent judges its area against the bar above,
applies its own changes, and reports what it did and why.

**Phase 2 - adversarial review.** Fresh agents, none of which saw phase 1's reasoning, over phase 1's
changes. Their job is to question the decisions rather than to repeat the work: a cut that removed a
real guard, a strengthening that pins something the code does not promise, a merge that lost a case
the surviving test does not cover, a finding whose stated failure mode does not hold once the file is
open. Give a phase 2 agent the diff and the production code, not the phase 1 agent's justification -
a justification read first is a conclusion agreed with.

**Phase 3 - arbitration.** You decide. Phase 2 disagreeing with phase 1 settles nothing on its own;
open the file and rule. Where the two phases disagree about the same shape in different areas, the
bar is what is unclear, not the tests, and fixing the bar comes before ruling on the instances.
Anything you cannot settle from the code goes to the operator rather than to your own judgement.

Three things hold across all three phases:

- Give each agent the bar above verbatim. Agents drift toward the extremes when told only to "look
  for problems" - they either find nothing or condemn half of what they read.
- Require every finding to name the production code the test drives and say what would have to change
  for the test to fail. A finding that cannot answer the second question is not a finding.
- Agent findings in this repository have needed correction at a material rate: a cost claim that did
  not hold once the named test was opened, a citation that had drifted, a conclusion that
  contradicted an agreed decision. Verify against the file before you act on any of them, in either
  direction.

Apply changes in batches you can verify. After each batch:

```
dotnet build --no-incremental
dotnet test
```

The build must stay at 0 warnings, 0 errors. Every remaining test must pass. A test that fails after
a strengthening is a genuine finding about the production code - stop and raise it rather than
weakening the assertion back.

## The deliverable

Two things.

**A report to the operator in session**, not a document: how many tests were read, how many cut,
merged and strengthened, the final count, and anything raised rather than acted on. Findings you
decided not to act on do not need writing down; a list of rejected candidates is read by nobody.

**A guide at `../conventions/Unit-Tests.md`.** `conventions/` outlives this build and travels with
the code into the canonical repository, which is where a guide governing how tests are written
belongs. This location is agreed; write it there.

The guide is written for someone about to add or change a test in this repository, who has read
nothing else. It is not a report on this audit and carries no findings, no counts, and no chronology.
Same register as `Code-Comments.md`: plain, concise, complete, present tense, no thesis paragraphs.
It should be short enough to read before writing a test, which means it stays well under that
document's length.

What it must carry:

- The rule that a test asserts a behaviour a caller can observe, and what that excludes.
- Each junk shape this audit actually found, as a rule with a short wrong-and-right example drawn
  from the real suite. Only the shapes found - do not pad it with shapes that never occurred here.
- The systematic causes behind them. This is the part that matters and the part that cannot be
  recovered later: a normalizing helper written to make one test readable and then reused until it
  hid a property; a test written against the shape of the code rather than the shape of the caller;
  a second test added beside an existing one instead of widening it. Name the cause, not just the
  symptom, so a reader recognizes the mistake while making it. Where phase 2 disagreed with phase 1,
  look at what the two read differently: a shape that two careful readers judge oppositely is one the
  guide has to settle explicitly, and those rules are worth more than any drawn from a clean finding.
- What the fixtures are for and which one to reach for: `ScratchVault` and its scope and op-class
  constructor arguments, `TestVaultFixture`, `FixedTimeProvider`, `RecordingProblemSink`,
  `DeclaredToolHarness`. A guide that does not say which fixture to use is why the next author
  builds a new one.
- The costs that make a test not worth writing: ambient or process-global state, anything requiring
  the suite to serialize, a production seam that exists only so a test can observe something,
  non-determinism, elevation. These were used to judge the coverage list and they hold generally.
- That test method names are documentation and are not rewritten, and that string literals are wire
  contract.
- How to decide between widening an existing test and adding a new one, with the default being to
  widen.

## What not to do

- Do not add tests. The coverage step that added them is finished, and a gap you notice here is a
  finding to raise rather than a test to write.
- Do not change anything under `src/`.
- Do not rename test methods, test data, or assertion strings.
- Do not refactor test files for tidiness - no reordering, no reformatting, no extracting helpers
  that no finding required.
- Do not write a log of what was considered and rejected. Nothing outside the guide and the report
  survives this job.
- Do not link this file from `CLAUDE.md`, `Plan.md`, or anywhere else. Delete it when the job is
  done.
