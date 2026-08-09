# Test Coverage Gaps

This is the work list for the test-coverage step. Every entry below names a behaviour that is
present and correct in the code today and that nothing in the suite asserts. They are candidates
for a test, not defects: the code does the right thing in each case, and the gap is that a later
change could stop it doing so without a single assertion failing.

Ten areas were read separately and their findings are merged here. Each entry says what the
behaviour is and where it lives, what the suite covers today, and what a plausible future change
would break silently. Where the extracting pass found that the cited code or the cited test no
longer matched - a moved line, a test that has since arrived, a claim that turned out to be
wrong - that is recorded with the entry, because the next step needs it before it writes anything.

A handful of entries turned out on inspection to be asserted after all, or to be too weak to build
a regression around. They are kept, marked as such, so that the next reader does not have to
rediscover why they were dropped.

The entries came from a harvest of the deleted system's test corpus. That harvest and its
reconciliation are deleted. The old case identifiers appear here only as origin markers; they
resolve nowhere, and nothing outside this document can be looked up from them.

## The three to write first

Three entries assert that something deliberately unscoped stays unscoped. That is the shape a later
reader is most likely to break, because adding a scope check to an unscoped call reads as a
tidy-up or a security improvement in review, and nothing fails when they do it.

- Delete's backlink census reaches outside the caller's scope (create and delete, old `CRD-095`).
- Move's wikilink repair reaches outside the caller's scope roots (move, old `MOV-043`).
- Search arguments reach the engine as a list and never through a shell (enumeration and content
  search, old `ENU-046` and `ENU-030`).

## Scope, path resolution, I/O and configuration

### Scope lookup by vault name ignores case

Asking a grant for its scope in a vault matches the scope-map key without regard to case, so a
caller naming the vault `Test` gets the entry stored under `test` rather than the "no entry for that
vault" exception. `Grant.ScopeIn` (`src/VaultShared/Auth/Grant.cs:106`, comparison at line 110) walks
the map comparing with `StringComparison.OrdinalIgnoreCase`, so the answer does not depend on which
comparer built the dictionary. `Grant.CanAccessVault` follows the same rule deliberately: the two
answer one question between them - whether the grant reaches a vault, and how far into it - and must
not disagree about a vault spelled in another case.

This is asserted. `GrantTests.CanAccessVault_ComparesWithoutRegardToCaseAndAgreesWithScopeIn`
(`tests/VaultShared.Tests/GrantTests.cs:56`) is a theory over `test`, `Test` and `TEST`; it builds
the grant's vault set with `StringComparer.Ordinal` and asserts both `CanAccessVault(requested)` and
`ScopeIn(requested) == VaultScope.Unscoped`. The narrower observation still holds -
`VaultScopeTests.ScopeCases` (`tests/VaultShared.Tests/VaultScopeTests.cs:64`) always spells the
vault exactly as the map key - but the property itself is pinned elsewhere. Rewriting the walk as a
plain `ScopeRoots.TryGetValue` would now fail.

The coverage claim is stale. The entry was written against a suite that did not yet have
`GrantTests.cs:66`. The code citation is verified. Old case `SCP-041`.

### The op-class gate compares without regard to case in the gate itself

`Grant.Allows` (`src/VaultShared/Auth/Grant.cs:71`) decides op-class membership by comparing every
held name with `StringComparison.OrdinalIgnoreCase` rather than by asking the set. The guarantee
belongs to the type, not to whichever producer built `OpClasses`: a grant whose op-class set was
constructed with the ordinal default still matches a floor spelled `Content:Write` against a held
`content:write`. Op-class names are configuration text on both sides - a grant row names what it
holds, a tool declares the floor it requires - so a comparison inherited from the set would make the
gate's answer depend on a construction detail.

This is asserted, and asserted at the gate rather than through a producer.
`GrantTests.Allows_ComparesWithoutRegardToCaseOverAnOrdinalSet`
(`tests/VaultShared.Tests/GrantTests.cs:34`) is a theory over three spellings against a set built
with `StringComparer.Ordinal`, and `GrantTests.Allows_DeniesAnOpClassTheGrantDoesNotHold` (line 86)
pins the other direction, so case-blindness cannot be met by matching everything.

Stale on both counts. The entry claimed that `Allows` is a bare `OpClasses.Contains` and that no test
pins it at the gate. Neither describes the code as it stands. Old case `SCP-053`.

### The missing-parent refusal names the immediate parent, not the deepest existing ancestor

When a write, a create, a folder-create or a move is refused because the containing folder is not
there, the refusal names the path's immediate parent - the whole vault-relative path minus its last
segment - however many directories along the way are missing. Creating `Projects/Widget/Widget.md`
where neither `Projects` nor `Projects/Widget` exists names `Projects/Widget`, not `Projects`. The
rule cannot vary by input, because `VaultPath.ParentValue`
(`src/VaultShared/Access/VaultPath.cs:57`) is computed unconditionally from the segment list. The one
exception is a path directly under the vault root, which has no parent to name and is reported as an
operational failure instead. The consumers are the shared `MissingParent` helper behind `Create`,
`Replace`, `CreateFolder` and `CreateGitKeep` (`src/VaultShared/Access/VaultFiles.cs:584`) and the
three move paths (`VaultFiles.cs:285`, `:338`, `:439`).

Only the two-directories-missing shape is asserted. `VaultFilesReadWriteTests`
(`tests/VaultShared.Tests/VaultFilesReadWriteTests.cs:166`, `:176`, `:185`) covers `Create`,
`Replace` and `CreateFolder` against `Projects/Widget/...` in a scratch vault where `Projects` itself
does not exist; each asserts `Assert.False(_vault.HasFolder("Projects"))`. The move-side refusals
(`tests/VaultShared.Tests/VaultFilesMoveTests.cs:79`, `:199`, `:259`, `:398`) and
`tests/VaultShared.Tests/Tools/CreateToolTests.cs:341` construct the same shape. Nothing in the suite
creates `Projects` and then aims a write at `Projects/Widget/Widget.md`.

What passes silently is any rule that agrees with "immediate parent" only when two or more
directories are missing - naming the outermost missing directory joined to the leaf, say, or naming
the parent only when it is itself absent and otherwise falling back. The suite cannot distinguish
those, because it never presents a path whose grandparent exists. Both shapes are needed: either
alone is satisfiable by the wrong rule. Old cases `SCP-063`, with the companion shape `SCP-062`.

### A sharing violation on read is a failure, not nothing-to-read

`VaultFiles.Read` (`src/VaultShared/Access/VaultFiles.cs:51`) classifies its outcome from the
exception the platform raised, in a fixed order. `FileNotFoundException` and
`DirectoryNotFoundException` become `ReadOutcome.NotFound` (the filter at line 59); every other
`IOException` - which is what a sharing violation is, when another process holds the file open with
`FileShare.None` - and `UnauthorizedAccessException` become `ReadOutcome.Failed` carrying a reason
(line 63). A note that exists but cannot currently be opened is never reported as though it were
absent.

Nothing covers the locked-file shape. `VaultFilesReadWriteTests` covers not-found (`:44`), a missing
folder on the way (`:54`), and one `Failed` case - `Read_ThereAndSomethingWentWrongIsTheOtherAnswer`
(`tests/VaultShared.Tests/VaultFilesReadWriteTests.cs:60`), which puts a folder where a note was
named. No test opens a file with `FileShare.None` and then reads it.

The regression is reordering the two catch clauses, or widening the `NotFound` filter to plain
`IOException`. Both `FileNotFoundException` and `DirectoryNotFoundException` derive from
`IOException`, so `catch (IOException)` first is an easy and natural-looking edit. Every existing
read test still passes, because the folder-where-a-file-was-named case raises
`UnauthorizedAccessException`, which is outside the `IOException` hierarchy the reordering moves. A
locked note would then report as "nothing there to read", and a caller acting on that - a tool
reporting the note as absent, or a create concluding the name is free - takes the wrong branch on a
transient condition.

One imprecision is worth recording: the entry described
`Read_ThereAndSomethingWentWrongIsTheOtherAnswer` as the access-denied shape. It is a folder standing
where a note was named, which on Windows surfaces as `UnauthorizedAccessException` - right about the
exception type, not about the scenario, and the distinction is what makes that test unaffected by the
reordering. Old case `SCP-067`, with the access-denied companion `SCP-068`.

### A path equal to a scope root is in scope

`VaultScope.Admits` (`src/VaultShared/Scope/VaultScope.cs:57`) matches a rooted scope by whole
segments and accepts a path whose segment count equals the root's, so the scope root folder itself is
inside the scope rather than only its contents. A grant rooted at `Projects/Widget` admits the path
`Projects/Widget`. The guarantee is the `<=` in the length test at line 64.

Nothing asserts it, directly or indirectly. `VaultScopeTests.ScopeCases`
(`tests/VaultShared.Tests/VaultScopeTests.cs:64`) has nineteen rows and the closest is root plus one
segment. `VaultAccessTests` resolves paths through `ForRead` and `ForWrite` under a narrow scope but
never one equal to that scope's root: the `Beneath` tests
(`tests/VaultShared.Tests/VaultAccessTests.cs:250`, `:266`) use scope root `Projects` and resolve the
folder `Projects/Widget`.

Changing the `<=` to `<` leaves every scope row and every path resolution in the suite passing. In
production it makes a scope root's own folder unreachable to the grant scoped to it - reading or
listing the root folder, creating a note directly in it, removing it - which surfaces as an
out-of-scope refusal on the one folder the grant most obviously owns.

One claim in the original is wrong. It said `VaultAccess.Beneath` depends on the equality branch.
`Beneath` (`src/VaultShared/Access/VaultAccess.cs:209`) does not call `VaultScope.Admits` at all; it
makes its own comparison, `contained.Equals(folder.Path)` or a `StartsWith` on the rendered path with
`OrdinalIgnoreCase`, and `VaultAccessTests.cs:258` covers that equality in `Beneath`'s own code. The
indirect coverage does not exist. Recorded against old case `SCP-023`.

## Enumeration and content search

### Arguments reach the search engine as a list, never a shell command line

This is one of the three to write first. The engine is invoked with an argument vector, not a
composed command string, and no shell is interposed. `SearchEngine.Run`
(`src/VaultShared/Access/SearchEngine.cs:47-49`) calls `Cli.Wrap(Command).WithArguments(arguments)`
where `arguments` is an `IReadOnlyList<string>` built by `VaultSearch`
(`src/VaultShared/Access/VaultSearch.cs:50`, `:69`, `:84`, `:98`). The caller's regex reaches the
engine as the value of `--regexp` and the caller's glob as the value of `--iglob`, both option values
rather than bare operands, and `--` precedes the `.` operand that names what to search
(`VaultSearch.cs:35`). A caller pattern beginning with a hyphen is matched as data and cannot be read
as a flag, and shell metacharacters in a pattern - `;`, `|`, `$`, backticks, quotes - have no
interpreter to reach.

Nothing asserts it. No test in `tests/VaultShared.Tests/VaultSearchTests.cs` or
`tests/VaultShared.Tests/VaultEnumerationTests.cs` supplies a pattern beginning with a hyphen or
carrying a shell metacharacter. The patterns the suite uses are `needle`, `haystack`, `nested`, `(`
as a malformed regex, and the globs `*.md`, `Notes/*.md`, `Notes/**`, `Nested.md`, `notes/alpha.MD`,
`Sub/*.md`, `Inbox/**`, `Nothing-Named-This.md` and `[` as a malformed glob.

If someone replaces the argument list with a composed string - to add quoting, to log the command, or
to route the invocation through `cmd /c` or `sh -c` on one platform - the suite stays green. The
regression is a pattern-injection surface: a caller-supplied pattern such as `x & del /q *` or
`$(...)` reaches an interpreter, and a hyphen-leading pattern such as `-i` or `--files` is consumed as
an option and quietly changes what the search does rather than being matched. Both failures produce
plausible-looking results.

The line range is corrected: the original cited `SearchEngine.cs:47-53`, but lines 50-53 are the
validation comment and `WithValidation`, so the argument-list guarantee is at 47-49. Confirmed
empirically that ripgrep 15.2.0 treats a leading-hyphen pattern passed as `--regexp -flag` as data. Old
cases `ENU-030` and `ENU-046`.

### Content search is case-sensitive; only the glob half is case-insensitive

The three content-search granularities pass no case flag at all, so the engine's case-sensitive
default stands: `SearchLines`, `SearchFiles` and `SearchCounts`
(`src/VaultShared/Access/VaultSearch.cs:69`, `:84`, `:98`) match `Needle` differently from `needle`.
The glob is the deliberate asymmetry - it is passed as `--iglob` (`VaultSearch.cs:123`), which is
unconditionally case-insensitive. `--no-config` is one of the three always-arguments, so an ambient
engine configuration file cannot inject `-i` behind the layer's back.

Nothing covers the content half. Every content pattern in
`tests/VaultShared.Tests/VaultSearchTests.cs` is all-lowercase and every fixture body it searches is
all-lowercase (`VaultSearchTests.cs:21-27`), so adding `-i` to the search invocations would not change
a single assertion. The glob half is asserted, by
`VaultEnumerationTests.APatternIsMatchedCaseInsensitively`
(`tests/VaultShared.Tests/VaultEnumerationTests.cs:86-90`), which enumerates with `notes/alpha.MD`
and expects `Notes/Alpha.md`.

The regression is adding `-i`, `--ignore-case` or `--smart-case` to the search argument vectors,
plausibly while making content search behave "like the glob does", since the asymmetry is unobvious.
Callers relying on a case-sensitive match - distinguishing a proper noun from a common word, or `TODO`
from `todo` - silently get a wider result set. The reverse regression, dropping `--iglob` for
`--glob`, is caught by the test above. Old cases `ENU-026` and `ENU-017`.

### Escape-shaped glob patterns match nothing rather than reaching outside the vault

A caller glob that attempts to climb out of the vault or name an absolute location elsewhere -
`../**`, `C:/Windows/**`, `**/../../*`, `/etc/**`, `..` - produces an empty `Results` outcome, not a
refusal and not a path outside the vault. Two mechanisms carry it. The engine is started in the vault
root and always runs against the `.` operand, and its glob matcher matches such patterns against
nothing in the walked tree; the glob is passed through to `--iglob` verbatim with no sanitizing
(`src/VaultShared/Access/VaultSearch.cs:121-125`). Independently, every path the engine emits must
resolve as a `VaultPath` in the vault that was searched, and anything that does not fails the whole
call rather than being dropped (`VaultSearch.cs:234-235` in `Paths`, with the equivalent checks at
`:269-270` in `Counted` and `:303-304` in `Matches`).

Nothing asserts it. No test passes an escape-shaped glob to `EnumerateNotes`, `EnumerateFiles`,
`SearchFiles`, `SearchLines` or `SearchCounts`. Escaping paths and escaping scope roots are covered
elsewhere in the suite; the glob argument is not.

If the engine's glob semantics change across an upgrade, or someone pre-processes the caller's glob
into an absolute path before handing it over, the `VaultPath` backstop turns the containment breach
into a hard failure - so the practical regression is a loud one. The genuinely silent case is
narrower: a future change that relaxes the backstop, for example dropping unparseable emissions
instead of failing, removes the second guard with no test noticing that the first one was ever
load-bearing.

The line numbers are corrected: the backstop was cited as `VaultSearch.cs:231-232`, which is the
`results` list declaration and the `foreach` header; the `TryCreate` guard and its `Failed` return are
lines 234-235. The behaviour was re-verified by hand against ripgrep 15.2.0 using the exact argument
vector `VaultSearch.Enumerate` builds; all five probes exited 1 with empty standard output, which the
layer classifies as an empty `Results`. Old cases `ENU-019` and `ENU-037`.

### Emitted output carries no terminal colour escape sequences

Paths and match text handed back to callers are plain text with no ANSI escape sequences embedded.
The layer does not pass `--color never`; it passes no `--color` argument at all
(`src/VaultShared/Access/VaultSearch.cs:50`, `:69`, `:84`, `:98`) and relies on the engine's `auto`
default seeing that its standard output is a pipe rather than a terminal
(`src/VaultShared/Access/SearchEngine.cs:53`, `ExecuteBufferedAsync`, which buffers both streams).
`--no-config` (`VaultSearch.cs:25`) closes the one ambient route by which a configuration file could
force colour on.

Nothing asserts that a returned path or `MatchLine.Text` is free of escape sequences.

A silent regression here is hard to construct, and that is worth saying plainly. If colour escapes
appeared in path output, the emitted text would not parse as a `VaultPath` and the whole call would
fail loudly (`VaultSearch.cs:234-235`). The one path that could degrade quietly is match text:
escapes wrapped around the matched substring inside `MatchLine.Text` flow through
`VaultSearch.cs:309-312` untouched and reach the caller as noise in the line body, with the suite's
exact-text assertion at `VaultSearchTests.cs:47` catching it only for the two lines it happens to
check.

Confirmed by inspection that no argument vector carries `--color`, and empirically that `--files`,
`--files-with-matches --null --stats` and `--count --null --stats` under ripgrep 15.2.0 emit no escape
sequences when piped. Old case `ENU-031`.

### Symbolic links are never followed during a walk

The engine walks the vault without descending through symbolic links, junctions or reparse points.
The mechanism is omission: `-L` / `--follow` is never among the arguments any surface builds
(`src/VaultShared/Access/VaultSearch.cs:25`, whose `Always` contains only `--no-config`, `--hidden`
and `--no-ignore`, and the four vectors at `:50`, `:69`, `:84`, `:98`), and the engine's default is
not to follow. Following a link is the one real route by which a walk rooted at the vault could read
files outside it, so this is a containment property and not merely a performance one.

Nothing asserts it. A repository-wide search for symlink creation in the test projects returns no
hit; no fixture, `ScratchVault` included, creates a link of any kind.

The regression is adding `--follow`, plausibly to make a vault that keeps attachments behind a link
enumerate "completely". Nothing fails, and enumeration begins reporting files outside the vault root.
Those emitted paths are in most cases still vault-relative strings the engine produced by walking
through the link, so the `VaultPath` backstop does not necessarily reject them and the scope predicate
judges them by their apparent location rather than their real one. That makes this the entry with the
widest gap between consequence and coverage. Writing the test is itself awkward on Windows, where
creating a symbolic link ordinarily requires Developer Mode or elevation, which is part of why no
fixture has one. Old case `ENU-048`.

### The engine's working directory is per-child and touches no process-global state

Each invocation sets the child process's working directory through
`WithWorkingDirectory(workingDirectory)` (`src/VaultShared/Access/SearchEngine.cs:49`), always the
vault root. The layer never calls `Directory.SetCurrentDirectory` or otherwise mutates the host
process's current directory, so two searches running concurrently, potentially against two different
vaults, cannot disturb each other's notion of where the walk is rooted. Starting the engine in the
vault directory is exactly what makes every emitted path vault-relative, so a process-global working
directory would make emitted paths race-dependent.

Nothing asserts it. There is no concurrency test over enumeration or search; the suite's only
concurrent-execution test is over unrelated health state
(`tests/VaultMcpServer.Tests/ServerHealthStateTests.cs:264`).

The regression is reworking the invocation to set the process directory and restore it, for instance
while adding a code path that needs relative resolution outside `CliWrap`. Under a single-threaded
test run nothing fails, and the defect appears only under concurrent calls, where a search
intermittently reports paths rooted in another vault or fails to find files that exist. A regression
test is difficult to write deterministically, which is worth weighing against the severity; the
property is also plainly visible in one line of code, which is the argument for leaving it untested.
Old case `ENU-047`.

### `--no-config` keeps ambient engine configuration out of every invocation

Every invocation carries `--no-config` (`src/VaultShared/Access/VaultSearch.cs:25`, with the reason
in the summary comment above it at `:16-24`), so the engine does not read the options file named by
the `RIPGREP_CONFIG_PATH` environment variable. Without it, an environment variable outside this
layer's control could inject `-i`, `--follow`, `--max-filesize`, additional globs or a `--color`
setting into every search the layer performs - ambient state that this layer refuses to read for
scope and refuses to read for the engine location, and so must also refuse here.

Nothing asserts it. No test sets `RIPGREP_CONFIG_PATH` and confirms its contents are ignored; a
repository-wide search for that variable name finds it only in documentation.

The regression is dropping `--no-config` from `Always`, for example during a change that reorganizes
the argument vectors, since the flag looks like defensive noise to a reader who has not read the
comment above it. Nothing in the suite fails, because a test machine has no `RIPGREP_CONFIG_PATH`
set. The failure appears only on a machine where an operator or another tool happens to have set that
variable, as searches that quietly return the wrong set of files. A test must set the variable for
the child process specifically; the property is mechanically awkward to assert without leaking
environment state across a parallel test run.

No old case identifier. This one was attributed to the enumeration and content search slice of
`docs/Build-Decisions.md` rather than to a harvested case, and this entry is the only record outside
that decision line.

## Frontmatter core

### Body bytes survive a frontmatter mutation, across the whole awkward-content checklist

`FrontmatterComposer.Compose` (`src/VaultShared/Frontmatter/FrontmatterComposer.cs:37`,
`tree.IsEmpty ? body : $"---\n{Emit(tree)}---\n{body}"`) concatenates the fenced block and the body
string with no inspection of the body at all, so every byte of the body is carried through a
frontmatter mutation unchanged - trailing whitespace on a line, literal tabs, non-ASCII characters, a
fenced code block, a `---` line in prose, a line shaped like `key: value`, and a body with no final
newline. The property holds structurally rather than case by case: there is no body-touching code to
get any one of those cases wrong.

It is covered in part, and never as the checklist.
`FrontmatterWritePathTests.AnOperationReserializesTheBlockAndLeavesEveryBodyByteAlone`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterWritePathTests.cs:160`) runs one operation over a
body that is plain ASCII text with a trailing newline and asserts the body comes back intact. Two
adjacent tests touch single checklist rows but not through a frontmatter mutation:
`FrontmatterWritePathTests.cs:86` (`ABodyOnlyWrite_SplicesAtTheOffsetAndDisturbsNoFrontmatterByte`)
is a body-only write, and `FrontmatterWritePathTests.cs:232`
(`TheGuard_LetsABodyThatMerelyMentionsAFenceThrough`) covers a `---` in prose, again on a body-only
write. Nothing asserts tabs, non-ASCII, a fenced code block, a key-shaped body line, trailing
whitespace on a body line, or a missing final newline across a mutation.

The regression is any body normalization introduced on the compose path - trimming trailing
whitespace, appending a final newline, expanding tabs, re-encoding - in the belief that it makes
output tidier. The one body-byte test uses `"Body text.\n"`, which survives all four of those
transformations unchanged, so it keeps passing while every real note with a code block or a tab
silently churns on its next frontmatter edit. Old case `FMC-114`.

### Applying the same mutation twice produces the same bytes both times

The emitter is a fixed point after one pass. Composing a note reserializes the whole block into the
emitter's canonical shape - indented sequences, LF terminators, no document markers, key order
preserved - so a second application of the same mutation to the already-composed output yields
byte-identical content rather than churning quoting or wrapping a second time
(`src/VaultShared/Frontmatter/FrontmatterComposer.cs:46`, `Emit`, with the four pinned settings at
`:54`, reached from `Compose` at `:37`). This is what makes a write idempotent for a caller that
retries.

Nothing asserts the fixed point.
`FrontmatterComposerTests.ComposingAnUntouchedTreeReproducesABlockThatWasAlreadyCanonical`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterComposerTests.cs:183`) is the closest and is
strictly weaker: it composes a hand-written block that is already in canonical shape and checks it
comes back identical; its own comment concedes the narrower claim. No test takes an
arbitrarily-quoted note, applies a mutation, applies the same mutation to the result, and compares
the two outputs. The only idempotence-named test in the tree is
`NoteSplitTests.NormalizationIsIdempotentAndAgreesWithTheSplit`
(`tests/VaultShared.Tests/Frontmatter/NoteSplitTests.cs:191`), which is about CRLF normalization.

The regression is an emitter setting or a tree-construction change that makes one round of
composition non-canonical - a value that emits as a folded scalar and re-parses to a different
wrapping, so pass two rewraps it. The canonical-block test keeps passing because its input never
exercises the non-canonical branch, and callers see a note whose bytes changed on a write that
changed no meaning, defeating the no-op and change-detection machinery downstream. Old case
`FMC-115`.

### A `no` or `yes` scalar survives a write as the text that was written

The tree carries a scalar's parsed text and style rather than a decoded YAML value
(`src/VaultShared/Frontmatter/FrontmatterTree.cs:300`, where an existing node is rebuilt from
`scalar.Value`), so a YAML 1.1 boolean spelling - `no`, `yes`, `on`, `off` - in an untouched key is
re-emitted as the same plain text when a mutation elsewhere reserializes the block
(`src/VaultShared/Frontmatter/FrontmatterComposer.cs:46`). Nothing coerces it to `false` or `true`
and nothing defensively quotes it. The emitted form is plain, which differs from the system being
replaced, where such values came back quoted.

Only the neighbouring cases are covered, and only on the read side.
`FrontmatterComposerTests.ADateShapedScalarComesBackAsTheTextThatWasWritten`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterComposerTests.cs:142`) covers date-shaped scalars
through an emit, and `FrontmatterTreeTests.cs:63-68` reads `flagged: true` back as the string
`"true"`. Grepping the frontmatter tests for `no` or `yes` as a value returns nothing, and no test
drives such a value through `FrontmatterWrite.Apply`.

The regression is a change that routes scalars through a typed YAML deserialize, or swaps the
representation-model layer for an object model, so that a note's `doc.published: no` comes back as
`false` - or quoted as `'no'` - on the next unrelated frontmatter edit. The date test still passes,
because a date-shaped plain scalar survives typed deserialization by a different route than a boolean
spelling does.

Two small corrections. The original attributed `flagged: true` to
`ADateShapedScalarComesBackAsTheTextThatWasWritten`; that test covers only `created` and `stamped`
dates, and `flagged: true` is asserted at `FrontmatterTreeTests.cs:63`. Its parenthetical claiming
that `doc.title: no` was probed and survives a reserialize as plain `no` was not re-run - the
mechanism was confirmed by reading the tree and emitter, not by execution. Old case `FMC-116`, its
`no`/`yes` half.

### An entry field's value carrying a newline cannot become block structure

A caller-supplied string containing a `\n`, including one containing a literal `---` line, is escaped
by the emitter as a scalar - folded, `>-`, with the content indented - rather than spliced into the
block as text. It re-reads as the same string, no key it appears to declare comes into existence, and
the body is untouched. A mapping entry's field is set as `new YamlScalarNode(field.Value)`
(`src/VaultShared/Frontmatter/FrontmatterTree.cs:304`), so the emitter owns the escaping
(`src/VaultShared/Frontmatter/FrontmatterComposer.cs:46`), and the write path's re-split guard is the
second line of defence.

For an entry field inside an `EntryPayload.Mapping`, nothing asserts it.
`FrontmatterRefusalTests.cs:141` constructs a mapping field value carrying `U+0007 BEL` and asserts
the control-character refusal, but no test constructs a mapping field value carrying a newline and
drives it through `FrontmatterWrite.Apply`. Grepping the frontmatter tests for a newline inside an
`EntryFieldValue` returns nothing.

The regression is a change to entry-field emission that writes the field text directly - a
string-building shortcut for entry blocks rather than going through the node layer - so that a
caller-supplied field value carrying `\n---\n` mints a second block or a key the validator never saw.
The scalar-value test keeps passing, because scalars and entry fields would then travel different
code paths.

The original is stale in part. It said no test drives a value or an entry field carrying a newline
through `FrontmatterWrite.Apply`. The value half is false:
`FrontmatterRefusalTests.ControlCharacter_NewlinesAndQuotesAreNotRefused`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs:149`) sets `doc.title` to
`"a \"quoted\" line\nand another"` through `Apply` and asserts the re-read value equals the original.
What that test does not assert is the injection property - it uses no `---` in the value and checks
neither for a spurious key nor for body integrity - so the end-to-end gap is real but narrower than
stated and located in the entry field. `ACallerValueCannotBecomeStructure`
(`FrontmatterComposerTests.cs:202`) was cited correctly and does cover the tree-and-composer path,
injection included. Old cases `FMC-030` and `FMC-055`.

### A note stored with a byte-order mark still has its opening fence recognized

`VaultFiles.Decode` (`src/VaultShared/Access/VaultFiles.cs:514`) reads with a `StreamReader`
constructed with `detectEncodingFromByteOrderMarks: true` (`:517`), so a UTF-8 BOM is consumed by the
decoder and never surfaces as a leading U+FEFF character in the text handed to `NoteSplit.Of`. The
note's first line is therefore exactly `---` and the block is found. Writes go back out as UTF-8 with
no BOM (`VaultFiles.cs:37`, used at `:538`), so the mark does not return. The system being replaced
had no such handling, where a BOM disqualified the opening fence; this is an improvement rather than
a carried-over behaviour.

The file layer is covered and the fence is not.
`VaultFilesReadWriteTests.Read_DecodesUtf8AndDropsBomBytes`
(`tests/VaultShared.Tests/VaultFilesReadWriteTests.cs:33`) writes a BOM'd file and asserts the
decoded text has no leading mark; `AWriteEmitsUtf8WithNoByteOrderMark` (`:265`) asserts the write
side. Neither note has frontmatter. Nothing in the frontmatter tests mentions a BOM at all - grepping
`tests/` for `BOM`, U+FEFF and `FEFF` returns only those two file-layer tests.

If the read path is changed to decode bytes directly, with `Encoding.UTF8.GetString` or an explicit
`new UTF8Encoding(false)` for symmetry with the writer, `Read_DecodesUtf8AndDropsBomBytes` fails, so
that regression is not wholly uncaught. What nothing catches is the frontmatter layer gaining its own
read path that bypasses `VaultFiles.Decode`: a BOM'd note would then report no frontmatter, with its
whole content landing in the body facet.

The original citation is stale. It cited `VaultFiles.cs:410, 431`; line 410 is inside
`PreflightMoveFolder` and line 431 inside the private `Preflight` helper - folder-move preconditions,
nothing to do with encoding. The behaviour is real, at `:514`/`:517` and `:37`/`:538`. Acting on
`:410` would have landed a change in the move preflight. No case identifier; the harvest listed this
as an explicit gap in the old suite rather than under a case.

### A block closed with `...` comes back closed with `---` after an operation

`NoteSplit.IsFence` (`src/VaultShared/Frontmatter/NoteSplit.cs:145`,
`run is "---" || (!opening && run is "...")`) accepts `...` as a closing fence but never as an
opening one, so a note terminated the YAML way is read as having frontmatter. The composer is the only
writer of fences and emits `---` unconditionally
(`src/VaultShared/Frontmatter/FrontmatterComposer.cs:37`), so any operation that reserializes the
block normalizes the closer to `---`. The `...` form is readable but not producible.

Only the split half is asserted. `NoteSplitTests.cs:68` splits
`"---\ntitle: Widget\n...\nBody.\n"` and `NoteSplitTests.cs:79` asserts `...` is rejected as an
opener. Nothing carries a `...`-closed note through `FrontmatterWrite.Apply`; grepping
`FrontmatterWritePathTests.cs`, `FrontmatterOperationTests.cs` and `FrontmatterRefusalTests.cs` for
`...` returns no hits.

The regression is weak, and this is the weakest entry in the area. It can be constructed only as the
composer gaining a "preserve the original closer" behaviour, or a second fence writer appearing, so
that a `...`-closed note starts emitting `...` - which this code then re-reads fine, but which is
outside what the emitter is meant to produce and invisible to every existing test. Whether the
normalization is a property worth pinning or an incidental consequence of the composer being the only
fence writer is a judgement for the next step; no case was found where a user notices. No case
identifier; the code site above was located during extraction rather than carried.

## Frontmatter write path

### A key spelled in quotes is still the key it names

Key names are read off the parsed scalar node's value, which has already had its quoting resolved:
`FrontmatterTree.Spelling` (`src/VaultShared/Frontmatter/FrontmatterTree.cs:316`) returns a key
node's `YamlScalarNode.Value`, `Find` at `:314` is documented as the only place key names are
compared, and `Keys` at `:139` projects every root child through `Spelling`. A key written as
`"agent.instructions":` therefore arrives as the bare string `agent.instructions`, and everything
downstream applies to it unchanged. On a caller-supplied block, `SuppliedTree`'s loop over
`tree.Keys` (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:225`) validates it, the entry schema is
checked against it, and `KeyAuthority` (`FrontmatterWrite.cs:246`, prefix match at `:666`) matches
the `agent.` namespace rule and asserts the elevated op class before the tree is returned. The same
holds for `create`, which classifies a supplied block's keys for authority off the same parsed
spellings, so `"agent.mode": auto` and `agent.mode: auto` are one key charged one op class. Nothing
in the code states this as a rule; it falls out of parsing rather than string-matching the block.

Both the frontmatter write area and the create and delete area raised this; it is one entry.

Nothing pins the quoted spelling. No test feeds a supplied block whose key is quoted. Every
frontmatter fixture that uses quoting quotes a value - `FrontmatterComposerTests.cs:137` (`a:
"quoted"`, `b: 'single'`, `d: "has: colon"`) and `FrontmatterRefusalTests.cs:153`. The supplied-block
tests that exist (`FrontmatterRefusalTests.SuppliedBlockInvalid_*` at lines 70, 78 and 87, and
`CreateToolTests.ASuppliedBlockIsValidatedWholeRatherThanKeyByKey` at
`tests/VaultShared.Tests/Tools/CreateToolTests.cs:112`) use plain unquoted keys. The elevated
requirement on `agent.*` is asserted only through unquoted spellings, at
`tests/VaultShared.Tests/Frontmatter/FrontmatterAuthorityTests.cs:153` and at
`CreateToolTests.AKeyRequiringElevationIsAssertedAndReported` (`CreateToolTests.cs:124`, which sends
`---\nagent.mode: auto\n---`).

The regression is swapping the parse layer, changing `Spelling` to use the node's raw source text, or
moving key classification off the parsed tree onto the block's raw text with a regex or line scan
over `InnerText` - a tempting shortcut for "just tell me which keys are present". A supplied block
carrying `"agent.instructions": ...` then produces the literal key with the quote marks attached.
That string does not start with `agent.`, so the namespace rule does not match and no elevated
authority is asserted: the block is either refused as an undeclared key, which is loud and harmless,
or - if the registry lookup were also made quote-tolerant while the prefix match was not - written
with no elevation check at all. The whole suite still passes, because nothing feeds a quoted key.
This was a real authority bypass once in the system being replaced, which is why the case exists.

Verified; the original cited no lines on either side, and the sites above were located and read
directly. Old cases `FMW-078`, `FMW-079` and `CRD-047`.

### A caller-supplied block that parses to a YAML non-mapping has its own refusal message

`SuppliedTree` runs `FrontmatterTree.TryParse` over the caller's text and distinguishes two failure
modes on the resulting `TreeParse` (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:215-221`). Both
refuse with `RefusalReason.SuppliedBlockInvalid`, with different messages: `TreeParse.Unparsable`
gives "The supplied block will not parse as frontmatter." and anything else - in practice
`TreeParse.NotAMapping`, which a YAML sequence or a bare scalar produces - gives "The supplied block
is not a mapping of keys to values." The reason code is deliberately the same for both, because the
caller supplied the text either way; only the message tells them which mistake they made.

Only the unparsable half is asserted.
`FrontmatterRefusalTests.SuppliedBlockInvalid_TextThatWillNotParseIsACallerErrorRatherThanTheNotesProblem`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs:87`) supplies `"a: b\n  c: d\n\t- x"`
and asserts only the reason. No test supplies a block that parses cleanly as a YAML sequence or
scalar. The note-side counterpart is covered, at `FrontmatterRefusalTests.cs:54`
(`NotAMapping_AStoredBlockThatIsNotAMappingRefuses`, feeding `"---\n- a\n- b\n---\n"` as the note's
own content), and the parser's own distinction in isolation at `FrontmatterTreeTests.cs:54-56`.
`FrontmatterRefusalTests.EveryReasonIsReachedBySomeCaseInThisSuite` (`:31`) does not close this: it
checks that each `RefusalReason` name appears in some test method name, and `SuppliedBlockInvalid` is
already satisfied three times over by the fence cases.

The regression is the conditional collapsing - the `parse == TreeParse.Unparsable` test being
inverted, or the two message strings merged during a message-wording sweep. A caller who supplies
`- a\n- b` as a block is then told the text will not parse, which is false and points them at the
wrong fix: their YAML is valid, it is simply not a mapping. The reason code is unchanged, so no
assertion notices. The same collapse would go unnoticed if a future `TreeParse` member were added and
fell into the non-mapping arm.

The line range is widened from the original `217-221`, which names the throw and its message but
omits line 215's parse call and line 216's branch condition. No case identifier; the original named
the old system's `frontmatter_not_a_map` error code as the counterpart, which resolves nowhere.

### `FrontmatterWrite` does not normalize a caller-supplied replacement body

`Apply` normalizes the note's stored content on entry, CRLF to LF
(`src/VaultShared/Frontmatter/FrontmatterWrite.cs:47`, `NoteSplit.Normalize(request.Content)`), and
`NoteSplit.Of` normalizes internally as well. `request.NewBody` gets neither treatment: it is read
raw at `FrontmatterWrite.cs:91` (`var body = request.NewBody ?? split.Body;`) and handed straight to
the composer or spliced after the block, so a `NewBody` containing CRLF is composed into the note
exactly as given and produces a file with mixed terminators. No caller can reach this today.
`EditTool` normalizes every body it produces - wholesale replacement and both halves of a
find/replace, at `src/VaultShared/Tools/EditTool.cs:260`, `:265`, `:279` - and `CreateTool` takes its
body from `NoteSplit.Of` (`src/VaultShared/Tools/CreateTool.cs:124`, carried through at `:140`),
which has already normalized. The absence of a normalize call at this layer is a real hole covered
entirely by the discipline of two call sites.

Nothing asserts the body side.
`FrontmatterWritePathTests.LineTerminatorsNormalizeOnEntry`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterWritePathTests.cs:174`) is the only CRLF case on
this path, and it passes CRLF as the note's stored `Content` with no `NewBody` at all - it pins line
47, not line 91. No test passes a CRLF `NewBody` to `Apply`, nor asserts that either tool normalizes
the body it forwards.

The regression is a `NoteSplit.Normalize` call dropped from `EditTool.NewBody` - say the one at
`EditTool.cs:279`, on the replacement half of a find/replace - or a third consumer of `Apply` that
forwards a caller's body without normalizing. The write succeeds, the guard passes because it
re-splits the composed text and compares only the frontmatter's inner text, and a note lands on disk
with LF frontmatter and a CRLF body. The next read of that note goes through `NoteSplit.Of`, which
normalizes, so even a round-trip test would not see it; only the bytes on disk are wrong.

Verified, including `NoteSplit.Normalize` at `NoteSplit.cs:41` and both claimed consumer
normalizations. No case identifier; the related `FMW-045` is named elsewhere in the reconciliation
but not for this property.

### An undeclared key elsewhere in the block survives a write and is not reported

After the operations run, the write path collects violations only for the keys the operations
actually touched: the loop at `src/VaultShared/Frontmatter/FrontmatterWrite.cs:86` iterates the
`touched` list (built at `:78-79`) rather than the tree's whole key set, so an undeclared key sitting
elsewhere in the note's frontmatter is neither validated nor reported, and because nothing removed it
from the working tree it is re-emitted into the composed note untouched. Editing one key is never
blocked by an unrelated one.

The behaviour is fully pinned.
`FrontmatterWritePathTests.ViolationsCoverTheKeysTheOperationsTouchedAndNothingElse`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterWritePathTests.cs:242`) writes `doc.title` into a
note holding `undeclared.key: kept` and asserts both that the violation list is empty and that
`undeclared.key: kept` survives into the composed content. It would fail if the loop were widened to
the whole tree or if pass-through were lost.

This is a documentation gap rather than a coverage gap, and it is carried for that reason. The
rationale for the rule, per the old system's record, was measured: real notes carry Obsidian's own
`obsidianUIMode` key and must stay writable. That measurement is not recoverable from this
repository, and the comment at `FrontmatterWrite.cs:82-84` states the rule without naming a key or a
measurement. A future reader who finds the rule inconvenient has no evidence of why it exists and
could argue it away, with the test looking like an arbitrary fixture rather than a stand-in for a
real file Obsidian writes. No regression could be constructed that the suite would fail to catch; the
remedy here is a comment naming the motivating key, not a test. Old case `FMW-036`, which is
precisely the problem - the measurement lived only there.

## Registry and schema

### An undeclared key inside a declared namespace is refused, and still priced by the namespace

Two independent answers are computed for a key, and nothing merges them. Admission is
`registry.Keys.TryGetValue`: a key the declaration does not carry is refused `UnknownKey` before its
shape, its value or the caller's authority is considered, so `agent.smuggled` is refused even under a
grant holding every operation class - on the per-operation path
(`src/VaultShared/Frontmatter/FrontmatterWrite.cs:273`, above the shape switch at `:277` and above
the authority assertion entirely) and on the supplied-block path (`:231-241`, reached before the
block's authority is asserted at `:246`). Pricing is separate: `KeyAuthority` (`:655-671`) consults
the key map for a declared `write_requires` at `:660` and then, at `:665-667`, adds every namespace
rule whose prefix the key matches, whether or not the key is declared. Failing to match a namespace
is not evidence that a key needs no authority.

Nothing asserts either half.
`FrontmatterAuthorityTests.ANamespacePrefixAddsToTheRequirementForAKeyDeclaringNone`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterAuthorityTests.cs:45`) uses `agent.mode`, which the
synthetic registry declares (`tests/VaultShared.Tests/Frontmatter/SyntheticVocabulary.cs:73`), so it
exercises the declared-key-plus-namespace path rather than the undeclared one. A repository-wide
search of `tests/` for `agent.*` keys returns only `agent.mode` and `agent.instructions`, both
declared. The unknown-key refusal tests (`FrontmatterRefusalTests.cs:162`, `:171`) use
`invented.key`, which matches no namespace rule.

The regression is "simplifying" `KeyAuthority` by folding the namespace loop inside the
`TryGetValue` success branch - a natural-looking cleanup, since the loop reads as dead work for a key
the map already priced. Every test passes, because every test's namespaced key is also declared. An
undeclared `agent.*` key would then be priced at nothing. It is unreachable through the write path
today, because the allowlist refuses first, so the consequence is latent - but the day a second
caller of `KeyAuthority` appears, or the allowlist order changes, it becomes an authority bypass with
no failing test between it and the change. Moving the shape or value checks above the allowlist is
the other exposure: an undeclared key would be refused for the wrong reason under a full grant, and
nothing asserts the ordering under elevation.

One correction: the reconciliation's summary cited `KeyAuthority` at `FrontmatterWrite.cs:637-653`,
which is `Required`, the caller. The method begins at 655 and its body runs to 671. Old cases
`REG-010` and `REG-028`.

### An impossible but well-shaped date is rejected, because the date constraint parses rather than pattern-matches

`DateConstraint.Check` (`src/VaultShared/Frontmatter/ValueConstraint.cs:64-67`; the record is
declared at `:57` and the default `yyyy-MM-dd` format at `:61`) calls `DateTime.TryParseExact` under
the invariant culture with `DateTimeStyles.None`. That rejects a value that fits the format's shape
but names no real day - `2026-13-45`, `2026-02-30` - as well as one that does not fit the shape. The
constraint's own summary says it matches a format without parsing it into a date, meaning it never
yields a `DateTime` to the caller and never rewrites the caller's text; the parse still runs, and
calendar validity is a side effect of using the parser as the matcher.

Only the shape half is asserted. `ValueConstraintTests.Date_IsAFormatCheckOnTheTextAsWritten`
(`tests/VaultShared.Tests/Frontmatter/ValueConstraintTests.cs:17`, rows at `:12-16`) covers
`2026-08-02` accepted and four rejections - `2026-8-2`, `02/08/2026`, `2026-08-02T00:00:00`, `not a
date`. Every rejection row is a shape failure that a format-only regex would also reject. No row is a
well-shaped impossible date, and grepping the whole test tree finds no impossible-date literal
anywhere.

The regression is rewriting `DateConstraint` as a compiled regex - attractive, since the summary says
the check is a format match and a regex reads as cheaper and more obviously non-coercing. Every
existing row still passes, and `doc.created: 2026-13-45` silently becomes a legal value in a vault.
Nothing downstream re-checks it, so a sort or a range query over `doc.created` later meets a value
that will not parse.

Verified in code and test citation. Old case `REG-017`.

### The control-character refusal beats the key's own value constraint

For a scalar set, the operation's value is screened for control characters before the declared
constraint is applied: `RefuseControlCharacters(set.Value, ...)` is the first statement of the
`SetScalar` arm (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:280`), ahead of the shape check at
`:281` and the constraint check at `:285`. A value that carries a control character and also fails the
key's constraint is refused `ControlCharacter`, attributed to the value, rather than `ValueRefused`
attributed to shape. The same ordering holds one level up: the key's own text is screened at `:270`,
above the allowlist lookup at `:273`, so a control character in a key name is a hygiene refusal rather
than an unknown-key refusal.

The refusal is asserted; the ordering is not.
`FrontmatterRefusalTests.ControlCharacter_InAScalarValueRefusesRatherThanStripping`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs:126`) sets `doc.title` to
`"Widget[31m"` and asserts reason, operation index and key - but `doc.title` is declared with a
`StringConstraint` (`SyntheticVocabulary.cs:69`), which admits everything, so the value fails only
the control-character check and the test cannot distinguish the two orderings.
`ControlCharacter_InAnEntryFieldRefusesToo` (`:136`) has the same property, its `routed` field also
carrying a `StringConstraint`. No test sends a control character into a constrained key -
`doc.created`, a date, or `doc.type`, a character class - and none sends one in a key name.

The regression is moving the control-character screen below the constraint check, while reordering
the arm so the cheap shape test runs first, or while consolidating hygiene into one pass at the end.
The refusal then reports `ValueRefused` with prose about a date format for a value whose real problem
is an escape sequence, and the caller's remedy is wrong: it reformats the date and resubmits the same
smuggled control characters. All current tests pass, because their values satisfy every constraint
they are checked against. The key-level ordering has the same exposure: `doc.title` would report as
an undeclared key, and the refusal would helpfully enumerate the declared keys for a caller whose
real problem is that it is emitting control characters.

The original line numbers are stale and the claim is correct. It cited `FrontmatterWrite.cs:272-279`,
which holds the allowlist lookup and the opening of the switch, containing neither the value screen
at `:280` nor the constraint check at `:285`. Old case `REG-021`, its ordering aspect.

### A block-list key holding an empty inline value reports a shape violation rather than passing

Shape is read from the YAML node kind, not from the value. A key declared `block_list` that the note
writes as a scalar - including `doc.notes: ''`, `doc.notes: '   '`, or `agent.instructions:` with
nothing under it - is a `YamlScalarNode`, so `ShapeOf`
(`src/VaultShared/Frontmatter/FrontmatterTree.cs:167-173`, answering `Block` only for a
`YamlSequenceNode` or `YamlMappingNode`, with the enum's own documentation at `:12-17`) answers
`Scalar`, and `CheckKey` reports `InlineValueWhereBlockListDeclared` and returns immediately
(`src/VaultShared/Frontmatter/FrontmatterChecks.cs:143-149`). It does not fall through to the entry
walk and it does not pass as an empty list. On the write path that violation is mapped to a hard
refusal rather than reported as data (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:736`).

Only the non-empty arm is asserted.
`FrontmatterValidatorTests.AnInlineValueWhereABlockListIsDeclared`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterValidatorTests.cs:88`) asserts the rule for
`doc.notes: text`. `AnEmptyBlockList` (`:82`) covers `doc.notes: []`, which is a sequence node and
takes the different `EmptyBlockList` branch at `FrontmatterChecks.cs:152-157`.
`ADeclaredKeyCarryingNoValue` (`:76`) covers the empty-value case for a scalar key (`doc.title:`),
not for a block-list key. Nothing supplies `doc.notes: ''`, `doc.notes:` with nothing under it, or a
whitespace-only inline value under a block-list key, in the validator suite or the refusal suite.

The regression is an emptiness short-circuit added to `CheckKey`, treating an empty scalar under a
block-list key as "no entries yet, nothing to check", which reads as reasonable and is the shape a
null-tolerant refactor produces. `agent.instructions:` with nothing under it then validates clean and
stops refusing on the write path, and the `doc.notes: text` test still passes because its value is
non-empty. Concretely, a note that lost its whole instruction list to a bad write reports as valid
rather than as damaged. The reverse direction is exposed too: if `ShapeOf` were changed to answer
`Block` for a valueless key, the violation would silently become `EmptyBlockList` and any caller
branching on the rule name would take the wrong remedy.

Verified as far as this repository can show. The claim that the arm was untested in the old suite as
well is transcribed rather than verified, the old suite being unreachable. Old case `REG-051`, its
second arm.

## Links, lock and result

### Shared-folder depth compares folder names case-insensitively

When wikilink ranking measures how many leading folders a candidate shares with the linking note's
own folder, it compares each pair of folder segments with `StringComparison.OrdinalIgnoreCase`
(`src/VaultShared/Access/WikilinkRanking.cs:166`, inside `SharedDepth`; the remarks stating the rule
are at `:151-156`). Two folders whose names differ only in case count as the same folder, so a
candidate under `Aa/` shares a prefix with a linking note under `aA/` and lands in the inside-the-
subtree group rather than the climbing group. The vault sits on a case-insensitive filesystem, where
two folder names differing only in case are one folder. This is a separate question from the final
tie-break, which is case-sensitive (`string.CompareOrdinal` on the two vault paths).

Nothing asserts it. `WikilinkRankingTests.cs` has thirteen tests and no fixture presents a folder
segment that differs from the linking note's corresponding segment only in case.
`TheTieBreakIsOrdinalRatherThanCultural` (`tests/VaultShared.Tests/WikilinkRankingTests.cs:145`)
looks like it covers this and does not: its candidates are `Aa/Dupe.md` and `aa/Dupe.md`, but its
linking note is `Note.md` at the vault root, so `from.Segments.Count - 1` is zero, the `while` loop in
`SharedDepth` never executes and no case comparison is made. That test pins the tie-break. Ranking is
also reached indirectly through `WikilinkUniverse.Resolve`
(`src/VaultShared/Links/WikilinkUniverse.cs:148`), but no folder name used in
`tests/VaultShared.Tests/Links/` differs from another only in case either.

The regression is "tightening" `SharedDepth` to `StringComparison.Ordinal` - easy to talk yourself
into, since the tie-break three lines away in the same file is deliberately ordinal. A vault whose
folder casing drifted (`Projects/` in one note's path, `projects/` in another's, indistinguishable on
Windows and macOS) then computes shared depth zero instead of one. The candidate moves from the
inside group to the outside group and loses to a candidate it used to beat, so a bare wikilink
silently opens a different note and `WikilinkRepair` rewrites bare links against the wrong winner.
The whole suite stays green.

Verified. Old case `LNK-029`, recorded as an untested branch in the old system too.

### Ranking's total order makes the result independent of the input order

`WikilinkRanking.Rank` returns the same ordering whatever order the candidates arrive in, by
construction rather than by luck: `Compare` falls through, after the group split and the
spelling-length comparison, to `string.CompareOrdinal(a.Value, b.Value)`
(`src/VaultShared/Access/WikilinkRanking.cs:114`), which is never zero for two distinct vault paths -
and candidates are distinct paths. The comparison is a total order, so `set.Order(...)` at `:67`
cannot be swayed by discovery order. `Compare`'s remarks at `:98-99` state this.

Nothing asserts it directly. No test in `WikilinkRankingTests.cs` presents one candidate set twice in
two orders and asserts the same result. Each test passes its candidates in a single order, usually one
that differs from the expected output, which proves the sort does something but not that a third
permutation would agree. `TheOwnFolderWinsWhicheverWayTheSubfolderSorts`
(`tests/VaultShared.Tests/WikilinkRankingTests.cs:92`) sounds like an order-independence test and is
not: it varies the subfolder's name, `Aaa` against `Zzz`, to show the ordinal comparison is not
consulted while the spellings differ in length.

The regression is an edit that gives `Compare` a path returning zero for two distinct candidates -
replacing `string.CompareOrdinal(a.Value, b.Value)` with a comparison of just the basename, or of the
relative spelling text, both of which are equal across candidates by definition here, since they are
same-named notes. The order then depends on `Order`'s stability and hence on the order
`WikilinkUniverse` enumerated the candidates, which depends on directory-walk order. Resolution stops
being deterministic across machines, and since repair rewrites a bare link only where it resolves to
the relocated item, two machines repair the same vault differently. Every existing test still passes,
because each one fixes the input order.

Verified. Old case `LNK-023`.

### The write lock is re-entrant on the thread that already holds it

The hold is a named `Mutex` (`src/VaultShared/Tools/VaultWriteLock.cs:67`,
`new Mutex(initiallyOwned: false, lockName)`), which the CLR makes re-entrant for its owning thread:
a second `Acquire` on the same thread with the same lock name succeeds immediately rather than
deadlocking against the outer hold, and the mutex is free only once matched by an equal number of
`ReleaseMutex` calls (`Hold.Dispose` at `:94-97`). `VaultWriteLock` relies on this OS and CLR
property rather than tracking ownership itself, and its remarks at `:14-15` record the related
constraint that the hold is synchronous and stays on one thread, a `Mutex` being thread-affine.

Nothing asserts it. `tests/VaultShared.Tests/Tools/VaultWriteLockTests.cs` never nests two acquires.
Its helper `FreeElsewhere` (`:130`) deliberately asks from another thread, and its doc comment names
re-entrancy as the reason - so the property is acknowledged in the suite as an obstacle to avoid,
never as something asserted. `ToolExecutionTests.cs:347-365` checks only that the lock is held during
a mutating invocation and free after.

This one would not break silently. Nothing in the current design nests a hold, so there is no caller
to regress: `ToolExecution` takes the lock once around a whole invocation and no tool acquires it
again inside. If someone later added a nested acquire and the implementation had meanwhile moved to a
`SemaphoreSlim` or an ownership flag, the failure would be a deadlock or a timeout refusal, which is
loud. It is carried because the entry must be carried; a test here pins a CLR guarantee. Old case
`LNK-039`.

### The abandoned-mutex signal fires once and only for the acquire that inherits it

When a lock owner dies without releasing, the next `WaitOne` throws `AbandonedMutexException`, which
`VaultWriteLock` catches, treats as success - the wait succeeded and this thread now owns the mutex -
and reports through `onInherited` (`src/VaultShared/Tools/VaultWriteLock.cs:76-82`). The signal is
one-shot: the OS clears the abandoned state when it delivers it, so a subsequent acquire of the same
lock name gets a clean wait and reports nothing. Nothing in `VaultWriteLock` tracks or suppresses a
repeat; the one-shot behaviour comes entirely from the OS.

It is asserted in part, and not this part.
`VaultWriteLockTests.Acquire_AfterAnOwnerDiedWithoutReleasing_ProceedsAndReports`
(`tests/VaultShared.Tests/Tools/VaultWriteLockTests.cs:67`) abandons a mutex from a dying thread and
asserts via `Assert.Single(reported)` that exactly one message arrived - but only one acquire happens
in that test, so `Single` pins "the inheriting acquire reports once", not "a later acquire reports
nothing". `Acquire_ForAVault_NamesTheVaultAndTheMutexInTheInheritedMessage` (`:100`) has the same
single-acquire shape. No test acquires the same lock name a second time after an inheritance and
asserts the callback stayed quiet.

The regression is state added to make the signal "more reliable" - a flag on the vault, or a
re-throw-and-retry loop around the wait that ends up invoking `onInherited` on each pass. The
operator then sees an inherited-lock warning on every subsequent write to a vault that had one crash,
the warning stops meaning "a process just died mid-write", and the operator learns to ignore the line
that exists to send them to `git status`. No assertion counts callbacks across two acquires.

Verified. Old case `LNK-044`.

### The lock name derivation has no second process to agree with

`Vault.LockName` (`src/VaultShared/Access/Vault.cs:66-76`) derives a system-wide mutex name from the
vault's root directory: `Path.GetFullPath(Root)`, trailing separators trimmed, lowercased
invariantly, SHA-256 hashed, first 32 hex characters, prefixed `Global\VaultWrite-` on Windows and
`VaultWrite-` elsewhere. Its remarks at `:53-59` call this a cross-process contract and name
`VaultWatcher` as the second holder it exists for, and `VaultWriteLock`'s remarks repeat the claim.
That second holder does not exist: `src/VaultWatcher/Program.cs:5-8` writes one line to the console
and returns, and `src/VaultWatcher/VaultWatcher.csproj` carries no `ProjectReference` to
`VaultShared` at all. The contract has exactly one implementation, so nothing can drift out of
agreement with it, and nothing is proving the two sides agree.

The derivation itself is asserted thoroughly, by five tests in `VaultLockNameTests` inside
`tests/VaultShared.Tests/VaultResolutionTests.cs`:
`LockName_IsThePrefixAnd32UppercaseHexCharacters` (`:133`),
`LockName_IsTheSha256OfTheLowercasedRoot` (`:144`),
`LockName_IgnoresCaseAndTrailingSeparatorsInTheConfiguredRoot` (`:155`),
`LockName_DerivesFromTheLocationNotTheName` (`:167`) and `LockName_DiffersBetweenVaults` (`:177`).
What is unasserted is the cross-process half, because no second process exists. The only production
consumer is `VaultWriteLock.Acquire(Vault, ...)` at `src/VaultShared/Tools/VaultWriteLock.cs:34`,
in-process.

Nothing can break today. The exposure is deferred: whoever implements `VaultWatcher` may reimplement
the derivation in its own code rather than referencing `VaultShared` - which the remarks explicitly
permit - and get one step wrong. Omitting the lowercasing, taking 32 bytes instead of 32 hex
characters, or dropping the `Global\` prefix produces a different name. The two processes then hold
different mutexes over the same files, each believing it has exclusive access, and the failure is a
torn vault under concurrent write with no error anywhere. The five tests test this side only.

Verified and reinforced: a repository-wide search for `LockName` finds consumers only in
`VaultShared` and in tests. No case identifier.

### Address's empty-component and trailing-separator rules are already asserted

`WikilinkTarget.Address` returns null for a target with an empty component anywhere but the leading
root anchor (`src/VaultShared/Access/WikilinkTarget.cs:92-93`,
`if (component.Length == 0) return null;`). `//Delta`, `./Delta/`, `./`, `/`, `//` and `../` all name
nothing; only the empty text before a single leading separator is read as a root anchor. Climbing
past the vault root is clamped rather than refused (`:100-101`, the `if (segments.Count > 0)` guard,
with `:108` returning null when the walk leaves nothing), so `../../../../Delta` written two folders
deep still addresses `Delta`. The rules are stated in the remarks at `:60-64`.

This is asserted directly against `Address`, contrary to what the original claimed.
`WikilinkAnchoringTests.AnEmptyComponentAnywhereButTheAnchorNamesNothing`
(`tests/VaultShared.Tests/WikilinkAnchoringTests.cs:107`) is a theory whose six rows are exactly
`//Delta`, `./Delta/`, `./`, `/`, `//` and `../`, each asserted `Assert.Null(Address(target))`.
`ClimbingPastTheVaultRootIsIgnored` (`:80`) covers the clamping and
`AnAnchorThatClimbsToTheRootAndNamesNothingElseNamesNothing` (`:115`) covers the degenerate `..` from
the root. `WikilinkMatchingTests.ARootedOrEmptyComponentMatchesNothing`
(`tests/VaultShared.Tests/WikilinkMatchingTests.cs:76`) asserts a different property - that
`WikilinkTarget.Matches`, which receives text already normalized by `Address`, does not accidentally
match a raw `/Alpha` or `Notes//Alpha`. It is defence in depth on `Matches`, not a stand-in for
coverage of `Address`.

There is no gap to regress against; the theory rows fail immediately if the empty-component rule
changes.

The original is stale. Its second half claimed the `//Delta` and trailing-separator forms are
asserted through `WikilinkMatchingTests.ARootedOrEmptyComponentMatchesNothing` rather than through
`Address` itself. Both forms are asserted through `Address`, as rows at
`WikilinkAnchoringTests.cs:101-102`. Acting on it would have duplicated tests that already exist. No
case identifier.

## Read, glob and grep

### Scoped grep is covered at the files granularity only

`grep` never refuses for scope. The caller names no path, only a `path_glob` pattern, so every path
the search engine emits is re-parsed as a vault path and passed through one predicate before it can
appear in the answer. That predicate - `VaultAccess.Filter(bool includeDotEntries, bool notesOnly)`
at `src/VaultShared/Access/VaultAccess.cs:180` - composes the dot rule, the `.md` test and
`Admits(path)`, and it is the identical predicate the `glob` tool's enumeration is filtered by. It is
handed to all three grep surfaces (`VaultAccess.cs:134` for `SearchLines`, `:144` for `SearchFiles`,
`:153` for `SearchCounts`) and to enumeration at `:113`, and applied inside the result parse at
`src/VaultShared/Access/VaultSearch.cs:237`, `:272` and `:306`. A note the grant cannot read is
absent from `grep`'s output at all three granularities, nothing in the answer says filtering
happened, and a search whose only matches lie outside the grant returns empty text rather than a
refusal.

The property itself is asserted. `ScopeFloorDispatchTests.Grep_ReturnsTheUnionOfTheGrantsRootAndTheFloorAndNothingElse`
(`tests/VaultMcpServer.Tests/ScopeFloorDispatchTests.cs:147`) drives a grant scoped to
`Projects/Widget` with an always-readable floor at `Resources/Vault/Guides` through `grep` over HTTP
and asserts the file-granularity result set exactly, not by containment, against the five reachable
notes. `Grep_AgainstAnOutOfScopeNotesOwnContent_AnswersEmptyRatherThanRefusing` (`:161`) asserts that
a pattern matching only an out-of-scope note answers empty text with `isError` false. The glob side
is `tests/VaultShared.Tests/Tools/SearchToolTests.cs:95`
(`Glob_FiltersWhatTheGrantCannotReadWithoutSayingSo`).

What remains unasserted is narrower. The scoped grep coverage is at the files granularity only. No
test drives a scoped grant through `grep` at the `lines` or `counts` granularity, and each
granularity is a separate engine invocation with its own output parse - `VaultSearch.Matches` and
`VaultSearch.Counted` - applying `admit` at its own call site. A change that drops the `admit` call
from `VaultSearch.Matches` (`VaultSearch.cs:306`) or from `VaultSearch.Counted` (`:272`) while
leaving it in `Paths` (`:237`) keeps the suite green while `grep` at the default `lines` granularity
leaks the text of notes outside the grant.

The original was stale in its central claim, that no test drives a scoped grant through `grep`; two
do, both in `tests/VaultMcpServer.Tests/ScopeFloorDispatchTests.cs`. The likely cause is that the
reconciliation's own header listed only `tests/VaultShared.Tests/Tools/{ReadToolTests,
SearchToolTests}.cs` and two `VaultCli.Tests` files as read, so the MCP dispatch tests were never
opened. `SearchToolTests.cs:95` and `VaultAccess.Filter` were cited correctly. Old case `RDT-063`.

### grep and glob return results in the search engine's order, and nothing reorders them

No sort exists anywhere on the read path. `VaultSearch` appends results to a list in the order the
engine emitted them, in `Paths`, `Counted` and `Matches`
(`src/VaultShared/Access/VaultSearch.cs:238`, `:273`, `:309`); `VaultAccess` passes the list through
untouched; and both tools join it with `\n` in that same order
(`src/VaultShared/Tools/GlobTool.cs:66`, `src/VaultShared/Tools/GrepTool.cs:109`).
`SearchOutcome<T>.Results` documents the guarantee at
`src/VaultShared/Access/SearchOutcome.cs:27` - "The results in the engine's order, filtered to what
the caller may read."

Nothing asserts it, and every result assertion in the suite sorts before comparing.
`tests/VaultShared.Tests/Tools/SearchToolTests.cs:34` defines
`Lines(string text) => [.. text.Split('\n').Order()]`, and every glob and grep assertion in that file
goes through it. `tests/VaultMcpServer.Tests/ScopeFloorDispatchTests.cs:112` defines its own sorting
`Lines` helper, and its doc comment at `:46` says outright that the search engine's own ordering is
not part of the contract. The dispatch assertions at
`tests/VaultMcpServer.Tests/VaultToolDispatchTests.cs:118` and `:128-130` compare single-line
results, which carry no ordering information.

The regression is an `.Order()` or an `OrderBy(p => p.Value)` added inside `VaultSearch` or either
tool, to make output tidy or to stabilize a flaky comparison. The whole suite still passes, and the
documented engine-order guarantee at `SearchOutcome.cs:27` becomes false with nothing to say so. A
caller relying on the engine's per-file match ordering - line 1 before line 3 within a file - would be
the one to notice.

Verified. `SearchToolTests.cs:34-35` is exactly the sorting helper described, and the engine-order
guarantee is documented on `SearchOutcome.Results` at line 27 rather than 41; the reconciliation's
`SearchOutcome.cs:41-48` citation elsewhere points at `Unreadable`, which is correct for that other
claim. Old case `RDT-087`.

### Neither read nor glob nor grep caps, pages, offsets or truncates its answer

There is no result cap, no page size, no offset, no line range and no truncation anywhere in the
three read-class tools or the search layer. `read` returns the whole note, the whole frontmatter
block or the whole body - the `ReadPart` enum (`src/VaultShared/Tools/ReadTool.cs:9`) is the entire
addressing vocabulary and its own summary states there is no line range and no section addressing,
and `ReadArguments` (`:34`) has four members, none a limit. `GlobArguments`
(`src/VaultShared/Tools/GlobTool.cs:13`) has two members and `GrepArguments`
(`src/VaultShared/Tools/GrepTool.cs:30`) four. The engine is never invoked with `--max-count`, `-m`,
`--max-filesize` or any other limiting flag: the invariant argument list is
`--no-config --hidden --no-ignore` (`src/VaultShared/Access/VaultSearch.cs:25`) and the four
`engine.Run` calls at `:50`, `:69`, `:84`, `:98` add nothing of the kind.

Nothing asserts it, and by its nature nothing can assert it directly - the property holds because no
such argument exists to pass, not because anything checks. The closest adjacent coverage is
`tests/VaultMcpServer.Tests/NativeToolSchemaTests.cs:466-467`, which asserts the exact advertised
argument name lists (`read` as `path, vault, part, with_content_hash`, `grep` as
`pattern, vault, granularity, path_glob`), so a new argument named on the wire would fail it. It says
nothing about an internally-applied cap.

The regression is a defensive internal truncation with no argument behind it - a `.Take(1000)` in
`GrepTool.Render`, a `--max-count` appended to `VaultSearch.Always`, or a byte cap in
`VaultFiles.Read` - added to protect a transport from a large answer. The schema test still passes
because no argument name changed, and every functional test uses fixture vaults of three to five tiny
notes, so no assertion comes near any plausible limit. A caller gets a silently incomplete answer
indistinguishable from a complete one, which is the exact failure mode `SearchOutcome.Unreadable`
exists to prevent on the other axis.

Verified; the absence is real across all five files. Old case `RDT-088`.

### A read's content hash is stable across an unchanged re-read, and changes when only frontmatter changed

`read`'s `content_hash` is SHA-256 of the file's stored bytes in lowercase hex, taken from the same
single byte read the returned content was decoded from: `VaultFiles.Read` calls `File.ReadAllBytes`
once and then `Decode(bytes)` and `Digest(bytes)` from that same array
(`src/VaultShared/Access/VaultFiles.cs:56-57`, with the digest itself at `:528`,
`Convert.ToHexStringLower(SHA256.HashData(bytes))`), and the tool surfaces it at
`src/VaultShared/Tools/ReadTool.cs:116-117`. Two consequences follow that nothing states as a test:
reading the same unchanged file twice returns the identical hash both times, and a change confined to
the frontmatter block changes the hash, because the digest is over the whole file's bytes rather than
over the part that was requested.

The load-bearing half is asserted and this pair is not.
`ReadToolTests.TheHashCoversTheWholeFileWhicheverPartWasRead`
(`tests/VaultShared.Tests/Tools/ReadToolTests.cs:118`, a theory over `all`, `frontmatter` and `body`)
asserts that all three parts return the whole file's digest, which is what stops a partial read from
authorizing a whole-file write. `TheHashDescribesTheBytesTheContentCameFromRatherThanALaterState`
(`:133`) asserts a single read's hash does not track a later rewrite, and `:145` asserts the hash is
64 lowercase hex characters. Nothing reads the same unchanged file twice and compares the two hashes,
and nothing captures a hash, applies a frontmatter-only change and asserts the hash moved. The
nearest adjacent coverage is cross-tool rather than read-to-read:
`tests/VaultShared.Tests/Tools/EditToolTests.cs:351`
(`TheReturnedHashIsTheOneAReadOfTheFileAnswers`) and
`tests/VaultMcpServer.Tests/EditDispatchTests.cs:190` (`TheHashAnEditAnswersIsTheOneAReadReturns`)
assert an edit's returned hash equals what a later read answers, and `EditToolTests.cs:372`
(`ANoOpAnswersTheHashTheFileAlreadyHad`) asserts a no-op edit returns the pre-existing hash. None of
these is a second `read`.

Making the digest cover something other than the raw stored bytes - the decoded text after a BOM was
stripped, or only the part the caller asked for - is caught in the second case by the whole-file
theory, which compares against `DigestOf(_vault.BytesOf(...))`. What is not covered is
nondeterminism: a digest that folded in the read time, a path or any per-call value returns a
different hash on each read, no test notices, and every read-then-guarded-write chain silently
breaks. That is the concrete regression to name.

Substantively verified; the line number drifts. The original cited `ReadToolTests.cs:108`, a blank
line - the test it describes has its doc comment at `:109` and its signature at `:118`. Old case
`RDT-021` and the first half of `RDT-023`.

### A directory whose own name ends in .md fails as read_failed, not path_not_found

`read`'s extension rule is a pure string test on the caller's own text, run before resolution and
before any filesystem contact: `ToolPaths.RequireNoteExtension`
(`src/VaultShared/Tools/ToolPaths.cs:157-158`) returns on any `.md` suffix, case-insensitively, with
no further check. Nothing probes whether the leaf is a file or a directory; no filesystem probe
exists on the read path at all. A caller naming a directory literally called `Something.md` gets past
the operand rule and past scope resolution, and the failure comes from the platform when the bytes
are read: opening a directory as a file raises `UnauthorizedAccessException`, classified as
`ReadOutcome.Failed` rather than `ReadOutcome.NotFound`
(`src/VaultShared/Access/VaultFiles.cs:63-66`, against the `NotFound` filter at `:59-62`), and
reaching the caller as `read_failed` carrying the path (`src/VaultShared/Tools/ReadTool.cs:108-111`)
- the same answer a permission-denied file gives, not the `path_not_found` a caller might expect.

Nothing asserts it. `tests/VaultShared.Tests/Tools/ReadToolTests.cs` builds its fixture entirely from
`WithNote(...)` calls (`:17-20`) and creates no directory with a `.md` name. `ReadToolTests.cs:156`
covers a missing note as `path_not_found`, `:164` a missing folder along the way as the same code,
and `:175` an existing-but-unopenable file as `read_failed`.

The regression is thin, and this is the weakest entry in the area. A change that added a
`File.Exists` probe before the read in order to return `path_not_found` for a nonexistent target
would flip this case's answer with no test objecting - but that same change reintroduces the
check-then-act window the layer deliberately avoids and would be caught by review. The likelier
silent break is platform drift: the case rests on .NET mapping "open a directory as a file" to
`UnauthorizedAccessException` on Windows and on Unix. If a runtime surfaced it as `IOException` the
answer stays `read_failed`; if it surfaced it as `DirectoryNotFoundException` the answer becomes
`path_not_found` on one platform and not the other, and nothing would catch the divergence. A caller
cannot create such a directory through this system - `create` refuses a `.md` name for a folder via
`ToolPaths.RequireNoNoteExtension` (`ToolPaths.cs:127-131`) - so it can only arise from a directory
the editor or a human made outside the tools.

Verified, though the original cited no line numbers at all and the code sites above were located
during extraction. The behaviour is a reasoned trace through `File.ReadAllBytes` on a directory
rather than something executed. No case identifier.

## Edit

### A body-only edit succeeds on a note whose frontmatter block will not parse

`FrontmatterWrite.Apply` parses the note's existing frontmatter block only when the write actually
interprets frontmatter - when at least one frontmatter operation was submitted or a wholesale block
was supplied. `var interprets = operations.Count > 0 || supplied is not null;`
(`src/VaultShared/Frontmatter/FrontmatterWrite.cs:61`, with lines 62-63 selecting
`FrontmatterTree.Empty()` over `Parse(split)` when `interprets` is false, and the justifying comment
at `:57-60`) means a request carrying neither - a body-only edit, whether a wholesale body
replacement or a substitution - never parses the block, so a note whose block is malformed YAML is
still editable in its body. The block's bytes pass through untouched: with no operation having run,
the write path concatenates the note's original prefix up to the body offset with the new body rather
than reserializing anything (`:113`).

Nothing pins the malformed-block case on the body-only path.
`EditToolTests.ABodyOnlyEditWithNoDeclaration_StillRuns`
(`tests/VaultShared.Tests/Tools/EditToolTests.cs:467`) is the nearest test and covers a different
axis, a missing registry declaration rather than an unparsable block.
`FrontmatterRefusalTests.cs:111` uses an unparsable block (`---\nnot: [a mapping\n---\n`) but only to
show that the path exclusion is decided ahead of the parse; that case refuses for the path, so it
does not exercise the success. No test in `EditToolTests`, `FrontmatterWritePathTests` or
`FrontmatterRefusalTests` sends a body-only edit against a note with a broken block and asserts it
succeeds.

The regression is hoisting the `Parse(split)` call out of the conditional - an attractive
simplification, since the ternary computes `Parse(split)` twice in adjacent lines and collapsing it
to one unconditional parse looks like pure cleanup. Every body-only edit to a note with a malformed
block then starts refusing `BlockInvalid` and the suite stays green. This is exactly the class of
note a caller most needs to be able to edit, so the regression would be user-visible and hard to
attribute.

Verified; the original cited `FrontmatterWrite.cs:58-63`, one line short at the top. No case
identifier.

### Nothing drives the dot-segment exclusion through the `edit` tool

An edit naming a path with any dot-prefixed segment is refused, whatever the edit does. `edit`
resolves its operand through `ToolPaths.WriteNote` (`src/VaultShared/Tools/EditTool.cs:149`,
resolving through `ToolPaths.cs:50-57`, where `vault.ForWrite(path)` returning non-`Admitted` becomes
`ToolRefusalEnvelope.Translate(resolution)`), so a dot path is refused before the write path is ever
entered. `FrontmatterWrite` then refuses the same class again as its first validate-phase act
(`FrontmatterWrite.cs:53` calling `RefusePathExclusion`, defined at `:168-177`, which refuses any
segment starting with `.`), so the guarantee holds on both the tool path and any direct substrate
call. A body-only edit is covered by the tool-level check but not by the substrate check, because the
substrate check runs on a call the tool never reaches for an excluded path.

The substrate is covered: `FrontmatterRefusalTests.PathExcluded_ADotSegmentRefusesWhateverTheOperation`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs:99`, path `.obsidian/Notes/Note.md`)
and `PathExcluded_IsDecidedBeforeAnythingElseAboutTheCall` (`:107`, path `Notes/.hidden/Note.md`),
both driving a `set` operation, neither body-only. The access layer is covered:
`VaultAccessTests.cs:342` asserts `ForWrite(".obsidian/Notes.md")` is `Excluded`, and
`VaultPathTests.cs:90,103` cover the spellings. Through `edit`, nothing: `EditToolTests` contains no
dot-segment path at all, and its two out-of-band-path cases (`ANamedPathOutsideTheGrant_RefusesLoudly`
at `:553` and `ANoteReachableOnlyThroughAnAlwaysReadableRoot_RefusesTheWrite` at `:568`) both assert
`OutOfScope`, a different refusal reached by a different route. Other tools do have tool-level
coverage - `ReadToolTests.cs:213` and `CreateToolTests.cs:290` assert `ToolCodes.PathExcluded` - so
`edit` is the hole rather than the rule being untested everywhere.

The regression is `EditTool` ceasing to resolve through `ToolPaths.WriteNote` - changed to take an
already-resolved path from a caller, or to resolve leniently and let the substrate decide - on a
body-only edit. The substrate check still fires for any edit carrying a frontmatter operation, so
`FrontmatterRefusalTests` stays green while a body-only edit to `.obsidian/anything.md` starts
writing to files the tooling does not own. The exposure is narrow, and it is a containment boundary.

Verified. Old cases `EDT-095`, `EDT-096` and `EDT-097`, `EDT-096` being the exact claim that a
body-only edit to an excluded path is refused.

### A no-op write leaves the file's timestamp alone

`EditTool.Store` hashes the composed note and compares it to the hash the file had when it was read.
Equal hashes mean the write would store the bytes already on disk, so the write is skipped entirely -
`VaultFiles.Replace` is never called - and the stored hash is returned unchanged
(`src/VaultShared/Tools/EditTool.cs:313-314`,
`if (string.Equals(composed, storedHash, StringComparison.Ordinal)) return storedHash;`). The
consequence the code's own remarks claim at `:305-309` is that an edit which turned out to be a no-op
does not disturb the file's modification time.

The hash half is asserted. `EditToolTests.ANoOpAnswersTheHashTheFileAlreadyHad`
(`tests/VaultShared.Tests/Tools/EditToolTests.cs:372`) asserts the returned hash equals the pre-edit
hash, that `AppliedOperations` is empty and that both `*Changed` flags are false. Nothing in the whole
test tree touches file timestamps - a search for `LastWrite`, `Timestamp` or `mtime` across `tests/`
returns no hits - so the property the remark claims is asserted nowhere.

The regression is the skip being removed or inverted, for instance on the reasoning that `Replace`
should always run so the returned hash comes from a single source, since `Replace` computes the
identical hash for identical bytes. `ANoOpAnswersTheHashTheFileAlreadyHad` still passes, because the
hash is unchanged either way; that is what makes the existing test blind here. What breaks is
anything watching the vault's file times: an external indexer or sync client sees every no-op edit as
a modification. Whether that matters depends on consumers outside this repository, so the regression
is concrete but its cost is not something this entry can size.

The original cited `EditTool.cs:303`, which is the opening of the `<exception cref="ToolRefusal">`
doc tag on `Store` rather than the skip. The substance is right; the line lands on documentation for a
different guarantee. No case identifier.

### `AmbiguousIdentity` for an anchor naming several entries is not reached through the tool

An add whose anchor identity matches more than one entry under the key is refused rather than
resolved to the first match. The substrate raises `RefusalReason.AmbiguousIdentity`
(`src/VaultShared/Frontmatter/FrontmatterWrite.cs:538`, the anchor arm, message "The anchor
\"{anchor.Identity}\" names more than one entry under \"{key}\"."; two sibling arms raise the same
reason for non-anchor addressing at `:522` and `:581`), and the tool layer maps it to the wire code
`entry_ambiguous` (`src/VaultShared/Tools/ToolRefusalEnvelope.cs:111`). Identity matching is
case-insensitive, so `skill-a` and `SKILL-A` under one key are two matches for one anchor.

The substrate is covered: `FrontmatterOperationTests.AddEntry_AnAnchorNamingSeveralEntriesIsRefused`
(`tests/VaultShared.Tests/Frontmatter/FrontmatterOperationTests.cs:213`) covers the anchor case
exactly, and `RemoveEntry_AnIdentityMatchingSeveralEntriesIsRefused` (`:263`) plus
`FrontmatterRefusalTests.AmbiguousIdentity_AnIdentityMatchingSeveralEntriesRefuses` (`:317`) cover
the identity case. The reason-to-code mapping is asserted in isolation at
`ToolRefusalEnvelopeTests.cs:104`. Through the tool, only the zero case:
`EditToolTests.AnAnchorNamingNoEntry_ReachesTheCallerAsEntryNotFound`
(`tests/VaultShared.Tests/Tools/EditToolTests.cs:505`) asserts `ToolCodes.EntryNotFound`. No
`EditToolTests` case produces `entry_ambiguous`.

The regression is the tool growing a path that resolves the anchor itself, or normalizing identity
differently on the way in - canonicalizing or case-folding entries before handing them to the
substrate - so that the several-match condition no longer arises at `FrontmatterWrite.cs:538` for a
request that reaches it through `edit`. The substrate test still passes, because it calls `Apply`
directly, and an anchored add silently lands beside an arbitrary one of the matching entries. The gap
is genuinely narrow: the mapping from reason to code is asserted and the zero case proves the refusal
travels from the substrate through the tool, so the untested composition is only ambiguous-anchor
specifically, end to end.

Verified, with line numbers supplied during extraction since the original gave none. No case
identifier on the bullet itself; the surrounding discussion attached the anchor behaviour to
`EDT-113`.

## Create and delete

### The delete backlink census reaches outside the caller's scope

This is one of the three to write first. `delete` runs its surviving-wikilink census against the
whole vault, not against the part of it the caller's grant reaches:
`src/VaultShared/Tools/DeleteTool.cs:114` invokes
`WikilinkMaintenance.Census(vault.BypassingScope(), [.. plan.Entries])`, so a note that sits outside
the caller's scope and links into the removed set is still reported. A report that stopped at the
scope boundary would under-report exactly where a caller most needs the truth, because the links it
cannot see are the ones it cannot fix.

Nothing asserts it at the `delete` level. The unscoped reach is exercised only one layer down, in
`tests/VaultShared.Tests/Links/WikilinkCensusTests.cs`, where every case passes
`_vault.Access.BypassingScope()` itself (the helper at line 19, and line 276) - so those tests assert
what the census does when handed a bypassing view, never that `DeleteTool` hands it one.
`tests/VaultShared.Tests/Tools/DeleteToolTests.cs` never narrows a grant's scope: it constructs every
vault with `ScratchVault(opClasses: ...)` and the word "scope" appears in the file only as a
`using VaultShared.Scope;` directive.

Dropping `.BypassingScope()` from `DeleteTool.cs:114`, or replacing it with the ordinary `vault`
while tidying up the call, leaves the whole suite passing. A caller scoped to `Projects/` would then
delete a note and be told there are no backlinks to fix, while a note in `Reference/` still links to
it.

Verified. Old case `CRD-095`.

### Only the exact name `.gitkeep` is exempt from the folder-emptiness guard

A non-recursive folder removal refuses a folder that holds anything, with exactly one exemption: a
folder whose sole file is `<folder>/.gitkeep` counts as empty and is removed along with the
placeholder. The exemption is a case-insensitive equality against the single constant
`VaultFiles.GitKeepName` (`".gitkeep"`, `src/VaultShared/Access/VaultFiles.cs:190`) inside
`RefuseIfNotEmpty` (`src/VaultShared/Tools/DeleteTool.cs:236-239`) - not a dot-file rule, not a
hidden-file rule, not a prefix. A folder holding only some other dot-file, `.placeholder` say, is not
empty and a plain call is refused.

Two neighbouring cases are covered and this one is not.
`DeleteToolTests.AFolderHoldingOnlyTheGitKeepCountsAsEmptyAndAPlainCallRemovesBoth`
(`tests/VaultShared.Tests/Tools/DeleteToolTests.cs:161`) covers `.gitkeep` alone, and
`AFolderHoldingTheGitKeepAndSomethingElseIsNotEmpty` (`:174`) covers `.gitkeep` plus a `.md` note. No
test puts a different dot-file in a folder by itself.

The regression is widening the exemption to any dot-file - a "generalize the placeholder rule"
change, or a switch from the exact-name comparison to `Path.GetFileName(...).StartsWith('.')`. A
folder holding a caller's own `.placeholder`, `.DS_Store` or a stashed `.env` is then silently
destroyed by a call that did not ask to recurse, which is precisely the case the deleted system had a
regression test for.

Verified. Old case `CRD-082`.

### A gitignored file is in the destruction plan and is destroyed

The destruction plan comes from a file census that inherits no ignore-file behaviour, so a file
listed in the vault's `.gitignore` is still planned, still reported to the caller and still removed.
It rests on `--no-ignore` being one of the arguments every search-engine invocation carries
(`src/VaultShared/Access/VaultSearch.cs:25`,
`private static readonly string[] Always = ["--no-config", "--hidden", "--no-ignore"];`), spliced
into every invocation including the `EnumerateFiles` census at `:50` that `DeleteTool.Plan` calls
(`src/VaultShared/Tools/DeleteTool.cs:152`). A gitignored file is still on disk and can still be
destroyed, so under-reporting it would tell a caller a removal is smaller than it is.

Only the access layer is covered, one step removed from delete.
`VaultEnumerationTests.EnumerateFiles_InheritsNoIgnoreFileBehaviour`
(`tests/VaultShared.Tests/VaultEnumerationTests.cs:59`) writes a `.gitignore` and asserts the ignored
file comes back from `EnumerateFiles`. No test in `DeleteToolTests.cs` creates a `.gitignore` at all,
so nothing asserts that the ignored file reaches the plan or the reported `Removed` set.

Two regressions sit behind this: `--no-ignore` dropped from `Always`, which the access-layer test
catches, and `DeleteTool.Plan` switched to some other enumeration that does honour ignore files,
which it does not. A recursive removal would then report a set smaller than what it destroys, or
leave gitignored files orphaned inside a folder it claims to have removed and then fail on the folder
itself.

Verified. Old case `CRD-112`, whose identifier also appears in the reconciliation's regression
section against a different property; this entry carries only the plan-membership half.

### A note that vanished mid-census is skipped; one that is present and will not open fails the pass

The census reads every surviving note through one helper, `WikilinkMaintenance.ReadAll`
(`src/VaultShared/Links/WikilinkMaintenance.cs:568-583`, with the switch at `:573-580`, reached from
the delete path via `Census` at `:207`), and it splits two failures that look alike. A note the
enumeration listed but that has since disappeared (`ReadOutcome.NotFound`) matches no `case`, leaves
its position null, and is skipped by the caller's `if (texts[i] is not { } text) continue;`. A note
that is still present and will not open (`ReadOutcome.Failed`) returns immediately and fails the
whole pass, because skipping it would turn a failed operation into a silent success - the delete
would report "no backlinks" for a note it could not read.

The unreadable half is asserted one layer down, at the census level:
`WikilinkCensusTests.ANoteThatIsThereAndWillNotOpenFailsThePass`
(`tests/VaultShared.Tests/Links/WikilinkCensusTests.cs:253`) holds a linking note open and expects
`LinkPassOutcome.ReadFailed`. The vanished half - `NotFound` skipped, pass still completes - is
asserted nowhere at any level. Neither half is asserted through `delete`: `DeleteToolTests` uses
`_vault.Unreadable(...)` twice (`:267` and `:283`) but both times on a file inside the removed set,
which `Census` deliberately never reads.

The regression is the switch growing a `case ReadOutcome.NotFound:` that returns a failure, or
`ReadAll`'s default behaviour being inverted so an unreadable note is skipped like a vanished one.
The second is the dangerous direction: a `delete` preview would report an empty or short backlink
list while a note it could not open still links into the removed set, and the caller would delete
believing nothing points at the target.

The original citation is stale. It cited `WikilinkMaintenance.cs:504-519`, which is inside
`Rewritten`, the splice loop that composes a note's rewritten text - the `replacement(target, alias)`
guard and the retarget and realias appends, nothing to do with reading notes. The method name in the
bullet was correct and led to the real site at `:568-583`. One wording correction: there is no
explicit `ReadOutcome.NotFound` case in the code. `NotFound` matches no case arm at all and is
skipped by falling through the switch, which produces the described behaviour but means a reader
grepping for `NotFound` in this file finds nothing. Old case `CRD-098`.

### The `.git` guard matches a `.git` file as well as a `.git` directory

The guard that refuses any removal containing a `.git` entry scans every path segment of every
planned entry, files and folders alike, for the exact segment `.git`, case-insensitively - the nested
loop in `RefuseOnGitDirectory` over `entry.Segments`
(`src/VaultShared/Tools/DeleteTool.cs:204-206`), matching with
`StringComparison.OrdinalIgnoreCase`. It never asks whether the entry is a directory. A `.git` file,
the gitlink form a submodule or a linked worktree leaves behind, refuses the removal just as a `.git`
directory does. Both point at a separate repository with its own history.

Only the directory form is asserted.
`DeleteToolTests.AGitDirectoryAnywhereInTheSetRefusesTheWholeRemoval`
(`tests/VaultShared.Tests/Tools/DeleteToolTests.cs:124`) writes `Projects/Widget/Vendored/.git/HEAD`,
so the matched `.git` segment is always an interior one belonging to a directory. Nothing exercises a
plan entry whose final segment is `.git`.

The regression is narrowing the loop to interior segments - stopping at `entry.Segments.Count - 1` on
the reasoning that the last segment is the filename and `.git` is a directory - or adding a
`Directory.Exists` test to the match. A vaulted submodule's gitlink file would then be removed,
taking the pointer to a repository this vault never tracked, and the existing test would still pass
because its `.git` segment is interior.

Verified; the segment scan applies no directory test. Old cases `CRD-085` and `CRD-086`.

## Move

### The move tool's wikilink repair reaches outside the caller's scope roots

This is one of the three to write first. `move` plans and applies its wikilink repair over a
vault-wide pass, not over the caller's scoped view. The tool obtains that pass by calling
`vault.BypassingScope()` once (`src/VaultShared/Tools/MoveTool.cs:155`, consumed by
`WikilinkMaintenance.PlanNoteRelocation` and `PlanFolderRelocation` on the next two lines and passed
into `Perform` at `:181`, with the rationale comment at `:86`) and uses it for both the plan and the
apply, so a link in a note the caller's grant can neither read nor write is still rewritten when the
moved note's name changes. A caller granted only `Projects` who moves `Projects/Widget/Notes.md`
still gets `Reference/Guide.md` repaired.

The primitive is asserted thoroughly, by `tests/VaultShared.Tests/VaultWidePassTests.cs`: that
fixture builds a vault scoped to `Projects` and asserts the pass reaches, reads and writes notes
outside it (`TheScopedSurfacesWithholdWhatThePassReaches`,
`EnumerateNotes_ReachesEveryNoteWhateverTheGrantAdmits`, `Read_ReachesANoteTheGrantCannotRead`,
`Replace_WritesANoteTheGrantCannotWrite`). Nothing asserts that `move` uses it. Every test in
`tests/VaultShared.Tests/Tools/MoveToolTests.cs` runs against a `ScratchVault` with no scope
restriction at all (`MoveToolTests.cs:19-24`), and the only grant-shaped test in the file
(`MoveToolTests.cs:505`) varies op classes - `content:write`, `agentic:write` - for the path-policy
authority check, not scope roots.

The regression is "tightening" the tool by swapping `vault.BypassingScope()` for the scoped surface,
a change that looks like a security improvement and reads as one in review. Every existing `move`
test still passes, because the fixture's grant admits the whole vault. In production a scoped
caller's move leaves every out-of-scope inbound link pointing at the old path, silently and with a
success result: the repair list is simply shorter and no refusal is raised.

The original citations are stale on both halves and the claim is correct. `MoveTool.cs:123` does not
use `BypassingScope()`; line 123 is `throw new ToolRefusal(` inside the folder-moved-into-itself
guard, and the real site is line 155. `VaultWidePassTests.cs:89` is a comment line inside
`EnumerateFiles_LeavesOutDotEntries`; the assertions are at lines 66, 74, 82, 93, 99, 107 and 116.
Old case `MOV-043`.

### The vault write lock is taken for a `move`, and its timeout refuses with `lock_timeout`

Two properties, both true today. `move` declares that it mutates - `MoveTool.Declaration.Mutates` is
true - and the shared execute path therefore acquires the named per-vault mutex around the whole
invocation, plan, repair, move and folder-note rename, releasing it on the way out
(`src/VaultShared/Tools/ToolExecution.cs:123`,
`using var hold = VaultWriteLock.Acquire(vault, onInheritedLock);`, guarded by the
`if (!declaration.Mutates) return tool.Invoke(call);` early return at `:111-112` and the "mutates but
names no vault" wiring-defect throw at `:119-121`). A preview takes the lock exactly as an apply
does. Separately, an acquire that does not succeed inside the timeout raises a `ToolRefusal` coded
`lock_timeout` carrying the timeout in seconds as `data.timeout`
(`src/VaultShared/Tools/VaultWriteLock.cs:70-74`).

Both halves are asserted away from `move`.
`ToolExecutionTests.MutatingTool_HoldsTheVaultLockAcrossTheWholeInvocation`
(`tests/VaultShared.Tests/Tools/ToolExecutionTests.cs:341`) drives a synthetic `RecordingTool`
declared `mutates: true` and proves the mutex is held during the invocation and free afterwards, with
`NonMutatingTool_TakesNoLock` (`:359`) as its negative.
`tests/VaultShared.Tests/Tools/VaultWriteLockTests.cs:49` asserts the `lock_timeout` code against the
primitive directly. `MoveToolTests.DeclaresAWriteFloorAndMutates` (`MoveToolTests.cs:44`) asserts
`MoveTool.Declaration.Mutates` is true. What does not exist is any test that runs the real `move`
tool against a held mutex and observes the refusal, or that observes the lock held during a real
`move`.

The regression is weak, and this is the weakest entry in the area. The three facts that compose the
guarantee are each asserted, and the composition is by construction rather than by convention: a tool
cannot opt out of the lock, because the execute path reads the declaration. What a `move`-level test
would catch that the existing three do not is narrow - someone adding a code path that invokes
`MoveTool.Invoke` directly, bypassing `ToolExecution`, or an integration seam where the front end
calls the tool outside the execute path. It would also catch nothing about lock duration under a real
move's I/O.

`ToolExecution.cs:123` is verified exactly. `VaultWriteLock.cs:63` is stale - line 63 is the closing
`/// </remarks>` of the `Acquire` doc comment, and the `lock_timeout` throw is at `:70-74`. The claim
that no `move`-level contention test exists is correct, but the original undersold what is covered:
it said only that the primitive is asserted, when the execute path's lock-a-mutating-tool behaviour
is asserted too, with a synthetic tool. Old cases `MOV-081` and `MOV-098`.

### A folder move that lands but cannot rename the folder note reports a partial mutation

A folder move is one `Directory.Move`, which carries the folder note across under the old folder's
basename. `RenameFolderNote` then renames it to match the new folder. If that second rename fails -
the file is held open, permissions, anything `VaultFiles.MoveFile` reports as other than `Moved` -
the folder has already moved and cannot be put back, so the tool raises a `partial_mutation` rather
than an ordinary refusal (`src/VaultShared/Tools/MoveTool.cs:316-325`, the
`if (VaultFiles.MoveFile(carried, ...) is { } outcome and not MoveOutcome.Moved) throw MutationSupport.Partial(...)`,
with the carried-path computation at `:312`, the two early returns at `:308-309` and `:313-314`, and
the call site at `:286`). That refusal names, in order, what landed - `move <path> -> <destination>`
plus every wikilink rewrite the plan applied - what did not - `rename <carried> -> <belongs>` - and
three ordered recovery steps ending in "rename it with a note move", pointing the caller at the
spelling the already-applied wikilink repair names. The two early returns above it are also live: a
folder with no folder note returns without renaming anything, and a move that keeps the folder's
basename finds the carried path already correctly spelled and returns.

Nothing asserts the failure branch. The suite has no `move`-level partial-mutation test of any kind -
the only `PartialMutation` assertion in the whole repository is
`DeleteToolTests.ARemovalThatStopsPartwayRaisesThePartialMutationVocabulary`
(`tests/VaultShared.Tests/Tools/DeleteToolTests.cs:264`), for `delete`. `move`'s own use of
`ScratchVault.Unwritable` is `MoveToolTests.ARepairThatWritesNothingIsAPlainWriteFailure`
(`tests/VaultShared.Tests/Tools/MoveToolTests.cs:221`), which exercises the opposite branch: a repair
that wrote nothing, refused as a plain `write_failed`, explicitly asserting that nothing in the vault
changed. The success path is covered - `MoveToolTests.cs:340`
(`MovesAFolderWithEverythingInItAndRenamesItsFolderNote`) asserts the rename lands, and `:356`
(`AFolderWithNoFolderNoteGetsNoneCreated`) covers the no-folder-note early return through the
result's `FolderNote` member. The other partial-mutation branch in this tool, `MoveTool.cs:269-277`,
where a repair pass wrote some notes then stopped and the move is abandoned with the repair half in
place, is equally unasserted; `MoveToolTests.cs:217` is the zero-written case only.

The regression is the recovery steps or the `applied` and `not_applied` split drifting out of
correspondence with what the code did. Concretely, someone reorders `Perform` so the folder-note
rename runs before `VaultFiles.MoveFolder` - which reads as a tidy-up, and which the `Perform` doc
comment at `MoveTool.cs:246-248` explicitly warns against - and the `applied` list still claims the
folder move landed when it did not, sending a caller to look for a folder that is still where it was.
Or the branch degrades to a plain `write_failed`, and a caller is told the move failed when the
folder has in fact moved and the vault is half-repaired. Nothing in the suite would notice either.

The original citation is stale. It pointed at `MoveTool.cs:239-248`, which is the `<exception>` and
`<remarks>` doc block on `Perform`, not code; the behaviour lives at `:316-325` in
`RenameFolderNote`. No case identifier.

### `caseOnly` is reported for a folder move as well as a file move, and both layers assert it

A rename that changes only letter case is performed rather than read as an occupied destination, and
is flagged in the result, for the folder form as much as the file form. At the primitive,
`VaultFiles.MoveFolder` (beginning at `src/VaultShared/Access/VaultFiles.cs:316`) computes `caseOnly`
as "the two paths name the same entry but are not spelled the same" (`:329-330`) and returns it on
`MoveOutcome.Moved` (`:345`). At the tool, `MoveTool` computes the same predicate itself from source
and destination (`src/VaultShared/Tools/MoveTool.cs:173`) and puts it on `MoveResult.CaseOnly`; the
tool's flag does not come from the primitive's outcome at all, which is why both layers need their
own assertion.

Both layers assert it, for the folder form. `VaultFilesMoveTests.MoveFolder_ReportsACaseOnlyRename`
(`tests/VaultShared.Tests/VaultFilesMoveTests.cs:211`) moves `Projects/Widget` to `Projects/WIDGET`
and asserts `outcome.CaseOnly` is true - precisely the primitive-level property the original said was
unasserted - and `MoveFolder_RelocatesTheFolderAndEverythingUnderIt` (`:172`) asserts the false case.
At the tool level, `MoveToolTests.ACaseOnlyFolderRenameIsNotReadAsAnOccupiedDestination`
(`tests/VaultShared.Tests/Tools/MoveToolTests.cs:442`) asserts `result.CaseOnly` is true for a folder
and that the on-disk spelling changed. `PreflightFolder_AnswersTheOccupiedDestinationAndAllowsACaseOnlyRename`
(`VaultFilesMoveTests.cs:299`) covers the pre-flight's agreement with it, and the file form is
separately covered at `VaultFilesMoveTests.cs:46` and `MoveToolTests.cs:276`.

There is no gap, and no regression can be constructed that this would fail to catch. The entry is
carried so that the list is complete and so that whoever weighs it can see this was checked and
refuted rather than dropped.

The original is wrong on all three counts. `VaultFiles.cs:312` is a `<param name="source">` doc line,
not the `caseOnly` computation at `:330`. `MoveToolTests.cs:207` is
`Assert.Equal(ToolCodes.AlreadyExists, refusal.Code)` inside
`AnOccupiedDestinationWithNothingToRepairIsACleanRefusal`, nothing to do with case-only renames; the
file-form case-only test is at `:276`. And the substantive claim, that the property is asserted only
for the file form, is false. Old case `MOV-073`, which should not be acted on if it surfaces again
from another copy.
