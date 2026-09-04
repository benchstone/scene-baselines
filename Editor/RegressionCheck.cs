using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBaselines
{
    // ─── Regression baselines: checking (step 2 of 4) ────────────────────────────
    //
    // Step 1 records what was known good. This reads those records back and answers the
    // question an agent session cannot answer about itself: "did something that used to
    // work stop working?" The agent remembers what it BELIEVED it did; the baseline
    // records what the editor actually contained, and only the second one can be diffed.
    //
    // Deliberately free of any LLM call. A regression check that asks a model whether
    // things still look right inherits the model's willingness to be agreeable, which is
    // the failure this whole layer exists to remove. This is a string comparison against
    // a committed file: it is cheap, deterministic, and reproducible on someone else's
    // machine — which is also what makes it usable from CI.

    /// <summary>
    /// The outcome of a regression check, as a value rather than as prose.
    /// </summary>
    /// <remarks>
    /// Extracted so that every consumer — the console line, the report artifact, the CI exit
    /// code — reaches the same conclusion from the same rule. A report that decided for itself
    /// whether findings counted as breakage would eventually disagree with the check it claims
    /// to be reporting, and the one thing this layer sells is that its answer can be trusted
    /// without re-running it.
    /// </remarks>
    public enum RegressionVerdict
    {
        /// <summary>Nothing could be compared. Explicitly NOT a pass.</summary>
        NotChecked,

        /// <summary>Baselines were compared and all of them still hold.</summary>
        Pass,

        /// <summary>
        /// Differences exist, but only against baselines captured from an unsaved scene, so they
        /// may be unsaved work reverting rather than anything breaking.
        /// </summary>
        Unconfirmed,

        /// <summary>Differences against a baseline whose state actually reached disk.</summary>
        Regressions
    }

    public enum RegressionKind
    {
        /// <summary>Recorded as known-good, no longer in the scene at all.</summary>
        Missing,

        /// <summary>Still present, but its recorded state no longer matches.</summary>
        Changed,

        /// <summary>
        /// Matched by identity, but it is no longer at the path it was recorded at — renamed,
        /// re-parented, or both.
        /// </summary>
        /// <remarks>
        /// Exists so that this stops being reported as MISSING. Before identities, a re-parented
        /// object was recorded at a path nothing occupied any more, so the report announced a
        /// deletion that had not happened — and a report that invents deletions is one users learn
        /// to disbelieve, which costs far more than the finding was worth.
        ///
        /// Still a finding, not a free pass: moving an object changes the scene, and whether that was
        /// intended is the reader's call, exactly as with a changed value.
        /// </remarks>
        Moved,

        /// <summary>An asset the scene depends on no longer has its recorded contents.</summary>
        /// <remarks>
        /// There is deliberately no AssetMissing counterpart. An asset leaving the baseline means
        /// either that something stopped referencing it or that the object referencing it is gone
        /// — and both are already reported against that object, the first as a changed reference
        /// and the second as MISSING. A second finding for the same edit would double the count
        /// and split one cause across two places in the report.
        /// </remarks>
        AssetChanged,

        /// <summary>A scene or project setting no longer matches what was recorded.</summary>
        SettingsChanged,

        /// <summary>
        /// An object that is not in the baseline and looks like it arrived by accident.
        /// </summary>
        /// <remarks>
        /// Additions are NOT regressions as a rule — a team building content adds objects every day,
        /// and a check that goes red on all of them gets switched off, after which it catches
        /// nothing. But that argument is about severity, and for a long time it was applied to
        /// visibility too: additions were kept as a bare COUNT, so a report could say "1 object
        /// added" without saying whether it was a new spawner or a duplicated Player from a stray
        /// Ctrl+D. Naming them costs nothing and was simply missing.
        ///
        /// This kind is the narrow exception that DOES speak: additions which are almost never
        /// deliberate — an exact duplicate of a baselined object, an object named like a copy of
        /// one, or a second instance of a component Unity only honours once. Ordinary new work
        /// stays silent. Kept narrow on purpose: every rule added here is a rule that can cry wolf,
        /// and a check nobody trusts is worth less than no check.
        /// </remarks>
        Added
    }

    public class RegressionFinding
    {
        public string path;

        /// <summary>
        /// Where the object is NOW, when that differs from <see cref="path"/>. Null otherwise.
        /// </summary>
        /// <remarks>
        /// A move is only legible as a pair: "Enemies/Grunt is now Pool/Grunt" tells the reader what
        /// happened, while either path alone reads as a different object.
        /// </remarks>
        public string livePath;

        /// <summary>
        /// The live object's identity, when it has one. Empty for MISSING, for assets and settings,
        /// and for objects with no usable id.
        /// </summary>
        /// <remarks>
        /// Carried so a reviewer can be taken straight to the object. Resolving a hierarchy path back
        /// to a GameObject is guesswork once names repeat; an id resolves exactly, which is the
        /// difference between "one click" and "one click that sometimes selects the wrong twin".
        /// </remarks>
        public string liveId;

        public RegressionKind kind;
        public string baselineState;
        public string liveState;

        /// <summary>The differing parts only — a full state string is unreadable in a report.</summary>
        public List<string> changedSegments = new List<string>();
    }

    public class BaselineComparison
    {
        public Baseline baseline;
        public List<RegressionFinding> findings = new List<RegressionFinding>();

        /// <summary>Objects the baseline recorded, i.e. how much this comparison covers.</summary>
        public int recordedObjectCount;

        /// <summary>Assets the baseline recorded by contents. Zero when it predates asset coverage.</summary>
        public int recordedAssetCount;

        /// <summary>Settings groups the baseline recorded. Zero when it predates settings coverage.</summary>
        public int recordedSettingsCount;

        /// <summary>
        /// Objects present now but not in the baseline. NOT regressions — building new things
        /// is the normal case, and counting them as breakage would make every request after a
        /// baseline look like damage. Reported only so the numbers are explicable.
        /// </summary>
        public int newObjectCount;

        /// <summary>
        /// The paths of those objects. Named rather than counted, so "1 object added" can be read
        /// as either "my new spawner" or "a duplicate of Player" without opening the scene.
        /// </summary>
        public List<string> newObjectPaths = new List<string>();

        /// <summary>
        /// Why this baseline could not be compared at all, or null when it was compared.
        /// </summary>
        /// <remarks>
        /// Distinct from "compared and found nothing". A baseline that could not be read against
        /// the current state format has zero findings, and treating that as clean would let an
        /// unreadable history report a pass — the same lie as reporting a pass with no baselines.
        /// </remarks>
        public string incomparableReason;

        /// <summary>
        /// Whether findings here may be called regressions. Resolved when the comparison is built,
        /// because it depends on the scene file on disk and not on the baseline alone.
        /// </summary>
        public bool stateReachedDisk;

        /// <summary>Why not, or null when this baseline can prove breakage.</summary>
        public string untrustworthyReason;

        public bool IsComparable => incomparableReason == null;

        /// <summary>Compared, and everything still matches.</summary>
        public bool IsClean => IsComparable && findings.Count == 0;

        /// <summary>Compared, and something differs.</summary>
        public bool IsBroken => IsComparable && findings.Count > 0;
    }

    public class RegressionRunResult
    {
        public string sceneName;
        public string scenePath;
        public bool sceneWasUnsaved;
        public bool sceneHadUnsavedChanges;
        public List<BaselineComparison> comparisons = new List<BaselineComparison>();

        /// <summary>
        /// True only when at least one baseline was actually compared and none of them broke.
        /// Having no baselines — or only unreadable ones — is NOT clean; see Describe().
        /// </summary>
        public bool HasBaselines => comparisons.Any(c => c.IsComparable);

        public int TotalFindings => comparisons.Sum(c => c.findings.Count);

        public bool IsClean => HasBaselines && TotalFindings == 0;
    }

    public static class RegressionCheck
    {
        [MenuItem("Scene Baselines/Check Regressions")]
        public static void CheckActiveSceneMenu()
        {
            RegressionRunResult result = RunForActiveScene();
            string report = Describe(result);

            // Logged rather than shown in a dialog so it can be copied, searched, and read by
            // anything tailing the editor log.
            if (result.TotalFindings > 0)
                Debug.LogWarning(report);
            else
                Debug.Log(report);
        }

        // ── Running ──────────────────────────────────────────────────────────────

        public static RegressionRunResult RunForActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();

            var result = new RegressionRunResult
            {
                sceneName = scene.name,
                scenePath = scene.path,
                sceneWasUnsaved = string.IsNullOrEmpty(scene.path),
                sceneHadUnsavedChanges = scene.isDirty
            };

            // A scene that has never been saved has no identity to look baselines up by.
            if (result.sceneWasUnsaved)
                return result;

            List<Baseline> baselines = BaselineStore.LoadForScene(scene.path);
            if (baselines.Count == 0)
                return result;

            List<BaselineObjectRecord> live = SceneCapture.CaptureBaselineObjects();

            // Captured once for every baseline: the sweep walks the whole dependency graph, and
            // repeating it per baseline would multiply the cost of the one part of a check that
            // is not a string comparison.
            List<BaselineAssetRecord> liveAssets = SceneCapture.CaptureReferencedAssets().assets
                .Select(a => new BaselineAssetRecord { path = a.path, type = a.type, state = a.state })
                .ToList();

            List<BaselineSettingsRecord> liveSettings = SceneCapture.CaptureSettings()
                .Select(s => new BaselineSettingsRecord { scope = s.scope, group = s.group, state = s.state })
                .ToList();

            // Resolved per baseline, here rather than inside Compare, so the comparison itself stays
            // free of IO and can be exercised with hand-built inputs.
            foreach (Baseline baseline in baselines)
            {
                result.comparisons.Add(Compare(baseline, live,
                    BaselineStore.ScenePersistedAfterCapture(baseline), liveAssets, liveSettings));
            }

            return result;
        }

        /// <summary>
        /// Compares one baseline against live scene state. Pure: no scene access, no IO — so it
        /// can be exercised directly with hand-built inputs.
        /// </summary>
        public static BaselineComparison Compare(Baseline baseline,
            List<BaselineObjectRecord> live, bool scenePersistedAfterCapture = false,
            List<BaselineAssetRecord> liveAssets = null,
            List<BaselineSettingsRecord> liveSettings = null)
        {
            var comparison = new BaselineComparison
            {
                baseline = baseline,
                stateReachedDisk = baseline != null && baseline.StateReachedDisk(scenePersistedAfterCapture),
                untrustworthyReason = baseline?.UntrustworthyReason(scenePersistedAfterCapture)
            };

            if (baseline?.objects == null)
            {
                comparison.incomparableReason = "the baseline records no objects";
                return comparison;
            }

            // Refuse rather than compare across a state-format change. Every object would mismatch
            // at once and the run would report a scene-wide regression that never happened.
            if (!baseline.StateFormatComparable)
            {
                comparison.recordedObjectCount = baseline.objects.Count;
                comparison.incomparableReason =
                    $"it was recorded in an older state format (schema v{baseline.schemaVersion}, " +
                    $"this tool compares v{BaselineStore.StateFormatSchemaVersion}+), which " +
                    "recorded a different set of objects, or less about each one, than is checked now. " +
                    "Comparing across the change would report objects as broken that nobody touched, " +
                    "so re-record this scene to replace it";
                return comparison;
            }

            // Indexed both ways: by identity, which survives a rename or a re-parent, and by path,
            // which is all a pre-v10 baseline has. Whichever key matched decides what can be claimed
            // about the object, so the two are kept apart rather than merged.
            var liveById = new Dictionary<string, BaselineObjectRecord>();
            var liveByPath = new Dictionary<string, BaselineObjectRecord>();

            if (live != null)
            {
                foreach (BaselineObjectRecord record in live)
                {
                    if (!string.IsNullOrEmpty(record.id))
                        liveById[record.id] = record;

                    liveByPath[record.path ?? ""] = record;
                }
            }

            comparison.recordedObjectCount = baseline.objects.Count;

            // Reference identity, so one live object can satisfy exactly one recorded object. Without
            // it, an object renamed away from a path that a NEW object then occupies would be matched
            // twice — reported as unchanged while the rename went unmentioned.
            var claimed = new HashSet<BaselineObjectRecord>();

            foreach (BaselineObjectRecord recorded in baseline.objects)
            {
                string path = recorded.path ?? "";
                string recordedState = recorded.state ?? "";

                // Identity wins over path deliberately. If an object was renamed AND something new
                // took its old path, the path lookup would pair the record with the newcomer and
                // report the rename as "unchanged" plus a bogus change — the id pairs it correctly.
                BaselineObjectRecord match = null;
                bool matchedById = false;

                if (!string.IsNullOrEmpty(recorded.id) &&
                    liveById.TryGetValue(recorded.id, out BaselineObjectRecord byId) &&
                    !claimed.Contains(byId))
                {
                    match = byId;
                    matchedById = true;
                }
                else if (liveByPath.TryGetValue(path, out BaselineObjectRecord byPath) &&
                         !claimed.Contains(byPath))
                {
                    match = byPath;
                }

                if (match == null)
                {
                    comparison.findings.Add(new RegressionFinding
                    {
                        path = path,
                        kind = RegressionKind.Missing,
                        baselineState = recordedState,
                        liveState = ""
                    });
                    continue;
                }

                claimed.Add(match);

                string liveState = match.state ?? "";
                string livePath = match.path ?? "";
                bool moved = matchedById && !string.Equals(path, livePath, StringComparison.Ordinal);
                bool changed = !string.Equals(recordedState, liveState, StringComparison.Ordinal);

                // A parent whose child LIST only grew is not a regression. Adding an object is
                // normal work and is deliberately not breakage, but the new child also rewrites its
                // parent's recorded order — so without this, the rule "additions are not
                // regressions" quietly stopped applying the moment the added object had a parent,
                // and routine content work reported its own parent as damage.
                if (changed && OnlyOrderPreservingAdditions(recordedState, liveState))
                    changed = false;

                // Reported as ONE finding, not as a move plus a change. Renaming or re-parenting an
                // object also rewrites its state (world bounds, its parent's child order), so
                // splitting them would print two findings for one edit and double the count.
                if (moved)
                {
                    comparison.findings.Add(new RegressionFinding
                    {
                        path = path,
                        livePath = livePath,
                        liveId = match.id ?? "",
                        kind = RegressionKind.Moved,
                        baselineState = recordedState,
                        liveState = liveState,
                        changedSegments = changed
                            ? DescribeStateDifferences(recordedState, liveState)
                            : new List<string>()
                    });
                }
                else if (changed)
                {
                    comparison.findings.Add(new RegressionFinding
                    {
                        path = path,
                        livePath = livePath,
                        liveId = match.id ?? "",
                        kind = RegressionKind.Changed,
                        baselineState = recordedState,
                        liveState = liveState,
                        changedSegments = DescribeStateDifferences(recordedState, liveState)
                    });
                }
            }

            // Counted by what went unclaimed rather than by unrecorded paths: a renamed object now
            // occupies a path the baseline never held, and calling that "new" while also reporting it
            // as moved would describe one object twice.
            List<BaselineObjectRecord> added = live == null
                ? new List<BaselineObjectRecord>()
                : live.Where(r => !claimed.Contains(r)).ToList();

            comparison.newObjectCount = added.Count;
            comparison.newObjectPaths = added.Select(r => r.path ?? "").ToList();

            AddSuspiciousAdditions(comparison, baseline, added, live);

            CompareAssets(comparison, baseline, liveAssets);
            CompareSettings(comparison, baseline, liveSettings);

            return comparison;
        }

        /// <summary>
        /// Adds findings for scene or project settings that no longer match.
        /// </summary>
        /// <remarks>
        /// Groups present only in the baseline are skipped rather than reported: that means this
        /// tool stopped recording a group, which is a change to the tool and not to the project,
        /// and blaming the user's scene for it would be a false regression on every check.
        /// </remarks>
        private static void CompareSettings(BaselineComparison comparison, Baseline baseline,
            List<BaselineSettingsRecord> liveSettings)
        {
            if (!baseline.RecordsSettings || baseline.settings == null || liveSettings == null)
                return;

            comparison.recordedSettingsCount = baseline.settings.Count;

            var liveMap = new Dictionary<string, string>();
            foreach (BaselineSettingsRecord setting in liveSettings)
                liveMap[SettingsKey(setting.scope, setting.group)] = setting.state ?? "";

            foreach (BaselineSettingsRecord recorded in baseline.settings)
            {
                string key = SettingsKey(recorded.scope, recorded.group);

                if (!liveMap.TryGetValue(key, out string liveState))
                    continue;

                string recordedState = recorded.state ?? "";
                if (string.Equals(recordedState, liveState, StringComparison.Ordinal))
                    continue;

                // Root order is a list like a parent's children, so it gets the same rule: adding a
                // top-level object grows the list and must stay silent, while reordering the objects
                // the baseline already knew about still speaks.
                if (OnlyOrderPreservingAdditions(recordedState, liveState))
                    continue;

                comparison.findings.Add(new RegressionFinding
                {
                    path = key,
                    kind = RegressionKind.SettingsChanged,
                    baselineState = recordedState,
                    liveState = liveState,
                    changedSegments = DescribeStateDifferences(recordedState, liveState)
                });
            }
        }

        // ── Additions that do not look deliberate ────────────────────────────────
        //
        // Components Unity honours exactly once per scene. A second one does not error — it
        // changes behaviour quietly, which is the failure mode this whole layer is for: two
        // AudioListeners make audio go wrong in a way nobody traces back to a scene edit, and a
        // second EventSystem stops UI input responding at all.
        private static readonly string[] SingletonComponents = { "AudioListener", "EventSystem" };

        /// <summary>
        /// Adds findings for new objects that look accidental rather than built on purpose.
        /// </summary>
        /// <remarks>
        /// Every rule here has to earn its place, because a false accusation on an object someone
        /// deliberately made is worse than silence: it teaches the reader that ADDED lines are noise,
        /// and the next one they skip is the real duplicate. So each rule needs evidence a human
        /// would accept — identical in every recorded value, Unity's own copy naming, or a component
        /// the engine only honours once.
        /// </remarks>
        private static void AddSuspiciousAdditions(BaselineComparison comparison, Baseline baseline,
            List<BaselineObjectRecord> added, List<BaselineObjectRecord> live)
        {
            if (added.Count == 0 || baseline.objects == null)
                return;

            var recordedByState = new Dictionary<string, string>();
            var recordedLeaves = new Dictionary<string, string>();

            foreach (BaselineObjectRecord recorded in baseline.objects)
            {
                string state = recorded.state ?? "";
                if (!string.IsNullOrEmpty(state) && !recordedByState.ContainsKey(state))
                    recordedByState[state] = recorded.path ?? "";

                string leaf = LeafName(recorded.path);
                if (!string.IsNullOrEmpty(leaf) && !recordedLeaves.ContainsKey(leaf))
                    recordedLeaves[leaf] = recorded.path ?? "";
            }

            foreach (BaselineObjectRecord newcomer in added)
            {
                string reason = SuspiciousAdditionReason(newcomer, recordedByState, recordedLeaves, live);

                if (reason == null)
                    continue;

                comparison.findings.Add(new RegressionFinding
                {
                    path = newcomer.path ?? "",
                    livePath = newcomer.path ?? "",
                    liveId = newcomer.id ?? "",
                    kind = RegressionKind.Added,

                    // No recorded side exists: the baseline has never seen this object. Empty rather
                    // than absent so the review window's "was/now" fallback cannot print half a pair.
                    baselineState = "",
                    liveState = newcomer.state ?? "",
                    changedSegments = new List<string> { reason }
                });
            }
        }

        private static string SuspiciousAdditionReason(BaselineObjectRecord newcomer,
            Dictionary<string, string> recordedByState, Dictionary<string, string> recordedLeaves,
            List<BaselineObjectRecord> live)
        {
            string state = newcomer.state ?? "";

            // Identical in every value the tool records, including world position. Two objects that
            // agree that exactly are a copy, not a coincidence.
            if (!string.IsNullOrEmpty(state) && recordedByState.TryGetValue(state, out string twin))
                return $"identical in every recorded value to {twin} — a copy, not new work";

            // Unity's own naming for a duplicate. Catches the copy that was made and then dragged
            // somewhere else, which the state rule above can no longer see.
            string copiedFrom = CopySourceName(LeafName(newcomer.path));

            if (copiedFrom != null && recordedLeaves.TryGetValue(copiedFrom, out string original))
                return $"named like a copy of {original}";

            foreach (string singleton in SingletonComponents)
            {
                if (!HasComponent(state, singleton))
                    continue;

                int total = live == null ? 1 : live.Count(r => HasComponent(r.state ?? "", singleton));

                if (total > 1)
                    return $"adds a second {singleton} — the scene now has {total}, and Unity honours one";
            }

            return null;
        }

        /// <summary>"Enemies/Grunt#2" → "Grunt". The sibling suffix is display, not part of the name.</summary>
        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;

            int hash = leaf.LastIndexOf('#');
            return hash > 0 ? leaf.Substring(0, hash) : leaf;
        }

        /// <summary>"Player (1)" → "Player". Null when the name is not shaped like a Unity copy.</summary>
        private static string CopySourceName(string leaf)
        {
            if (string.IsNullOrEmpty(leaf) || !leaf.EndsWith(")", StringComparison.Ordinal))
                return null;

            int open = leaf.LastIndexOf(" (", StringComparison.Ordinal);
            if (open <= 0)
                return null;

            string inside = leaf.Substring(open + 2, leaf.Length - open - 3);

            if (inside.Length == 0 || !inside.All(char.IsDigit))
                return null;

            return leaf.Substring(0, open);
        }

        /// <summary>Whether a recorded state lists a component type, read from its own text.</summary>
        /// <remarks>
        /// Parsed out of the state string rather than looked up in the scene so that Compare stays
        /// pure — no scene access, no IO — which is what lets the free suite exercise these rules
        /// with hand-built inputs instead of by building a scene.
        /// </remarks>
        private static bool HasComponent(string state, string type)
        {
            const string marker = "components=[";

            if (string.IsNullOrEmpty(state))
                return false;

            int open = state.IndexOf(marker, StringComparison.Ordinal);
            if (open < 0)
                return false;

            int start = open + marker.Length;
            int close = state.IndexOf(']', start);
            if (close < 0)
                return false;

            foreach (string token in state.Substring(start, close - start).Split(','))
            {
                if (string.Equals(token.Trim(), type, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // ── Ordered lists ────────────────────────────────────────────────────────

        /// <summary>Segments whose value is an ordered list of names rather than a value.</summary>
        private static readonly string[] OrderListKeys = { "children", "roots" };

        /// <summary>
        /// True when two states differ ONLY by names added to an ordered list, with everything the
        /// baseline recorded still in its recorded order.
        /// </summary>
        /// <remarks>
        /// The subsequence test is the whole point. Comparing the lists as strings makes every
        /// addition a finding, which contradicts the rule that additions are not regressions;
        /// ignoring the lists entirely makes reordering invisible, which is the bug a user found by
        /// dragging a Camera down the Hierarchy and being told nothing had changed. Asking "are the
        /// names I recorded still in this order, ignoring newcomers" answers both at once.
        /// </remarks>
        private static bool OnlyOrderPreservingAdditions(string baselineState, string liveState)
        {
            Dictionary<string, string> before = SplitStateSegments(baselineState);
            Dictionary<string, string> after = SplitStateSegments(liveState);

            if (before.Count == 0 || before.Count != after.Count)
                return false;

            bool sawOrderList = false;

            foreach (var kvp in before)
            {
                // A segment that appeared or vanished is a component or a value coming or going,
                // never a list growing.
                if (!after.TryGetValue(kvp.Key, out string now))
                    return false;

                if (string.Equals(kvp.Value, now, StringComparison.Ordinal))
                    continue;

                if (!OrderListKeys.Contains(kvp.Key))
                    return false;

                if (!IsSubsequence(ListedNames(kvp.Value), ListedNames(now)))
                    return false;

                sawOrderList = true;
            }

            return sawOrderList;
        }

        /// <summary>Moved names printed before the line stops being a summary.</summary>
        private const int MaxReportedMoves = 5;

        /// <summary>
        /// Pairs of names past which the move analysis is skipped as not worth the work.
        /// </summary>
        /// <remarks>
        /// The analysis is quadratic. At this ceiling that is 40,000 string comparisons — nothing —
        /// but a scene with thousands of siblings would make a check that must stay instant feel
        /// broken, and the raw fallback still says what changed.
        /// </remarks>
        private const int MaxOrderAnalysisPairs = 40000;

        /// <summary>
        /// Describes an order change as the objects that MOVED, with their positions.
        /// </summary>
        /// <remarks>
        /// Asked for by the first user to see a root-order finding: "what if a real project has a
        /// hundred objects — the user must read all of them to discover what moved". Exactly right,
        /// and the same objection the property narrowing answered for components.
        ///
        /// Which names count as "moved" is the whole problem. Comparing by index reports every
        /// object AFTER the moved one as changed too, because they all shifted up by one — the
        /// renumbered-siblings noise this file already refuses elsewhere. So the moved set is the
        /// complement of the longest common subsequence: the fewest names whose removal leaves the
        /// rest in their recorded order, which is the smallest honest answer to "what did you do".
        ///
        /// Returns false — falling back to the raw lists — whenever the answer would be a guess:
        /// different sets of names, repeated names (position is then ambiguous), or a list too long
        /// to analyse. A wrong claim about what moved is worse than an unreadable true one.
        /// </remarks>
        private static bool TryDescribeOrderChange(List<string> lines, string key, string was, string now)
        {
            if (!OrderListKeys.Contains(key))
                return false;

            List<string> before = ListedNames(was);
            List<string> after = ListedNames(now);

            if (before.Count == 0 || after.Count == 0)
                return false;

            if (before.Count * after.Count > MaxOrderAnalysisPairs)
                return false;

            // Same names on both sides, each appearing once. Anything else is an addition, a
            // removal or an ambiguous twin, and those are reported as their own findings.
            if (before.Count != after.Count ||
                before.Distinct().Count() != before.Count ||
                !before.OrderBy(n => n, StringComparer.Ordinal)
                       .SequenceEqual(after.OrderBy(n => n, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                return false;
            }

            List<string> moved = NamesOutOfOrder(before, after);

            if (moved.Count == 0)
                return false;

            foreach (string name in moved.Take(MaxReportedMoves))
            {
                lines.Add($"{key}: {name} moved {before.IndexOf(name) + 1} → " +
                          $"{after.IndexOf(name) + 1} of {after.Count}");
            }

            if (moved.Count > MaxReportedMoves)
                lines.Add($"{key}: +{moved.Count - MaxReportedMoves} more moved");

            return true;
        }

        /// <summary>
        /// The fewest names that must move for the rest to stay in their recorded order.
        /// </summary>
        private static List<string> NamesOutOfOrder(List<string> before, List<string> after)
        {
            int n = before.Count;
            int m = after.Count;

            // Longest common subsequence lengths, filled from the end so the walk below can follow
            // the better branch at each step.
            var table = new int[n + 1, m + 1];

            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    table[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }

            var kept = new HashSet<string>();
            int x = 0, y = 0;

            while (x < n && y < m)
            {
                if (string.Equals(before[x], after[y], StringComparison.Ordinal))
                {
                    kept.Add(before[x]);
                    x++;
                    y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }

            return before.Where(name => !kept.Contains(name)).ToList();
        }

        /// <summary>The names inside a "children=(a,b,c)" style segment.</summary>
        private static List<string> ListedNames(string segment)
        {
            if (string.IsNullOrEmpty(segment))
                return new List<string>();

            int open = segment.IndexOf('(');
            int close = segment.LastIndexOf(')');

            string inside = open >= 0 && close > open
                ? segment.Substring(open + 1, close - open - 1)
                : segment;

            return inside.Length == 0
                ? new List<string>()
                : inside.Split(',').Select(n => n.Trim()).ToList();
        }

        /// <summary>Whether every name in <paramref name="recorded"/> appears in order in <paramref name="live"/>.</summary>
        private static bool IsSubsequence(List<string> recorded, List<string> live)
        {
            int next = 0;

            foreach (string name in live)
            {
                if (next < recorded.Count && string.Equals(recorded[next], name, StringComparison.Ordinal))
                    next++;
            }

            return next == recorded.Count;
        }

        /// <summary>Stable identity for a settings group, e.g. "project/physics".</summary>
        public static string SettingsKey(string scope, string group)
        {
            return (scope ?? "") + "/" + (group ?? "");
        }

        /// <summary>
        /// Adds findings for assets whose CONTENTS changed, attributed to the asset itself.
        /// </summary>
        /// <remarks>
        /// Only assets present in both are compared. One absent now is not reported here — see
        /// RegressionKind.AssetChanged for why that would double-report an edit already visible
        /// on the object that referenced it. One present only now is new work, exactly as a new
        /// object is, and new work is not breakage.
        /// </remarks>
        private static void CompareAssets(BaselineComparison comparison, Baseline baseline,
            List<BaselineAssetRecord> liveAssets)
        {
            // An empty list on an older baseline means "never recorded", not "nothing to record".
            // Treating those as clean would let a pre-asset baseline report coverage it never had.
            if (!baseline.RecordsAssets || baseline.assets == null || liveAssets == null)
                return;

            // Refuse rather than compare across the asset state-format change, for the same reason
            // objects are refused across theirs: every material recorded a resolved render queue
            // where capture now records the stored override, so all of them would report a change
            // nobody made. recordedAssetCount stays 0 so the report states the section was not
            // covered instead of claiming a clean sweep over records it never read.
            if (!baseline.AssetStateComparable)
                return;

            comparison.recordedAssetCount = baseline.assets.Count;

            var liveMap = new Dictionary<string, string>();
            foreach (BaselineAssetRecord asset in liveAssets)
                liveMap[asset.path ?? ""] = asset.state ?? "";

            foreach (BaselineAssetRecord recorded in baseline.assets)
            {
                if (!liveMap.TryGetValue(recorded.path ?? "", out string liveState))
                    continue;

                string recordedState = recorded.state ?? "";
                if (string.Equals(recordedState, liveState, StringComparison.Ordinal))
                    continue;

                comparison.findings.Add(new RegressionFinding
                {
                    path = recorded.path,
                    kind = RegressionKind.AssetChanged,
                    baselineState = recordedState,
                    liveState = liveState,
                    changedSegments = DescribeStateDifferences(recordedState, liveState)
                });
            }
        }

        // ── Verdict ──────────────────────────────────────────────────────────────

        /// <summary>The single rule for what a whole run means. Every renderer must use this.</summary>
        public static RegressionVerdict VerdictFor(RegressionRunResult result)
        {
            // A scene with no saved identity, or one with no baselines at all, compared nothing.
            // Reporting that as a pass is the easiest way for this feature to lie, so it is a
            // distinct outcome rather than a clean one.
            if (result == null || result.sceneWasUnsaved || !result.HasBaselines)
                return RegressionVerdict.NotChecked;

            if (result.TotalFindings == 0)
                return RegressionVerdict.Pass;

            // Only claim breakage when at least one broken baseline recorded state that actually
            // reached disk. Otherwise the honest answer is that we cannot tell.
            bool anyTrustworthy = result.comparisons
                .Any(c => c.IsBroken && c.stateReachedDisk);

            return anyTrustworthy ? RegressionVerdict.Regressions : RegressionVerdict.Unconfirmed;
        }

        /// <summary>The same rule applied to one baseline, for per-baseline reporting.</summary>
        public static RegressionVerdict VerdictFor(BaselineComparison comparison)
        {
            if (comparison?.baseline == null || !comparison.IsComparable)
                return RegressionVerdict.NotChecked;

            if (comparison.IsClean)
                return RegressionVerdict.Pass;

            return comparison.stateReachedDisk
                ? RegressionVerdict.Regressions
                : RegressionVerdict.Unconfirmed;
        }

        /// <summary>Stable lower-case token for machine-readable output. Do not localise.</summary>
        public static string VerdictToken(RegressionVerdict verdict)
        {
            switch (verdict)
            {
                case RegressionVerdict.Pass:         return "pass";
                case RegressionVerdict.Regressions:  return "regressions";
                case RegressionVerdict.Unconfirmed:  return "unconfirmed";
                default:                             return "not-checked";
            }
        }

        // ── Reporting ────────────────────────────────────────────────────────────

        public static string Describe(RegressionRunResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Scene baseline check — scene '{result.sceneName}'");

            // "Nothing to compare" must never render as a pass. A clean-looking result from a
            // check that compared nothing is precisely the false confidence baselines exist to
            // stop, and it is the easiest possible way for this feature to lie to someone.
            if (VerdictFor(result) == RegressionVerdict.NotChecked)
            {
                sb.AppendLine(DescribeVerdict(result));
                sb.AppendLine(result.sceneWasUnsaved
                    ? "Save the scene to start building regression history."
                    : "A baseline is recorded automatically the next time a request verifies fully clean.");
                return sb.ToString().TrimEnd();
            }

            if (result.sceneHadUnsavedChanges)
            {
                sb.AppendLine("Note: the scene has unsaved changes, so this reflects the editor's " +
                    "current state, not what is committed on disk.");
            }

            sb.AppendLine($"Compared against {result.comparisons.Count} baseline(s).");
            sb.AppendLine();

            foreach (BaselineComparison comparison in result.comparisons)
                sb.AppendLine(DescribeComparison(comparison));

            sb.AppendLine(DescribeVerdict(result));
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The one-line headline for a run, shared by the console output and the report artifact
        /// so the two can never announce different outcomes for the same check.
        /// </summary>
        public static string DescribeVerdict(RegressionRunResult result)
        {
            RegressionVerdict verdict = VerdictFor(result);

            if (verdict == RegressionVerdict.NotChecked)
            {
                if (result != null && result.sceneWasUnsaved)
                {
                    return "NOT CHECKED — the scene has never been saved, so it has no identity to " +
                           "attach baselines to. This is not a pass.";
                }

                // Baselines that exist but cannot be read are a different problem from having none,
                // and the fix is different too: one needs a fresh capture, the other needs a first.
                int unreadable = result?.comparisons.Count ?? 0;
                if (unreadable > 0)
                {
                    return $"NOT CHECKED — {unreadable} baseline(s) exist for this scene but none could " +
                           "be compared, so nothing was verified. This is not a pass.";
                }

                return "NOT CHECKED — no baselines recorded for this scene yet, so nothing could be " +
                       "compared. This is not a pass.";
            }

            int total = result.comparisons.Count(c => c.IsComparable);
            int skipped = result.comparisons.Count - total;
            string skippedNote = skipped > 0
                ? $" ({skipped} older baseline(s) skipped as unreadable)"
                : "";

            if (verdict == RegressionVerdict.Pass)
                return $"PASS — {total} baseline(s) still hold{skippedNote}.";

            int brokenCount = result.comparisons.Count(c => c.IsBroken);

            return verdict == RegressionVerdict.Regressions
                ? $"REGRESSIONS FOUND — {result.TotalFindings} across {brokenCount} of {total} baseline(s)."
                : $"UNCONFIRMED — {result.TotalFindings} difference(s) across {brokenCount} of {total} " +
                  "baseline(s), but no affected baseline can prove its recorded state ever reached disk, " +
                  "so these may be unsaved work reverting rather than regressions.";
        }

        /// <summary>Added object paths as one line, cut short before it becomes a wall.</summary>
        /// <remarks>
        /// Capped because bulk additions are normal — importing a level or unpacking a prefab can add
        /// hundreds at once, and a report that prints all of them buries the findings that matter
        /// underneath work nobody thinks is broken. The count above is always exact.
        /// </remarks>
        public static string DescribeAddedPaths(List<string> paths)
        {
            const int limit = 10;

            if (paths == null || paths.Count == 0)
                return "(none)";

            string listed = string.Join(", ", paths.Take(limit));

            return paths.Count > limit
                ? listed + $", +{paths.Count - limit} more"
                : listed;
        }

        private static string DescribeComparison(BaselineComparison comparison)
        {
            var sb = new StringBuilder();

            // The establish date is the point of the whole line: "this worked on 2026-08-01"
            // is what turns a diff into evidence.
            string established = comparison.baseline?.DescribeCreated() ?? "(unknown date)";
            string request = string.IsNullOrWhiteSpace(comparison.baseline?.originalRequest)
                ? "(no request recorded)"
                : comparison.baseline.originalRequest;

            sb.AppendLine($"── Baseline {comparison.baseline?.id} — established {established}");

            // A hand-recorded baseline proves the scene WAS this way, never that it was right.
            // Printing "(no request recorded)" for one would read as lost data and quietly imply
            // the same standing as a verified record; say the grade instead.
            if (comparison.baseline?.IsManuallyRecorded == true)
                sb.AppendLine("   recorded by hand — captures what the scene WAS, not that it was correct");
            else
                sb.AppendLine($"   was: \"{request}\"");

            sb.AppendLine($"   covers {comparison.recordedObjectCount} object(s)" +
                (comparison.newObjectCount > 0
                    ? $"; {comparison.newObjectCount} object(s) added since (not regressions)"
                    : ""));

            // Named, not just counted. A count cannot be acted on: "1 object added" reads the same
            // whether it is the spawner someone built on purpose or a duplicate of the Player.
            if (comparison.newObjectCount > 0)
                sb.AppendLine("      added: " + DescribeAddedPaths(comparison.newObjectPaths));

            // Stated because it changes what a MISSING line means. With identities, MISSING means the
            // object is genuinely gone; without them, a rename or a re-parent produces the same line,
            // and a reader who cannot tell the two apart will eventually distrust both.
            if (comparison.baseline?.RecordsObjectIds != true)
                sb.AppendLine("   matched by hierarchy path only — recorded before object identities " +
                    "were captured, so a renamed or re-parented object reports as MISSING; " +
                    "re-record to match by identity");

            // Coverage of assets is stated every time, including when it is zero, because "no
            // asset findings" and "assets were never looked at" are the same silence otherwise.
            if (comparison.baseline?.RecordsAssets == true &&
                comparison.baseline?.AssetStateComparable == false)
            {
                sb.AppendLine("   assets NOT compared — recorded in an older asset format " +
                    $"(schema v{comparison.baseline.schemaVersion}, this tool compares " +
                    $"v{BaselineStore.AssetStateFormatSchemaVersion}+), which stored a material's " +
                    "render queue as the value resolved from its shader rather than the override " +
                    "the material itself holds; re-record to restore asset coverage");
            }
            else if (comparison.baseline?.RecordsAssets == true)
            {
                int unchecked_ = comparison.baseline.uncheckedAssetCount;
                sb.AppendLine($"   covers {comparison.recordedAssetCount} referenced asset(s) by contents" +
                    (unchecked_ > 0
                        ? $"; {unchecked_} other asset(s) referenced but NOT content-checked " +
                          "(textures, meshes, audio and the like)"
                        : ""));
            }
            else
            {
                sb.AppendLine("   assets NOT covered — recorded before asset contents were checked; " +
                    "re-record to cover them");
            }

            // Printed but never compared: on an additive setup part of what made this scene work
            // may live next door, and the reader should know which record they are trusting.
            List<string> alongside = comparison.baseline?.otherLoadedScenes;
            if (alongside != null && alongside.Count > 0)
                sb.AppendLine($"   recorded with {string.Join(", ", alongside)} also loaded " +
                    "(context only — not compared)");

            if (comparison.baseline?.RecordsSettings == true)
                sb.AppendLine($"   covers {comparison.recordedSettingsCount} scene/project settings group(s)");
            else
                sb.AppendLine("   settings NOT covered — recorded before scene and project settings " +
                    "were checked; re-record to cover them");

            int confirmed = comparison.baseline?.verifiedChecks?.Count ?? 0;
            if (confirmed > 0)
                sb.AppendLine($"   guaranteed {confirmed} verified behaviour(s) at that time");

            if (!comparison.IsComparable)
            {
                sb.AppendLine($"   NOT COMPARED — {comparison.incomparableReason}.");
                return sb.ToString().TrimEnd();
            }

            if (comparison.IsClean)
            {
                sb.AppendLine("   OK — every recorded object still matches.");
                return sb.ToString().TrimEnd();
            }

            // Findings against a baseline that was never persisted are ambiguous, and saying so is
            // the whole point: the objects may have reverted because the scene was closed without
            // saving rather than because anything broke. Calling that a regression would be the
            // tool inventing breakage, which costs more trust than reporting nothing at all.
            string untrustworthy = comparison.untrustworthyReason;
            if (untrustworthy != null)
            {
                sb.AppendLine($"   NOTE: {untrustworthy}. If the scene was closed without saving, the " +
                    "findings below are that revert — not regressions.");
            }

            foreach (RegressionFinding finding in comparison.findings)
            {
                if (finding.kind == RegressionKind.Missing)
                {
                    sb.AppendLine($"   MISSING  {finding.path} — recorded as known-good, not in the scene now");
                    continue;
                }

                // Named as a move rather than a change so the reader is not sent hunting for a broken
                // value: the object is intact, it is somewhere else. Stated as fact, not guessed from
                // a name, because the pairing came from the object's own identity.
                if (finding.kind == RegressionKind.Moved)
                {
                    sb.AppendLine($"   MOVED    {finding.path} — renamed or re-parented, now at {finding.livePath}");

                    if (finding.changedSegments.Count > 0)
                    {
                        foreach (string segment in finding.changedSegments)
                            sb.AppendLine($"              {segment}");
                    }
                    else if (!string.Equals(finding.baselineState, finding.liveState,
                                 StringComparison.Ordinal))
                    {
                        sb.AppendLine($"              was:  {finding.baselineState}");
                        sb.AppendLine($"              now:  {finding.liveState}");
                    }

                    continue;
                }

                // Named as an asset rather than an object, because "the material changed" is what
                // happened — reporting it against each object that uses it would blame the wrong
                // thing and repeat one edit across the whole report.
                switch (finding.kind)
                {
                    case RegressionKind.AssetChanged:
                        sb.AppendLine($"   ASSET    {finding.path}");
                        break;

                    // Says outright that this is not the usual "you added something" line, because
                    // the reader has been told all their working life that additions are fine here.
                    case RegressionKind.Added:
                        sb.AppendLine($"   ADDED    {finding.path} — not in the baseline, and does not look deliberate");
                        break;

                    // Named as a setting because it is project-wide: nothing in the scene moved,
                    // and looking for a broken object would waste the reader's time.
                    case RegressionKind.SettingsChanged:
                        sb.AppendLine($"   SETTING  {finding.path}");
                        break;

                    default:
                        sb.AppendLine($"   CHANGED  {finding.path}");
                        break;
                }
                if (finding.changedSegments.Count > 0)
                {
                    foreach (string segment in finding.changedSegments)
                        sb.AppendLine($"              {segment}");
                }
                else
                {
                    sb.AppendLine($"              was:  {finding.baselineState}");
                    sb.AppendLine($"              now:  {finding.liveState}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        // ── State diffing ────────────────────────────────────────────────────────

        /// <summary>
        /// Reduces two state strings to just the parts that differ, as "key: was → now" lines.
        /// A state string is a sequence of `key(...)` / `key=[...]` segments, so reporting the
        /// whole thing would bury a moved transform in unchanged component lists.
        /// </summary>
        private static List<string> DescribeStateDifferences(string baselineState, string liveState)
        {
            var lines = new List<string>();

            Dictionary<string, string> before = SplitStateSegments(baselineState);
            Dictionary<string, string> after = SplitStateSegments(liveState);

            foreach (var kvp in before)
            {
                if (!after.TryGetValue(kvp.Key, out string now))
                    lines.Add($"{kvp.Key}: {Bare(kvp.Key, kvp.Value)} → (gone)");
                else if (!string.Equals(kvp.Value, now, StringComparison.Ordinal))
                    AddDifference(lines, kvp.Key, kvp.Value, now);
            }

            foreach (var kvp in after)
            {
                if (!before.ContainsKey(kvp.Key))
                    lines.Add($"{kvp.Key}: (absent) → {Bare(kvp.Key, kvp.Value)}");
            }

            return lines;
        }

        /// <summary>
        /// Reports a changed segment, narrowed to the fields inside it that actually differ.
        /// </summary>
        /// <remarks>
        /// Without this, one flipped bool on a component with twenty serialized fields prints two
        /// near-identical hundred-character strings and leaves the reader to spot the difference —
        /// which is the unreadable scene-YAML diff developers already have and already ignore. The
        /// legibility IS the product here, so narrowing is not cosmetic.
        ///
        /// Falls back to the whole segment whenever the two sides do not decompose the same way:
        /// a wrong guess about structure must never hide a difference.
        /// </remarks>
        private static void AddDifference(List<string> lines, string key, string was, string now)
        {
            // An ordered list must diff as "what moved", never as two full lists. A scene with a
            // hundred roots would otherwise print two hundred-name lines and leave the reader to
            // spot the one that moved — which is the unreadable YAML diff this tool exists to
            // replace, reintroduced by us.
            if (TryDescribeOrderChange(lines, key, was, now))
                return;

            string innerWas = InnerContent(key, was);
            string innerNow = InnerContent(key, now);

            if (innerWas != null && innerNow != null)
            {
                Dictionary<string, string> before = SplitStateSegments(innerWas);
                Dictionary<string, string> after = SplitStateSegments(innerNow);

                // Only narrow a segment built from named fields. pos(0,0,0) decomposes into a
                // segment KEYED BY ITS OWN VALUE, so narrowing it would report the old position
                // as "gone" and the new one as "absent → …" — two confusing lines in place of
                // one clear one.
                if (before.Count > 0 && after.Count > 0 && IsNamedFields(before) && IsNamedFields(after))
                {
                    int added = 0;

                    foreach (var kvp in before)
                    {
                        if (!after.TryGetValue(kvp.Key, out string current))
                        {
                            lines.Add($"{key}.{kvp.Key}: {kvp.Value} → (gone)");
                            added++;
                        }
                        else if (!string.Equals(kvp.Value, current, StringComparison.Ordinal))
                        {
                            lines.Add($"{key}.{kvp.Key}: {Value(kvp.Value)} → {Value(current)}");
                            added++;
                        }
                    }

                    foreach (var kvp in after)
                    {
                        if (!before.ContainsKey(kvp.Key))
                        {
                            lines.Add($"{key}.{kvp.Key}: (absent) → {kvp.Value}");
                            added++;
                        }
                    }

                    if (added > 0)
                        return;
                }
            }

            lines.Add($"{key}: {Bare(key, was)} → {Bare(key, now)}");
        }

        /// <summary>
        /// Strips a segment's own name, since the line is already labelled with it.
        /// </summary>
        /// <remarks>
        /// Without it a toggled object reads "active: active=true → active=false", saying the word
        /// three times for one fact. Display only — the recorded state string is untouched, so this
        /// is not a format change and costs nobody their baselines.
        /// </remarks>
        private static string Bare(string key, string segment)
        {
            if (string.IsNullOrEmpty(segment))
                return segment;

            if (segment.StartsWith(key + "=", StringComparison.Ordinal))
                return segment.Substring(key.Length + 1);

            return InnerContent(key, segment) ?? segment;
        }

        /// <summary>True when every segment is a `name=value` pair rather than a bare value.</summary>
        private static bool IsNamedFields(Dictionary<string, string> segments)
        {
            return segments.Values.All(s => s.IndexOf('=') > 0);
        }

        /// <summary>The text inside `key(...)`, or null when the segment is not that shape.</summary>
        private static string InnerContent(string key, string segment)
        {
            if (segment == null || !segment.StartsWith(key + "(", StringComparison.Ordinal) ||
                !segment.EndsWith(")", StringComparison.Ordinal))
                return null;

            int start = key.Length + 1;
            return segment.Substring(start, segment.Length - start - 1);
        }

        /// <summary>Strips the leading `name=` so a narrowed line does not repeat the name.</summary>
        private static string Value(string segment)
        {
            int cut = segment.IndexOf('=');
            return cut > 0 ? segment.Substring(cut + 1) : segment;
        }

        /// <summary>
        /// Splits a state string into its top-level segments, keyed by the part before the
        /// first '(' or '='. Splitting happens only at bracket depth zero because segments such
        /// as rect(...) legitimately contain both spaces and nested parentheses.
        /// </summary>
        private static Dictionary<string, string> SplitStateSegments(string state)
        {
            var segments = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(state))
                return segments;

            int depth = 0;
            int start = 0;

            for (int i = 0; i <= state.Length; i++)
            {
                if (i < state.Length)
                {
                    char c = state[i];
                    if (c == '(' || c == '[') depth++;
                    else if (c == ')' || c == ']') depth--;
                    else if (c == ' ' && depth == 0) { AddSegment(segments, state.Substring(start, i - start)); start = i + 1; }
                    continue;
                }

                AddSegment(segments, state.Substring(start));
            }

            return segments;
        }

        private static void AddSegment(Dictionary<string, string> segments, string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return;

            segment = segment.Trim();

            int cut = segment.IndexOfAny(new[] { '(', '=' });
            string key = cut > 0 ? segment.Substring(0, cut) : segment;

            // Duplicate keys would silently drop a difference; keep the first and disambiguate.
            if (segments.ContainsKey(key))
            {
                int n = 2;
                while (segments.ContainsKey(key + "#" + n)) n++;
                key = key + "#" + n;
            }

            segments[key] = segment;
        }
    }
}
