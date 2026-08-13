# Scene Baselines

[![CI](https://github.com/Saba123625/scene-baselines/actions/workflows/ci.yml/badge.svg)](https://github.com/Saba123625/scene-baselines/actions/workflows/ci.yml)

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
https://github.com/Saba123625/scene-baselines.git
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

Two of the seventeen groups need a saved scene open, and the Test Runner opens an untitled one
for the duration of a run, so they report as **skipped with the reason**. The menu entry
**Scene Baselines ▸ Tests ▸ Property Capture (free)** runs all seventeen against whatever scene
you have open, and is the one to use before trusting a release.

For tests to appear at all, the project's `Packages/manifest.json` needs this package listed as
testable:

```json
"testables": [ "com.sabashalvashvili.scenebaselines" ]
```

## Licence

Source-available under the [PolyForm Shield License 1.0.0](LICENSE.md). Free forever for your own
projects, including commercial games. You may not resell it as a competing product or service.
