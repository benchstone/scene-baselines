# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
