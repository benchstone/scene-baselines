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
- **Proves itself on your history.** Replay past commits and read what it would have caught at
  the time — before you record a single baseline. [How](#see-what-it-would-have-caught).

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

## See what it would have caught

A tool like this normally asks you to take its word for it: record a baseline, wait, and maybe one
day it catches something. Replay answers now, out of your own repository. It walks back through
your history, records each commit's parent as known-good, checks the commit against it, and reports
what it would have told you at the time.

```bash
Tools~/replay-history.sh --project <path to a clone> --unity <path/to/Unity> \
      --commits 10 --scene Assets/Scenes/Level_01.unity --out ~/replay
```

> **Point it at a throwaway clone, not the repository you work in.** A replay checks commits out
> one at a time, because a scene can only be captured by an editor that has it open. It refuses
> to start when a tracked file has uncommitted changes, when a scene is unsaved, or in Play Mode;
> it puts your branch back when it finishes or is interrupted; and it writes its baselines,
> report and state outside the project entirely. A clone makes all of that moot, and it also
> settles the one thing a checkout cannot: untracked files stay put through every checkout, so
> they are present at commits that predate them. `git clone <your repo> ../replay-clone` costs a
> minute.

On Windows, the same run in PowerShell:

```powershell
.\replay-history.ps1 -Project <path to a clone> -Unity "<path\to\Unity.exe>" `
      -Commits 10 -Scene Assets/Scenes/Level_01.unity -Out $HOME\replay
```

Both scripts ship with the package — `Tools~/` in a clone of this repository, or
`Library/PackageCache/com.benchstone.scenebaselines@*/Tools~/` when it was installed through the
Package Manager. They do the same job and take the same options.

A step prints what the editor is doing while it is doing it, so a slow run looks slow rather than
stuck, and the first finding reaches you without waiting for the last commit.

**What it costs.** About a minute per commit, and the first launch also imports the project, which
on a large one takes considerably longer than that. `--commits 10` counts commits *worth
replaying*: a commit that changed nothing your scenes depend on cannot have changed how they look,
so it is never opened. On a real project that is most of them — `--scan` bounds how far back to
look, and `--all-commits` replays every one regardless.

**What comes out** is one markdown file, newest commit first:

```
## `f90779a7` fix: Moving utp to component instead of child GameObject (#752)

Compared against its parent `1a267a99` — **91 finding(s)**.

Scope: 2 file(s) changed, 2 of them file(s) these scene(s) depend on — 45 object(s) and
2 asset(s) differ.
```

The `Scope:` line is there because "45 object(s) differ" means nothing on its own. A three-hundred
file refactor that moves a lot of scene is ordinary; a two-file commit that does is the shape of a
change nobody knew they were making. It is context and not a score — findings are never reordered,
and nothing is ever hidden.

**Two things to know before you read one.** A quiet report is the correct answer, not a failure:
most commits change nothing about how a scene looks. And a finding is a real difference, not
necessarily a mistake — most changes in any history were intended, and no tool can know which. What
it can do is surface the ones that landed where nobody was looking. On a real open-source game it
reported a regression that arrived through a prefab, in a scene whose own diff was empty.

Project-wide settings — tags, layers, physics, quality — are not compared during a replay. Unity
reads them once per session instead of importing them like assets, so after a checkout the editor's
view of them can still belong to a different commit. Every report says so, and says how many
findings it set aside. An ordinary check compares them as usual.

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

Twenty-nine groups, and all twenty-nine run — in the Test Runner and in CI alike. Four of them
need a saved scene to work on. Rather than skip, they use the scene you already have open, and
otherwise create a temporary one and put your scene setup back afterwards. If anything in the editor
has unsaved changes they refuse to run at all, rather than risk discarding it.

The menu entry **Scene Baselines ▸ Tests ▸ Property Capture (free)** runs the same twenty-nine
against whatever scene you have open.

### What CI checks

**Structure and behaviour, both.** On every push and pull request GitHub Actions checks that every
`.cs` and folder has a `.meta`, that no `.meta` is orphaned, that `package.json` agrees with the
changelog, and that `Editor/` makes no network calls — that last one guards a claim this README makes.

**Then it runs all twenty-nine EditMode tests on real editors**, headless on Linux, against minimal
projects under `CI~/` that install this package by relative path — once on **Unity 6** and once on
**2021.3 LTS**, the floor this package claims support for. A green badge means the structure checks
and both runs of the twenty-nine tests passed, on hardware that is not the author's. CI also
asserts that each run actually contained twenty-nine tests, because a run that discovers none would
otherwise pass while proving nothing.

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
