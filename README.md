# Scene Baselines

[![CI](https://github.com/benchstone/scene-baselines/actions/workflows/ci.yml/badge.svg)](https://github.com/benchstone/scene-baselines/actions/workflows/ci.yml)

Record what a Unity scene looks like when it is known-good. Find out when it stops matching.

Nobody writes a rule saying *"the camera should be at z = -10"*. So when it moves, no test
fails — someone notices in a build, days later, and then spends hours finding out what changed.
Scene Baselines closes that gap without asking you to write rules: it records the whole scene,
and tells you precisely what differs from the last state you agreed was correct.

- **Deterministic.** A string comparison against a committed file. No AI, no network calls, no
  account. The same check on someone else's machine gives the same answer.
- **Runs in CI.** One command, a meaningful exit code, and a markdown report.
- **Reads like objects, not YAML.** `Player/Sprite → BoxCollider2D.size` instead of four thousand
  lines of scene diff full of file IDs.

## Install

Unity ▸ Window ▸ Package Manager ▸ **+** ▸ *Add package from git URL…*

```
https://github.com/benchstone/scene-baselines.git
```

Requires Unity **2021.3** or newer. Changes per version are listed in
[CHANGELOG.md](CHANGELOG.md).

## The loop

| Step | Where |
|---|---|
| Record the scene as known-good | `Scene Baselines ▸ Record Baseline for Open Scene` |
| See what differs now | `Scene Baselines ▸ Check Regressions` |
| Judge each difference, one by one | `Scene Baselines ▸ Review Findings` |
| Write a report for CI or a PR | `Scene Baselines ▸ Write Regression Report` |

In **Review Findings**, each difference has a `Select` button that highlights the object in the
Hierarchy, and a checkbox. `Accept checked as intentional` rewrites those records as the new
known-good.

There is deliberately **no Reject button**: walking away already rejects, and the check stays red
until the scene actually matches.

## What it covers

- Every object in the active scene, **including inactive ones**, with component properties as the
  Inspector shows them — public fields, `[SerializeField]` privates, references, child order
- Contents of referenced Materials, ScriptableObjects and PhysicsMaterials
- Scene and project settings: physics, time, tags and layers, render, lighting, input, build list
- Objects are matched by Unity's `GlobalObjectId`, so renaming or re-parenting reports as **moved**,
  not as deleted

**Not covered, deliberately:** contents of textures, meshes, audio and animation clips (they are
counted and reported as *referenced but not content-checked*), NavMesh and occlusion data, and
Quality settings, which vary per machine and would report differences nobody made.

## CI

```bash
Unity -batchmode -projectPath . \
      -executeMethod SceneBaselines.RegressionReport.RunBatch \
      -baselineScene "Assets/Scenes/Level_01.unity" \
      -logFile ci.log
```

Exit codes: **0** everything holds · **1** regressions · **2** nothing could be checked.

Reports are written to `SceneBaselineReports/` as `regression-report.md` and
`regression-report.json`. Pipe the markdown to `$GITHUB_STEP_SUMMARY` to put it on the pull
request page.

> **Never pass `-quit`.** This method calls `EditorApplication.Exit` itself so it can return a
> meaningful code; `-quit` would race it to a 0.

## Two things worth understanding

**A baseline is a fingerprint, not a backup.** It records enough to prove a BoxCollider went
missing, and nothing that could put it back. That is why nothing here ever modifies your scene —
undoing is the job of the version control you already have. This tool's job is to point at the
damage precisely.

**Baselines belong in version control.** They are written to `Assets/SceneBaselines/` as
pretty-printed JSON so they can be reviewed in a pull request. When someone accepts a change, the
diff shows what they accepted — a second pair of eyes, on the workflow your team already uses.
Read them with `git diff --word-diff`; the state is one long line per object.

## Running the tests

**Window ▸ General ▸ Test Runner ▸ EditMode.** The suite runs against throwaway objects — no
Play Mode, no scene of yours is touched, nothing is written.

Nineteen groups, and all nineteen run — in the Test Runner and in CI alike. Three of them need a
saved scene to work on. Rather than skip, they use the scene you already have open, and otherwise
create a temporary one and put your scene setup back afterwards. If anything in the editor has
unsaved changes they refuse to run at all, rather than risk discarding it.

The menu entry **Scene Baselines ▸ Tests ▸ Property Capture (free)** runs the same nineteen against
whatever scene you have open.

### What CI checks

**Structure and behaviour, both.** On every push and pull request GitHub Actions checks that every
`.cs` and folder has a `.meta`, that no `.meta` is orphaned, that `package.json` agrees with the
changelog, and that `Editor/` makes no network calls — that last one guards a claim this README makes.

**Then it runs all nineteen EditMode tests on a real Unity 6000.5.1f1 editor**, headless on Linux,
against the minimal project in `CI~/TestProject` which installs this package by relative path. A green
badge means the structure checks and the nineteen tests all passed, on hardware that is not the
author's.

One honest caveat: a fork cannot see the repository's Unity credentials, and GitHub does not pass
secrets to workflows from forks. On a fork the Unity job reports a clean skip rather than a red X, so
a badge there covers structure only.

For tests to appear at all, the project's `Packages/manifest.json` needs this package listed as
testable:

```json
"testables": [ "com.benchstone.scenebaselines" ]
```

## Licence

Source-available under the [PolyForm Shield License 1.0.0](LICENSE.md). Free forever for your own
projects, including commercial games. You may not resell it as a competing product or service.
