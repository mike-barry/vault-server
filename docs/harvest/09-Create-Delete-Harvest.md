# Harvest: The `create` and `delete` tools

Sources:
- `C:\Repos\vault-server\tools\Tests\create.Tests.ps1` (708 lines)
- `C:\Repos\vault-server\tools\Tests\delete.Tests.ps1` (631 lines)
- `C:\Repos\vault-server\test-vault\**` (fixture, 6 notes) — read to state link-census cases precisely

Read the implementation for:
- `C:\Repos\vault-server\tools\create.ps1` (329 lines) — to confirm create performs **no** link
  maintenance at all, to pin the exact basename character class, and to confirm which checks are
  validate-phase.
- `C:\Repos\vault-server\tools\delete.ps1` (505 lines) — to state the dangling-backlink census
  case-by-case (bare vs path-qualified resolution, the self-exclusion of planned files, the
  read-failure/skip split), which the tests exercise only in aggregate. Cases sourced this way are
  marked in `Note`.

## Summary

Two write-class tools, both single-operand, both running under the per-vault write lock as a
strict validate → apply, both declaring `OpClass: content:write` in comment-help.

Three things a reconciler should know up front:

1. **`create` does no link maintenance whatsoever.** There is no wikilink pinning, no census, no
   backlink write, and no link-related field in its return. A new note's links are simply the text
   the caller supplied. The only link-adjacent rule is negative: link-breaking characters
   (`[ ] # ^ |`) are refused *in the basename* so the created note can be linked to at all. If the
   new specification gives create any link behavior, that is new, not recovered.
2. **`delete`'s link maintenance is report-only, always.** It never rewrites a link and never
   refuses because of one. It emits `danglingBacklinks[]` — a vault-wide, deliberately *unscoped*
   census of `{file, link}` pairs whose link resolved to a file in the destruction plan — and
   proceeds. The stated reason is that a deletion leaves no new name to point at.
3. **`delete` runs two different enumerations on purpose.** The destruction plan comes from a raw
   filesystem walk (`Get-ChildItem -Force`) so dot-files and gitignored files are in
   `filesDeleted[]`, in the emptiness guard, and in the agentic classification; the backlink census
   keeps the shared ripgrep layers because a link graph is a question about agent-visible files.
   Several cases exist only to pin that split (CRD-083, CRD-115, CRD-116).

Both tools assert `agentic:write` in-script as a *degenerate case of edit's outcome diff*: create
diffs the supplied frontmatter against an empty original, delete diffs the note's frontmatter
against an empty result. Both use the identical three-way split — injected set holding the class
allows, injected-but-empty set **denies**, no injected set at all (standalone) **degrades open**.

`create` has no `apply` parameter (its preview would echo the request back); `delete`'s `apply`
defaults to **true** and a plan differs from a result in the `applied` field alone.

Error identities harvested: create — `already_exists`, `parent_not_found`, `invalid_argument`,
`frontmatter_integrity`, `schema_validation_failed`, `malformed_op`, `insufficient_capability`,
`frontmatter_path_excluded`, `out_of_scope`, `unknown_vault`, `vault_unavailable`, `lock_timeout`.
delete — `not_found`, `kind_mismatch`, `invalid_argument`, `folder_not_empty`,
`nested_repository`, `insufficient_capability`, `readonly_targets`, `read_failure`,
`partial_state`, `out_of_scope`, `unknown_vault`, `vault_unavailable`, `lock_timeout`.
Create notably has **no** `partial_state` (one atomic write by construction) and **no**
`not_found`; delete has **no** `force`/`purge` (git is the undo).

---

## Cases

## `create`

### CRD-001 — A note create returns kind, path, created[], sha, namingWarnings and writes the composed file

- **Behavior:** Creating a note returns a single JSON object carrying the kind, the normalized
  path, `created` listing exactly that one path, a 64-hex-character `sha`, and an empty
  `namingWarnings`. The file on disk is exactly the frontmatter fenced with `---` lines followed
  immediately by the body, byte for byte.
- **Trigger:** `kind: note`, `path: Inbox/New-Note.md`, frontmatter `doc.created: 2026-07-25\ndoc.type: Note`, body `# New Note\nalpha\n`.
- **Kind:** `semantic`
- **Source:** `create.Tests.ps1:72` — `returns a single well-formed JSON object for a note create`
- **Note:** The exact expected text is `"---\ndoc.created: 2026-07-25\ndoc.type: Note\n---\n# New Note\nalpha\n"` — the fence is re-emitted by the tool, LF newlines, no blank line between the closing fence and the body.

### CRD-002 — A created file is UTF-8 with no BOM

- **Behavior:** The file written carries no UTF-8 preamble and encodes non-ASCII characters as
  UTF-8 (é as `C3 A9`); the first byte is the first content byte.
- **Trigger:** Creating a note whose body is `café\n`.
- **Kind:** `semantic`
- **Source:** `create.Tests.ps1:87` — `writes UTF-8 with no BOM`

### CRD-003 — An empty frontmatter string produces a note with no frontmatter block at all

- **Behavior:** `frontmatter: ""` is a legal, distinct request meaning "this note deliberately
  carries no block". The file is the body alone — no `---` fence is emitted.
- **Trigger:** `kind: note`, `frontmatter: ''`, `body: '# Bare\n'` → file is exactly `# Bare\n`.
- **Kind:** `semantic`
- **Source:** `create.Tests.ps1:95` — `accepts an empty frontmatter for a note deliberately carrying no block`

### CRD-004 — An unsupplied body defaults to empty

- **Behavior:** Omitting the body creates the note with frontmatter and nothing after the closing
  fence.
- **Trigger:** `kind: note`, `frontmatter: 'doc.type: Note'`, no body → file is exactly `---\ndoc.type: Note\n---\n`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:101` — `defaults the body to empty when it is not supplied`

### CRD-005 — Frontmatter is accepted either already-fenced or as bare inner lines, producing identical output

- **Behavior:** Supplying `---\ndoc.type: Note\n---` and supplying `doc.type: Note` yield
  byte-identical files.
- **Trigger:** Two creates differing only in whether the frontmatter argument carries its own fences.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:106` — `accepts an already-fenced frontmatter block as well as bare inner lines`

### CRD-006 — Path separators fold to "/" and a leading slash is trimmed

- **Behavior:** A path is normalized: backslashes become forward slashes and a leading `/`
  (vault-root spelling) is removed. The returned `path` is the normalized form and the file lands
  at it.
- **Trigger:** `path: '/Inbox\Normalized.md'` → returned path `Inbox/Normalized.md`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:113` — `normalizes separators and a leading slash in the path`

### CRD-007 — Surrounding whitespace is trimmed off the whole path rather than refused

- **Behavior:** Leading and trailing whitespace around the entire path argument is normalized away
  before any basename validation runs, so a trailing space does not become an invalid trailing
  character.
- **Trigger:** `kind: folder`, `path: '  Projects/Alpha  '` → returned path `Projects/Alpha`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:333` — `trims surrounding whitespace off the whole path rather than refusing it`
- **Note:** Ordering matters and is asserted deliberately: trim first, *then* the "no trailing dot or space" basename rule of CRD-036.

### CRD-008 — capabilitiesUsed and filesSkippedUnreadable are always present and serialize as [] when empty

- **Behavior:** The write-class envelope fields are structurally present on every success and
  serialize as JSON empty arrays, not null and not omitted.
- **Trigger:** Any successful create (note or folder).
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:119` — `emits capabilitiesUsed and filesSkippedUnreadable as arrays, [] when empty`; `create.Tests.ps1:188` — `emits the write-class envelope for a folder create too`
- **Note:** `filesSkippedUnreadable` is structurally always `[]` for create — it is single-target and reads nothing it could skip. It is present anyway so callers read one shape.

### CRD-009 — lockWarning is omitted entirely, not null, when the lock was not abandoned

- **Behavior:** The `lockWarning` property is absent from the returned object unless the write
  inherited an abandoned mutex. An always-present null would read as checked-and-clear.
- **Trigger:** Any normal create.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:129` — `omits lockWarning entirely when the lock was not abandoned`

### CRD-010 — The returned sha is the digest of the file as written

- **Behavior:** `sha` equals SHA-256 over the created file's raw bytes, lowercase hex.
- **Trigger:** Any note create; compared against an independently computed digest of the file.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:135` — `returns a sha that is the digest of the file as written`

### CRD-011 — The returned sha chains directly into a following edit's expected_sha with no interleaved read

- **Behavior:** The sha create returns is accepted verbatim as the optimistic-concurrency token of
  an immediately following whole-body edit, which succeeds.
- **Trigger:** create `Inbox/Chained.md`, then edit that path with `expected_sha` = the returned sha.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:140` — `chains straight into an edit whole-body replace with no interleaved read`
- **Note:** This is the stated *reason* create returns a sha at all. Cross-tool: couples to the `edit` area.

### CRD-012 — A naming-convention violation is a warning, never a failure

- **Behavior:** A basename that does not follow Title-Case-With-Hyphens produces one entry in
  `namingWarnings` naming the convention, and the note is still created.
- **Trigger:** `path: 'Inbox/loose note.md'` → one warning matching `Title-Case-With-Hyphens`, file exists.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:149` — `reports a naming-convention violation as a warning without failing`

### CRD-013 — A folder create makes a bare directory: no folder note, no placeholder, no warning

- **Behavior:** `kind: folder` brings an empty directory into existence and seeds nothing at all —
  no folder note, no `.placeholder` — and does not warn about the missing folder note, because a
  folder is expected to be unpopulated at creation.
- **Trigger:** `kind: folder`, `path: 'Projects/Alpha'` → the directory has zero entries even with hidden files counted; `namingWarnings` empty.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:160` — `creates a bare directory with no folder note and no placeholder`

### CRD-014 — A folder create is mkdir-p and reports every intermediate it created

- **Behavior:** Missing ancestors are created along with the target, and `created[]` lists them all
  in order from shallowest to deepest.
- **Trigger:** `kind: folder`, `path: 'Projects/Alpha/Corpus/Deep'` → `created` = `['Projects/Alpha', 'Projects/Alpha/Corpus', 'Projects/Alpha/Corpus/Deep']`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:170` — `creates intermediates (mkdir-p) and reports every created path`

### CRD-015 — created[] names only the paths actually brought into existence

- **Behavior:** An ancestor that already existed is not listed in `created[]`.
- **Trigger:** `Projects/Widget` exists in the fixture; `kind: folder`, `path: 'Projects/Widget/Corpus'` → `created` = `['Projects/Widget/Corpus']` alone.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:176` — `reports only the paths it actually brought into existence`

### CRD-016 — A folder create returns a null sha

- **Behavior:** `sha` is JSON `null` for a folder — a folder has no content to digest — rather than
  an empty string or the digest of empty content, so a caller cannot mistake it for a real hash.
- **Trigger:** `kind: folder` create; output matches `"sha": null`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:182` — `returns a null sha (a folder has no content to digest)`

### CRD-017 — already_exists on a note path that is occupied

- **Behavior:** Creating over an existing note is refused `already_exists` with `data.path` naming
  the target, and the existing file is not modified in any way.
- **Trigger:** `kind: note`, `path: 'Resources/Gizmo-Reference.md'` (present in the fixture).
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:197` — `errors already_exists on an existing note`
- **Note:** Zero mutation is asserted by re-reading the survivor's content.

### CRD-018 — already_exists on a folder path that is occupied

- **Behavior:** `kind: folder` against an existing directory is refused `already_exists` with
  `data.path`.
- **Trigger:** `kind: folder`, `path: 'Projects/Widget'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:205` — `errors already_exists on an existing folder`

### CRD-019 — A collision of the wrong shape is still already_exists

- **Behavior:** A *directory* occupying a note's path collides: the existence guard tests the path
  without regard to shape, so the caller gets `already_exists` rather than an obscure write failure.
- **Trigger:** A directory literally named `Inbox/Occupied.md`; then `kind: note` at that path.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:211` — `errors already_exists when a folder occupies the note path`

### CRD-020 — A note under a missing parent directory is refused, and the directory is not created

- **Behavior:** create never brings a directory into existence for a note. A note whose parent
  folder does not exist is refused `parent_not_found` with `data.parent` naming the missing folder,
  and that folder still does not exist afterwards.
- **Trigger:** `kind: note`, `path: 'Projects/Nonexistent/Note.md'` → `data.parent` = `Projects/Nonexistent`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:219` — `refuses a note under a missing parent directory rather than creating it`
- **Note:** The test comment records this as *structural* — the write chokepoint raises it, not a create pre-check. That is an implementation fact; the observable behavior is the refusal and the untouched filesystem.

### CRD-021 — A note whose parent path component is a file is parent_not_found

- **Behavior:** Descending "into" a file is refused `parent_not_found`.
- **Trigger:** `kind: note`, `path: 'Resources/Gizmo-Reference.md/Child.md'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:228` — `refuses a note whose parent path component is a file`

### CRD-022 — A folder whose path component is a file is refused before any intermediate is created

- **Behavior:** The mkdir-p walk detects a file occupying a path component and refuses
  `parent_not_found` naming that component, before creating any of the intermediates — so a failed
  deep folder create leaves no partial tree.
- **Trigger:** `kind: folder`, `path: 'Resources/Gizmo-Reference.md/Deep/Deeper'` → `data.parent` = `Resources/Gizmo-Reference.md`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:233` — `refuses a folder whose path component is a file, before creating any intermediate`

### CRD-023 — An unrecognized kind is invalid_argument, naming the value

- **Behavior:** A kind outside {note, folder} is the tool's own coded refusal with `data.kind`
  echoing what the caller sent — not a binding/parameter-validation failure.
- **Trigger:** `kind: 'widget'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:241` — `errors invalid_argument on an unrecognized kind`

### CRD-024 — The retired `project` and `area` kinds are refused

- **Behavior:** `project` and `area` are not create kinds; scaffolding is composed from folder and
  note creates. Both are `invalid_argument`.
- **Trigger:** `kind: 'project'` and `kind: 'area'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:247` — `errors invalid_argument on the retired project/area kinds`
- **Note:** Test comment dates the removal to 2026-07-24. A redesign that never had these kinds satisfies this trivially, but the *absence* of server-side scaffolding is the harvested behavior.

### CRD-025 — kind is matched case-insensitively after a trim

- **Behavior:** `'  NOTE  '` is the `note` kind, and the returned `kind` is the normalized
  lowercase form.
- **Trigger:** `kind: '  NOTE  '` → result `kind` = `note`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:255` — `matches kind case-insensitively after a trim`

### CRD-026 — frontmatter is required for kind note, and unsupplied differs from empty

- **Behavior:** Omitting frontmatter for a note is `invalid_argument` with `data.kind = 'note'` and
  `data.param = 'frontmatter'`. Passing `''` is the legal way to say "no block" (CRD-003), so
  unsupplied and empty are distinct requests.
- **Trigger:** `kind: note` with no frontmatter argument.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:260` — `requires frontmatter for kind note`
- **Note:** The implementation additionally treats a bound-but-JSON-null frontmatter as unsupplied. Not directly asserted by a test.

### CRD-027 — frontmatter supplied for kind folder is refused as inert, and nothing is created

- **Behavior:** An inert parameter is refused rather than silently ignored: `invalid_argument`,
  `data.param = 'frontmatter'`, and the folder is *not* created.
- **Trigger:** `kind: folder`, `path: 'Projects/Alpha'`, `frontmatter: 'doc.type: Note'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:267` — `refuses frontmatter as inert for kind folder`

### CRD-028 — body supplied for kind folder is refused as inert

- **Behavior:** Same rule for body: `invalid_argument`, `data.param = 'body'`.
- **Trigger:** `kind: folder`, `body: '# Nope'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:275` — `refuses body as inert for kind folder`
- **Note:** The implementation checks frontmatter first, so supplying both names `frontmatter`.

### CRD-029 — A whitespace-only path is invalid_argument

- **Behavior:** An empty or whitespace-only path is a malformed operand.
- **Trigger:** `path: '   '`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:283` — `errors invalid_argument on a whitespace path`

### CRD-030 — A vault-escaping path is invalid_argument, not a miss

- **Behavior:** A path resolving outside the vault root is refused as a malformed operand
  (`invalid_argument`), not as not-found or out-of-scope.
- **Trigger:** `path: '../outside.md'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:288` — `errors invalid_argument on a vault-escaping path`

### CRD-031 — An extensionless note path is refused, and the error names the ".md" form

- **Behavior:** ".md" is required for kind note and is **never inferred**. The refusal is
  `invalid_argument`, the message contains the corrected path, and `data.suggestedPath` carries it
  so the caller can retry in one call. Neither the extensionless nor the ".md" file is created.
- **Trigger:** `path: 'Inbox/Extensionless'` → `data.suggestedPath` = `Inbox/Extensionless.md`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:293` — `refuses an extensionless note path as invalid_argument naming the .md form`
- **Note:** The stated reason is that create and a following edit can never disagree about what the operand was. Contrast delete (CRD-073), which also refuses to infer but surfaces it as `not_found`.

### CRD-032 — A non-".md" note path is refused rather than converted

- **Behavior:** A note path with any other extension is `invalid_argument` with
  `data.suggestedPath` carrying the extension replaced by `.md` — create is note-semantic and never
  converts.
- **Trigger:** `path: 'Inbox/Notes.txt'` → `data.suggestedPath` = `Inbox/Notes.md`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:304` — `refuses a non-.md note path (create is note-semantic and never converts)`

### CRD-033 — A ".MD" spelling is accepted and preserved as the caller wrote it

- **Behavior:** The extension check is case-insensitive, and the returned path keeps the caller's
  casing — the tool does not normalize the extension.
- **Trigger:** `path: 'Inbox/Upper-Case.MD'` → returned path `Inbox/Upper-Case.MD`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:310` — `accepts a .MD spelling as the caller wrote it`

### CRD-034 — Link-breaking characters in a basename are refused outright

- **Behavior:** `[`, `]`, `#`, `^`, `|` in the basename block the create with `invalid_argument`
  and a message naming "link-breaking or OS-invalid" — unlike a naming-convention miss, these are
  fatal, because a name carrying them cannot be linked to at all.
- **Trigger:** each of `Inbox/Bad#Name.md`, `Inbox/Bad|Name.md`, `Inbox/Bad^Name.md`, `Inbox/Bad[Name].md`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:315` — `refuses link-breaking characters in the basename`
- **Note:** This is the *only* link-aware rule in create. The check is against the basename only — the full path legitimately contains `/`. Read from the implementation, the full class is `[ ] # ^ | < > : " / \ ? *` plus control characters `\x00-\x1F`.

### CRD-035 — The same basename character rule applies to folders

- **Behavior:** OS-invalid characters in a folder basename are refused `invalid_argument` too.
- **Trigger:** `kind: folder`, `path: 'Projects/Bad*Name'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:323` — `refuses an OS-invalid basename for a folder too`

### CRD-036 — A basename ending in a dot (or space) is refused

- **Behavior:** A trailing `.` or space is an invalid trailing character and blocks the create.
- **Trigger:** `kind: folder`, `path: 'Projects/Trailing.'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:328` — `refuses a basename ending in a dot (an invalid trailing character on Windows)`
- **Note:** Windows-motivated but applied unconditionally. The trailing-space half of the rule is read from the implementation (`[. ]$`); only the dot is asserted, and CRD-007 shows a trailing space on the *path* is trimmed before this check ever sees it.

### CRD-037 — An unclosed frontmatter fence is frontmatter_integrity / unclosed_fence

- **Behavior:** A supplied block whose opening `---` never closes is refused with code
  `frontmatter_integrity` and `data.reason = 'unclosed_fence'`, and nothing reaches disk.
- **Trigger:** `frontmatter: "---\ndoc.type: Note"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:342` — `errors frontmatter_integrity on a supplied block whose fence never closes`

### CRD-038 — Content trailing the closing fence is frontmatter_integrity / content_after_fence

- **Behavior:** Body text riding in through the frontmatter argument is refused
  (`data.reason = 'content_after_fence'`) — otherwise it would bypass the body facet's own guards.
- **Trigger:** `frontmatter: "---\ndoc.type: Note\n---\n# Smuggled body"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:349` — `errors frontmatter_integrity on content trailing the closing fence`

### CRD-039 — A body that opens its own "---" fence is refused

- **Behavior:** A frontmatter block can only be authored through the frontmatter parameter, where
  it is schema-validated and capability-classified. A body that mints its own block is
  `frontmatter_integrity` and nothing is written.
- **Trigger:** `frontmatter: ''`, `body: "---\nnav.contents: smuggled\n---\n# B\n"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:357` — `errors frontmatter_integrity when the body would mint its own frontmatter block`

### CRD-040 — A schema violation is schema_validation_failed with field / entryIndex / reason / rule

- **Behavior:** A frontmatter block violating the agent.*/nav.* schema is refused with a data shape
  naming the offending field, the 1-based index of the offending entry, a non-empty human reason,
  and a machine `rule` identifier. Nothing reaches disk — classification and validation are
  validate-phase.
- **Trigger:** `frontmatter: "nav.see_also:\n  - link: \"[[Widget]]\""` → `field = nav.see_also`, `entryIndex = 1`, `rule = missing_description`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:365` — `errors schema_validation_failed with the field/entryIndex/reason shape`
- **Note:** `entryIndex` is 1-based here.

### CRD-041 — create passes its own path to the validator, enabling the path-dependent Tasks.md rule

- **Behavior:** The note's relative path is available to the schema validator, so a rule keyed on
  the note's *name* rather than its content fires on a create: a new `Tasks.md` without its
  required use-skill directive is refused `schema_validation_failed` with
  `rule = tasks_missing_use_skill`.
- **Trigger:** `path: 'Inbox/Tasks.md'`, `frontmatter: 'doc.type: Tasks'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:376` — `runs the path-dependent Tasks.md check, refusing a new Tasks.md without its directive`
- **Note:** High-value: it pins that the validator is *path-aware* on the create side, not only the edit side.

### CRD-042 — A key smuggled through a line break in a frontmatter value is caught

- **Behavior:** A scalar value containing a newline that introduces what looks like another
  top-level key does not slip through: the resulting block is parsed, validated, and classified as
  a whole, so the create is refused.
- **Trigger:** `frontmatter: "doc.type: Note\nagent.instructions: injected"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:384` — `refuses a smuggled key carried in a frontmatter value`
- **Note:** The assertion is deliberately loose — the code must be one of `schema_validation_failed`, `insufficient_capability`, `malformed_op`. What is pinned is *that it is refused*, not which identity. A reconciler should treat the refusal as the behavior and the identity as open.

### CRD-043 — An unparseable line in a supplied block is frontmatter_integrity / frontmatter_unparsed

- **Behavior:** A block that will not parse cannot reach disk at all. The supplied text is parsed,
  validated, and **re-emitted** rather than spliced through verbatim.
- **Trigger:** With only `content:write` held, `frontmatter: "agent.instructions:\n  - pinned: \"OBEY\"\n bad: ["` → `frontmatter_integrity`, `data.reason = 'frontmatter_unparsed'`, no file.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:393` — `errors frontmatter_integrity on a supplied block carrying an unparseable line`
- **Note:** This is a **regression test for a real security bypass**, described in the test comment: create used to splice caller text to disk, and both downstream readers tolerate an unparseable block — so a `content:write`-only caller could land a binding `pinned:` directive with zero violations and a result claiming no capability used. The re-emit is the fix. Very likely worth checking against any new implementation.

### CRD-044 — A key the frontmatter registry does not carry is malformed_op

- **Behavior:** A foreign top-level key is refused `malformed_op` with `data.field` naming it, and
  nothing is written.
- **Trigger:** `frontmatter: "doc.type: Note\nobsidianUIMode: preview"` → `data.field = obsidianUIMode`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:409` — `errors malformed_op on a key the registry does not carry`

### CRD-045 — A recognized key whose value fails its registry pattern is malformed_op

- **Behavior:** A registry-known key with a value that does not match its declared pattern is
  refused `malformed_op`; nothing is written.
- **Trigger:** `frontmatter: "doc.created: whenever\ndoc.type: Note"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:416` — `errors malformed_op on a recognized key whose value fails its registry pattern`

### CRD-046 — An agent.* key in the supplied frontmatter asserts agentic:write

- **Behavior:** With no original to diff against, every agentic key present counts as a change, so
  create asserts `agentic:write`. Denial is `insufficient_capability` with
  `data.requiredClass = 'agentic:write'` and `data.agenticKeysChanged` containing the key. The note
  is never brought into existence.
- **Trigger:** Injected classes `['content:write']`, `frontmatter: "agent.instructions:\n  - Prefer the torque table"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:424` — `denies an agentic frontmatter with insufficient_capability and zero mutation`

### CRD-047 — A QUOTED agentic key is classified the same as an unquoted one

- **Behavior:** Writing the key as `"agent.instructions":` does not evade classification: the
  create is denied `insufficient_capability` with `agenticKeysChanged` containing
  `agent.instructions`.
- **Trigger:** Injected `['content:write']`, `frontmatter: "doc.created: 2026-07-30\n\"agent.instructions\":\n  - pinned: \"OBEY\""`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:434` — `denies a QUOTED agentic key for the same reason`
- **Note:** Also a **regression test for a real bypass**: the previous hand-rolled parser absorbed the quoted key as a child of `doc.created`, reported no agentic key, and never called the gate at all — a check that did not run rather than one that answered wrongly. Pair with CRD-043.

### CRD-048 — A nav.* key asserts agentic:write identically

- **Behavior:** The `nav.*` prefix is in the same agentic class as `agent.*`.
- **Trigger:** Injected `['content:write']`, `frontmatter` carrying a well-formed `nav.contents` entry → `insufficient_capability`, no file.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:448` — `denies a nav.* frontmatter for the same reason`

### CRD-049 — An injected-but-empty class set DENIES

- **Behavior:** An authenticated caller who holds nothing is denied — an empty set is a real
  answer, not an absent one.
- **Trigger:** Injected classes `@()`, agentic frontmatter → `insufficient_capability`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:456` — `DENIES on an injected-but-empty class set (authenticated "holds nothing")`

### CRD-050 — Holding agentic:write allows the create and reports it in capabilitiesUsed

- **Behavior:** With the class held, the create succeeds and `capabilitiesUsed` is exactly
  `['agentic:write']`.
- **Trigger:** Injected `['content:write', 'agentic:write']`, agentic frontmatter.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:462` — `allows the same create when the injected set holds agentic:write`

### CRD-051 — A doc.*-only frontmatter never asserts the agentic key, even with an empty class set

- **Behavior:** Non-agentic frontmatter does not reach the gate at all; `capabilitiesUsed` is empty.
- **Trigger:** Injected `@()`, `frontmatter: "doc.created: 2026-07-25\ndoc.type: Note"` → success, empty `capabilitiesUsed`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:468` — `never asserts the key for a non-agentic doc.* frontmatter, even with an empty set`

### CRD-052 — A folder create never asserts the agentic key, even with an empty class set

- **Behavior:** There is no frontmatter to classify, so a folder create is never gated on
  `agentic:write`.
- **Trigger:** Injected `@()`, `kind: folder` → success, empty `capabilitiesUsed`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:474` — `never asserts the key for a folder create, even with an empty set`

### CRD-053 — Standalone (no injected class set at all) degrades OPEN

- **Behavior:** With no ambient op-class set injected — a genuinely separate process, no host
  context — an agentic create succeeds and still reports `capabilitiesUsed = ['agentic:write']`,
  and the agentic key is written to disk.
- **Trigger:** `pwsh -File create.ps1` in a fresh process with an `agent.instructions` frontmatter.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:675` — `degrades OPEN on an agentic frontmatter with no injected op-class set`
- **Note:** The third state of the three-way split (CRD-049 is the second). The separate process is essential — a leftover global would make it pass vacuously. `capabilitiesUsed` still reports the class even though nothing gated it.

### CRD-054 — A note path outside the ambient scope roots is out_of_scope

- **Behavior:** Scope membership is checked against the operand path; a miss is `out_of_scope` with
  `data.path`, and nothing is created.
- **Trigger:** Scope roots `{scratch: ['Projects']}`, `path: 'Inbox/Out.md'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:482` — `errors out_of_scope when a note path falls outside the ambient scope roots`

### CRD-055 — A folder create is scope-checked too

- **Behavior:** `out_of_scope` fires for `kind: folder` as well, and the directory is not created.
- **Trigger:** Scope roots `{scratch: ['Projects']}`, `kind: folder`, `path: 'Areas/Outdoors'` → `Areas` does not exist afterwards.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:490` — `errors out_of_scope for a folder create too (the only scope check it reaches)`
- **Note:** The test comment records that a folder create never reaches the write chokepoint, so this validate-phase pre-check is the *whole* of scope enforcement for it — an implementation fact worth knowing if the redesign relies on a chokepoint for scope.

### CRD-056 — An in-scope path creates normally

- **Behavior:** With scope roots configured, a path inside them proceeds unchanged.
- **Trigger:** Scope roots `{scratch: ['Projects']}`, `kind: folder`, `path: 'Projects/Alpha'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:499` — `creates normally when the path falls inside the ambient scope roots`

### CRD-057 — An unconfigured vault name is unknown_vault

- **Behavior:** A vault name absent from configuration is `unknown_vault` with `data.vault` naming
  it, raised before anything is locked.
- **Trigger:** `vault: 'nope'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:505` — `refuses an unconfigured vault with unknown_vault`

### CRD-058 — A configured vault whose root is missing is vault_unavailable

- **Behavior:** Configured-but-root-missing is a distinct identity from unknown.
- **Trigger:** A config naming vault `gone` at a non-existent directory; `vault: 'gone'`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:511` — `refuses a configured vault whose root is missing with vault_unavailable`

### CRD-059 — A write lock held by another process yields lock_timeout with the timeout named

- **Behavior:** create runs under a per-vault write lock; contention from a genuinely separate
  process makes the create fail `lock_timeout` with `data.timeoutSeconds` echoing the configured
  timeout, and nothing is created.
- **Trigger:** A background job holding the vault write lock; timeout env set to 1 second.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:523` — `fails lock_timeout when the write lock is held by another process`
- **Note:** Windows-only in the old suite (`-Skip:(-not $IsWindows)`). The mutex is reentrant for the calling thread, so in-process contention is impossible — a cross-process fixture is required.

### CRD-060 — create declares OpClass content:write in its comment-help

- **Behavior:** The tool script's `.NOTES` carries a machine-readable `OpClass: content:write` line
  from which the loader derives the dispatch floor.
- **Trigger:** Static inspection of the script text.
- **Kind:** `artifact`

- **Source:** `create.Tests.ps1:574` — `declares the content:write floor in its .NOTES comment-help`
- **Note:** The *floor* (create requires content:write) is semantic; the comment-help spelling is the artifact. Dispatcher floor-gating itself was proven C#-side, not here.

### CRD-061 — Every parameter is documented in comment-help

- **Behavior:** `kind`, `path`, `frontmatter`, `body`, `vault` each have a `.PARAMETER` block —
  which the loader requires to load the tool at all.
- **Trigger:** Static inspection.
- **Kind:** `artifact`

- **Source:** `create.Tests.ps1:583` — `documents every parameter in comment-help, which the loader requires to load the tool`
- **Note:** The parameter *surface* — exactly these five names — is semantic and worth carrying.

### CRD-062 — kind carries no ValidateSet, so an unrecognized value is create's own coded error

- **Behavior:** Members are advertised in prose (`note`, `folder`) rather than enforced at the
  binding layer, because a binding rejection is a language error rather than a tool result.
- **Trigger:** Static inspection: no `[ValidateSet(` anywhere, and the `.PARAMETER kind` prose names both members.
- **Kind:** `artifact`

- **Source:** `create.Tests.ps1:590` — `declares no ValidateSet on kind, so an unrecognized value is create's own error`
- **Note:** The underlying rule — an invalid enum value must come back as a structured tool error, not a transport/binding failure — is semantic. Pairs with CRD-023 and CRD-076.

### CRD-063 — create declares no `apply` parameter

- **Behavior:** create deliberately has no preview mode: it is single-target and takes
  caller-supplied content, so a preview would only echo the request back.
- **Trigger:** Static inspection: no `.PARAMETER apply`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:598` — `declares no apply parameter (create takes caller-supplied content)`
- **Note:** Marked semantic despite being a static check: the *absence* of assess-before-mutate on create is a contract decision a reimplementation should reproduce. Direct contrast with delete (CRD-097).

### CRD-064 — A note under .claude/skills is refused frontmatter_path_excluded

- **Behavior:** Certain paths are excluded from frontmatter handling entirely, and a create
  targeting one is refused `frontmatter_path_excluded` with nothing written.
- **Trigger:** `path: '.claude/skills/temp-skill/New.md'` with any frontmatter.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:605` — `refuses a note under .claude/skills, and writes nothing`
- **Note:** The stated reason is that those files carry Claude Code's own frontmatter schema, whose folded description scalars do not survive reserialization intact. create inherits the refusal from the shared chokepoint with no wiring of its own.

### CRD-065 — The exclusion holds however the path is spelled

- **Behavior:** Case variation, backslash separators, and a traversal segment all normalize to the
  same excluded path and are all refused `frontmatter_path_excluded`.
- **Trigger:** `.CLAUDE/Skills/temp/New.md`, `.claude\skills\temp\New.md`, `Inbox/../.claude/skills/temp/New.md`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:616` — `refuses however the excluded path is spelled`
- **Note:** Note the third spelling: a `..` traversal that stays inside the vault is normalized rather than rejected as escaping, and *then* matched against the exclusion.

### CRD-066 — .claude/guides remains creatable — the exclusion is skills, not the dot directory

- **Behavior:** The exclusion is scoped to `.claude/skills`, not to `.claude` generally; a note
  under `.claude/guides` creates normally.
- **Trigger:** Pre-create `.claude/guides`, then `kind: note`, `path: '.claude/guides/New-Guide.md'` → `created = ['.claude/guides/New-Guide.md']`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:623` — `leaves .claude/guides creatable - the exclusion is skills, not the dot directory`
- **Note:** The test itself must pre-create the parent, which re-demonstrates CRD-020.

### CRD-067 — create is invocable standalone via `pwsh -File`

- **Behavior:** The tool script runs outside the MCP server — no runspace context, no ambient
  scope, no injected globals — against a real vault, exits 0, and prints the same JSON result to
  stdout, writing the same file.
- **Trigger:** A separate `pwsh -NoProfile -NonInteractive -File create.ps1 -Kind note -Path ... -Frontmatter ... -Body ...` process with only the vaults-config env var set.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:655` — `runs via a literal "pwsh -File create.ps1" invocation (the Tool Authoring Standard spelling)`
- **Note:** Comes from a Tool Authoring Standard requiring every tool to be standalone-invocable and to carry a test asserting it. Reproducible in any language as "the tool is runnable as a process, not only in-host".

### CRD-068 — A standalone failure exits non-zero and emits the shared JSON error shape on stderr

- **Behavior:** Under `pwsh -File`, a refusal produces a non-zero exit code and a
  `{code, message, data?}` JSON object on stderr — the same identity as the in-process error.
- **Trigger:** Standalone create over an existing note → stderr matches `"code": "already_exists"`.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1:694` — `surfaces the shared JSON error shape on stderr under pwsh -File`

### CRD-069 — create performs no link maintenance of any kind

- **Behavior:** A note create neither scans for nor writes any wikilink, and its return carries no
  link-related field. A newly created note's links are exactly the text the caller supplied. The
  only link-aware rule is the negative one in CRD-034.
- **Trigger:** Every create case in the suite — none asserts a link field or a link rewrite.
- **Kind:** `semantic`

- **Source:** `create.Tests.ps1` (whole file — absence); confirmed against `tools/create.ps1` (no link pattern, no census, no backlink pass).
- **Note:** Harvested as an explicit negative because the task brief asked about "wikilink pinning on creation". There is none in the old implementation. create's own doc block instead directs callers away from create-plus-delete precisely because it does *not* maintain links — "a create-plus-delete pair silently breaks every reference to the old path; use move".

---

## `delete`

### CRD-070 — A single-file delete returns applied/path/kind/filesDeleted/folderNote*/recoveryHint and removes the file

- **Behavior:** Deleting one file returns a single JSON object with `applied: true`, the normalized
  path, `kind: 'file'`, `filesDeleted` containing exactly that path, both folder-note booleans
  false, and a non-empty `recoveryHint`. The file is gone.
- **Trigger:** `path: 'Inbox/Note-Loose-Thought.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:58` — `returns a well-formed Tier-2 JSON object and removes the file`

### CRD-071 — delete carries the full write-class envelope with lockWarning omitted

- **Behavior:** `capabilitiesUsed`, `filesSkippedUnreadable`, `unrecoverableFiles`, and
  `danglingBacklinks` are all structurally present on every success; `lockWarning` is absent unless
  an abandoned mutex was inherited.
- **Trigger:** Any successful single-file delete.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:74` — `carries the full write-class envelope, with lockWarning omitted when it did not fire`

### CRD-072 — A matching kind assertion is accepted

- **Behavior:** `kind` is an optional intent assertion; supplying the correct one changes nothing.
- **Trigger:** `path: 'Inbox/Note-Loose-Thought.md'`, `kind: 'file'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:86` — `accepts a matching kind assertion`

### CRD-073 — delete takes the path verbatim: no ".md" inference

- **Behavior:** `Foo` and `Foo.md` are different values. A path missing its extension misses with
  `not_found` rather than quietly deleting the note beside it, and the note survives.
- **Trigger:** `path: 'Inbox/Note-Loose-Thought'` → `not_found`; the `.md` file still exists.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:91` — `takes the path verbatim - no ".md" inference`
- **Note:** Same "never infer .md" rule as create (CRD-031) but a *different* error identity — `not_found` here, `invalid_argument` with a `suggestedPath` there. Worth checking the redesign spells both.

### CRD-074 — Nothing at the path is not_found, never a silent no-op

- **Behavior:** A missing sole target is a real error carrying `data.path`, not a successful
  zero-effect result.
- **Trigger:** `path: 'Inbox/Nope.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:101` — `errors not_found when nothing is at the path`

### CRD-075 — An asserted kind that contradicts the target's shape is kind_mismatch

- **Behavior:** `kind_mismatch` carries both `data.assertedKind` and `data.resolvedKind`, and the
  target is untouched. Holds in both directions.
- **Trigger:** `kind: 'file'` against folder `Projects/Widget` (asserted `file`, resolved `folder`); and `kind: 'folder'` against `Inbox/Note-Loose-Thought.md`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:107` — `errors kind_mismatch when file is asserted against a folder`; `delete.Tests.ps1:115` — `errors kind_mismatch when folder is asserted against a file`

### CRD-076 — An unrecognized kind is invalid_argument, not a binding error

- **Behavior:** delete's kind vocabulary is `file` and `folder` only. `note` — a legal create kind —
  is refused `invalid_argument` with `data.kind` echoing the value.
- **Trigger:** `kind: 'note'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:120` — `rejects an unrecognized kind as invalid_argument (not a PowerShell binding error)`
- **Note:** The two tools' `kind` enums deliberately do not overlap: create takes note/folder, delete takes file/folder. A caller who reuses `note` gets a clean refusal.

### CRD-077 — An empty path is invalid_argument

- **Behavior:** A whitespace-only path is a malformed operand.
- **Trigger:** `path: '   '`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:126` — `rejects an empty path as invalid_argument`

### CRD-078 — A vault-escaping path is invalid_argument

- **Behavior:** A path resolving outside the vault root is refused as malformed, not as a miss.
- **Trigger:** `path: '../outside.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:131` — `rejects a vault-escaping path as invalid_argument`

### CRD-079 — `recursive` against a file target is refused as inert

- **Behavior:** A flag that would do nothing is refused rather than ignored — `invalid_argument`
  with `data.param = 'recursive'` — so a caller who believes they are deleting a folder learns
  otherwise. The file is not deleted.
- **Trigger:** `path: 'Inbox/Note-Loose-Thought.md'`, `recursive: true`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:136` — `refuses recursive against a file target as an inert parameter`

### CRD-080 — `recursive: false` against a file is refused too — supplied is supplied

- **Behavior:** Presence, not value, is what makes the parameter inert. Explicitly passing the
  default value against a file is still `invalid_argument`.
- **Trigger:** `path: 'Inbox/Note-Loose-Thought.md'`, `recursive: false`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:143` — `refuses recursive:false against a file too - supplied is supplied`
- **Note:** Distinguishing "unsupplied" from "supplied as the default value" is a wire-level requirement the redesign must be able to express.

### CRD-081 — A non-empty folder without recursive is folder_not_empty, with a child sample

- **Behavior:** The refusal carries `data.path`, `data.childCount`, `data.sampleChildren` (up to
  five, sorted), and — notably — `data.unrecoverableFiles`, so the refusal itself shows what a
  recursive retry would destroy beyond git's reach. Nothing is removed.
- **Trigger:** `path: 'Resources'` (one child) → `childCount = 1`, `sampleChildren` contains `Resources/Gizmo-Reference.md`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:150` — `refuses a non-empty folder with folder_not_empty, naming a child sample`
- **Note:** `unrecoverableFiles` appearing inside an *error's* data is unusual and easy to miss.

### CRD-082 — A folder holding only a dot-file trips folder_not_empty

- **Behavior:** "Contents" means anything a raw filesystem walk finds — hidden and gitignored files
  included — so a folder holding only a `.placeholder` is not empty and refuses without
  `recursive`. The dot-file appears in `sampleChildren`.
- **Trigger:** A folder containing only `.placeholder` → `folder_not_empty`, `childCount = 1`, sample contains the `.placeholder` path.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:160` — `trips folder_not_empty on a folder holding only a .placeholder (the enumeration regression)`
- **Note:** Explicitly a **regression test**: under the ripgrep census this folder enumerates as EMPTY, the guard never fires, and a triad folder deletes without `recursive`. The tool's spec was amended for this. High-value.

### CRD-083 — A genuinely empty folder deletes without recursive

- **Behavior:** The `recursive` requirement applies only to a folder with contents; an empty one
  deletes with `filesDeleted` empty and `kind: 'folder'`.
- **Trigger:** A newly created empty directory.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:175` — `deletes a genuinely empty folder with no recursive flag`

### CRD-084 — A recursive folder delete reports every file, dot-files included

- **Behavior:** `filesDeleted[]` is a complete plan drawn from the filesystem walk, listing nested
  files and dot-files alike, and the whole tree is removed.
- **Trigger:** `path: 'Projects/Widget'`, `recursive: true`, with a `.placeholder` added → `filesDeleted` contains `Projects/Widget/.placeholder`, `Projects/Widget/Widget.md`, `Projects/Widget/Inbox/Plan-Widget-Assembly.md`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:185` — `deletes a folder recursively and reports every file, dot-files included`

### CRD-085 — A nested repository under the target is refused, and recursive does not override it

- **Behavior:** Meeting a `.git` **directory** below the target refuses `nested_repository` with
  `data.nestedRepository` naming the path found, even with `recursive: true`, and nothing is
  removed.
- **Trigger:** `Projects/Widget/vendor/.git/` created, then `path: 'Projects/Widget'`, `recursive: true`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:199` — `refuses a folder containing a nested repository root even with recursive:true`
- **Note:** The refusal happens at the entry, before descending, so it stays cheap and does not bury the plan in thousands of untracked object files.

### CRD-086 — A `.git` FILE (submodule working tree) is also a nested repository

- **Behavior:** A `.git` file containing a `gitdir:` pointer triggers the same `nested_repository`
  refusal as a directory — the check is on the name, not the shape.
- **Trigger:** `Projects/Widget/vendor/.git` as a file with `gitdir: ../../.git/modules/vendor`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:209` — `refuses a nested repository declared as a .git FILE (a submodule working tree)`

### CRD-087 — Deleting a folder note while its folder survives reports folderNoteOrphaned

- **Behavior:** Deleting `Foo/Foo.md` while `Foo/` survives with other content breaks the
  folder-note invariant. delete is warn-and-proceed: `folderNoteOrphaned: true`,
  `folderNoteDeleted: false`, the note is removed, and the folder is left for lint.
- **Trigger:** `path: 'Projects/Widget/Widget.md'` (folder note of `Projects/Widget`, which has surviving children).
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:219` — `reports folderNoteOrphaned when a folder note is deleted and its folder survives`
- **Note:** The name match is case-insensitive against the parent directory name; the implementation only sets the flag when there is at least one *surviving* sibling.

### CRD-088 — An ordinary note does not report folderNoteOrphaned

- **Behavior:** The flag is false for a note whose stem does not match its parent folder's name.
- **Trigger:** `path: 'Projects/Widget/Tasks.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:228` — `does not report folderNoteOrphaned for an ordinary note`

### CRD-089 — folderNoteDeleted is true when a recursive folder delete takes the folder note with it

- **Behavior:** Deleting a folder recursively reports `folderNoteDeleted: true` when the plan
  contains `<folder>/<folder>.md`. Both folder-note fields are always present, each structurally
  false for the other kind.
- **Trigger:** `path: 'Projects/Widget'`, `recursive: true` → `folderNoteDeleted: true`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:185` — `deletes a folder recursively and reports every file, dot-files included`; also `delete.Tests.ps1:262`

### CRD-090 — danglingBacklinks names every surviving note whose link resolved to the deleted target

- **Behavior:** Deleting a linked-to note returns a `danglingBacklinks[]` of `{file, link}` objects
  naming each surviving note that pointed at it. All linking notes are found, wherever they sit.
- **Trigger:** `path: 'Resources/Gizmo-Reference.md'` → `file` values include `Projects/Widget/Tasks.md`, `Projects/Widget/Widget.md`, `Projects/Widget/Inbox/Plan-Widget-Assembly.md`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:236` — `names every note whose link resolved to the deleted target`
- **Note:** Each entry is one *match*, not one file — a note linking twice contributes two entries, which is why the test de-duplicates before asserting.

### CRD-091 — Both bare and path-qualified link spellings are identified

- **Behavior:** The census recognizes a bare wikilink (`[[Gizmo-Reference]]`) and a path-qualified
  one (`[[Resources/Gizmo-Reference]]`) as pointing at the same deleted file, and reports the
  `link` field as the target text exactly as written in the source note.
- **Trigger:** Same delete as CRD-090 → `link` values include both `Gizmo-Reference` and `Resources/Gizmo-Reference`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:236` — `names every note whose link resolved to the deleted target`
- **Note:** The fixture also carries heading (`[[Gizmo-Reference#Torque Specs]]`) and display-text (`[[Gizmo-Reference#Torque Specs|the torque table]]`) spellings, which the census also matches — the reported `link` for those is the target portion, the anchor and alias having been normalized away by the shared link-key function. Read from `delete.ps1:379-401` plus `Get-VaultLinkKey`; not separately asserted.

### CRD-092 — The census is report-only: no link is ever rewritten

- **Behavior:** delete surfaces broken links and changes nothing. After the delete, every linking
  note still contains its original wikilink text verbatim. The stated reason is that a deletion
  leaves no new name to point at.
- **Trigger:** After deleting `Resources/Gizmo-Reference.md`, `Projects/Widget/Tasks.md` still matches `[[Gizmo-Reference]]`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:245` — `names every note whose link resolved to the deleted target` (final assertion)
- **Note:** The highest-order fact about delete's link maintenance. Direct contrast with `move`, which rewrites.

### CRD-093 — A broken link never blocks the delete

- **Behavior:** Inbound links are reported, not enforced: there is no error identity for "still
  linked to" and no refusal path driven by `danglingBacklinks`. The delete applies regardless of
  how many links break.
- **Trigger:** Every dangling-backlink case in the suite deletes successfully.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:234-259` (whole context — absence of any refusal); confirmed against `tools/delete.ps1` (the census is computed after all refusals and feeds only the report).
- **Note:** Harvested as an explicit negative because the task brief asked whether delete "refuses or reports". It reports, always.

### CRD-094 — An empty danglingBacklinks list when nothing links at the target

- **Behavior:** The field is an empty array, present, when no surviving note pointed at the deleted
  file.
- **Trigger:** `path: 'Test-Vault.md'` (nothing in the fixture links to it) → `danglingBacklinks.Count = 0`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:249` — `reports an empty list when nothing links at the deleted file`

### CRD-095 — The backlink scan is vault-wide and deliberately UNSCOPED — only the operand is gated

- **Behavior:** Scope roots gate the delete's operand; they do not narrow the census. Notes outside
  the grant's scope are still scanned and still reported as dangling.
- **Trigger:** Scope roots `{scratch: ['Resources']}`, delete `Resources/Gizmo-Reference.md` → `danglingBacklinks` still contains `Projects/Widget/Tasks.md`, which is outside the grant.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:254` — `scans outside the grant's scope roots - the operand alone is gated`
- **Note:** Deliberate, mirroring move's link machinery. A redesign that scopes the census would silently under-report. Strong candidate for a spec gap.

### CRD-096 — A bare link is resolved the way Obsidian would, and a same-basename bystander is not reported

- **Behavior:** For a bare (unqualified) link whose basename matches a deleted file, the census
  resolves the link against all same-basename candidates in the vault and reports it only when the
  *winner* is a file in the destruction plan. A link that actually resolves to a surviving
  same-named note elsewhere is untouched and unreported.
- **Trigger:** Deleting `A/Dupe.md` while `B/Dupe.md` survives, from a note that resolves `[[Dupe]]` to `B/Dupe.md`.
- **Kind:** `semantic`

- **Source:** `delete.ps1:389-398` — read from the implementation; **not directly asserted** by the delete suite (the fixture it copies has no duplicate basenames).
- **Note:** Harvested because it is load-bearing and would be easy to get wrong. Path-qualified links take a different route: a component-aligned suffix match is treated as definitive with no resolution step (`delete.ps1:382-388`). Recorded as a likely coverage hole in the *old* suite as well as a spec risk.

### CRD-097 — Files inside the destruction plan are excluded from the census

- **Behavior:** A note being deleted is not scanned for backlinks — its own links go away with it —
  so a folder delete does not report its own internal cross-links as dangling.
- **Trigger:** A recursive folder delete of a folder whose notes link to each other.
- **Kind:** `semantic`

- **Source:** `delete.ps1:348-367` — read from the implementation; not directly asserted, though the `Projects/Widget` recursive cases would fail loudly if it were wrong.

### CRD-098 — A note that vanished mid-scan is recorded in filesSkippedUnreadable; a present-but-unreadable one is read_failure

- **Behavior:** During the backlink scan, a file that disappeared between enumeration and read (or
  a dangling symlink) is skipped and its path recorded in `filesSkippedUnreadable[]`. A file that
  is present but cannot be read is a hard `read_failure` carrying `data.path` and `data.error` —
  not a silent skip.
- **Trigger:** A note removed by another process between the census enumeration and its read.
- **Kind:** `semantic`

- **Source:** `delete.ps1:369-377` — read from the implementation; the suite only asserts `filesSkippedUnreadable` is present and empty (`delete.Tests.ps1:74`). The distinction itself is untested in the old suite.
- **Note:** The same missing-vs-unreadable split governs the *agentic classification* read (`delete.ps1:307-317`), where a present-but-unreadable **target** note blocks the delete with `read_failure`, because proceeding would remove a note whose agentic layer was never checked. The `partial_state` test at `delete.Tests.ps1:446` deliberately uses `.bin` files rather than `.md` for exactly this reason.

### CRD-099 — apply:false removes nothing and returns the identical report with applied:false

- **Behavior:** A plan runs plan-and-validate and returns the complete blast radius — full
  `filesDeleted`, folder-note fields, `unrecoverableFiles`, `danglingBacklinks`, `recoveryHint`,
  the write-class envelope, and `capabilitiesUsed` — without removing anything.
- **Trigger:** `path: 'Projects/Widget'`, `recursive: true`, `apply: false` → `applied: false`, `filesDeleted.Count = 3`, `folderNoteDeleted: true`, `capabilitiesUsed = ['agentic:write']`, files still present.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:262` — `apply:false removes nothing and returns the identical shape with applied:false`
- **Note:** `apply` defaults to **true** — mutation is the point of the call, so a single-file delete needs no ceremony. A plan still takes the write lock. `capabilitiesUsed` reports the class the plan *would* use, and the capability is asserted (not merely reported) even in a plan.

### CRD-100 — A plan and its result differ in `applied` alone

- **Behavior:** Serializing a plan and the subsequent real result and excluding `applied` yields
  identical JSON, with identical property names in identical order.
- **Trigger:** The same recursive folder delete run first with `apply: false` then for real.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:277` — `a plan and its result differ in \`applied\` alone`
- **Note:** A strong structural contract: the report is computed entirely before the apply phase and the apply phase adds nothing to it.

### CRD-101 — apply:false refuses everything a real call would refuse

- **Behavior:** A plan is not a permissive mode — every validate-phase refusal fires identically.
- **Trigger:** `path: 'Resources'`, `apply: false` → `folder_not_empty`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:289` — `apply:false still refuses everything a real call would refuse`

### CRD-102 — Deleting a note carrying nav.* keys asserts agentic:write

- **Behavior:** Deletion is the degenerate case of the outcome diff — the diff is the removal of
  the note's entire frontmatter — so a note carrying any `agent.*`/`nav.*` key requires
  `agentic:write`. Denial is `insufficient_capability` with `data.requiredClass = 'agentic:write'`
  and `data.paths` naming the offending note; the note survives.
- **Trigger:** Injected `['content:write']`, `path: 'Projects/Widget/Widget.md'` (carries `nav.contents`).
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:296` — `denies deleting a note carrying nav.* keys, with zero mutation and the path named`
- **Note:** The stated reason: without this, a `content:write` agent could shed a `pinned:` constraint simply by deleting its carrier note.

### CRD-103 — A folder delete is denied when ANY contained note carries the agentic layer

- **Behavior:** Classification runs over the whole enumerated set. One agentic note anywhere in the
  tree denies the entire delete, `data.paths` names it, and not one file is removed.
- **Trigger:** Injected `['content:write']`, `path: 'Projects/Widget'`, `recursive: true` → `data.paths` contains `Projects/Widget/Widget.md`; `Projects/Widget/Tasks.md` still present.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:305` — `denies a folder delete when ANY contained note carries the agentic layer`

### CRD-104 — An injected-but-empty class set DENIES the delete

- **Behavior:** Same three-way split as create: an authenticated caller holding nothing is denied.
- **Trigger:** Injected `@()`, agentic note → `insufficient_capability`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:314` — `DENIES on an injected-but-empty class set (authenticated "holds nothing")`

### CRD-105 — Holding agentic:write allows the delete and reports it

- **Behavior:** `capabilitiesUsed` is exactly `['agentic:write']` on the successful delete of an
  agentic note.
- **Trigger:** Injected `['content:write', 'agentic:write']`, `path: 'Projects/Widget/Widget.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:320` — `allows the same delete when the injected set holds agentic:write`

### CRD-106 — A doc.*-only note never asserts the agentic key, even with an empty class set

- **Behavior:** Non-agentic frontmatter does not reach the gate; `capabilitiesUsed` empty.
- **Trigger:** Injected `@()`, `path: 'Inbox/Note-Loose-Thought.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:326` — `never asserts the key for a note carrying only doc.* keys, even with an empty set`

### CRD-107 — A non-note file never asserts the agentic key

- **Behavior:** Only `.md` files are read for classification; a binary or other non-markdown file
  is deleted with no capability assertion even with an empty class set.
- **Trigger:** Injected `@()`, `path: 'Resources/diagram.png'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:332` — `never asserts the key for a non-note file, even with an empty set`
- **Note:** The extension test is case-insensitive on the path suffix, not a content sniff.

### CRD-108 — Standalone delete degrades OPEN on an agentic note

- **Behavior:** With no injected op-class set at all (separate process), deleting an agentic note
  succeeds and still reports `capabilitiesUsed = ['agentic:write']`.
- **Trigger:** `pwsh -File delete.ps1 -Path Projects/Widget/Widget.md` in a fresh process → exit 0, note removed.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:598` — `degrades OPEN on an agentic note with no injected op-class set`

### CRD-109 — A tracked file yields an empty unrecoverableFiles and a plain recoveryHint

- **Behavior:** When git tracks everything in the plan, `unrecoverableFiles` is empty and
  `recoveryHint` names the restoring command for the operand path.
- **Trigger:** In a real git repo, delete a committed file → hint matches `git checkout -- 'Inbox/Note-Loose-Thought.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:350` — `reports an empty unrecoverableFiles and a plain recoveryHint for a tracked file`
- **Note:** The whole git-recoverability context is fixture-heavy: it runs `git init`, writes a `.gitignore`, adds, and commits inside the temp vault, because recoverability is genuinely a git question.

### CRD-110 — An untracked file is named unrecoverable and the recoveryHint is qualified

- **Behavior:** A file git never tracked is listed in `unrecoverableFiles[]`, and `recoveryHint`
  says plainly that the git command does NOT cover it — the hint is never a blanket promise.
- **Trigger:** An untracked `Resources/Untracked.md`, then `path: 'Resources'`, `recursive: true` → `unrecoverableFiles = ['Resources/Untracked.md']`, hint matches `does NOT cover`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:356` — `names an untracked file as unrecoverable and qualifies the recoveryHint`
- **Note:** Tracked files in the same plan still appear in `filesDeleted[]` — the two lists are the plan and a subset of it, not alternatives.

### CRD-111 — A gitignored file is both in the plan and named unrecoverable

- **Behavior:** A `.gitignore`-excluded file is in `filesDeleted[]` (the walk does not honor
  `.gitignore`) and in `unrecoverableFiles[]` (git never tracked it).
- **Trigger:** `.gitignore` containing `Secrets/`, a `Secrets/Keys.md`, then `path: 'Secrets'`, `recursive: true` → both lists = `['Secrets/Keys.md']`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:364` — `names a gitignored file as unrecoverable and puts it in the plan at all`
- **Note:** The implementation uses `git ls-files` as the sole authority rather than `git check-ignore`, explicitly so a force-added ignored file — which git *does* track and can restore — is not misclassified as unrecoverable. Not separately tested.

### CRD-112 — A gitignored note carrying the agentic layer still demands agentic:write

- **Behavior:** The agentic classification covers gitignored notes, so deleting a hidden-from-the-
  census `pinned:` carrier is refused `insufficient_capability` naming its path, and the note
  survives.
- **Trigger:** `.gitignore` covering `Secrets/`, `Secrets/Pinned.md` carrying `agent.instructions: - pinned:`, injected `['content:write']`, `path: 'Secrets'`, `recursive: true`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:374` — `demands agentic:write for a GITIGNORED note carrying the agentic layer`
- **Note:** The compounding failure the enumeration amendment exists to close: under the ripgrep census this note is invisible, so it would be deleted — shedding a pinned constraint — without the key ever being asserted. Together with CRD-082, the strongest argument for the two-enumeration design.

### CRD-113 — A non-git vault makes the entire plan unrecoverable

- **Behavior:** When the vault is not a git repository (or git is unavailable), every file in the
  plan is listed in `unrecoverableFiles[]` and `recoveryHint` says so — the honest answer, not an
  empty list.
- **Trigger:** Remove the `.git` directory, then delete one file → `unrecoverableFiles` = that file, hint matches `not a git repository`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:388` — `reports the whole plan as unrecoverable when the vault is not a git repository`
- **Note:** git's absence degrades the *report*, never the delete. The delete still applies.

### CRD-114 — A read-only file anywhere in the plan refuses the whole delete

- **Behavior:** A pre-flight over every planned file refuses `readonly_targets` with
  `data.readOnlyFiles` naming them, before the first removal, so a read-only file partway through
  cannot leave a half-deleted folder. Nothing is removed.
- **Trigger:** Set `Projects/Widget/Tasks.md` read-only, then `path: 'Projects/Widget'`, `recursive: true`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:397` — `refuses readonly_targets and mutates nothing`

### CRD-115 — An operand outside the ambient scope roots is out_of_scope

- **Behavior:** `out_of_scope` with `data.path`; the target survives.
- **Trigger:** Scope roots `{scratch: ['Projects']}`, `path: 'Inbox/Note-Loose-Thought.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:414` — `errors out_of_scope when the operand falls outside the ambient scope roots`
- **Note:** Only the operand is gated; a folder's contents are inside it by construction, and the census is unscoped (CRD-095).

### CRD-116 — An in-scope operand deletes normally

- **Behavior:** With scope roots configured, an operand inside them proceeds.
- **Trigger:** Scope roots `{scratch: ['Projects']}`, `path: 'Projects/Widget/Tasks.md'` → `applied: true`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:422` — `deletes normally when the operand falls inside the ambient scope roots`

### CRD-117 — An unconfigured vault is unknown_vault; a configured-but-missing root is vault_unavailable

- **Behavior:** The same two-identity vault resolution as create, raised before anything is locked.
- **Trigger:** `vault: 'nope'` → `unknown_vault` with `data.vault`; a configured `gone` pointing at a non-existent directory → `vault_unavailable`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:428` — `refuses an unconfigured vault with unknown_vault`; `delete.Tests.ps1:434` — `refuses a configured vault whose root is missing with vault_unavailable`

### CRD-118 — A folder delete that fails partway raises partial_state naming what was removed

- **Behavior:** The lock serializes but does not make the operation atomic. A failure mid-apply
  raises `partial_state` with `data.filesDeleted` (already removed, in plan order),
  `data.remaining` (untouched), and `data.error`; the message points at `git checkout`. The
  already-removed files stay removed.
- **Trigger:** Two `.bin` files in a folder, the second held with an exclusive `FileShare.None` handle; `path`, `recursive: true` → `filesDeleted = ['…/a.bin']`, `remaining = ['…/b.bin']`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:446` — `names the files already removed when a folder delete fails partway`
- **Note:** The fixture uses non-`.md` files deliberately: a locked `.md` would fail the *validate*-phase agentic read as `read_failure` instead — a different and correct refusal (see CRD-098). Files are removed one at a time in plan order so `remaining` is exactly the untouched tail. create has no equivalent code because a single note is one atomic write.

### CRD-119 — A write lock held by another process yields lock_timeout, with nothing deleted

- **Behavior:** delete runs under the per-vault write lock; contention from a separate process
  fails `lock_timeout` with `data.timeoutSeconds`, and the target survives.
- **Trigger:** A background job holding the lock; timeout env set to 1 second.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:481` — `fails lock_timeout when the write lock is held by another process`
- **Note:** Windows-only in the old suite. Mirrors CRD-059.

### CRD-120 — delete declares OpClass content:write in its comment-help

- **Behavior:** The dispatch floor for delete is `content:write` — the same floor as create; delete
  has no dedicated destructive class, because git is the backstop.
- **Trigger:** Static inspection of the script text.
- **Kind:** `artifact`

- **Source:** `delete.Tests.ps1:532` — `declares the content:write floor in its .NOTES comment-help`
- **Note:** The floor itself is semantic and notable — a destructive tool sharing the write floor with create is a deliberate decision resting on `unrecoverableFiles` and `recoveryHint`.

### CRD-121 — Every delete parameter is documented, and the surface is path/kind/recursive/apply/vault

- **Behavior:** The parameter surface is exactly those five, each documented.
- **Trigger:** Static inspection.
- **Kind:** `artifact`

- **Source:** `delete.Tests.ps1:537` — `documents every parameter in comment-help, which the loader requires to load the tool`
- **Note:** The five-name surface is the semantic half.

### CRD-122 — delete's kind carries no ValidateSet, and the prose names "file" or "folder"

- **Behavior:** An unrecognized kind is delete's own coded error rather than a binding failure.
- **Trigger:** Static inspection: no `[ValidateSet(`; `.PARAMETER kind` prose contains `"file" or "folder"`.
- **Kind:** `artifact`

- **Source:** `delete.Tests.ps1:544` — `declares no ValidateSet on kind, so an unrecognized value is delete's own error`

### CRD-123 — delete declares no force or purge parameter — git is the undo

- **Behavior:** There is deliberately no git-bypassing hard delete and no escape hatch parameter.
  Recoverability is communicated (`unrecoverableFiles`, `recoveryHint`) rather than overridden.
- **Trigger:** Static inspection: no `.PARAMETER force`, no `.PARAMETER purge`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:550` — `declares no force/purge parameter - git is the undo`
- **Note:** Marked semantic despite the static check: the absence of an override is a contract decision. Note also that `recursive` is explicitly NOT an override for the nested-repository refusal (CRD-085).

### CRD-124 — delete is invocable standalone via `pwsh -File`, exit 0 with the JSON result

- **Behavior:** The tool runs as a plain process against a real vault with no host context, exits
  0, prints the result to stdout, and removes the file.
- **Trigger:** `pwsh -NoProfile -NonInteractive -File delete.ps1 -Path 'Inbox/Note-Loose-Thought.md'`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:579` — `runs via a literal "pwsh -File delete.ps1" invocation (the Tool Authoring Standard spelling)`

### CRD-125 — A standalone delete failure exits non-zero with the JSON error shape on stderr

- **Behavior:** Non-zero exit code and a `{code, message, data?}` object on stderr.
- **Trigger:** Standalone delete of `Inbox/Nope.md` → stderr matches `"code": "not_found"`.
- **Kind:** `semantic`

- **Source:** `delete.Tests.ps1:617` — `surfaces the shared JSON error shape on stderr under pwsh -File`

---

## Fixture cases

### CRD-126 — Both suites run against a fresh scratch copy of test-vault per test

- **Behavior:** Each test copies `test-vault/` into a per-test temp root named `scratch`, writes a
  `vaults.json` naming `scratch` as the default vault, and points `VAULT_MCP_VAULTS_JSON` at it.
  The committed `test-vault/` is never written. Ambient globals (`VaultMcpScopeRoots`,
  `VaultMcpOpClasses`) are removed after every test.
- **Trigger:** Every case in both suites.
- **Kind:** `fixture`

- **Source:** `create.Tests.ps1:54`; `delete.Tests.ps1:40`
- **Note:** Unlike the module suites, these tool suites do **not** use `TestHelpers.ps1` — there is no `test`/`scratch` pair, no injected `.obsidian/junk.md`, and no `A/Dupe.md`/`B/Dupe.md`. That absence is why CRD-096 (the bare-link bystander rule) is untested here.

### CRD-127 — The fixture vault's link graph is what the census cases rest on

- **Behavior:** `Resources/Gizmo-Reference.md` is linked from three notes in four spellings: bare
  (`[[Gizmo-Reference]]` in `Tasks.md`, `Widget.md`, `Plan-Widget-Assembly.md`), with a heading
  (`[[Gizmo-Reference#Torque Specs]]`), with heading plus display text
  (`[[Gizmo-Reference#Torque Specs|the torque table]]`), and path-qualified
  (`[[Resources/Gizmo-Reference]]`). `Test-Vault.md` is linked by nothing. `Projects/Widget/`
  contains its folder note `Widget.md` (carrying `nav.contents`, hence agentic), `Tasks.md`
  (no frontmatter), and `Inbox/Plan-Widget-Assembly.md`.
- **Trigger:** CRD-090, CRD-091, CRD-094, CRD-102, CRD-103, CRD-106.
- **Kind:** `fixture`

- **Source:** `C:\Repos\vault-server\test-vault\**`
- **Note:** The fixture note itself says `Gizmo-Reference` is "the primary rename target for backlink-sync tests: it is linked bare, path-qualified, with display text, and by heading". `Projects/Widget` holds exactly 3 files, which is why CRD-099 asserts `filesDeleted.Count = 3`. `Tasks.md` having no frontmatter at all is what makes CRD-088 and the scope case at CRD-116 non-agentic.

### CRD-128 — The standalone contexts spawn a genuinely separate pwsh process

- **Behavior:** The standalone suites do not call the script in the Pester session; they launch a
  new process so that no leftover `VaultMcpScopeRoots` or `VaultMcpOpClasses` global from the host
  wrapper or from another test can be mistaken for injected host context.
- **Trigger:** CRD-053, CRD-067, CRD-068, CRD-108, CRD-124, CRD-125.
- **Kind:** `fixture`

- **Source:** `create.Tests.ps1:633`; `delete.Tests.ps1:557`
- **Note:** Essential to CRD-053 and CRD-108: an in-process run would make the degrade-open assertion pass vacuously.
