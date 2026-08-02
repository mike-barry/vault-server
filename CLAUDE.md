# CLAUDE.md - Vault Access Rewrite Build

This repository is a from-scratch build of the vault access layer, its tool layer, and two front-ends over them, specified entirely by the documents in `spec/`.

**Read `spec/Brief-Vault-Access-Build.md` first.** It is the single entry point: what is being built, which documents specify it, which code is retained and already correct, and which constraints are deliberate. Everything else in `spec/` is reached through it.

## Constraints

- **This repo has no access to the knowledge vault, and must not seek one.** The as-built documentation of the system being replaced is deliberately out of reach. Do not look for it, do not ask for excerpts of it, and do not reconstruct it from memory of another session. A redesign grounded in documentation of the old system inherits the old system's shape, which is the one outcome this build exists to avoid.
- **Most of this repo is retained code and is already correct.** Authentication and authorization, the scope primitives, health, logging, configuration loading, host wiring, and the script-loading framework all carry forward. Read them, call them, and do not reimplement or refactor them. The brief lists what is retained and what was deleted.
- **The deleted implementation is not recoverable, by design.** Git history was restarted. There is nothing to check the specifications against, which is what makes them the specification rather than a summary.
- **Gaps escalate; they are not resolved locally.** On a question the specs cannot answer, stop that thread and raise it with the operator rather than deciding from judgment. Only an agreed escalation is written to `spec/Questions.md`, and the answer returns as an amended spec. Clear the entry once the answer is incorporated into both the specs and the code.
- **Record architectural decisions in `spec/Decisions.md` as they land**, with the alternative rejected and why. Nothing outside this repo observed the build, so an unrecorded decision is one nobody can review.
- **`spec/Plan.md` holds the working plan.** Read it at the start of a session and say which step is in play. It is the build's own plan and is meant to be revised as the work is understood, with changes proposed to the operator rather than made silently.
