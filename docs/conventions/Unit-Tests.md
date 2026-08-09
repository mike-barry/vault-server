# Unit Tests

The standard for this repository's tests, in all three test projects. Read it
before adding or changing a test.

## The rule

A test asserts a behaviour a caller can observe, and fails when that behaviour
changes.

That excludes the shape of the code. A test that breaks on a rename, or on a
refactor no caller notices, is a maintenance cost with no guard behind it.

Two questions settle most cases. What would have to change in `src/` for this
test to fail, and would a caller notice that change? If the first answer is
"nothing" or "only this test file", the test guards nothing.

## What a sound assertion looks like

- **State the expected value; do not compute it.** An expectation derived with
  the expression under test moves whenever that expression moves, so nothing
  fails.
- **Confirm the arrangement before asserting an absence.** An assertion that
  something is missing passes just as readily when the setup produced nothing at
  all. A positive assertion first tells the two apart.
- **Assert the whole answer where the arrangement determines it.** Containment
  passes when a result set grows, so it does not carry a claim about what is *not*
  in the set. Use it only where the arrangement genuinely leaves the rest open.
- **Assert what is promised, and no more.** An incidental ordering, a set the code
  may add to, or the contents of a fixture that exists to exercise a mechanism are
  all broken by changes that were always allowed.
- **Drive production code.** If an assertion can be satisfied without executing
  anything under `src/`, the test is about itself. Where a behaviour is genuinely
  unobservable from a test, assert what is observable and say in the class remarks
  that the rest is uncovered.

Be wary of a helper that orders, trims or otherwise reshapes a result on the way
to an assertion. It removes that property from every assertion that uses it. Keep
one where the normalization is the point, say in a comment why, and make it
explicit rather than relying on a default.

## The fixtures

Reach for one of these before building anything.

- **`ScratchVault`** - a vault on a fresh temp directory, seeded with `WithNote`
  and `WithFolder`. Its constructor takes the grant's scope, its op classes and
  the vault's path policy, so a full-authority grant and a narrow one are one
  argument apart. `Unreadable` holds a file open so a write against it fails.
  Dispose it.
- **`TestVaultFixture`** - two temp copies of the committed test vault: one for
  read-only assertions, one for mutation. Use it where a test needs a realistic
  vault, `ScratchVault` where it needs a specific one.
- **`FixedTimeProvider`** - a `TimeProvider` pinned to a settable instant. Any
  test touching expiry uses it rather than the wall clock.
- **`RecordingProblemSink`** - keeps every problem a load records, so a loader
  test can assert what was recorded without a host's health state.
- **`DeclaredToolHarness`** - invokes a tool body with arguments bound as the
  execute path binds them, skipping the session check, the op-class floor and
  vault resolution. Use the execute path for anything about the floor itself.

Assert behaviour against a synthetic declaration the tests own. The committed
declaration and vault are asserted only for what being committed buys: that they
load, and that they stay wired up.

## Costs that make a test not worth writing

Where one applies and the property is covered elsewhere, do not write it: ambient
or process-global state (the wall clock, the working directory, environment
variables, filesystem timestamp granularity); anything requiring the suite to
serialize; a production seam existing only so a test can observe something;
non-determinism, including unguaranteed enumeration order; elevation.

## Widening against adding

**The default is to widen** - another `[InlineData]` row or another assertion in
the existing test, where a reader meets the whole property in one place. Add a
separate test where the arrangement genuinely differs, or where the existing
test's name would stop describing its body: a name that no longer fits is the
signal to split rather than to rename.

`Code-Comments.md` is the comment standard, and governs a test file as much as
`src/`.
