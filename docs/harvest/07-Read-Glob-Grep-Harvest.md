# Harvest: The read-class tools — `read`, `glob`, `grep`

Sources:
- `C:\Repos\vault-server\tools\Tests\read.Tests.ps1` (520 lines)
- `C:\Repos\vault-server\tools\Tests\glob.Tests.ps1` (180 lines)
- `C:\Repos\vault-server\tools\Tests\grep.Tests.ps1` (314 lines)

Read the implementation for:
- `C:\Repos\vault-server\tools\read.ps1` (180 lines) — to state the argument surface, the default
  `part`, and the exact ordering of the operand-shape rules the tests only pin obliquely.
- `C:\Repos\vault-server\tools\glob.ps1` (73 lines), `C:\Repos\vault-server\tools\grep.ps1` (118 lines) —
  argument surface and defaults.
- `C:\Repos\vault-server\powershell\VaultAccess\Public\Enumerate.ps1` (269 lines) —
  `Get-VaultFilePath` / `Search-VaultContent`, for the visibility defaults (`.gitignore`,
  dot-directories, symlinks), the containment guard, the exit-code classification, and to confirm
  the **absence** of any limit/truncation feature. Cases derived from the implementation alone are
  marked in `Note`.
- `C:\Repos\vault-server\test-vault\` — fixture inventory (6 notes).

## Summary

Three tools, one op class (`content:read`), one output tier ("Tier-1": raw plain text, never JSON —
no envelope, no path echo, no line numbers except grep's own `path:line:text` shape).

There is **no pagination, no result cap, no truncation and no `head_limit`/`offset` anywhere** in
this area. `glob` and `grep` emit every match ripgrep produced, in ripgrep's order, unsorted;
`read` returns the whole requested part however large. A spec-derived suite is likelier to have
*invented* a limit than to have lost one — but the absence is itself a pinned property (RDT-054,
RDT-072) and should be checked in that direction.

The read/glob/grep split is a load-bearing contract: `glob` matches **paths only**, `grep` matches
**content only**, `read` takes a path and never a name. `read` is an "operate-on" tool (exactly one
existing target; a miss is an error); `glob` and `grep` are "query" tools (zero matches is empty
output, never an error).

The distinction the task flags — missing vs. directory vs. non-markdown vs. extensionless vs.
out-of-scope — is genuinely four-way in `read`, and rule *order* is asserted, not just the codes:

1. vault resolution (`unknown_vault` / `vault_unavailable`) — before all path work,
2. path normalization + escape check (`invalid_argument`),
3. scope membership (`out_of_scope`) — **before any file access**,
4. folder probe (`kind_mismatch`, `resolvedKind=folder`) — **before** the extension rule,
5. extensionless rule (`invalid_argument` + `suggestedPath`) — **before** existence,
6. existing non-`.md` leaf (`kind_mismatch`, `resolvedKind=file`),
7. the read itself: absent → `not_found`; present-but-unreadable → `read_failure`.

Scope is applied two different ways, deliberately: `read` **errors** `out_of_scope`; `glob` and
`grep` **silently drop** out-of-scope results and never raise it — including when the caller names
an out-of-scope folder explicitly. That asymmetry is the single most likely thing for a spec to
lose. Scope absent or empty degrades **open** (a standalone run injects none).

`read`'s `part` enum has a fourth member, `sha`, which reads the file as raw bytes and carries its
own duplicate of the whole error surface — every error case is asserted twice, once for the text
parts and once for `sha`.

Fixture note: every test copies `test-vault/` (6 notes: `Test-Vault.md`, `Inbox/Note-Loose-Thought.md`,
`Projects/Widget/{Widget,Tasks}.md`, `Projects/Widget/Inbox/Plan-Widget-Assembly.md`,
`Resources/Gizmo-Reference.md`) to a temp `scratch` vault with a generated `vaults.json` naming it
default, via `VAULT_MCP_VAULTS_JSON`. `Projects/Widget/Tasks.md` has **no frontmatter**;
`Resources/Gizmo-Reference.md` carries the unique token `quorvium` on exactly two lines.

## Cases

### RDT-001 — read returns the whole note by default

- **Behavior:** With no part requested, read returns the note's entire content — frontmatter and body together — as raw text.
- **Trigger:** `read` on `Projects/Widget/Widget.md` with no `part`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:59` — `returns the full note content by default`

### RDT-002 — read's return is raw text, not a JSON envelope

- **Behavior:** The result is the note's native text. It is not wrapped in a JSON object, and does not begin with `{`.
- **Trigger:** `read` on any note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:65` — `returns raw text, not JSON (read is a Tier-1 native-format cognate)`
- **Note:** Called the "Tier-1 native-format cognate" rule; the same tier governs glob and grep output.

### RDT-003 — part "full" is exactly the default

- **Behavior:** Requesting part `full` explicitly produces byte-identical output to omitting the part.
- **Trigger:** `read -Part 'full'` vs. `read` with no part, same note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:71` — `accepts part full explicitly, matching the default`

### RDT-004 — part "frontmatter" returns the block's inner text without the fences

- **Behavior:** Part `frontmatter` returns the raw YAML text between the leading `---` fences. The fence lines themselves are not included, and no body content is included.
- **Trigger:** `read -Part 'frontmatter'` on a note with frontmatter.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:77` — `returns only the frontmatter block with part frontmatter`
- **Note:** The block is returned **raw and never parsed** — a ratified contract per the tool's own documentation. No YAML round-trip, no reserialization.

### RDT-005 — part "frontmatter" on a note without frontmatter is empty

- **Behavior:** A note with no frontmatter block yields an empty result for part `frontmatter` — not an error, not the body.
- **Trigger:** `read -Part 'frontmatter'` on `Projects/Widget/Tasks.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:85` — `returns an empty result for part frontmatter on a note with no frontmatter`

### RDT-006 — part "body" is everything after the closing fence, verbatim

- **Behavior:** Part `body` returns every character after the closing `---` line, byte-for-byte, including a leading blank line and the trailing newline. Nothing is trimmed or normalized.
- **Trigger:** A note written as `---\ndoc.type: Test\n---\n\n# Body-Case\nline two\n`; part `body` returns exactly `\n# Body-Case\nline two\n`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:90` — `returns everything after the closing fence, verbatim, with part body`
- **Note:** The verbatim contract is load-bearing: part `body` is where a caller takes an `edit` `old_string` anchor from. Any trimming breaks anchoring. See RDT-013.

### RDT-007 — part "body" excludes the frontmatter on a real note

- **Behavior:** Part `body` on a note with frontmatter contains none of the frontmatter keys and all of the prose.
- **Trigger:** `read -Part 'body'` on `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:98` — `omits the frontmatter block from part body on a real fixture note`

### RDT-008 — part "body" on a note without frontmatter is the whole note

- **Behavior:** For a note carrying no frontmatter block, part `body` equals part `full`.
- **Trigger:** `read -Part 'body'` on `Projects/Widget/Tasks.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:104` — `returns the whole content with part body on a note with no frontmatter`

### RDT-009 — an empty fence pair is not frontmatter: body is the whole file, fences included

- **Behavior:** A file opening `---` immediately closed by another `---` with no line between them is **not** a frontmatter block. It reads as two thematic breaks, so part `body` returns the entire file, fence lines included.
- **Trigger:** File content `---\n---\nBody`; part `body` returns `---\n---\nBody\n`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:121` — `returns the whole file, fences included, for an empty fence pair`
- **Note:** The suite's comment says the tooling no longer *produces* this shape (an emptying removal drops the fence entirely), so this pins how a file arriving from elsewhere is read.

### RDT-010 — an empty fence pair yields empty frontmatter

- **Behavior:** The same empty-fence-pair file yields an empty result for part `frontmatter` — the fences are not treated as delimiting an empty block.
- **Trigger:** File content `---\n---\nBody`; part `frontmatter`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:128` — `returns an empty result for part frontmatter on an empty fence pair`

### RDT-011 — the empty-fence-pair rule holds under CRLF line endings

- **Behavior:** The empty-fence-pair reading does not depend on the line terminator: a CRLF-spelled `---\r\n---\r\n...` reads the same way as its LF spelling — whole file as body.
- **Trigger:** File content `---\r\n---\r\n# Note\r\nline two\r\n`; part `body` returns it unchanged, CRLFs preserved.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:140` — `returns the whole file for an empty fence pair under CRLF endings too`
- **Note:** Explicitly a **deliberate divergence from the Markdig 0.44.0 oracle** the grammar is otherwise pinned against: Markdig reads `---\r\n---\r\n` as a YAML frontmatter block while reading the LF spelling as two thematic breaks. The rule chosen is "a reading of the shape, not of the terminator", and this is called the one place the locator departs from its oracle. Very easy for a spec to lose; also note the CRLFs survive into the output untranslated.

### RDT-012 — an empty fence pair with nothing after it returns just the fences

- **Behavior:** A file that is only `---\n---\n` returns `---\n---` as its body — the degenerate case still reads as prose, not as an empty note.
- **Trigger:** File content `---\n---\n`; part `body`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:147` — `returns the fences themselves for an empty fence pair with nothing after it`

### RDT-013 — read's body and the substrate facet `edit` patches against are the same text

- **Behavior:** For any given note, part `body` is exactly the body facet against which an edit's patch anchor is resolved. A caller may read part `body`, take an anchor from what it got, and have the patch resolve.
- **Trigger:** Asserted directly with `-BeExactly` against `Split-VaultNoteContent(...).Body` for the empty-fence content `---\n---\n# Note\nbody line\n`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:154` — `agrees with the substrate body facet edit patches against, for an empty fence`
- **Note:** A cross-tool coupling, not a read-local property. Whatever the frontmatter/body split rule is, `read -part body` and `edit`'s anchor resolution must agree on it. This is the reason the empty-fence cases exist at all.

### RDT-014 — part values are matched case-insensitively and surrounding whitespace is trimmed

- **Behavior:** The part value is normalized before matching: `Frontmatter` and `' SHA '` are accepted and behave as `frontmatter` and `sha`.
- **Trigger:** `read -Part 'Frontmatter'`; `read -Part ' SHA '`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:165` — `matches part values case-insensitively`; `tools/Tests/read.Tests.ps1:240` — `matches part sha case-insensitively`
- **Note:** The whitespace-trim half is only visible in the sha test (`' SHA '`), never stated separately. Both trim and case-fold are asserted.

### RDT-015 — an unrecognized part is invalid_argument echoing the value

- **Behavior:** A part value outside the enum is refused as `invalid_argument`, and the offending value is echoed back in the error's structured data under `part`.
- **Trigger:** `read -Part 'banana'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:170` — `rejects an unrecognized part as invalid_argument`
- **Note:** Deliberately *not* a binding-time/schema rejection — the tool must produce its own structured error, so any value has to reach the tool's own validation.

### RDT-016 — the rejection message names every accepted part, sha included

- **Behavior:** The `invalid_argument` message for a bad part enumerates the accepted values, and `sha` is among them.
- **Trigger:** `read -Part 'banana'`; message matches `'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:246` — `names sha among the accepted values when rejecting an unrecognized part`
- **Note:** Full accepted set per the implementation: `full`, `frontmatter`, `body`, `sha`.

### RDT-017 — an empty note reads as an empty result

- **Behavior:** A zero-length note returns empty content, not an error.
- **Trigger:** `read` on an empty `.md` file.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:176` — `returns an empty result for an empty note`

### RDT-018 — part "sha" is the SHA-256 of the file bytes as lowercase 64-char hex

- **Behavior:** Part `sha` returns the SHA-256 digest of the file's bytes on disk, rendered as 64 lowercase hex characters.
- **Trigger:** A byte-precise UTF-8-no-BOM file `---\ndoc.type: Test\n---\n\n# Sha-Case\nline two\n` yields `395b96389461e3349dc411539f5c54228bdccc9b5ec833c2accbd7afd67c3f63`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:187` — `returns the SHA-256 of the file bytes as lowercase 64-char hex for a known fixture`
- **Note:** Literal constant, deliberately not recomputed by the code under test. Pins algorithm (SHA-256), encoding (raw bytes, no decoding or newline translation), and casing (lowercase).

### RDT-019 — an empty note has the canonical empty-input digest, with no special-casing

- **Behavior:** An empty file's sha is the SHA-256 of zero bytes, `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` — empty content is not special-cased to an empty string or a sentinel.
- **Trigger:** `read -Part 'sha'` on an empty file.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:197` — `returns the SHA-256 of an empty note (no special-casing of empty content)`

### RDT-020 — the digest matches the raw bytes on disk for a real note

- **Behavior:** For any note, sha equals an independently computed SHA-256 over the file's bytes as read from disk.
- **Trigger:** `read -Part 'sha'` on `Projects/Widget/Widget.md`, compared against a directly computed digest.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:203` — `matches the digest of the raw bytes on disk for a real fixture note`

### RDT-021 — sha is stable across re-reads with no intervening change

- **Behavior:** Reading sha twice with no modification returns the same value. Reading is not itself a mutation.
- **Trigger:** Two consecutive `read -Part 'sha'` calls.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:211` — `returns the same digest on a no-op re-read`

### RDT-022 — sha changes when the body changes

- **Behavior:** A body edit produces a different sha.
- **Trigger:** Rewrite a note's body line; compare sha before and after.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:217` — `changes when the body changes`

### RDT-023 — sha changes when only the frontmatter changes, though the body is unchanged

- **Behavior:** The digest covers the whole file, not the slice a caller read: adding a frontmatter key changes sha while part `body` returns identical text.
- **Trigger:** Add `doc.created:` to a note's frontmatter; body output unchanged, sha differs.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:226` — `changes when only the frontmatter changes (the digest covers the whole file)`
- **Note:** This is the staleness-token contract with `edit`: a caller that read only one part still holds a token identifying the entire on-disk state, so `expected_sha` catches a concurrent change to a part the caller never looked at.

### RDT-024 — path separators fold to forward slash and a leading slash is trimmed

- **Behavior:** A path spelled with backslashes and/or a leading separator resolves to the same note as its canonical forward-slash, unrooted form.
- **Trigger:** `read -Path '\Projects\Widget\Widget.md'` returns Widget's content.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:254` — `folds backslashes and trims a leading slash`
- **Note:** Beyond separator normalization the path is taken **verbatim** — no case-folding of segments, no fuzzy resolution, no name search.

### RDT-025 — read never infers ".md": an extensionless path is refused, naming the .md form

- **Behavior:** A path whose final segment has no extension is refused as `invalid_argument` rather than resolved by appending `.md`. The error names the `.md` spelling both in its message and as structured data `suggestedPath`, so the caller can retry in one step.
- **Trigger:** `read -Path 'Projects/Widget/Widget'` (the `.md` file exists) → `invalid_argument`, `data.suggestedPath = 'Projects/Widget/Widget.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:259` — `does not infer .md: a bare extensionless path is refused, not resolved`

### RDT-026 — a missing note is not_found, echoing the path

- **Behavior:** A well-formed `.md` path naming nothing errors `not_found` with `data.path` set to the requested path. read is an operate-on tool: exactly one existing target is required and a miss is a real error, never an empty result.
- **Trigger:** `read -Path 'Inbox/Nonexistent-Note.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:268` — `errors not_found on a missing note (operate-on tools require exactly one target)`

### RDT-027 — a folder target is kind_mismatch, and the folder probe precedes the extension rule

- **Behavior:** A path resolving to an existing folder errors `kind_mismatch` with `data.requiredKind = 'note'` and `data.resolvedKind = 'folder'`. This holds even though the operand is also extensionless — the folder probe runs first, so the caller is told it named a folder rather than being suggested a `.md` sibling that is not where the folder note lives.
- **Trigger:** `read -Path 'Projects/Widget'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:274` — `rejects a folder target as kind_mismatch (read is note-semantic)`
- **Note:** The **ordering** is the point, not just the code. A spec that lists the rules without ordering them will produce `invalid_argument` + `suggestedPath 'Projects/Widget.md'` here, which the old suite explicitly refuses.

### RDT-028 — an existing non-markdown file is kind_mismatch, resolvedKind "file"

- **Behavior:** A path naming an existing file whose extension is not `.md` errors `kind_mismatch` with `data.resolvedKind = 'file'` (distinct from `'folder'`). read is note-semantic.
- **Trigger:** `read -Path 'Inbox/attachment.txt'` where that file exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:284` — `rejects an existing non-md file as kind_mismatch`
- **Note:** Extension comparison is case-insensitive per the implementation (`-ine '.md'`), so `Note.MD` is admitted as a note. Not directly asserted for read.

### RDT-029 — an extensionless path naming nothing is invalid_argument, not not_found

- **Behavior:** When the operand is extensionless *and* nothing exists at it, the answer is `invalid_argument` (with `data.path` = the given path and `data.suggestedPath` = path + `.md`), never `not_found`. The malformed-operand reading wins over the missing-target reading.
- **Trigger:** `read -Path 'Inbox/Nonexistent-Note'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:291` — `refuses an extensionless path naming nothing as invalid_argument, not not_found`
- **Note:** With RDT-026 this is the four-way distinction the task warns about: missing `.md` → `not_found`; missing extensionless → `invalid_argument`; existing folder → `kind_mismatch/folder`; existing non-md → `kind_mismatch/file`.

### RDT-030 — the extension rule precedes existence: an existing extensionless file is still invalid_argument

- **Behavior:** A file that exists but whose leaf has no extension (e.g. `LICENSE`) is refused as `invalid_argument` with `suggestedPath`, **not** as `kind_mismatch`. The no-inference rule is checked before the file is classified.
- **Trigger:** `read -Path 'Inbox/LICENSE'` where that file exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:299` — `refuses an existing extensionless file as invalid_argument (the rule precedes existence)`
- **Note:** Directly contradicts the intuitive reading ("it exists and isn't a note, so kind_mismatch"). Pair with RDT-031: an existing file with a *non-md dotted* extension does get `kind_mismatch`. The discriminator is presence of an extension, not markdown-ness.

### RDT-031 — a dotted non-md leaf falls to the kind_mismatch rule, not the extension rule

- **Behavior:** A leaf like `.placeholder` counts as having an extension, so it passes the no-inference rule and is classified by kind: `kind_mismatch` with `data.resolvedKind = 'file'`.
- **Trigger:** `read -Path 'Inbox/.placeholder'` where that file exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:315` — `admits a dotted non-md leaf to the kind_mismatch rule, not the extension rule`
- **Note:** A dotfile is treated as all-extension; the definition of "extension" is the trailing dot-delimited part of the final segment, and a leading dot counts.

### RDT-032 — "extension" is read from the last path segment, not the whole path

- **Behavior:** A dotted **folder** name earlier in the path does not supply the operand's extension. `Inbox/Archive.md/Notes` has an extensionless leaf and is refused as such, suggesting `Inbox/Archive.md/Notes.md`.
- **Trigger:** A directory literally named `Archive.md`; `read -Path 'Inbox/Archive.md/Notes'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:306` — `reads the extension from the last segment, not the whole path`
- **Note:** Guards against a naive "does the path end in .md / does the path contain .md" implementation.

### RDT-033 — read has no Kind parameter

- **Behavior:** read exposes no kind/type discriminator argument; supplying one is a hard argument-binding failure, not a tool error. read's only legal kind would be `note`, and note-ness is enforced on the target regardless, so the assertion would be inert.
- **Trigger:** `read -Path ... -Kind 'note'` throws.
- **Kind:** `artifact`
- **Source:** `tools/Tests/read.Tests.ps1:322` — `rejects the dropped Kind parameter as a binding error (the assertion is retired)`
- **Note:** The *absence* of the parameter is semantic (the argument surface must not carry it); the "binding error" spelling is the artifact. Retained here because it records a deliberate removal a redesign might re-add.

### RDT-034 — a whitespace-only path is invalid_argument

- **Behavior:** A path that is empty or only whitespace after normalization is refused as `invalid_argument`.
- **Trigger:** `read -Path '   '`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:327` — `errors invalid_argument on a whitespace path`

### RDT-035 — a vault-escaping path is invalid_argument, not not_found

- **Behavior:** A path escaping the vault root is a malformed operand, refused `invalid_argument` — not reported as a miss and never resolved outside the vault.
- **Trigger:** `read -Path '../outside.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:332` — `errors invalid_argument on a vault-escaping path`
- **Note:** Per the implementation the same rule covers a rooted path (`C:/...`), asserted for grep (RDT-070) but not directly for read.

### RDT-036 — a present-but-unreadable note is read_failure, not not_found

- **Behavior:** When the note exists but cannot be read (permissions, sharing lock, I/O), the error is `read_failure`, distinctly not `not_found`. Classification comes from the failure itself, not from a prior existence probe.
- **Trigger:** Hold the note open with no sharing, then `read` it.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:337` — `errors read_failure (not not_found) when the note exists but is locked`
- **Note:** The implementation comment gives the reason a probe is refused: probing is racy, and a dangling symlink passes a leaf-existence check while being unreadable.

### RDT-037 — part sha raises not_found on a missing note

- **Behavior:** The sha path carries the same error vocabulary as the text parts: a missing note is `not_found` with `data.path`, not a second vocabulary of its own.
- **Trigger:** `read -Path 'Inbox/Nonexistent-Note.md' -Part 'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:352` — `errors not_found for part sha on a missing note`

### RDT-038 — part sha rejects a folder as kind_mismatch

- **Behavior:** A folder target under part `sha` errors `kind_mismatch` with `resolvedKind = 'folder'`.
- **Trigger:** `read -Path 'Projects/Widget' -Part 'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:358` — `rejects a folder target as kind_mismatch for part sha`

### RDT-039 — part sha refuses an extensionless path as invalid_argument with suggestedPath

- **Behavior:** The no-`.md`-inference rule applies identically under part `sha`, carrying the same `suggestedPath` data.
- **Trigger:** `read -Path 'Projects/Widget/Widget' -Part 'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:364` — `refuses an extensionless path as invalid_argument for part sha`

### RDT-040 — part sha rejects an existing non-md file as kind_mismatch

- **Behavior:** A non-`.md` existing file under part `sha` errors `kind_mismatch` with `resolvedKind = 'file'` — sha does not become a general-purpose file hasher.
- **Trigger:** `read -Path 'Inbox/attachment.txt' -Part 'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:370` — `rejects an existing non-md file as kind_mismatch for part sha`

### RDT-041 — part sha raises read_failure on a locked note, echoing the path

- **Behavior:** A present-but-unreadable note under part `sha` errors `read_failure` with `data.path` set.
- **Trigger:** Exclusive-open the note, then `read -Part 'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:377` — `errors read_failure for part sha when the note exists but is locked`

### RDT-042 — part sha refuses out_of_scope before any file access

- **Behavior:** The scope check runs before the file is opened, so an out-of-scope path under part `sha` errors `out_of_scope` and no read is attempted. A digest is not a way around the scope floor.
- **Trigger:** Scope roots `{scratch: ['Projects']}`; `read -Path 'Resources/Gizmo-Reference.md' -Part 'sha'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:390` — `errors out_of_scope for part sha before any file access`

### RDT-043 — read errors out_of_scope for a path outside the ambient scope roots

- **Behavior:** When the caller's grant carries a non-empty set of scope roots for the vault, a path outside them errors `out_of_scope` with `data.path` (and, per the implementation, `data.vault`). read **errors** here rather than pretending the note is missing or returning nothing.
- **Trigger:** Scope roots `{scratch: ['Projects']}`; `read -Path 'Resources/Gizmo-Reference.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:420` — `errors out_of_scope when the path falls outside the ambient scope roots`
- **Note:** Contrast RDT-062 and RDT-063: glob/grep never raise `out_of_scope`, they drop silently. The two postures coexist deliberately — read names exactly one target, so refusing is informative; a query tool that named its exclusions would leak them.

### RDT-044 — read succeeds normally for a path inside the scope roots

- **Behavior:** A path inside the grant's scope roots reads with no difference in behavior from an unscoped run.
- **Trigger:** Scope roots `{scratch: ['Projects']}`; `read -Path 'Projects/Widget/Widget.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:427` — `reads normally when the path falls inside the ambient scope roots`

### RDT-045 — scope roots are per-vault, and absent or empty scope degrades open

- **Behavior:** Scope roots are supplied as a per-vault map; the entry for the *resolved* vault is the one applied, so roots configured for another vault the grant reaches never constrain this one. When there are no roots for this vault, or the set is empty, every path is in scope.
- **Trigger:** Scope roots are always injected keyed by vault name (`@{ scratch = @(...) }`) in every scoped test; the standalone runs inject none and read/glob/grep succeed unscoped.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:391,421` (keyed injection) — implementation `tools/read.ps1:91-95`, `powershell/VaultAccess/Public/Enumerate.ps1:28,90,202`
- **Note:** Derived largely from the implementation; the tests only exercise the single-vault shape. "Degrades open" is the explicit design posture at every chokepoint, not an oversight.

### RDT-046 — an unconfigured vault name is refused unknown_vault, structured

- **Behavior:** Naming a vault that is not configured errors `unknown_vault` with `data.vault` set to the name given. This is a machine-routable structured code even in a standalone run, not free prose from a lower layer.
- **Trigger:** `read -Path ... -Vault 'nope'`; same for glob and grep.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:401`; `tools/Tests/glob.Tests.ps1:102`; `tools/Tests/grep.Tests.ps1:179` — `refuses an unconfigured vault with unknown_vault`

### RDT-047 — a configured vault whose root is missing is refused vault_unavailable

- **Behavior:** A vault that *is* configured but whose root directory does not exist errors `vault_unavailable`, carrying a `data.reason` that says the root does not exist. Distinct from `unknown_vault`.
- **Trigger:** A `vaults.json` with `gone` pointing at a nonexistent directory; call any of the three tools with `-Vault 'gone'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:407`; `tools/Tests/glob.Tests.ps1:108`; `tools/Tests/grep.Tests.ps1:185` — `refuses a configured vault whose root is missing with vault_unavailable`

### RDT-048 — vault resolution runs before any path work, so a vault failure is never misfiled

- **Behavior:** The vault is resolved first, ahead of path validation and the search, so a vault failure surfaces as `unknown_vault`/`vault_unavailable` rather than being caught by a downstream wrapper and re-reported as `invalid_argument`.
- **Trigger:** grep with both a `path` and an unconfigured `vault` still yields `unknown_vault` (`grep -Pattern 'quorvium' -Path 'Projects' -Vault 'nope'`).
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:196` — `classifies an unconfigured vault as unknown_vault even with a path argument`
- **Note:** The test comment records this as a fixed regression: the path check resolves the vault internally, so without the up-front resolution it surfaced as `invalid_argument` carrying lower-layer prose. Ordering, again, is the behavior.

### RDT-049 — the vault argument defaults to the configured default vault

- **Behavior:** All three tools accept an optional vault selector; omitted, they operate on the configured default vault.
- **Trigger:** Every test calls without `-Vault` and hits the `scratch` vault named `default_vault` in the config.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:46`, `tools/Tests/glob.Tests.ps1:38`, `tools/Tests/grep.Tests.ps1:42` (config setup) — argument documented at `tools/read.ps1:36`
- **Note:** Vault configuration is located via a `VAULT_MCP_VAULTS_JSON` environment override in the tests; a real standalone run falls back to a module-relative `vaults.json`.

### RDT-050 — read declares the content:read op-class floor

- **Behavior:** read declares `content:read` as its required capability floor and asserts no additional class. Every path through it is a content read — including reading the agentic-layer (`agent.*`/`nav.*`) frontmatter, since that is how an agent navigates at all; only *mutating* that layer is a write class. read takes no write lock.
- **Trigger:** The tool's own metadata declares `OpClass: content:read`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:435` — `declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)`
- **Note:** The *spelling* (an `OpClass:` line in PowerShell comment help, parsed by a script-tool loader) is an artifact; the floor assignment is semantic. The gate itself is enforced elsewhere and proven generically.

### RDT-051 — glob declares the content:read op-class floor

- **Behavior:** glob's capability floor is `content:read` with no additional class: it mutates nothing and discloses only paths inside the caller's scope, so broader results require a broader **grant**, not a stronger class. No write lock.
- **Trigger:** glob's metadata declares `OpClass: content:read`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:121` — `declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)`

### RDT-052 — grep declares the content:read op-class floor

- **Behavior:** grep's capability floor is `content:read` with no additional class, agentic-layer frontmatter included; it never mutates and discloses only in-scope lines. No write lock.
- **Trigger:** grep's metadata declares `OpClass: content:read`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:240` — `declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)`

### RDT-053 — each read-class tool is invocable standalone, outside the server

- **Behavior:** Every tool runs correctly when invoked directly as a script against a real vault, with no host runspace, no injected scope context, and no environment setup beyond vault configuration. It exits zero on success and emits its normal output.
- **Trigger:** `pwsh -NoProfile -NonInteractive -File read.ps1 -Path 'Projects/Widget/Widget.md'`; same shape for `read -Part 'sha'`, `glob.ps1 -Pattern '**/Widget.md'`, `grep.ps1 -Pattern 'quorvium'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:469,488`; `tools/Tests/glob.Tests.ps1:151`; `tools/Tests/grep.Tests.ps1:270`
- **Note:** A stated Tool Authoring Standard: every tool must be standalone-invocable and must carry a test proving it. The tests deliberately spawn a genuinely separate process so no leftover host global could be mistaken for injected context. Standalone also means ripgrep resolves off `PATH` with no configured override — described as the expected degradation.

### RDT-054 — a standalone no-match run emits nothing and still exits zero

- **Behavior:** Zero results from a query tool is success: no output, exit code zero. Emptiness is never signalled through a failing exit.
- **Trigger:** `glob.ps1 -Pattern '**/NoSuchFileXYZ.md'`; `grep.ps1 -Pattern 'zzz-token-not-in-vault-zzz'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:166`; `tools/Tests/grep.Tests.ps1:285` — `emits no output for a no-match pattern and exits cleanly`

### RDT-055 — a standalone failure emits the shared structured error on stderr with a nonzero exit

- **Behavior:** Under standalone invocation an error is written to the error stream in the shared `{code, message, data?}` structured shape and the process exits nonzero — the same identity a caller sees through the server, not a stack trace or bare prose.
- **Trigger:** `read.ps1 -Path 'Inbox/Nonexistent-Note.md'` → stderr contains `"code": "not_found"`; `grep.ps1 -Pattern '('` → stderr contains `"code": "invalid_pattern"`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/read.Tests.ps1:506`; `tools/Tests/grep.Tests.ps1:300` — `surfaces the shared JSON error shape on stderr under pwsh -File`

---

### RDT-056 — glob returns matching vault-relative paths, one per line

- **Behavior:** glob emits each matching file's vault-relative, forward-slash path on its own line as plain text. No JSON, no counts, no metadata.
- **Trigger:** `glob -Pattern '**/Widget.md'` → exactly `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:50` — `returns matching vault-relative paths, one per line`

### RDT-057 — a glob pattern expresses location as well as name

- **Behavior:** An anchored pattern constrains where matches may come from: `Projects/**/*.md` returns only paths under `Projects/`.
- **Trigger:** `glob -Pattern 'Projects/**/*.md'` → 3 results, every one beginning `Projects/`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:55` — `honors a location-expressing (anchored) pattern`
- **Note:** The count 3 is fixture-dependent (`Projects/Widget/Widget.md`, `Tasks.md`, `Projects/Widget/Inbox/Plan-Widget-Assembly.md`).

### RDT-058 — a root-level file is reachable and returned with a bare relative path

- **Behavior:** A file directly at the vault root is matched by `**/Name.md` and returned as a bare `Name.md` with no leading `./` or `/`.
- **Trigger:** `glob -Pattern '**/Test-Vault.md'` → `Test-Vault.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:61` — `matches the root-level file`
- **Note:** Guards the path-prefix normalization: the underlying search prints paths relative to a `.` root, so a naive implementation returns `./Test-Vault.md`.

### RDT-059 — glob matching is case-insensitive, in both name and extension

- **Behavior:** Pattern matching ignores case throughout the path, extension included, matching the native Glob tool's behavior on Windows.
- **Trigger:** `glob -Pattern '**/widget.MD'` → `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:65` — `matches case-insensitively like the native Glob tool`

### RDT-060 — zero glob matches is empty output, never an error

- **Behavior:** A pattern matching nothing produces no output and no error. glob is a query tool: any result count is a result, so it can be used as an existence check with no error handling.
- **Trigger:** `glob -Pattern '**/NoSuchFileXYZ.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:69` — `produces empty output (not an error) when nothing matches`
- **Note:** glob never raises an ambiguity or not-found error either — many matches is simply many lines.

### RDT-061 — a glob pattern cannot escape the vault; escape probes return nothing

- **Behavior:** A pattern attempting to reach outside the vault root yields no results — and no error. Confinement is a property of the search's rooting, plus a per-result assertion that each match resolves under the vault root (covering junctions and reparse points), not of pattern sanitization.
- **Trigger:** Each of `../**`, `../../**`, `/Windows/**`, `C:/Windows/**`, `**/../../*` returns empty.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:75` — `confines <_> to the vault` (`-ForEach` over the five patterns)
- **Note:** Note the posture: escape is **empty output**, not `invalid_argument`. read refuses an escaping path (RDT-035); glob quietly finds nothing. Both are asserted, so a spec unifying them would break one.

### RDT-062 — glob silently drops out-of-scope matches

- **Behavior:** Files outside the grant's scope roots are omitted from glob's results with no `out_of_scope` error. A broader result set requires a broader grant, not a different error path.
- **Trigger:** Documented posture of the shared enumeration's scope post-filter.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/Enumerate.ps1:68,135`; `tools/glob.ps1:33-37`
- **Note:** **Derived from the implementation** — glob.Tests.ps1 removes the scope global in `AfterEach` but never sets one, so glob's scope filtering is asserted only indirectly through the shared enumeration and through grep's equivalent (RDT-063). A thin spot in the old suite worth flagging: glob's scope post-filter has no direct test at the tool level.

### RDT-063 — grep silently drops out-of-scope matches

- **Behavior:** Matches in files outside the grant's scope roots are omitted from grep's output with no `out_of_scope` error raised — the caller sees a naturally narrowed result set and is not told what was withheld.
- **Trigger:** Scope roots `{scratch: ['Projects/Widget']}`; `grep -Pattern 'quorvium'` returns only `Projects/Widget/...` lines; `Resources/Gizmo-Reference.md` matches but is absent.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:137` — `drops out-of-scope matches with no out_of_scope error`

### RDT-064 — naming an out-of-scope folder explicitly returns empty output, not out_of_scope

- **Behavior:** Even when the caller explicitly points grep at a folder wholly outside its scope, the answer is empty output rather than an `out_of_scope` refusal. The tool does not confirm or deny the existence of content there.
- **Trigger:** Scope roots `{scratch: ['Projects/Widget']}`; `grep -Pattern 'quorvium' -Path 'Resources'` → empty.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:145` — `an explicit out-of-scope path returns empty output, not out_of_scope`
- **Note:** The sharpest form of the read/query asymmetry, and the case most likely to be lost: the obvious spec reading is "the caller named something out of scope, so refuse it", which is exactly what read does (RDT-043) and exactly what grep must not do.

### RDT-065 — an empty or whitespace-only glob pattern is invalid_argument

- **Behavior:** A pattern that is empty or only whitespace is refused as `invalid_argument` — the one argument invariant glob owns itself.
- **Trigger:** `glob -Pattern ''`; `glob -Pattern '   '`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:81,86` — `rejects an empty pattern with invalid_argument`, `rejects a whitespace-only pattern with invalid_argument`
- **Note:** Deliberately validated by the tool rather than rejected at binding/schema time, so the caller gets the structured code rather than a framework error. Same design in grep (RDT-068).

### RDT-066 — a malformed glob pattern is invalid_pattern carrying the matcher's diagnostic

- **Behavior:** A syntactically invalid glob is refused as `invalid_pattern` (a distinct code from `invalid_argument`), with the underlying matcher's error text carried through as `data.stderr` rather than discarded.
- **Trigger:** `glob -Pattern '['`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/glob.Tests.ps1:91` — `maps a malformed glob to invalid_pattern carrying rg stderr`
- **Note:** Per the implementation, `data` also carries `pattern`. The classification depends on a caller-supplied pattern being present: the same underlying exit code with no pattern is a `ripgrep_failed`, not an `invalid_pattern`.

### RDT-067 — glob's visibility defaults: gitignore respected, dot-directories hidden, symlinks never followed

- **Behavior:** Enumeration honors `.gitignore`, does not descend into dot-directories (`.git`, `.obsidian`, `.claude`, ...), and never follows symlinks. Symlink-following is the one real vault-escape vector and is guarded by simply never opting in.
- **Trigger:** The standing default for every glob and grep call.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/Enumerate.ps1:60-66,155`; `tools/glob.ps1` / `tools/grep.ps1` synopses
- **Note:** **Derived from the implementation** — the glob and grep tool suites do not assert these directly (the fixture has no dot-directory, no `.gitignore` and no symlink). Called out as accepted v1 defaults. A related, separately-implemented rule for the note-only enumeration skips any path whose *directory* segments start with `.` — same intent, different mechanism.

### RDT-068 — glob results are file-general, not note-only

- **Behavior:** glob enumerates files, not only `.md` notes, so a pattern can match attachments and other non-markdown files. It is the same census `move` uses, so the two can never drift to independent notions of what exists.
- **Trigger:** Any pattern that would match a non-`.md` file.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/Enumerate.ps1:42-53`; `tools/glob.ps1:1-24`
- **Note:** **Derived from the implementation.** Not asserted by glob.Tests.ps1 — the fixture contains only `.md` files. The equivalent for grep *is* stated ("matches any text file rg reaches, not only `.md` notes"). Contrast read, which is strictly note-semantic (RDT-028).

---

### RDT-069 — grep's default output mode emits path:line:text per matching line

- **Behavior:** With no mode requested, grep emits one line per matching line of content, formatted `<vault-relative path>:<line number>:<matched line text>`. Paths use forward slashes and contain no backslashes.
- **Trigger:** `grep -Pattern 'quorvium'` → includes `Resources/Gizmo-Reference.md:11:The gizmo is cast from quorvium alloy, ...`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:54` — `emits path:line:text per matching line, vault-relative forward slashes`
- **Note:** The line number is 1-based. The colon convention relies on a vault-relative path never containing a colon — that assumption is what lets the path prefix be re-normalized without ambiguity.

### RDT-070 — grep reports every matching line in a file, not only the first

- **Behavior:** All matches within a file are emitted; the search does not stop at a file's first hit.
- **Trigger:** `grep -Pattern 'quorvium'` yields exactly 2 lines for `Resources/Gizmo-Reference.md` (lines 7 and 11).
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:62` — `accumulates every matching line in a file, not just the first hit`
- **Note:** Explicitly a fixed regression from the predecessor `search` tool, which broke on first hit. Worth checking a spec-derived suite for.

### RDT-071 — zero grep matches is empty output, never an error

- **Behavior:** A pattern matching nothing anywhere produces no output and no error.
- **Trigger:** `grep -Pattern 'zzz-token-not-in-vault-zzz'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:69` — `produces empty output (not an error) when nothing matches`

### RDT-072 — files_with_matches mode emits one bare path per matching file

- **Behavior:** In `files_with_matches` mode each file containing at least one match contributes exactly one line, the bare vault-relative path — no line number, no matched text, no duplication for multiple matches.
- **Trigger:** `grep -Pattern 'quorvium' -output_mode 'files_with_matches'` → `Resources/Gizmo-Reference.md` appears exactly once despite two matching lines.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:75` — `emits one vault-relative path per matching file, no line/text`

### RDT-073 — count mode emits path:count per matching file

- **Behavior:** In `count` mode each file with matches contributes one `<path>:<count>` line, the count being the number of matches in that file. Files with no matches are absent.
- **Trigger:** `grep -Pattern 'quorvium' -output_mode 'count'` → includes `Resources/Gizmo-Reference.md:2`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:90` — `emits path:count per file with matches`
- **Note:** The fixture has one match per line, so the tests do not distinguish "matching lines" from "matches" for a line with two hits. The underlying flag counts matching **lines**.

### RDT-074 — output_mode is matched case-insensitively (and trimmed)

- **Behavior:** The mode value is normalized before matching, so `Files_With_Matches` is accepted as `files_with_matches`.
- **Trigger:** `grep -Pattern 'quorvium' -output_mode 'Files_With_Matches'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:83` — `matches output_mode case-insensitively`
- **Note:** Per the implementation the value is trimmed as well as lowercased, mirroring read's `part` (RDT-014). The trim is not directly asserted for grep.

### RDT-075 — grep is case-sensitive by default

- **Behavior:** With no case option, matching respects case, matching the native Grep cognate.
- **Trigger:** `grep -Pattern 'QUORVIUM'` → empty, though `quorvium` occurs.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:98` — `is case-sensitive by default (native-Grep fidelity)`
- **Note:** Directly opposite to glob, which is case-**insensitive** by default (RDT-059). Two tools, two defaults, both deliberate — an easy thing for a spec to unify by accident.

### RDT-076 — an opt-in flag makes grep matching case-insensitive

- **Behavior:** Setting the ignore-case option makes matching case-insensitive.
- **Trigger:** `grep -Pattern 'QUORVIUM' -ignore_case $true` → matches `Resources/Gizmo-Reference.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:102` — `matches case-insensitively when ignore_case is set`
- **Note:** The argument is boolean and defaults to false/absent.

### RDT-077 — the path argument narrows the search to a subtree, output still vault-relative

- **Behavior:** An optional vault-relative folder narrows the walk. Result paths remain **vault-relative**, not relative to the narrowed folder.
- **Trigger:** `grep -Pattern 'quorvium' -Path 'Resources'` → every line begins `Resources/`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:110` — `searches only under the given vault-relative folder, output still vault-relative`

### RDT-078 — the path argument folds backslashes and strips leading/trailing slashes

- **Behavior:** The narrowing path is normalized the same way read's target is: separators fold to forward slash and leading/trailing slashes are trimmed, so `\Resources\` is the same operand as `Resources`.
- **Trigger:** `grep -Pattern 'quorvium' -Path '\Resources\'` → every line begins `Resources/`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:116` — `folds a backslash/leading-slash path spelling`
- **Note:** The **trailing** slash trim is asserted here but not for read.

### RDT-079 — a narrowed subtree with no matches is empty output, not an error

- **Behavior:** Narrowing to a subtree that contains no match returns empty output. The path's existence is not required and its emptiness is not an error.
- **Trigger:** `grep -Pattern 'quorvium' -Path 'Projects/Widget/Inbox' -output_mode 'files_with_matches'` → `Projects/Widget/Inbox/Plan-Widget-Assembly.md`; `grep -Pattern 'zzz-nope-zzz' -Path 'Resources'` → empty.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:121` — `a subtree with no matches is empty output, not an error`
- **Note:** Per the implementation, existence of the narrowing path is explicitly **not** validated — only that it is non-rooted and non-escaping. A nonexistent folder therefore yields empty output rather than `not_found`. Not directly asserted; a distinction a spec could easily lose.

### RDT-080 — the glob argument filters which files grep searches

- **Behavior:** An optional file-filter glob restricts the search to files the glob selects, independently of the content pattern. It is case-insensitive, like the glob tool's matching.
- **Trigger:** `grep -Pattern 'quorvium' -Glob 'Tasks.md' -output_mode 'files_with_matches'` → exactly `Projects/Widget/Tasks.md`, though other files also contain `quorvium`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:129` — `restricts matches to files the glob selects`
- **Note:** The filter is a basename-reaching glob here (`Tasks.md` matched a nested file), consistent with the underlying matcher's glob semantics. Case-insensitivity of this filter is documented but not asserted.

### RDT-081 — an empty or whitespace-only grep pattern is invalid_argument

- **Behavior:** A content pattern that is empty or only whitespace is refused as `invalid_argument`.
- **Trigger:** `grep -Pattern ''`; `grep -Pattern '   '`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:152,156` — `rejects an empty pattern`, `rejects a whitespace-only pattern`
- **Note:** This forbids the "match every line" degenerate call. It also means a pattern of literal spaces cannot be searched for.

### RDT-082 — an unrecognized output_mode is invalid_argument, echoing the value

- **Behavior:** A mode outside the three accepted values is refused as `invalid_argument` with the offending value echoed in structured data (`outputMode`).
- **Trigger:** `grep -Pattern 'quorvium' -output_mode 'banana'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:160` — `rejects an unrecognized output_mode, echoing the value`
- **Note:** The data key is `outputMode` (camelCase) while the argument is `output_mode` (snake_case) — an artifact-flavored detail inside a semantic case. The message names all three accepted values.

### RDT-083 — a rooted grep path is invalid_argument

- **Behavior:** A narrowing path given as an absolute/rooted path is a malformed operand, refused `invalid_argument` — never searched.
- **Trigger:** `grep -Pattern 'x' -Path 'C:/Windows'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:166` — `rejects a rooted path`

### RDT-084 — a vault-escaping grep path is invalid_argument

- **Behavior:** A narrowing path escaping the vault root is refused `invalid_argument`.
- **Trigger:** `grep -Pattern 'x' -Path '../outside'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:170` — `rejects a vault-escaping path`
- **Note:** grep's *path* argument refuses escape (like read, RDT-035); glob's *pattern* escape returns empty (RDT-061). The rule follows the argument's kind — a path operand is validated, a pattern is not.

### RDT-085 — a malformed regex is invalid_pattern, discriminated by the matcher's parse-error signature

- **Behavior:** A syntactically invalid content regex is refused as `invalid_pattern`, carrying the offending pattern (`data.pattern`) and the matcher's diagnostic (`data.stderr`, containing a regex-parse signature). The pattern is rejected before any search runs.
- **Trigger:** `grep -Pattern '('` → `invalid_pattern`, `data.pattern = '('`, `data.stderr` matches `regex parse error`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:205` — `maps a malformed regex to invalid_pattern carrying rg stderr (regex-parse signature)`
- **Note:** Marked in the suite as a "spec pinning test". The discriminator between `invalid_pattern` and `ripgrep_failed` is the **stderr signature**, not the exit code — both are the same exit code. The literal string `regex parse error` is an artifact of the matcher; the *need* for a discriminator is semantic. Contrast glob, which discriminates by whether a caller pattern was supplied at all (RDT-066).

### RDT-086 — an unreadable file aborts the whole grep scan as ripgrep_failed, even after other files matched

- **Behavior:** A per-file read error fails the entire search — `ripgrep_failed`, carrying the exit code and the diagnostic — rather than silently narrowing the result to the files that could be read. This holds even when matches from other files were already found and printed.
- **Trigger:** Hold `Resources/Gizmo-Reference.md` open exclusively, then `grep -Pattern 'quorvium'` → `ripgrep_failed`, `data.exitCode = 2`, `data.stderr` non-empty and NOT containing `regex parse error`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/grep.Tests.ps1:212` — `aborts the whole scan as ripgrep_failed on a per-file read error, even though other files matched`
- **Note:** Marked in the suite as a "spec pinning test", and the highest-value case here. The intuitive spec reading is that a query tool degrades gracefully past an unreadable file — this asserts the opposite ("coarse posture"): partial results are never silently returned as complete. Also asserts it is not misfiled as `invalid_pattern`. The test skips itself if an exclusive open cannot be constructed on the machine.

### RDT-087 — grep and glob emit results in the matcher's order, unsorted

- **Behavior:** Output ordering is whatever the underlying search produced; results are not sorted, grouped, or otherwise reordered by the tool.
- **Trigger:** Any multi-result call.
- **Kind:** `semantic`
- **Source:** `tools/glob.ps1:68-72`, `tools/grep.ps1:112-116`, `tools/Tests/glob.Tests.ps1:8-11` (suite header)
- **Note:** **Derived from the implementation and suite headers.** No test asserts a specific order — the multi-result assertions all use order-insensitive forms. Worth recording because a redesign that sorts results would satisfy every existing assertion while violating the stated contract.

### RDT-088 — none of the three tools paginates, caps, or truncates its result

- **Behavior:** There is no result limit, offset, head-limit, or truncation anywhere in read, glob, or grep. read returns the whole requested part; glob and grep emit every match found.
- **Trigger:** Any call. No such argument exists on any of the three.
- **Kind:** `semantic`
- **Source:** `tools/read.ps1:49-54`, `tools/glob.ps1:45-49`, `tools/grep.ps1:58-66` (complete argument surfaces); no assertion in any of the three suites
- **Note:** **Derived from the implementation — an assertion of absence.** Recorded because a spec-derived reimplementation is more likely to have invented a cap (native Grep has `head_limit`) than to have dropped one. If the new suite has a truncation test, the gap runs the other way and is a spec addition, not a harvest miss.

### RDT-089 — the read-class tools take no write lock

- **Behavior:** None of the three acquires a write lock; they are read-class and do not serialize against each other or block writers by virtue of reading.
- **Trigger:** Any call.
- **Kind:** `semantic`
- **Source:** `tools/read.ps1:41-44`, `tools/glob.ps1:36-38`, `tools/grep.ps1:53-55` (`.NOTES`)
- **Note:** **Derived from the implementation's own documentation.** Not asserted by any test in this area, but stated identically in all three tools, so it reads as a contract rather than an implementation accident.

### RDT-090 — the fixture vault and its test harness

- **Behavior:** Each test runs against a fresh recursive copy of a six-note fixture vault into a temp `scratch` directory, with a generated vault configuration naming `scratch` as the default vault and pointed at via an environment override. Ambient scope and op-class globals are removed after every test so no test leaks context into the next.
- **Trigger:** `BeforeEach`/`AfterEach` in all three suites.
- **Kind:** `fixture`
- **Source:** `tools/Tests/read.Tests.ps1:41-56`; `tools/Tests/glob.Tests.ps1:33-47`; `tools/Tests/grep.Tests.ps1:37-51`
- **Note:** Fixture contents relied on by cases above: `Test-Vault.md` (root level); `Inbox/Note-Loose-Thought.md`; `Projects/Widget/Widget.md` (has frontmatter incl. `nav.contents:`); `Projects/Widget/Tasks.md` (**no frontmatter** — RDT-005, RDT-008); `Projects/Widget/Inbox/Plan-Widget-Assembly.md`; `Resources/Gizmo-Reference.md` (contains the unique token `quorvium` on lines 7 and 11 — RDT-069, RDT-070, RDT-073). Unlike the module suite's fixture, this one injects no `.obsidian/junk.md` and no duplicate-basename pair, which is why the visibility defaults (RDT-067) go untested here.

### RDT-091 — tool test files live outside the tool discovery directory

- **Behavior:** Test files are kept in a subdirectory rather than beside the tool scripts, because the tool loader scans the tool directory's top level for tool scripts and would otherwise load a test file as a broken tool.
- **Trigger:** Structural.
- **Kind:** `artifact`
- **Source:** `tools/Tests/read.Tests.ps1:5-7`; `tools/Tests/glob.Tests.ps1:5-6`; `tools/Tests/grep.Tests.ps1:5-6` (file headers)
- **Note:** An artifact of the script-tool loading design, harvested only so a reconciler is not puzzled by the layout. Carries no vault behavior.
