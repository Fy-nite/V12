# Contributing to V12

Thanks for wanting to help. This file covers the two things every contribution
needs to get right: the license grant, and the workflow.

For day-to-day build details, repo boundaries, and component conventions, read
[`Docs/WORKING_ON_V12.md`](Docs/WORKING_ON_V12.md). For the architecture split
between the engine and the renderers, read
[`Docs/ARCHITECTURE.md`](Docs/ARCHITECTURE.md). This file does not repeat them.

## Licensing of contributions

V12 is AGPL-3.0 with a [Commercial Exception](COMMERCIAL-EXCEPTION.md). The
exception is what lets Finite sell a closed-source license to studios that need
one, and Finite can only sell it for code it is allowed to relicense.

By submitting a contribution to any V12 project, you agree to Section 14 of the
Commercial Exception. In short:

- you grant Finite a perpetual, worldwide, royalty-free, irrevocable license to
  reproduce, modify, distribute, sublicense, and **relicense** your contribution
  under any terms, including proprietary and commercial terms;
- you confirm you have the right to grant that, and that the contribution is
  your own work or is compatible with the AGPL;
- you keep copyright in your contribution and may use it however you like
  elsewhere.

If you cannot grant that, do not submit the contribution. Open an issue
describing the change instead, and someone else can implement it.

Contributions to V12 itself are also accepted under the AGPL-3.0. The
relicensing grant above is additional and does not take anything away from other
users. `Contract` is MIT licensed and has its own contributing guide in
`Contract/CONTRIBUTING.md`.

## Clone

Clone the super-repo with submodules:

```powershell
git clone --recurse-submodules https://github.com/Fy-nite/V12.git
```

If you already have a checkout and the submodules are stale:

```powershell
git submodule update --init --recursive
```

## When your change belongs in a submodule

`V12-Engine`, `V12.Basic`, `V12.Monogame`, `Nova`, `V12.Pak`, `V12-Sdk`,
`V12.Paper`, `V12.Editor`, and `Contract` are separate repositories.

If you change a file in one of them, make the change **in that repository**, commit
it there, then bump the gitlink in the super-repo and commit that separately. A
super-repo commit that mixes a gitlink bump with unrelated changes is hard to
review, so keep them apart.

## Build and check

```powershell
dotnet build V12-Monorepo.slnx
dotnet test V12.Architecture.Tests/V12.Architecture.Tests.csproj
```

The architecture tests protect the engine/renderer boundary. If one fails, fix
the reference or add a contract to the engine. Do not add an exception to the
test without a documented reason.

Renderer changes: read [`Docs/RendererContract.md`](Docs/RendererContract.md)
before touching `Nova` or `V12.Monogame`.

## Changelog

Every change that a user of V12 can notice gets an entry in
[`Docs/CHANGELOG.md`](Docs/CHANGELOG.md), in the same pull request:

- add it under `## [Unreleased]`, never under a versioned heading;
- pick the matching subsection: `Added`, `Changed`, `Deprecated`, `Removed`,
  `Fixed`, `Security`;
- newest entry at the top of its subsection;
- one terse, specific entry per user-visible change, written in the present
  tense ("Add support for ...", "Fix ...", "Remove ...");
- skip dependency bumps, whitespace, and refactors that change nothing
  user-visible.

If you bundle several related changes, group them under one entry in the most
relevant subsection rather than spraying fragments.

## Commit messages

Write what changed and why. A commit message is read months later by someone
who was not in your head, so a vague one ("fix stuff", "updates") is worse than
no commit at all.

Conventional prefixes are the house style: `feat:`, `fix:`, `chore:`, `docs:`,
`refactor:`, `test:`. Then a specific summary:

```text
feat(physics): add continuous kinematic motion for moving platforms
```

```text
docs(licensing): rewrite the commercial exception with tier and revenue rules
```

If a change needs a paragraph of explanation, put it in the body, not the
subject. Reference the issue number when there is one.

## Pull requests

A good pull request:

- does one thing, and says which thing in the title;
- passes the build and the architecture tests;
- carries its changelog entry;
- explains anything that is not obvious from the diff, especially a change to
  the engine/renderer boundary or to a serialized format;
- does not reformat files it did not otherwise change.

## Reporting bugs

Open an issue at <https://github.com/Fy-nite/V12/issues>. Include:

- what you ran and with which flags (for desktop samples, `--xr-mode off`);
- the renderer or backend (`NovaV2`, `V12.Monogame`);
- what you expected, what happened, and the full error or log;
- the smallest `.v12pak` or project that reproduces it, when you can share one.

Please read the relevant document under `Docs/` before filing. A bug report that
shows the expected behavior from the docs is much easier to act on than one
that does not, and it is usually faster for you than waiting for an answer.

## Questions and licensing

- Usage questions: open an issue.
- Commercial licenses: see [`COMMERCIAL-EXCEPTION.md`](COMMERCIAL-EXCEPTION.md),
  or write to <https://finite.ovh>.
