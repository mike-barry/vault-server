# task-001: The removal census resolves over notes, and a removal destroys files

**Goal:** The removal census indexes every file in the vault rather than only notes, and the direct-matching fallback is gone.

## Context

Carried over from `docs/Sidequests.md`, which was retired. The original entry is reproduced in full under Notes; it states what is wrong now, what it costs while it stays, what the fix touches, and what is less obvious than it looks.

## Acceptance criteria

Not yet written - see Open questions.

## Open questions

- The dot-directory behaviour change is a consequence of the fix rather than part of it. The entry argues it is defensible; is it agreed, or does it need deciding on its own before the fix lands?
- Does removing the fallback change behaviour the specs state, and so require a spec amendment in the same commit?
- The entry notes the fix leaves `WikilinkTarget.Matches` reachable only through `WikilinkUniverse` and simplifies the remarks on both. Is that simplification in scope here or a follow-on?

## Notes

The entry as it stood in `docs/Sidequests.md`, verbatim:

> ## The removal census resolves over notes, and a removal destroys files
>
> Approved: 2026-08-04.
>
> A link resolves against every file in the vault, not every note - the editor ranks an attachment the same way it ranks a note. `WikilinkMaintenance.Census` builds its index from `VaultWidePass.EnumerateNotes`, so no file that is not a note is ever in it and a target naming one resolves to nothing. The census falls back to matching such a target against the removal set directly, and matching is deliberately wider than resolution: it says which files a target *names*, not which one it opens. With `Far/Diagram.png` and `Notes/Diagram.png` both present, a `![[Diagram.png]]` in `Notes/Ref.md` opens the nearer one, but removing the far one reports that link as orphaned. The error runs one way only - the fallback is reached only where nothing resolved - so the census over-reports and never misses.
>
> The cost while it stays is a delete preview naming links a delete would not break. Nothing is rewritten, so no content is at risk, and it takes two same-named attachments in different folders before it says anything wrong at all. It is the last place where the three passes disagree about what a link opens.
>
> The fix removes code rather than adding it. `VaultWidePass` gains an `EnumerateFiles` beside `EnumerateNotes` - the same call without the markdown predicate - and the census indexes that, reading and scanning only the entries that are notes, because links live in notes. The direct-matching fallback then disappears, which also leaves `WikilinkTarget.Matches` reachable only through `WikilinkUniverse` and simplifies the remarks on both. `WikilinkUniverse` itself needs nothing: `StemOf` strips only the note extension, so `Diagram.png` registers once under its own name; `NameMatches` already handles a foreign extension and already refuses `Diagram` for `Diagram.png`; ranking reads paths and knows nothing of extensions. Nor is there cross-talk, since a `Widget.png` never registers the name `Widget`.
>
> Two things are less obvious than they look. Enumeration prunes dot entries, so once the fallback is gone a file under a dot-directory is no longer indexed and a link naming one stops being reported - a behaviour change beyond the fix, defensible because the editor does not index dot-directories either, so such a link resolves to nothing there and was already dead. And the same change must not be made to relocation and creation: their emission counts the notes bearing a basename to choose the bare form over the qualified one, and an extensionless file beside a note of the same name would inflate that count and push emission to qualified. That may be what the editor does, but it is unmeasured, and neither pass can reach a file that is not a note anyway - `ToolPaths.KindOf` classifies every such path as a folder, which `VaultToolArgumentTests` pins.
