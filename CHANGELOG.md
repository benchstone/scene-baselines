# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.0] — 2026-09-09

### Added

- **Each replayed commit says how far it reached.** One line under the commit's heading, before
  its findings:

  ```
  Scope: 2 file(s) changed, 1 of them file(s) these scene(s) depend on — 45 object(s) and
  2 asset(s) differ.
  ```

  It is the one thing a reader cannot get from the findings themselves. "45 object(s) differ"
  means nothing until it sits beside "2 file(s) changed": a large refactor that moves a lot of
  scene is ordinary, and a two-file commit that moves a lot of scene is the shape of a change
  nobody knew they were making — which is exactly how the regression this package found on
  a real open-source game arrived, through a prefab, where the scene's own diff is empty.

  Objects are counted once however many findings they carry, because the number answers "how far
  did this reach", not "how much is there to read" — that is what grouping is for. A count git
  cannot supply is left unsaid rather than printed as a zero: a merge lists no files of its own.

  **Context, not a score, and deliberately so.** Measured over fifteen replayed commits of a real
  project, findings per file changed ranks the one known true regression ninth of fifteen, and
  "findings in a scene whose own file the commit never touched" holds for 59 of 85 scene sections
  — in a prefab-heavy project that is the normal case, not an alarm. So the numbers are
  printed and the reader judges. Nothing is reordered, and nothing is ever dropped.

## [0.4.0] — 2026-09-06

### Added

- **Replay a project's own history**, newest commit first, streaming what this tool would
  have caught at each one. A healthy project reports nothing, which is the right answer
  and a useless demonstration — nobody can tell "it works and there is nothing wrong"
  from "it does not work". A repository's past answers that from the project's own
  evidence: on a real open-source game it reported a regression that entered through a
  prefab, where the scene's own diff shows nothing at all.

  ```bash
  Tools~/replay-history.sh --project <path> --unity <path/to/Unity> \
        --commits 10 --scene Assets/Scenes/Level.unity --out <dir outside the project>
  ```

  About a minute per commit, so the first finding arrives in one or two — measured at
  48-75 seconds per commit over a repository of this size. The editor is relaunched for
  each step, because checking a commit out underneath a live editor crashes Unity: a
  domain reload swaps assemblies, and objects using `[SerializeReference]` cannot be read
  back by assemblies that did not write them. The driver script exists to do the one
  thing an editor cannot do for itself — check out a commit while no editor is running.
  Every decision stays in `SceneBaselines.HistoryReplay`, where it is tested.

  **A replay never writes into the repository it inspects.** The report, the baselines
  and the run's own state file all live outside it. It refuses to start in Play Mode,
  with unsaved scene changes, or with uncommitted changes to tracked files, and it puts
  the branch back on every exit path, including a failure. The four files Unity rewrites
  simply by opening a project are allowed through and restored afterwards, because a
  strict rule about them would mean the feature never runs at all.

  It stops rather than guesses. Where a commit no longer compiles — an old dependency
  that no longer resolves, most often — the report says it cannot replay past that
  commit, instead of reporting a project that produced no scene as a mass deletion.

- **A replay opens only the commits that could have changed the scenes.** `--commits 10` counts
  commits worth replaying rather than a distance back through history: a commit that touched
  nothing any of the scenes depends on cannot have changed how they look, so it is skipped without
  launching an editor. Depth was never the thing that made a replay expensive — a commit costs the
  same whether there are twenty behind it or two hundred thousand — but on a real project most
  commits have nothing to do with any given scene, and each one still cost a full editor launch to
  prove it.

  What counts as a dependency is what Unity reports for the scenes: the scene files, and the
  prefabs, materials, ScriptableObjects and scripts they reference, plus the `.meta` file of each.
  Deliberately not "commits that touched a scene file", which would skip exactly the regressions
  this tool exists to catch — the ones that arrive through a prefab, where the scene's own diff is
  empty.

  Each commit is still compared with its own parent, so a finding belongs to one commit and never
  to a range. Two limits, both stated at the top of every report that used the filter: the
  dependency list is read at the commit the run starts from, so a file the scenes no longer
  reference is not noticed; and merge commits are not replayed, because git attributes their
  changes to the commits they merge, which are. `--scan <n>` bounds how far back to look
  (500 commits by default), and `--all-commits` replays every commit regardless.

### Changed

- **A replay does not compare project-wide settings**, and says so twice: once in the report
  header, and once in each commit's section, naming how many findings it set aside. It is
  the only place anything in this package drops a finding — choosing which commits to replay
  decides what gets compared, never what a comparison reports. Unity reads project settings
  once per session rather than importing them like assets, so after a checkout the editor's
  tags, layers and physics can still be a commit nobody asked about: a commit that touched
  only `README.md` reported two tag findings for a tag that exists in no commit being
  replayed. Scene-scope settings still compare, because they live in the scene file, which
  Unity does re-import. An ordinary check is untouched — there the editor's view and the
  working tree are the same thing, which is exactly the assumption a replay breaks.

- Each pair of commits in a replay records into its own baseline folder. Sharing one
  folder let a baseline outlive the commit it described, so a finding was re-reported at
  every commit after the one that caused it.

## [0.3.0] — 2026-09-05

### Changed

- A finding shows at most eight changed properties and then states how many more it
  found. One asset in a real project reported 74 rewritten fields, and a scene whose 33
  findings were otherwise unremarkable took 1,567 of a report's 2,070 lines. Measured
  over a 15-commit replay: 27 of 3,022 findings carry more than eight changes, and those
  27 alone are a third of every change line produced.

- Asset findings that changed the same properties report as one line naming them, which
  object findings already did. One shader edit or re-import lands on every material at
  once, so 43 identical lines could open a report; 632 of 713 asset findings in the
  replay sat in clusters of five or more. A finding's kind is part of what the line
  claims, so a material and a GameObject that changed the same property are never
  reported as one event.

- Objects with the same name deleted from the same parent report as one line. Each is
  the topmost missing object on its own branch, so the existing subtree rule could not
  reach them: 23 objects named Imp under one parent printed 23 near-identical lines
  while their descendants were correctly rolled up beneath them.

Together these render a report of 1,736 findings in 543 lines instead of 3,878. Every
count and verdict is unchanged: grouping and the cap are presentation, never filtering,
the true total still prints before the groups, and the cap states what it held back.

## [0.2.0] — 2026-09-05

### Fixed

- A material's render queue is recorded as the override the material itself stores, not
  as the value Unity resolves from the shader. The resolved value depends on how far
  shader import has progressed, so it moved between runs over an unchanged project and
  reported changes nobody made — a fifth of all findings in a 15-commit replay over a
  real project. An explicit override is still asserted; a material that inherits its
  queue now records that it inherits it.
- A hierarchy path built under same-named siblings now carries the sibling's `#n` through
  to its children. The suffix used to be appended to the finished path, so the sixteenth
  object named `Imp` was recorded as `Entrance/Imp#15` while its own child read
  `Entrance/Imp/Graphics#15` — hanging off the FIRST `Imp`. Paths stayed unique, so nothing
  failed loudly; the reader was simply sent to the wrong object, for 10.7% of all objects
  in the project this was measured on. Matching is unaffected — objects pair by
  `GlobalObjectId`, and the path is display.

### Changed

- State schema is now **v11**. Only the asset section changed: objects, settings and
  identities in an existing baseline keep comparing exactly as before. Asset records
  written at v10 or earlier are set aside rather than compared, because they hold the
  old resolved number and every material would otherwise report a queue nobody touched.
  The report says the section was not covered and asks for a re-record — it never
  reports uncompared assets as clean.
- Findings are grouped by what caused them before they are printed. A deleted subtree
  reports once, naming how many objects went with it; objects that changed the same set of
  properties report as one line naming that set. Everything else prints exactly as before.
- Grouping is presentation only, and deliberately never filtering: the finding count and
  the verdict are identical either way, every finding is still listed inside its group, and
  the report states the full count before the groups so a short report cannot be mistaken
  for a quiet one. Measured on a 15-commit replay of a real project, where one scene
  produced 936 true findings nobody could read — the same scene now renders as 45 groups.

## [0.1.0] — 2026-08-13

First public release.

### Added

- Record a baseline for the open scene, and check the scene against it — no AI, no
  network calls, no account.
- Capture covers every object in the active scene including inactive ones, component
  properties as the Inspector shows them, child and root order, the contents of
  referenced Materials, ScriptableObjects and PhysicsMaterials, and 11 groups of scene
  and project settings.
- Objects are identified by Unity's `GlobalObjectId`, so a rename or a re-parent reports
  as **moved** rather than as a deletion plus an addition.
- **Review Findings** window: judge each difference on its own, select the object in the
  Hierarchy, and accept the ones that were intentional.
- CI entry point `SceneBaselines.RegressionReport.RunBatch`, writing a markdown and a
  JSON report and exiting **0** (holds) / **1** (regressions) / **2** (nothing checked).

### Known limitations

Stated openly rather than discovered later:

- Textures, meshes, audio, animation clips and animator controllers are counted and
  reported as *referenced but not content-checked*.
- NavMesh data, occlusion data and Quality settings are not captured. Quality settings
  vary per machine and would report differences nobody made.
- References are matched by name and type, so swapping in a different asset of the same
  name is invisible.
- Floats are recorded at 3 decimal places, nesting depth is 2, and 24 properties per
  component are recorded. Truncation past that cap is announced in the record, never
  silent.

### Baseline format

The captured state string is the comparison key, so a change to its format makes older
baselines uncomparable — the check reports them rather than guessing. This release is
state schema **v10**, report artifact schema **v5**. A versioning policy for future
format changes is being written and will land before 1.0.
