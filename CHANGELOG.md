# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- A material's render queue is recorded as the override the material itself stores, not
  as the value Unity resolves from the shader. The resolved value depends on how far
  shader import has progressed, so it moved between runs over an unchanged project and
  reported changes nobody made — a fifth of all findings in a 15-commit replay over a
  real project. An explicit override is still asserted; a material that inherits its
  queue now records that it inherits it.

### Changed

- State schema is now **v11**. Only the asset section changed: objects, settings and
  identities in an existing baseline keep comparing exactly as before. Asset records
  written at v10 or earlier are set aside rather than compared, because they hold the
  old resolved number and every material would otherwise report a queue nobody touched.
  The report says the section was not covered and asks for a re-record — it never
  reports uncompared assets as clean.

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
