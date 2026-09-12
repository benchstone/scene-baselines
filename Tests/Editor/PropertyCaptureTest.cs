using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBaselines
{
    // Checks that a baseline records what a designer actually tunes, and that a change to it is
    // reported in a form a human will read. Costs nothing: no Play Mode, no model call, no scene —
    // capture runs directly on throwaway objects and its output is fed to the real comparison.
    //
    // This closes the gap found by the 08-06 demo. Capture recorded component TYPE NAMES only, so
    // "the BoxCollider is still there" passed while isTrigger had been flipped and the object had
    // stopped being a wall. The demo had to be broken a different way, and respondent D — who
    // built a property history tool himself — would have found it in the first minute.
    //
    // Two failure modes are guarded here, not one. Missing a real change is the obvious one. The
    // other is reporting a change nobody made: a check that cries wolf gets switched off, and then
    // it catches nothing at all, so the no-drift and not-serialized cases below matter as much as
    // the detection cases.
    //
    // Three ways in, one body of checks:
    //   Test Runner:  Window ▸ General ▸ Test Runner ▸ EditMode
    //   Menu:         Scene Baselines/Tests/Property Capture (free)
    //   Batch:        -runTests -testPlatform EditMode   (or -executeMethod
    //                 SceneBaselines.PropertyCaptureTest.RunBatch for the single-exit-code form)
    //
    // The [Test] methods below are the real entry points. They are deliberately thin — each one
    // runs a group from the same code the menu runs, so the two can never drift into checking
    // different things. What the attributes buy is DISCOVERY: a group added to this file shows up
    // in Test Runner and in CI whether or not somebody remembered to add it to a list.
    [TestFixture]
    public class PropertyCaptureTest
    {
        public static void RunBatch() => EditorApplication.Exit(Run() == 0 ? 0 : 2);

        [MenuItem("Scene Baselines/Tests/Property Capture (free)")]
        public static void RunMenu() => Run();

        // ── Test Runner surface ──────────────────────────────────────────────────
        //
        // [Order] is not decoration. These groups share one piece of global state — the scene the
        // editor has open — and some of them open, add to and close scenes to do their work. Run
        // them in NUnit's default alphabetical order and two fail: the additive-scene group lands
        // while an earlier group still has an untitled scene open, and the root-order group reads
        // a scene a previous group left behind.
        //
        // The order below is the one the suite was written and proven in. Keeping it explicit is
        // the honest fix: the dependency was always there, silently held by the order of the calls
        // in Run(), and is now stated where it can be seen. Isolating each group from the open
        // scene would be better still, and is worth doing before this suite grows.

        [Test, Order(1)] public void Detects_property_changes() => Verify(CheckDetectsPropertyChanges);
        [Test, Order(2)] public void Does_not_invent_changes() => Verify(CheckDoesNotInventChanges);
        [Test, Order(3)] public void Record_is_portable() => Verify(CheckRecordIsPortable);
        [Test, Order(4)] public void Report_is_readable() => Verify(CheckReportIsReadable);
        [Test, Order(5)] public void Inactive_objects_are_covered() => Verify(CheckInactiveObjectsAreCovered);
        [Test, Order(6)] public void Asset_contents_are_covered() => Verify(CheckAssetContentsAreCovered);
        [Test, Order(7)] public void Settings_are_covered() => Verify(CheckSettingsAreCovered);
        [Test, Order(8)] public void Only_the_active_scene_is_captured()
        {
            RefuseToRunOverUnsavedWork();
            Verify(() => InASavedScene(CheckOnlyTheActiveSceneIsCaptured));
        }

        [Test, Order(9)] public void Child_order_is_covered() => Verify(CheckChildOrderIsCovered);
        [Test, Order(10)] public void Identity_matching_survives_rename_and_reparent() => Verify(CheckIdentityMatching);
        [Test, Order(11)] public void Capture_produces_identities() => Verify(CheckCaptureProducesIdentities);
        [Test, Order(12)] public void Accept_rewrites_only_what_was_chosen() => Verify(CheckAcceptRewritesOnlyWhatWasChosen);
        [Test, Order(13)] public void Order_compares_as_subsequence() => Verify(CheckOrderComparesAsSubsequence);
        [Test, Order(14)] public void Order_change_names_what_moved() => Verify(CheckOrderChangeNamesWhatMoved);
        [Test, Order(15)] public void Root_order_is_covered()
        {
            RefuseToRunOverUnsavedWork();
            Verify(() => InASavedScene(CheckRootOrderIsCovered));
        }

        [Test, Order(16)] public void Additions_speak_only_when_suspicious() => Verify(CheckAdditionsSpeakOnlyWhenSuspicious);
        [Test, Order(17)] public void Dedup_covers_every_section() => Verify(CheckDedupCoversEverySection);

        [Test, Order(18)] public void Capture_is_stable_across_a_scene_reopen()
        {
            RefuseToRunOverUnsavedWork();
            Verify(() => InASavedScene(CheckCaptureIsStableAcrossReopen));
        }

        [Test, Order(19)] public void Capture_does_not_depend_on_locale() => Verify(CheckCaptureDoesNotDependOnLocale);
        [Test, Order(20)] public void Baselines_can_be_written_outside_the_project() => Verify(CheckBaselinesCanBeWrittenOutsideTheProject);
        [Test, Order(21)] public void Render_queue_records_the_override_not_the_resolved_value() => Verify(CheckRenderQueueRecordsTheOverride);
        [Test, Order(22)] public void A_twins_children_hang_from_the_twin() => Verify(() => InASavedScene(CheckTwinChildrenHangFromTheTwin));
        [Test, Order(23)] public void Findings_group_by_cause_without_losing_any() => Verify(CheckFindingsGroupByCause);
        [Test, Order(24)] public void A_grouped_report_still_prints_every_finding() => Verify(CheckGroupedReportPrintsEveryFinding);
        [Test, Order(25)] public void Repeated_assets_and_lost_siblings_group_too() => Verify(CheckRepetitionTheSubtreeRuleCannotReach);
        [Test, Order(26)] public void A_long_finding_is_capped_and_says_what_it_held_back() => Verify(CheckLongFindingsAreCappedHonestly);
        [Test, Order(27)] public void A_replay_sets_aside_only_project_settings() => Verify(CheckReplaySetsAsideOnlyProjectSettings);
        [Test, Order(28)] public void Commits_are_chosen_by_what_the_scenes_depend_on() => Verify(CheckCommitsAreChosenByWhatTheScenesDependOn);
        [Test, Order(29)] public void A_commit_says_how_far_it_reached() => Verify(CheckCommitScopeIsStated);
        [Test, Order(30)] public void A_replay_report_leads_with_findings() => Verify(CheckReplayReportLeadsWithFindings);
        [Test, Order(31)] public void A_filter_narrows_on_facts_and_hides_nothing_silently() => Verify(CheckReviewFilterNarrowsOnFacts);
        [Test, Order(32)] public void An_addition_can_be_adopted_without_re_recording() => Verify(CheckAdditionsCanBeAdopted);

        // ── A scene of the suite's own ───────────────────────────────────────────
        //
        // Four groups need a real, SAVED scene. The additive-scene group cannot even start without
        // one, because Unity refuses to add a scene alongside one that has never been saved; the
        // root-order group has nothing to describe in an empty scene; and the reopen group has
        // nothing to reopen.
        //
        // That used to mean they were SKIPPED under the Test Runner, which opens a fresh untitled
        // scene for the run — so the menu covered them and CI did not. Four of twenty-seven groups, and
        // among them the one guarding the determinism defect found on 2026-09-01, which is exactly
        // the property CI exists to hold. A test that only runs when a human remembers to click a
        // menu is not a guarantee.
        //
        // So the groups now bring their own scene when there isn't one. Two rules make that safe:
        //
        //   1. A real open scene is PREFERRED. Interactively the user has one, and the project's own
        //      scene is a better subject than anything synthetic — it is the scene whose reopen
        //      exposed the sweep-order bug. The fixture is the fallback, not the default, so this
        //      change adds CI coverage without weakening what the menu already covered.
        //
        //   2. Unsaved work is never touched. Every path here opens or reloads scenes, so if
        //      anything in the editor is dirty the group refuses rather than risk discarding it.
        //      Under the Test Runner nothing is dirty, so CI always proceeds.
        //
        // The fixture is deleted and the editor's original scene setup restored in a finally, so a
        // failing assertion cannot leave a stray scene asset behind in a user's project.

        private const string FixtureFolderName = "SceneBaselinesTestFixture";
        private const string FixtureFolder = "Assets/" + FixtureFolderName;
        private const string FixtureScenePath = FixtureFolder + "/CaptureFixture.unity";

        /// <summary>Skips the calling test when anything in the editor has unsaved changes.</summary>
        private static void RefuseToRunOverUnsavedWork()
        {
            if (!EditorHasUnsavedWork(out string why))
                return;

            Assert.Ignore(why + " These groups open and reload scenes, so they will not run while " +
                          "there is unsaved work to lose. Save, then run them again.");
        }

        private static bool EditorHasUnsavedWork(out string why)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty)
                    continue;

                why = "The scene " +
                      (string.IsNullOrEmpty(scene.path) ? "(untitled)" : $"'{scene.path}'") +
                      " has unsaved changes.";
                return true;
            }

            why = null;
            return false;
        }

        /// <summary>
        /// Runs a group against a saved scene: the one already open, or a temporary one built for it.
        /// </summary>
        private static int InASavedScene(System.Func<int> group)
        {
            if (EditorHasUnsavedWork(out string why))
            {
                Debug.Log("[Scene Baselines] SKIPPED: " + why + " Groups that need a saved scene do " +
                          "not run while there is unsaved work to lose.");
                return 0;
            }

            Scene active = SceneManager.GetActiveScene();

            // A real scene beats a synthetic one, so use it when the editor already has one open.
            if (active.IsValid() && !string.IsNullOrEmpty(active.path))
                return group();

            return InAFixtureScene(group);
        }

        private static int InAFixtureScene(System.Func<int> group)
        {
            SceneSetup[] toRestore = EditorSceneManager.GetSceneManagerSetup();

            if (!AssetDatabase.IsValidFolder(FixtureFolder))
                AssetDatabase.CreateFolder("Assets", FixtureFolderName);

            Scene fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Deliberately NOT in alphabetical order. Root order is recorded as hierarchy order, and
            // a fixture whose creation order matched its sorted order could not tell the two apart —
            // which matters now that the object sweep itself sorts.
            new GameObject("Zone");
            new GameObject("Anchor");
            new GameObject("Marker");

            if (!EditorSceneManager.SaveScene(fixture, FixtureScenePath))
            {
                Debug.Log("[Scene Baselines] SKIPPED: could not save a temporary scene at " +
                          FixtureScenePath + ", so this group has no saved scene to run against.");
                RestoreSceneSetup(toRestore);
                return 0;
            }

            try
            {
                return group();
            }
            finally
            {
                // Restored BEFORE the asset is deleted: Unity will not delete a scene that is open.
                RestoreSceneSetup(toRestore);
                AssetDatabase.DeleteAsset(FixtureScenePath);
                AssetDatabase.DeleteAsset(FixtureFolder);
            }
        }

        private static void RestoreSceneSetup(SceneSetup[] toRestore)
        {
            // An untitled scene has no path, and RestoreSceneManagerSetup cannot reopen one — the
            // Test Runner's own scene is exactly that. A fresh empty scene is the honest equivalent
            // of what was found, and is what the runner would have had anyway.
            bool restorable = toRestore != null && toRestore.Length > 0;

            if (restorable)
            {
                foreach (SceneSetup setup in toRestore)
                {
                    if (!string.IsNullOrEmpty(setup.path))
                        continue;

                    restorable = false;
                    break;
                }
            }

            if (restorable)
                EditorSceneManager.RestoreSceneManagerSetup(toRestore);
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        /// <summary>
        /// Runs one group and reports every assertion that failed, not just the first.
        /// </summary>
        /// <remarks>
        /// The groups count failures rather than throwing, so one bad assertion does not hide the
        /// nine after it — that property is worth keeping, so this collects the messages and fails
        /// once with all of them. Throwaway objects are destroyed even when a group throws.
        /// </remarks>
        private static void Verify(System.Func<List<GameObject>, int> group)
        {
            Failures.Clear();
            var created = new List<GameObject>();
            try
            {
                group(created);
            }
            finally
            {
                foreach (GameObject go in created)
                {
                    if (go != null)
                        UnityEngine.Object.DestroyImmediate(go);
                }
            }

            if (Failures.Count > 0)
                Assert.Fail($"{Failures.Count} assertion(s) failed:\n  - {string.Join("\n  - ", Failures)}");
        }

        private static void Verify(System.Func<int> group) => Verify(_ => group());

        private static int Run()
        {
            int failures = 0;
            Failures.Clear();
            var created = new List<GameObject>();

            try
            {
                failures += CheckDetectsPropertyChanges(created);
                failures += CheckDoesNotInventChanges(created);
                failures += CheckRecordIsPortable(created);
                failures += CheckReportIsReadable(created);
                failures += CheckInactiveObjectsAreCovered(created);
                failures += CheckAssetContentsAreCovered();
                failures += CheckSettingsAreCovered();
                failures += InASavedScene(CheckOnlyTheActiveSceneIsCaptured);
                failures += CheckChildOrderIsCovered(created);
                failures += CheckIdentityMatching();
                failures += CheckCaptureProducesIdentities();
                failures += CheckAcceptRewritesOnlyWhatWasChosen();
                failures += CheckOrderComparesAsSubsequence();
                failures += CheckOrderChangeNamesWhatMoved();
                failures += InASavedScene(CheckRootOrderIsCovered);
                failures += CheckAdditionsSpeakOnlyWhenSuspicious();
                failures += CheckDedupCoversEverySection();
                failures += InASavedScene(CheckCaptureIsStableAcrossReopen);
                failures += CheckCaptureDoesNotDependOnLocale();
                failures += CheckBaselinesCanBeWrittenOutsideTheProject();
                failures += CheckRenderQueueRecordsTheOverride();
                failures += InASavedScene(CheckTwinChildrenHangFromTheTwin);
                failures += CheckFindingsGroupByCause();
                failures += CheckGroupedReportPrintsEveryFinding();
                failures += CheckRepetitionTheSubtreeRuleCannotReach();
                failures += CheckLongFindingsAreCappedHonestly();
                failures += CheckReplaySetsAsideOnlyProjectSettings();
                failures += CheckCommitsAreChosenByWhatTheScenesDependOn();
                failures += CheckCommitScopeIsStated();
                failures += CheckReplayReportLeadsWithFindings();
                failures += CheckReviewFilterNarrowsOnFacts();
                failures += CheckAdditionsCanBeAdopted();
            }
            finally
            {
                foreach (GameObject go in created)
                {
                    if (go != null)
                        Object.DestroyImmediate(go);
                }
            }

            Debug.Log(failures == 0
                ? "[Scene Baselines] Property capture: ALL ASSERTIONS HELD"
                : $"[Scene Baselines] Property capture: {failures} ASSERTION(S) FAILED");

            return failures;
        }

        // ── Does it see what a designer changes? ─────────────────────────────────

        private static int CheckDetectsPropertyChanges(List<GameObject> created)
        {
            int failures = 0;

            // The exact change that escaped the 08-06 demo.
            GameObject wall = New(created, "Wall");
            BoxCollider box = wall.AddComponent<BoxCollider>();
            string solid = Capture(wall);
            box.isTrigger = true;
            failures += Held("a flipped isTrigger is recorded", solid != Capture(wall));

            GameObject crate = New(created, "Crate");
            Rigidbody body = crate.AddComponent<Rigidbody>();
            string light = Capture(crate);
            body.mass = 40f;
            failures += Held("a changed mass is recorded", light != Capture(crate));

            // A disabled renderer is still a renderer, and components=[…] cannot tell the
            // difference — the object just silently stops being visible. Unity does not return
            // m_Enabled from the property iterator (it is drawn in the component header), so this
            // asserts on the recorded value rather than on the states merely differing: a collider
            // being switched off also changes its bounds, which would let this pass while the
            // enabled flag went unrecorded.
            GameObject lamp = New(created, "Lamp");
            MeshRenderer renderer = lamp.AddComponent<MeshRenderer>();
            failures += Held("an enabled component records enabled=true",
                Capture(lamp).Contains("enabled=true"));
            renderer.enabled = false;
            failures += Held("a disabled component records enabled=false",
                Capture(lamp).Contains("enabled=false"));

            // A user's own script — the case that matters most, and the one no list of built-in
            // component types would ever cover.
            GameObject player = New(created, "Player");
            PropertyFixture tuned = player.AddComponent<PropertyFixture>();
            string authored = Capture(player);
            tuned.speed = 12f;
            failures += Held("a changed public field on a user script is recorded",
                authored != Capture(player));

            // [SerializeField] private is how most Unity code stores tuned values. Reflection over
            // public fields would miss it and leave the gap half open.
            tuned.speed = 5f;
            string beforeHidden = Capture(player);
            tuned.SetHiddenTuning(99f);
            failures += Held("a changed [SerializeField] private field is recorded",
                beforeHidden != Capture(player));

            // A reference going null is one of the failures studios already write asserts for.
            tuned.SetHiddenTuning(1f);
            tuned.linkedTarget = crate;
            string linked = Capture(player);
            tuned.linkedTarget = null;
            string unlinked = Capture(player);
            failures += Held("a reference going missing is recorded", linked != unlinked);
            failures += Held("a missing reference is recorded as such", unlinked.Contains("=none"));

            return failures;
        }

        // ── Does it stay quiet when nothing changed? ─────────────────────────────

        private static int CheckDoesNotInventChanges(List<GameObject> created)
        {
            int failures = 0;

            GameObject stable = New(created, "Stable");
            stable.AddComponent<BoxCollider>();
            stable.AddComponent<PropertyFixture>();

            // The single most important assertion here. If two captures of an untouched object
            // ever differ, every baseline reports regressions forever and the feature is worse
            // than useless — it trains its user to ignore it.
            failures += Held("two captures of an untouched object are identical",
                Capture(stable) == Capture(stable));

            // Not serialized, so not authored state. Recording it would report a change nobody made.
            var fixture = stable.GetComponent<PropertyFixture>();
            string before = Capture(stable);
            fixture.runtimeOnly = 123f;
            failures += Held("a [NonSerialized] field is not recorded", before == Capture(stable));

            // The transform is recorded above as pos/rot/scale, in a form that survives a resized
            // Game View. Recording it again as properties would undo that care.
            failures += Held("the transform is not recorded twice",
                !Capture(stable).Contains("props:Transform"));

            return failures;
        }

        // ── Is sibling order covered, without becoming noise? ────────────────────

        private static int CheckChildOrderIsCovered(List<GameObject> created)
        {
            int failures = 0;

            GameObject panel = New(created, "Panel");
            var first = new GameObject("First");
            var second = new GameObject("Second");
            first.transform.SetParent(panel.transform);
            second.transform.SetParent(panel.transform);

            string ordered = Capture(panel);
            failures += Held("a parent records its children in order",
                ordered.Contains("children=(First,Second)"));

            // Under a Canvas this IS draw order, so it must not read as unchanged.
            second.transform.SetSiblingIndex(0);
            string swapped = Capture(panel);
            failures += Held("reordering children changes the parent's record", ordered != swapped);

            BaselineComparison comparison = CompareStates(ordered, swapped);
            failures += Held("a reorder is reported once, against the parent",
                comparison.findings.Count == 1 &&
                comparison.findings[0].changedSegments.Any(s => s.StartsWith("children")));

            // The reason order lives on the parent rather than as an index on each child: with
            // per-child indices, deleting one child renumbers every later sibling and buries the
            // deletion under findings nobody caused.
            GameObject onlyChild = New(created, "Solo");
            var lonely = new GameObject("Lonely");
            lonely.transform.SetParent(onlyChild.transform);
            failures += Held("a single child records no order, which cannot differ",
                !Capture(onlyChild).Contains("children="));

            return failures;
        }

        // ── Does the record survive a reload and a different machine? ────────────

        private static int CheckRecordIsPortable(List<GameObject> created)
        {
            int failures = 0;

            GameObject holder = New(created, "Holder");
            GameObject target = New(created, "Target");
            PropertyFixture fixture = holder.AddComponent<PropertyFixture>();
            fixture.linkedTarget = target;

            string state = Capture(holder);

            // Object IDs are regenerated on every domain reload. One recorded in a baseline would
            // make the next script compile look like a scene-wide regression.
            failures += Held("no object ID is recorded",
                // Each editor rejects the other's spelling, so this needs the define: GetEntityId
                // arrived in 6000.4, and from that version GetInstanceID is obsolete AS AN ERROR
                // (CS0619), not merely deprecated. Either name serves here -- the assertion only
                // needs some id that the capture must never contain.
#if UNITY_6000_4_OR_NEWER
                !state.Contains(target.GetEntityId().ToString()));
#else
                !state.Contains(target.GetInstanceID().ToString()));
#endif
            failures += Held("a reference is recorded by name", state.Contains("Target"));

            // Truncation must announce itself. A record that quietly covers less than it appears to
            // is the exact false confidence this layer exists to remove.
            GameObject heavy = New(created, "Heavy");
            heavy.AddComponent<ManyPropertyFixture>();
            failures += Held("dropping properties past the cap is announced",
                Capture(heavy).Contains("truncated=("));

            // A stock renderer must fit inside the budget. When baking flags fill it instead, the
            // properties that matter are the ones pushed out — truncation would then be honest
            // about hiding exactly the state worth checking.
            GameObject rendered = New(created, "Rendered");
            rendered.AddComponent<MeshFilter>();
            rendered.AddComponent<MeshRenderer>();
            failures += Held("a stock renderer fits without truncation",
                !Capture(rendered).Contains("truncated=("));

            return failures;
        }

        // ── Will a human read the finding? ───────────────────────────────────────

        private static int CheckReportIsReadable(List<GameObject> created)
        {
            int failures = 0;

            GameObject wall = New(created, "ReportWall");
            BoxCollider box = wall.AddComponent<BoxCollider>();
            string recorded = Capture(wall);
            box.isTrigger = true;
            string live = Capture(wall);

            BaselineComparison comparison = CompareStates(recorded, live);

            failures += Held("the flipped isTrigger is reported as one finding",
                comparison.findings.Count == 1);

            List<string> segments = comparison.findings.Count == 1
                ? comparison.findings[0].changedSegments
                : new List<string>();

            // Narrowed to the field that changed. Printing two near-identical hundred-character
            // strings is the unreadable YAML diff developers already have and already ignore.
            failures += Held("the finding names the property that changed",
                segments.Any(s => s.IndexOf("IsTrigger", System.StringComparison.OrdinalIgnoreCase) >= 0));

            failures += Held("the finding shows the old and new value",
                segments.Any(s => s.Contains("false") && s.Contains("true")));

            // A moved object must still read as one line about position, not as the old position
            // vanishing and a new one appearing.
            GameObject moved = New(created, "Moved");
            string atOrigin = Capture(moved);
            moved.transform.position = new Vector3(3f, 0f, 0f);

            BaselineComparison movedComparison = CompareStates(atOrigin, Capture(moved));
            failures += Held("a moved object reports a single position line",
                movedComparison.findings.Count == 1 &&
                movedComparison.findings[0].changedSegments.Count(s => s.StartsWith("pos")) == 1);

            return failures;
        }

        // ── Are disabled objects covered at all? ─────────────────────────────────

        private static int CheckInactiveObjectsAreCovered(List<GameObject> created)
        {
            int failures = 0;

            GameObject toggled = New(created, "Toggled");
            string on = Capture(toggled);
            failures += Held("an active object records active=true", on.Contains("active=true"));

            toggled.SetActive(false);
            string off = Capture(toggled);
            failures += Held("a deactivated object records active=false", off.Contains("active=false"));

            BaselineComparison comparison = CompareStates(on, off);
            failures += Held("deactivating an object is reported as a change to it",
                comparison.findings.Count == 1 &&
                comparison.findings[0].kind == RegressionKind.Changed &&
                comparison.findings[0].changedSegments.Any(s => s.StartsWith("active")));

            failures += CheckSceneEnumerationIncludesInactive();

            return failures;
        }

        /// <summary>
        /// The case the assertions above cannot reach: whether the scene SWEEP finds inactive
        /// objects, which is where the defect actually was.
        /// </summary>
        /// <remarks>
        /// Every other case here runs on HideAndDontSave objects, which belong to no scene and are
        /// therefore invisible to CaptureBaselineObjects by design. Covering the sweep needs a real
        /// object in the open scene, so this creates one and removes it again — and runs only when
        /// the scene has no unsaved changes, because clearing dirtiness is how the scene is left
        /// exactly as it was found and doing that over a user's real edits would discard their work.
        /// Skipped loudly rather than silently: a test that quietly stops covering the thing it was
        /// written for is the failure this project keeps paying for.
        /// </remarks>
        private static int CheckSceneEnumerationIncludesInactive()
        {
            Scene scene = SceneManager.GetActiveScene();

            MethodInfo clearDirtiness = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(Scene) }, null);

            if (!scene.IsValid() || scene.isDirty || clearDirtiness == null)
            {
                Debug.Log("[Scene Baselines] SKIPPED: scene sweep covers inactive objects — needs a " +
                    "scene with no unsaved changes, and a way to leave it exactly as it was found.");
                return 0;
            }

            var probe = new GameObject("SceneBaselinesInactiveProbe");
            try
            {
                probe.SetActive(false);

                bool found = SceneCapture.CaptureBaselineObjects()
                    .Any(o => o.path != null && o.path.EndsWith("SceneBaselinesInactiveProbe"));

                return Held("the scene sweep captures inactive objects", found);
            }
            finally
            {
                Object.DestroyImmediate(probe);
                clearDirtiness.Invoke(null, new object[] { scene });
            }
        }

        // ── Are the assets the scene points at covered? ──────────────────────────

        private static int CheckAssetContentsAreCovered()
        {
            int failures = 0;

            // In memory only: never written to the project, so no asset is created or modified.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
            {
                Debug.Log("[Scene Baselines] SKIPPED: asset contents — no stock shader to build a " +
                    "material from on this render pipeline.");
                return 0;
            }

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                string original = SceneCapture.DescribeAsset(material);

                failures += Held("a material records its shader", original.Contains("shader="));

                // The whole reason this is not the generic serialized dump. A material's tuned
                // values sit at depth 3-5 behind an entry per texture slot, so a depth-limited
                // walk records pages of texture boilerplate and never reaches the colour.
                failures += Held("a material records a named shader property",
                    original.Contains("_BaseColor=") || original.Contains("_Color="));

                string colourProperty = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                material.SetColor(colourProperty, new Color(0.1f, 0.9f, 0.3f, 1f));
                string recoloured = SceneCapture.DescribeAsset(material);

                failures += Held("recolouring a material changes its record", original != recoloured);

                BaselineComparison comparison = CompareAssetStates(original, recoloured);
                failures += Held("a recoloured material is one asset finding",
                    comparison.findings.Count == 1 &&
                    comparison.findings[0].kind == RegressionKind.AssetChanged);

                failures += Held("the finding names the shader property that changed",
                    comparison.findings.Count == 1 &&
                    comparison.findings[0].changedSegments.Any(s => s.StartsWith(colourProperty)));

                // "Everything turned pink" — a material losing its shader must not read as unchanged.
                // Unity never actually reports a null shader: assigning null substitutes
                // Hidden/InternalErrorShader, which IS the magenta the artist sees. So the record
                // has to change, and has to name the shader it ended up with.
                material.shader = null;
                string broken = SceneCapture.DescribeAsset(material);
                failures += Held("a material losing its shader changes its record", broken != recoloured);
                failures += Held("a material losing its shader records the error shader",
                    broken.Contains("InternalErrorShader"));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }

            // A baseline taken before assets were recorded must not look like one that checked
            // them and found nothing — the silence is identical, the meaning is opposite.
            var older = new Baseline { schemaVersion = BaselineStore.AssetSchemaVersion - 1 };
            var current = new Baseline { schemaVersion = BaselineStore.AssetSchemaVersion };
            failures += Held("a pre-asset baseline reports that it does not cover assets",
                !older.RecordsAssets && current.RecordsAssets);

            // Recording assets and being able to COMPARE them are now two different claims.
            failures += Held("a baseline predating the render-queue record cannot compare assets",
                !new Baseline { schemaVersion = BaselineStore.AssetStateFormatSchemaVersion - 1 }
                    .AssetStateComparable &&
                new Baseline { schemaVersion = BaselineStore.AssetStateFormatSchemaVersion }
                    .AssetStateComparable);

            return failures;
        }

        private static BaselineComparison CompareAssetStates(string recorded, string live,
            int schemaVersion = BaselineStore.SchemaVersion)
        {
            var baseline = new Baseline
            {
                schemaVersion = schemaVersion,
                objects = new List<BaselineObjectRecord>(),
                assets = new List<BaselineAssetRecord>
                {
                    new BaselineAssetRecord { path = "Assets/Probe.mat", type = "Material", state = recorded }
                }
            };

            var liveAssets = new List<BaselineAssetRecord>
            {
                new BaselineAssetRecord { path = "Assets/Probe.mat", type = "Material", state = live }
            };

            return RegressionCheck.Compare(baseline, new List<BaselineObjectRecord>(),
                scenePersistedAfterCapture: true, liveAssets: liveAssets);
        }

        // ── Are the settings everything rests on covered? ────────────────────────

        private static int CheckSettingsAreCovered()
        {
            int failures = 0;

            List<SceneCapture.SceneSettingsRecord> settings = SceneCapture.CaptureSettings();

            failures += Held("scene render settings are recorded",
                settings.Any(s => s.scope == "scene" && s.group == "render" && s.state.Contains("fog=")));
            failures += Held("project physics settings are recorded",
                settings.Any(s => s.scope == "project" && s.group == "physics" && s.state.Contains("gravity=")));

            // 2D was barely covered in the first version — gravity only — while 3D was covered in
            // full. A 2D project had its physics effectively unchecked.
            failures += Held("2D physics settings are recorded to the same depth as 3D",
                settings.Any(s => s.group == "physics" &&
                    s.state.Contains("gravity2D=") && s.state.Contains("velocityIterations2D=")));
            failures += Held("the 2D collision matrix has its own group",
                settings.Any(s => s.group == "layers2D" && s.state.Contains("ignoredPairs=")));

            failures += Held("scripting defines and the active build target are recorded",
                settings.Any(s => s.group == "defines" &&
                    s.state.Contains("defines=") && s.state.Contains("activeBuildTarget=")));
            failures += Held("the build scene list is recorded",
                settings.Any(s => s.group == "build" && s.state.Contains("count=")));
            failures += Held("input axes are recorded",
                settings.Any(s => s.group == "input" && s.state.Contains("axis00=")));
            failures += Held("tags and layers are recorded",
                settings.Any(s => s.group == "tags" && s.state.Contains("tags=") && s.state.Contains("layers=")));

            // Runtime state, not an authored setting. Recording it would report a regression
            // because somebody paused the game.
            failures += Held("timeScale is not recorded",
                !settings.Any(s => s.state.Contains("timeScale")));

            // Found live: LightmapsMode is a FLAGS enum stringifying as "Single, Dual", and an
            // unwrapped space splits one value into two segments and corrupts every segment after
            // it. Any value containing a space must be wrapped in parens, which keeps splitting
            // depth-aware and the state string parseable.
            foreach (SceneCapture.SceneSettingsRecord record in settings)
                failures += Held($"{record.scope}/{record.group} has no unwrapped spaces in values",
                    !HasUnwrappedSpace(record.state));

            // Two captures with nothing touched in between must agree, or every check cries wolf.
            failures += Held("two captures of untouched settings agree",
                string.Join("|", settings.Select(s => s.state)) ==
                string.Join("|", SceneCapture.CaptureSettings().Select(s => s.state)));

            // A halved gravity is the canonical invisible break: no object moves, no asset
            // changes, and the whole game plays differently.
            Vector3 gravity = Physics.gravity;
            string before = settings.First(s => s.group == "physics").state;
            try
            {
                Physics.gravity = new Vector3(0f, -2f, 0f);
                string after = SceneCapture.CaptureSettings().First(s => s.group == "physics").state;

                failures += Held("changed gravity changes the record", before != after);

                BaselineComparison comparison = CompareSettingsStates("project", "physics", before, after);
                failures += Held("changed gravity is one settings finding",
                    comparison.findings.Count == 1 &&
                    comparison.findings[0].kind == RegressionKind.SettingsChanged &&
                    comparison.findings[0].path == "project/physics");
                failures += Held("the finding names gravity",
                    comparison.findings.Count == 1 &&
                    comparison.findings[0].changedSegments.Any(s => s.StartsWith("gravity")));
            }
            finally
            {
                Physics.gravity = gravity;
            }

            // The layer matrix is recorded by NAME so a finding says which pair stopped
            // colliding, rather than handing the reader two hex masks to decode.
            string layersBefore = SceneCapture.CaptureSettings().First(s => s.group == "layers").state;
            bool ignoredBefore = Physics.GetIgnoreLayerCollision(0, 1);
            try
            {
                Physics.IgnoreLayerCollision(0, 1, !ignoredBefore);
                string layersAfter = SceneCapture.CaptureSettings().First(s => s.group == "layers").state;

                failures += Held("a changed layer collision pair changes the record",
                    layersBefore != layersAfter);
                failures += Held("layer collisions are recorded by name, not as bitmasks",
                    layersAfter.Contains(LayerMask.LayerToName(0)) || layersBefore.Contains(LayerMask.LayerToName(0)));
            }
            finally
            {
                Physics.IgnoreLayerCollision(0, 1, ignoredBefore);
            }

            var older = new Baseline { schemaVersion = BaselineStore.SettingsSchemaVersion - 1 };
            var current = new Baseline { schemaVersion = BaselineStore.SettingsSchemaVersion };
            failures += Held("a pre-settings baseline reports that it does not cover settings",
                !older.RecordsSettings && current.RecordsSettings);

            return failures;
        }

        /// <summary>
        /// True when a segment at bracket depth zero is a bare word rather than a `name=value`
        /// pair — the signature of a value whose space escaped its parentheses.
        /// </summary>
        private static bool HasUnwrappedSpace(string state)
        {
            int depth = 0;
            int start = 0;

            for (int i = 0; i <= state.Length; i++)
            {
                bool end = i == state.Length;

                if (!end)
                {
                    char c = state[i];
                    if (c == '(' || c == '[') { depth++; continue; }
                    if (c == ')' || c == ']') { depth--; continue; }
                    if (c != ' ' || depth != 0) continue;
                }

                string segment = state.Substring(start, i - start).Trim();
                start = i + 1;

                if (segment.Length > 0 && segment.IndexOf('=') <= 0)
                    return true;
            }

            return false;
        }

        private static BaselineComparison CompareSettingsStates(string scope, string group,
            string recorded, string live)
        {
            var baseline = new Baseline
            {
                schemaVersion = BaselineStore.SchemaVersion,
                objects = new List<BaselineObjectRecord>(),
                settings = new List<BaselineSettingsRecord>
                {
                    new BaselineSettingsRecord { scope = scope, group = group, state = recorded }
                }
            };

            var liveSettings = new List<BaselineSettingsRecord>
            {
                new BaselineSettingsRecord { scope = scope, group = group, state = live }
            };

            return RegressionCheck.Compare(baseline, new List<BaselineObjectRecord>(),
                scenePersistedAfterCapture: true, liveAssets: null, liveSettings: liveSettings);
        }

        // ── Does a baseline describe ONE scene? ──────────────────────────────────

        /// <summary>
        /// Proves the sweep ignores objects belonging to other loaded scenes.
        /// </summary>
        /// <remarks>
        /// Needs a genuinely additive scene, because the defect was in which scenes the sweep
        /// walked, not in how it read an object. The extra scene is created empty and in memory,
        /// never saved, and closed again; the case is skipped when the active scene has unsaved
        /// changes so that clearing dirtiness afterwards cannot discard the user's work.
        /// </remarks>
        private static int CheckOnlyTheActiveSceneIsCaptured()
        {
            Scene active = SceneManager.GetActiveScene();

            MethodInfo clearDirtiness = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(Scene) }, null);

            if (!active.IsValid() || active.isDirty || clearDirtiness == null)
            {
                Debug.Log("[Scene Baselines] SKIPPED: only-the-active-scene sweep — needs a scene " +
                    "with no unsaved changes, and a way to leave it exactly as it was found.");
                return 0;
            }

            int failures = 0;
            int before = SceneCapture.CaptureBaselineObjects().Count;

            Scene extra = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                // NewScene makes the new scene ACTIVE even in Additive mode, which would leave this
                // measuring the empty scene rather than the additive setup it means to test.
                SceneManager.SetActiveScene(active);

                var stranger = new GameObject("SceneBaselinesForeignProbe");
                SceneManager.MoveGameObjectToScene(stranger, extra);

                List<BaselineObjectRecord> captured = SceneCapture.CaptureBaselineObjects();

                // The whole bug: this used to be `before + 1`, and the object would then report
                // MISSING on every later check made without the second scene open.
                failures += Held("an object in another loaded scene is not captured",
                    captured.Count == before &&
                    !captured.Any(o => o.path != null && o.path.EndsWith("SceneBaselinesForeignProbe")));

                // A never-saved scene reports an empty name, which would print as a blank in the
                // report and read as a bug rather than as a fact about the scene.
                failures += Held("the other loaded scene is recorded as context",
                    SceneCapture.OtherLoadedScenes()
                        .Contains(SceneCapture.UnsavedSceneName));
            }
            finally
            {
                SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(extra, true);
                clearDirtiness.Invoke(null, new object[] { active });
            }

            failures += Held("closing the extra scene leaves the sweep as it was",
                SceneCapture.CaptureBaselineObjects().Count == before);

            return failures;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the real comparison over two captured states, so this test cannot pass against a
        /// capture the checker would refuse.
        /// </summary>
        private static BaselineComparison CompareStates(string recorded, string live)
        {
            var baseline = new Baseline
            {
                schemaVersion = BaselineStore.SchemaVersion,
                objects = new List<BaselineObjectRecord>
                {
                    new BaselineObjectRecord { path = "Object", state = recorded }
                }
            };

            var liveObjects = new List<BaselineObjectRecord>
            {
                new BaselineObjectRecord { path = "Object", state = live }
            };

            return RegressionCheck.Compare(baseline, liveObjects, scenePersistedAfterCapture: true);
        }

        // ── Does identity survive a rename or a re-parent? ───────────────────────

        /// <summary>
        /// Guards the fix for the worst FALSE finding this tool had: a renamed or re-parented object
        /// reported as MISSING, i.e. the report announcing a deletion that never happened.
        /// </summary>
        /// <remarks>
        /// Built from hand-made records rather than from a scene on purpose. Comparison is pure, so
        /// the exact pairings that used to go wrong can be stated directly — including the ones no
        /// live scene would reliably reproduce, like two same-named siblings swapping suffixes.
        /// </remarks>
        private static int CheckIdentityMatching()
        {
            int failures = 0;

            const string idA = "GlobalObjectId_V1-2-abc-111-0";
            const string idB = "GlobalObjectId_V1-2-abc-222-0";

            // Renamed: same object, same state, new path. The old behaviour was MISSING + 1 new.
            BaselineComparison renamed = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Player", "state=1", idA) },
                new List<BaselineObjectRecord> { Rec("Hero", "state=1", idA) });

            failures += Held("a renamed object is reported as MOVED, not MISSING",
                renamed.findings.Count == 1 && renamed.findings[0].kind == RegressionKind.Moved);
            failures += Held("a renamed object names where it went now",
                renamed.findings.Count == 1 && renamed.findings[0].livePath == "Hero");
            failures += Held("a renamed object is not also counted as a new object",
                renamed.newObjectCount == 0);

            // Re-parented: the case named in the coverage limits.
            BaselineComparison reparented = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Enemies/Grunt", "state=1", idA) },
                new List<BaselineObjectRecord> { Rec("Pool/Grunt", "state=1", idA) });

            failures += Held("a re-parented object is reported as MOVED, not MISSING",
                reparented.findings.Count == 1 && reparented.findings[0].kind == RegressionKind.Moved);

            // The point of a MISSING line is that it means something. A deletion must still report it.
            BaselineComparison deleted = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Player", "state=1", idA) },
                new List<BaselineObjectRecord>());

            failures += Held("a genuinely deleted object is still reported as MISSING",
                deleted.findings.Count == 1 && deleted.findings[0].kind == RegressionKind.Missing);

            // Identity must beat the path, or a newcomer at the old path absorbs the match and the
            // rename is reported as "nothing happened".
            BaselineComparison displaced = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Player", "state=1", idA) },
                new List<BaselineObjectRecord>
                {
                    Rec("Player", "state=999", idB),
                    Rec("Hero", "state=1", idA)
                });

            failures += Held("identity wins over a new object occupying the old path",
                displaced.findings.Count == 1 &&
                displaced.findings[0].kind == RegressionKind.Moved &&
                displaced.findings[0].livePath == "Hero");
            failures += Held("the newcomer at the old path counts as added, not as a change",
                displaced.newObjectCount == 1);

            // Same-named siblings: "#n" is assigned by capture order, so twins could pair up wrongly
            // and report each other's states as changes. Identity pins them regardless of suffix.
            BaselineComparison twins = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Twin", "state=A", idA), Rec("Twin#2", "state=B", idB) },
                new List<BaselineObjectRecord> { Rec("Twin", "state=B", idB), Rec("Twin#2", "state=A", idA) });

            failures += Held("same-named siblings pair by identity, not by suffix order",
                twins.findings.All(f => f.kind == RegressionKind.Moved));

            // The fallback. A pre-v10 baseline has no ids at all and must behave exactly as before,
            // or shipping identities would break every baseline already recorded.
            BaselineComparison legacy = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Player", "state=1", "") },
                new List<BaselineObjectRecord> { Rec("Player", "state=2", "") });

            failures += Held("a baseline without ids still matches by path",
                legacy.findings.Count == 1 && legacy.findings[0].kind == RegressionKind.Changed);

            BaselineComparison legacyClean = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Player", "state=1", "") },
                new List<BaselineObjectRecord> { Rec("Player", "state=1", "") });

            failures += Held("a baseline without ids reports no finding when nothing changed",
                legacyClean.findings.Count == 0);

            // An id-less baseline must not imply identity coverage it does not have.
            var older = new Baseline { schemaVersion = BaselineStore.IdentitySchemaVersion - 1 };
            var current = new Baseline { schemaVersion = BaselineStore.IdentitySchemaVersion };
            failures += Held("a pre-identity baseline reports that it matches by path only",
                !older.RecordsObjectIds && current.RecordsObjectIds);

            // Identities are additive: bumping them must NOT invalidate existing baselines, because a
            // forced re-record would bake today's scene in as known-good and lose the real history.
            failures += Held("adding identities did not invalidate existing baselines",
                new Baseline { schemaVersion = BaselineStore.IdentitySchemaVersion - 1 }
                    .StateFormatComparable);

            return failures;
        }

        // ── Does accepting a finding do exactly what it claims? ──────────────────

        /// <summary>
        /// Accepting is the only operation that WRITES to a known-good record, so its blast radius
        /// matters more than any other: adopting a difference nobody selected would silence a real
        /// regression forever, and that is a failure no later check can recover from.
        /// </summary>
        /// <remarks>
        /// Exercises <see cref="BaselineAccept.Apply"/>, which is the same decision logic the
        /// window uses without the save — so these assertions cost nothing and leave no files behind.
        /// </remarks>
        private static int CheckAcceptRewritesOnlyWhatWasChosen()
        {
            int failures = 0;

            const string idA = "GlobalObjectId_V1-2-abc-111-0";
            const string idB = "GlobalObjectId_V1-2-abc-222-0";

            // Accepting a changed value records the new value as known-good.
            Baseline baseline = Baseline(Rec("Player", "speed=5", idA));
            var changed = new RegressionFinding
            {
                path = "Player", liveId = idA, kind = RegressionKind.Changed, liveState = "speed=12"
            };

            BaselineAccept.Result outcome =
                BaselineAccept.Apply(baseline, new List<RegressionFinding> { changed });

            failures += Held("accepting a change records the new state",
                outcome.acceptedCount == 1 && baseline.objects[0].state == "speed=12");

            // The whole point of per-finding accept: an unselected difference must survive untouched.
            Baseline twoObjects = Baseline(Rec("Player", "speed=5", idA), Rec("Enemy", "hp=3", idB));
            BaselineAccept.Apply(twoObjects, new List<RegressionFinding>
            {
                new RegressionFinding { path = "Player", liveId = idA, kind = RegressionKind.Changed, liveState = "speed=12" }
            });

            failures += Held("accepting one finding leaves the unselected record alone",
                twoObjects.objects.First(o => o.path == "Enemy").state == "hp=3");

            // Accepting a move rewrites where the object is expected, and keeps its state.
            Baseline moved = Baseline(Rec("Enemies/Grunt", "hp=3", idA));
            BaselineAccept.Apply(moved, new List<RegressionFinding>
            {
                new RegressionFinding
                {
                    path = "Enemies/Grunt", livePath = "Pool/Grunt", liveId = idA,
                    kind = RegressionKind.Moved, liveState = "hp=4"
                }
            });

            failures += Held("accepting a move records the new path",
                moved.objects[0].path == "Pool/Grunt");
            failures += Held("accepting a move records the new state too",
                moved.objects[0].state == "hp=4");

            // Accepting a deletion drops the record, so the object stops being expected at all.
            Baseline deleted = Baseline(Rec("Target", "state=1", idA), Rec("Player", "state=2", idB));
            BaselineAccept.Apply(deleted, new List<RegressionFinding>
            {
                new RegressionFinding { path = "Target", liveId = "", kind = RegressionKind.Missing }
            });

            failures += Held("accepting a deletion removes the record",
                deleted.objects.Count == 1 && deleted.objects[0].path == "Player");

            // A finding that matches nothing must be reported, never counted as done. Silently
            // "accepting" it would leave the user certain it was handled while the check still fails.
            Baseline stale = Baseline(Rec("Player", "speed=5", idA));
            BaselineAccept.Result unmatched =
                BaselineAccept.Apply(stale, new List<RegressionFinding>
                {
                    new RegressionFinding { path = "GoneFromBaseline", liveId = "", kind = RegressionKind.Changed, liveState = "x=1" }
                });

            failures += Held("a finding matching no record is skipped, not counted as accepted",
                unmatched.acceptedCount == 0 && unmatched.skippedCount == 1);
            failures += Held("a skipped finding leaves the baseline untouched",
                stale.objects[0].state == "speed=5" && stale.acceptedFindingCount == 0);

            // The grade must travel with the record: a baseline holding accepted findings is no longer
            // purely "what the scene was", and a reader has to be able to tell.
            failures += Held("accepting is recorded on the baseline as provenance",
                baseline.acceptedFindingCount == 1 && baseline.HasAcceptedFindings &&
                !string.IsNullOrEmpty(baseline.lastAcceptedUtc));

            // Accepting nothing must write nothing — the guard behind "walking away IS rejecting".
            Baseline untouched = Baseline(Rec("Player", "speed=5", idA));
            BaselineAccept.Result empty =
                BaselineAccept.Apply(untouched, new List<RegressionFinding>());

            failures += Held("accepting an empty selection changes nothing",
                empty.acceptedCount == 0 && untouched.objects[0].state == "speed=5" &&
                !untouched.HasAcceptedFindings);

            // Settings and assets are accepted in their own sections, not against objects.
            var withSettings = new Baseline
            {
                schemaVersion = BaselineStore.SchemaVersion,
                objects = new List<BaselineObjectRecord>(),
                settings = new List<BaselineSettingsRecord>
                {
                    new BaselineSettingsRecord { scope = "project", group = "physics", state = "gravity=-9.81" }
                }
            };

            BaselineAccept.Apply(withSettings, new List<RegressionFinding>
            {
                new RegressionFinding
                {
                    path = RegressionCheck.SettingsKey("project", "physics"),
                    kind = RegressionKind.SettingsChanged, liveState = "gravity=-20"
                }
            });

            failures += Held("accepting a settings change records the new setting",
                withSettings.settings[0].state == "gravity=-20");

            return failures;
        }

        private static Baseline Baseline(params BaselineObjectRecord[] records) =>
            new Baseline
            {
                schemaVersion = BaselineStore.SchemaVersion,
                sceneName = "TestScene",
                objects = records.ToList()
            };

        /// <summary>
        /// The checks above prove the MATCHING with hand-built records. This proves capture actually
        /// produces identities from a real scene — the half no hand-built record can cover.
        /// </summary>
        private static int CheckCaptureProducesIdentities()
        {
            Scene scene = SceneManager.GetActiveScene();
            List<BaselineObjectRecord> live = SceneCapture.CaptureBaselineObjects();

            // Announced rather than skipped in silence. A check that quietly passes when it could not
            // run is the same lie as a report claiming PASS with no baselines behind it.
            if (string.IsNullOrEmpty(scene.path) || live.Count == 0)
            {
                Debug.LogWarning("[Scene Baselines] SKIPPED (not a failure): object identities can " +
                                 "only be checked against a saved, non-empty scene — open one to " +
                                 "cover this assertion.");
                return 0;
            }

            return Held("capture gives every object in a saved scene a stable identity",
                live.All(r => !string.IsNullOrEmpty(r.id)));
        }

        // ── Does order distinguish "something arrived" from "something moved"? ───
        //
        // Both defects a real user found on 2026-08-11 live here. Adding an object rewrites its
        // parent's child list, so comparing those lists as strings made routine content work report
        // its own parent as damage — while a Hierarchy REORDER, which changes no object's state,
        // reported nothing at all. One rule answers both: are the recorded names still in their
        // recorded order, ignoring newcomers.

        private static int CheckOrderComparesAsSubsequence()
        {
            int failures = 0;

            failures += Held("adding a child leaves the parent silent",
                CompareStates("children=(A,B)", "children=(A,B,C)").findings.Count == 0);

            failures += Held("a child inserted between two others is still silent",
                CompareStates("children=(A,B)", "children=(A,C,B)").findings.Count == 0);

            failures += Held("reordering the children that were recorded is reported",
                CompareStates("children=(A,B)", "children=(B,A)").findings.Count == 1);

            // A removed child is reported here AND as its own MISSING line, by design: the parent
            // genuinely changed, and the two findings describe different halves of one edit.
            failures += Held("a removed child still changes its parent's record",
                CompareStates("children=(A,B,C)", "children=(A,C)").findings.Count == 1);

            failures += Held("a real value change beside an untouched list is still reported",
                CompareStates("pos(0,0,0) children=(A)", "pos(1,0,0) children=(A)").findings.Count == 1);

            failures += Held("a root list that only grew is silent",
                CompareStates("roots=(Ground,Player)", "roots=(Ground,Player,Pickup)").findings.Count == 0);

            failures += Held("reordering roots is reported",
                CompareStates("roots=(Ground,Player)", "roots=(Player,Ground)").findings.Count == 1);

            return failures;
        }

        private static int CheckOrderChangeNamesWhatMoved()
        {
            int failures = 0;

            List<string> oneMove = CompareStates("roots=(A,B,C)", "roots=(C,A,B)")
                .findings[0].changedSegments;

            // Named, with positions. The alternative — printing both lists — is the unreadable diff
            // this tool exists to replace, and a real project has hundreds of roots.
            failures += Held("an order change names the object that moved",
                oneMove.Count == 1 && oneMove[0].Contains("C moved 3 → 1 of 3"));

            // Comparing by index would report B and C as changed too, because both shifted up one.
            failures += Held("objects that merely shifted are not reported as moved",
                oneMove.Count == 1 && !oneMove[0].Contains("A ") && !oneMove[0].Contains("B "));

            // The scale objection, exactly as asked: one drag among a hundred must stay one line.
            var many = new List<string>();
            for (int i = 1; i <= 100; i++)
                many.Add("Obj" + i);

            List<string> reshuffled = new List<string>(many);
            reshuffled.RemoveAt(99);
            reshuffled.Insert(0, "Obj100");

            List<string> big = CompareStates(
                    "roots=(" + string.Join(",", many.ToArray()) + ")",
                    "roots=(" + string.Join(",", reshuffled.ToArray()) + ")")
                .findings[0].changedSegments;

            failures += Held("one move among a hundred objects is one line",
                big.Count == 1 && big[0].Contains("Obj100 moved 100 → 1 of 100"));

            // Where the answer would be a guess, the raw states must come back rather than a
            // confident wrong claim about which twin moved.
            List<string> twins = CompareStates("children=(A,A,B)", "children=(B,A,A)")
                .findings[0].changedSegments;

            failures += Held("repeated names fall back instead of guessing which one moved",
                twins.All(s => !s.Contains("moved")));

            return failures;
        }

        private static int CheckRootOrderIsCovered()
        {
            int failures = 0;

            SceneCapture.SceneSettingsRecord rootOrder = SceneCapture.CaptureSettings()
                .FirstOrDefault(s => s.scope == "scene" && s.group == "rootOrder");

            failures += Held("the scene's root order is captured at all", rootOrder != null);

            if (rootOrder == null)
                return failures;

            failures += Held("root order is recorded as an ordered name list",
                rootOrder.state.StartsWith("roots=("));

            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();

            // The level DescribeChildOrder cannot reach: a root object has no parent to record it.
            failures += Held("root order lists the scene's own top-level objects",
                roots.Length == 0 || rootOrder.state.Contains(roots[0].name));

            return failures;
        }

        // ── Does an addition speak only when it looks accidental? ────────────────

        private static int CheckAdditionsSpeakOnlyWhenSuspicious()
        {
            int failures = 0;

            const string idA = "GlobalObjectId_V1-2-abc-111-0";
            const string idB = "GlobalObjectId_V1-2-abc-222-0";
            const string idC = "GlobalObjectId_V1-2-abc-333-0";

            var recorded = new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform,AudioListener] pos(0,0,0)", idA)
            };

            // Building new things is the normal case and must stay silent, or a team adding content
            // daily gets a red check every day and switches it off.
            BaselineComparison honest = CompareObjects(recorded, new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform,AudioListener] pos(0,0,0)", idA),
                Rec("Spawner", "components=[Transform] pos(2,0,0)", idB)
            });

            failures += Held("ordinary new work is not a finding", honest.findings.Count == 0);
            failures += Held("added objects are NAMED, not only counted",
                honest.newObjectCount == 1 && honest.newObjectPaths.Contains("Spawner"));

            // The stray Ctrl+D: identical in every recorded value, including position.
            BaselineComparison duplicate = CompareObjects(recorded, new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform,AudioListener] pos(0,0,0)", idA),
                Rec("Player#2", "components=[Transform,AudioListener] pos(0,0,0)", idB)
            });

            failures += Held("an exact duplicate of a baselined object is reported",
                duplicate.findings.Count == 1 && duplicate.findings[0].kind == RegressionKind.Added);

            // The copy that was made and then dragged somewhere else, which the state rule misses.
            BaselineComparison copy = CompareObjects(recorded, new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform,AudioListener] pos(0,0,0)", idA),
                Rec("Player (1)", "components=[Transform,AudioListener] pos(9,9,9)", idB)
            });

            failures += Held("an object named like a copy is reported",
                copy.findings.Count == 1 && copy.findings[0].kind == RegressionKind.Added);

            // Unity honours one. A second changes behaviour silently, which is this layer's whole
            // subject matter.
            BaselineComparison singleton = CompareObjects(recorded, new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform,AudioListener] pos(0,0,0)", idA),
                Rec("Minimap", "components=[Transform,Camera,AudioListener] pos(0,20,0)", idC)
            });

            failures += Held("a second AudioListener is reported",
                singleton.findings.Count == 1 &&
                singleton.findings[0].changedSegments[0].Contains("AudioListener"));

            // Accepting an addition must ADOPT it, not merely silence the line: a silenced object
            // stays uncovered forever while looking as though it was dealt with.
            Baseline adopting = Baseline(Rec("Player", "speed=5", idA));
            BaselineAccept.Apply(adopting, new List<RegressionFinding>
            {
                new RegressionFinding
                {
                    path = "Turret", livePath = "Turret", liveId = idB,
                    kind = RegressionKind.Added, liveState = "hp=10"
                }
            });

            failures += Held("accepting an added object records it as covered",
                adopting.objects.Any(o => o.path == "Turret" && o.state == "hp=10"));

            return failures;
        }

        // ── Can new work join a baseline without re-recording it? ────────────────
        //
        // 🚨 The gap a user found in the editor, not in a test. They made an object, saved, opened
        // the review window and were told nothing differed. True, and useless: everything built
        // since the baseline was unwatched, and the only way to change that was to re-record the
        // scene — which adopts every other difference in it at the same time, read or not.
        //
        // So the property under test is not "additions are listed". It is that adopting one
        // PROTECTS it: after the tick, losing that object has to be reported like any other loss.
        // Silencing a line and covering an object look identical until exactly that moment.

        private static int CheckAdditionsCanBeAdopted()
        {
            int failures = 0;

            const string idA = "GlobalObjectId_V1-2-abc-111-0";
            const string idB = "GlobalObjectId_V1-2-abc-222-0";
            const string idC = "GlobalObjectId_V1-2-abc-333-0";

            var recorded = new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform] pos(0,0,0)", idA)
            };

            var live = new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform] pos(0,0,0)", idA),
                Rec("Square", "components=[Transform,SpriteRenderer] pos(2,0,0)", idB)
            };

            BaselineComparison offered = CompareObjects(recorded, live);

            failures += Held("ordinary new work is still not a finding", offered.findings.Count == 0);
            failures += Held("an added object is offered for adoption",
                offered.newObjects.Count == 1 &&
                offered.newObjects[0].path == "Square" &&
                offered.newObjects[0].kind == RegressionKind.Added);
            failures += Held("what is offered carries the identity and state accept needs",
                offered.newObjects[0].liveId == idB &&
                offered.newObjects[0].liveState.Contains("SpriteRenderer"));

            // An addition the check already accuses is tickable on its own finding. Offering it a
            // second time would ask for one decision in two places, and the second accept would find
            // the object recorded and report itself skipped.
            BaselineComparison accused = CompareObjects(recorded, new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform] pos(0,0,0)", idA),
                Rec("Player (1)", "components=[Transform] pos(9,9,9)", idC)
            });

            failures += Held("an addition already reported is not offered twice",
                accused.findings.Count == 1 && accused.newObjects.Count == 0);

            // Adopted exactly as the window does it: the offered record, straight into accept.
            Baseline baseline = Baseline(Rec("Player", "components=[Transform] pos(0,0,0)", idA));

            BaselineAccept.Result outcome =
                BaselineAccept.Apply(baseline, new List<RegressionFinding> { offered.newObjects[0] });

            failures += Held("adopting an offered addition is accepted, not skipped",
                outcome.acceptedCount == 1 && outcome.skippedCount == 0);
            failures += Held("adopting one object leaves every other record as it was recorded",
                baseline.objects.Count == 2 &&
                baseline.objects.Any(o => o.path == "Player" &&
                                          o.state == "components=[Transform] pos(0,0,0)"));

            BaselineComparison afterwards = CompareObjects(baseline.objects, live);

            failures += Held("an adopted object stops being offered",
                afterwards.newObjects.Count == 0 && afterwards.findings.Count == 0);

            // Silencing would have passed every assertion above. This is where the two part company.
            BaselineComparison lost = CompareObjects(baseline.objects, new List<BaselineObjectRecord>
            {
                Rec("Player", "components=[Transform] pos(0,0,0)", idA)
            });

            failures += Held("losing an adopted object is reported like any other loss",
                lost.findings.Count == 1 &&
                lost.findings[0].kind == RegressionKind.Missing &&
                lost.findings[0].path == "Square");

            failures += CheckAdoptedObjectsKeepTheirPlace();
            failures += CheckDeletedAndMovedObjectsLeaveTheirPlace();

            return failures;
        }

        // ── …and does it learn WHERE the object sits? ────────────────────────────
        //
        // Asked by the user straight after adoption shipped: roots were MainCamera, Light; they
        // built Square and accepted it. The record was written, the order was not — and because
        // MainCamera, Light stay in sequence wherever Square goes, dragging Square to the top was
        // silent for an object the window had just called covered.

        private static int CheckAdoptedObjectsKeepTheirPlace()
        {
            int failures = 0;

            const string idCamera = "GlobalObjectId_V1-2-abc-111-0";
            const string idLight = "GlobalObjectId_V1-2-abc-222-0";
            const string idSquare = "GlobalObjectId_V1-2-abc-333-0";
            const string idWall = "GlobalObjectId_V1-2-abc-444-0";

            BaselineObjectRecord camera = Rec("MainCamera", "components=[Transform,Camera] pos(0,1,-10)", idCamera);
            BaselineObjectRecord light = Rec("Light", "components=[Transform,Light] pos(0,3,0)", idLight);
            BaselineObjectRecord square = Rec("Square", "components=[Transform,SpriteRenderer] pos(2,0,0)", idSquare);

            Baseline RootsBaseline()
            {
                Baseline b = Baseline(Rec(camera.path, camera.state, camera.id), Rec(light.path, light.state, light.id));
                b.settings = new List<BaselineSettingsRecord> { Setting("scene", "rootOrder", "roots=(MainCamera,Light)") };
                return b;
            }

            BaselineComparison CompareRoots(Baseline b, string roots, params BaselineObjectRecord[] live) =>
                RegressionCheck.Compare(b, live.ToList(), scenePersistedAfterCapture: true,
                    liveSettings: new List<BaselineSettingsRecord> { Setting("scene", "rootOrder", roots) });

            // Built at the bottom of the Hierarchy.
            Baseline atEnd = RootsBaseline();
            BaselineComparison offered = CompareRoots(atEnd, "roots=(MainCamera,Light,Square)", camera, light, square);
            BaselineAccept.Result placed = BaselineAccept.Apply(atEnd, offered.newObjects);

            failures += Held("adopting a top-level object records its place in the root order",
                offered.findings.Count == 0 && placed.orderLeftAloneCount == 0 &&
                atEnd.settings[0].state == "roots=(MainCamera,Light,Square)");
            failures += Held("an adopted object in its place reads as unchanged",
                CompareRoots(atEnd, "roots=(MainCamera,Light,Square)", camera, light, square).findings.Count == 0);

            BaselineComparison dragged = CompareRoots(atEnd, "roots=(Square,MainCamera,Light)", camera, light, square);
            failures += Held("dragging an adopted object up the Hierarchy is reported",
                dragged.findings.Count == 1 && dragged.findings[0].kind == RegressionKind.SettingsChanged);

            // Built between two recorded objects: the place, not merely "somewhere in the list".
            Baseline between = RootsBaseline();
            BaselineAccept.Apply(between,
                CompareRoots(between, "roots=(MainCamera,Square,Light)", camera, light, square).newObjects);
            failures += Held("an object built between two others is recorded between them",
                between.settings[0].state == "roots=(MainCamera,Square,Light)");

            // 🚨 The tick was about Square. A reorder of MainCamera and Light sitting in the same
            // live list has not been reviewed, and copying the live order would adopt it silently.
            Baseline pending = RootsBaseline();
            BaselineComparison reordered = CompareRoots(pending, "roots=(Light,MainCamera,Square)", camera, light, square);
            BaselineAccept.Result refused = BaselineAccept.Apply(pending, reordered.newObjects);

            failures += Held("adopting an object does not adopt a reorder nobody reviewed",
                pending.settings[0].state == "roots=(MainCamera,Light)" &&
                reordered.findings.Any(f => f.kind == RegressionKind.SettingsChanged));
            failures += Held("an object that could not be placed is still covered, and says so",
                refused.acceptedCount == 1 && refused.orderLeftAloneCount == 1 &&
                pending.objects.Any(o => o.path == "Square"));

            // Two names capture writes identically: "Door A" is recorded, "Door_A" is new. Which
            // "Door_A" in the list is the new one is a coin toss, so it is not tossed.
            Baseline doors = Baseline(Rec("Door A", "components=[Transform] pos(0,0,0)", idCamera));
            doors.settings = new List<BaselineSettingsRecord> { Setting("scene", "rootOrder", "roots=(Door_A)") };
            BaselineComparison lookalike = RegressionCheck.Compare(doors,
                new List<BaselineObjectRecord>
                {
                    Rec("Door A", "components=[Transform] pos(0,0,0)", idCamera),
                    Rec("Door_A", "components=[Transform] pos(5,0,0)", idSquare)
                },
                scenePersistedAfterCapture: true,
                liveSettings: new List<BaselineSettingsRecord> { Setting("scene", "rootOrder", "roots=(Door_A,Door_A)") });
            BaselineAccept.Result guessed = BaselineAccept.Apply(doors, lookalike.newObjects);

            failures += Held("a place that would be a guess is not guessed",
                guessed.orderLeftAloneCount == 1 && doors.settings[0].state == "roots=(Door_A)");

            // A child: its place lives in the parent's record, which must change there and nowhere else.
            Baseline level = Baseline(
                Rec("Level", "components=[Transform] pos(0,0,0) children=(Floor,Wall)", idCamera),
                Rec("Level/Floor", "components=[Transform] pos(0,0,0)", idLight),
                Rec("Level/Wall", "components=[Transform] pos(0,2,0)", idWall));

            var liveLevel = new List<BaselineObjectRecord>
            {
                Rec("Level", "components=[Transform] pos(0,0,0) children=(Floor,Square,Wall)", idCamera),
                Rec("Level/Floor", "components=[Transform] pos(0,0,0)", idLight),
                Rec("Level/Square", "components=[Transform,SpriteRenderer] pos(2,0,0)", idSquare),
                Rec("Level/Wall", "components=[Transform] pos(0,2,0)", idWall)
            };

            BaselineAccept.Apply(level, RegressionCheck.Compare(level, liveLevel, scenePersistedAfterCapture: true).newObjects);

            failures += Held("adopting a child records its place in the parent's child list, and only that",
                level.objects.Any(o => o.path == "Level" &&
                                       o.state == "components=[Transform] pos(0,0,0) children=(Floor,Square,Wall)"));

            liveLevel[0] = Rec("Level", "components=[Transform] pos(0,0,0) children=(Square,Floor,Wall)", idCamera);
            BaselineComparison childDragged = RegressionCheck.Compare(level, liveLevel, scenePersistedAfterCapture: true);

            failures += Held("dragging an adopted child above its siblings is reported",
                childDragged.findings.Count == 1 && childDragged.findings[0].path == "Level");

            // A parent with ONE recorded child has no list at all (capture writes none below two),
            // so the first sibling to arrive has to create the list, where capture would put it.
            Baseline single = Baseline(
                Rec("Grid", "components=[Transform,Grid] pos(0,0,0) props:Grid(m_CellSize=1)", idCamera),
                Rec("Grid/Floor", "components=[Transform] pos(0,0,0)", idLight));

            var liveGrid = new List<BaselineObjectRecord>
            {
                Rec("Grid", "components=[Transform,Grid] pos(0,0,0) children=(Floor,Square) props:Grid(m_CellSize=1)", idCamera),
                Rec("Grid/Floor", "components=[Transform] pos(0,0,0)", idLight),
                Rec("Grid/Square", "components=[Transform,SpriteRenderer] pos(2,0,0)", idSquare)
            };

            BaselineAccept.Apply(single, RegressionCheck.Compare(single, liveGrid, scenePersistedAfterCapture: true).newObjects);

            failures += Held("a second child creates its parent's list exactly where capture writes it",
                RegressionCheck.Compare(single, liveGrid, scenePersistedAfterCapture: true).findings.Count == 0);

            return failures;
        }

        // ── …and the other way round: deleting, renaming, re-parenting ──────────
        //
        // The same two-places problem in reverse. Accepting a deletion dropped the record and left
        // the name in the list, so the list went on reporting the deletion the reviewer had just
        // accepted — as a second finding, on a different line, that they then had to find and tick.
        // The measure is simply: accept the one finding a human would tick, and nothing is left over.

        private static int CheckDeletedAndMovedObjectsLeaveTheirPlace()
        {
            int failures = 0;

            const string idCamera = "GlobalObjectId_V1-2-abc-111-0";
            const string idLight = "GlobalObjectId_V1-2-abc-222-0";
            const string idSquare = "GlobalObjectId_V1-2-abc-333-0";
            const string idWall = "GlobalObjectId_V1-2-abc-444-0";

            List<BaselineSettingsRecord> Roots(string state) =>
                new List<BaselineSettingsRecord> { Setting("scene", "rootOrder", state) };

            BaselineComparison Check(Baseline b, string roots, params BaselineObjectRecord[] live) =>
                RegressionCheck.Compare(b, live.ToList(), scenePersistedAfterCapture: true, liveSettings: Roots(roots));

            List<RegressionFinding> OnlyKind(BaselineComparison c, RegressionKind kind) =>
                c.findings.Where(f => f.kind == kind).ToList();

            BaselineObjectRecord camera = Rec("MainCamera", "components=[Transform,Camera] pos(0,1,-10)", idCamera);
            BaselineObjectRecord light = Rec("Light", "components=[Transform,Light] pos(0,3,0)", idLight);
            BaselineObjectRecord square = Rec("Square", "components=[Transform,SpriteRenderer] pos(2,0,0)", idSquare);

            // Accept rewrites records in place, so a baseline never shares one with a live list.
            BaselineObjectRecord Copy(BaselineObjectRecord r) => Rec(r.path, r.state, r.id);

            // A top-level object deleted.
            Baseline deleted = Baseline(Copy(camera), Copy(light), Copy(square));
            deleted.settings = Roots("roots=(MainCamera,Light,Square)");

            BaselineComparison gone = Check(deleted, "roots=(MainCamera,Light)", camera, light);
            BaselineAccept.Result removed = BaselineAccept.Apply(deleted, OnlyKind(gone, RegressionKind.Missing));

            failures += Held("accepting a deleted top-level object takes its name out of the root order",
                removed.orderLeftAloneCount == 0 && deleted.settings[0].state == "roots=(MainCamera,Light)");
            failures += Held("nothing is left over after accepting a deletion",
                Check(deleted, "roots=(MainCamera,Light)", camera, light).findings.Count == 0);

            // Same-named twins, both deleted in one accept: the second must still find its entry
            // after the first has already left the list.
            Baseline crates = Baseline(
                Rec("Crate", "components=[Transform] pos(0,0,0)", idCamera),
                Rec("Crate#2", "components=[Transform] pos(1,0,0)", idLight),
                Rec("Wall", "components=[Transform] pos(9,0,0)", idWall));
            crates.settings = Roots("roots=(Crate,Wall,Crate)");

            BaselineAccept.Result bothGone = BaselineAccept.Apply(crates,
                OnlyKind(Check(crates, "roots=(Wall)", Rec("Wall", "components=[Transform] pos(9,0,0)", idWall)),
                    RegressionKind.Missing));

            failures += Held("deleting both of two same-named objects removes both entries",
                bothGone.acceptedCount == 2 && bothGone.orderLeftAloneCount == 0 &&
                crates.settings[0].state == "roots=(Wall)");

            // A child deleted, down to one — where capture stops writing the list at all.
            Baseline pair = Baseline(
                Rec("Level", "components=[Transform] pos(0,0,0) children=(Floor,Wall) props:Grid(m_CellSize=1)", idCamera),
                Rec("Level/Floor", "components=[Transform] pos(0,0,0)", idLight),
                Rec("Level/Wall", "components=[Transform] pos(0,2,0)", idWall));

            var liveAfterDelete = new List<BaselineObjectRecord>
            {
                Rec("Level", "components=[Transform] pos(0,0,0) props:Grid(m_CellSize=1)", idCamera),
                Rec("Level/Floor", "components=[Transform] pos(0,0,0)", idLight)
            };

            BaselineComparison childGone = RegressionCheck.Compare(pair, liveAfterDelete, scenePersistedAfterCapture: true);
            BaselineAccept.Apply(pair, OnlyKind(childGone, RegressionKind.Missing));

            failures += Held("deleting a child is reported once for the child and once for its parent's list",
                childGone.findings.Count == 2);
            failures += Held("accepting only the child leaves nothing over, down to the list vanishing",
                RegressionCheck.Compare(pair, liveAfterDelete, scenePersistedAfterCapture: true).findings.Count == 0);

            // A rename is a move: the old name leaves the list and the new one takes its place.
            Baseline renamed = Baseline(Copy(camera), Copy(light));
            renamed.settings = Roots("roots=(MainCamera,Light)");

            BaselineObjectRecord sun = Rec("Sun", light.state, idLight);
            BaselineComparison renaming = Check(renamed, "roots=(MainCamera,Sun)", camera, sun);
            BaselineAccept.Apply(renamed, OnlyKind(renaming, RegressionKind.Moved));

            failures += Held("accepting a rename renames the entry in place",
                renamed.settings[0].state == "roots=(MainCamera,Sun)" &&
                Check(renamed, "roots=(MainCamera,Sun)", camera, sun).findings.Count == 0);

            // Re-parented: out of the root order, into the parent's list between its two children.
            Baseline reparented = Baseline(
                Rec("Level", "components=[Transform] pos(0,0,0) children=(Floor,Wall)", idCamera),
                Rec("Level/Floor", "components=[Transform] pos(0,0,0)", idLight),
                Rec("Level/Wall", "components=[Transform] pos(0,2,0)", idWall),
                Rec("Square", square.state, idSquare));
            reparented.settings = Roots("roots=(Level,Square)");

            BaselineObjectRecord[] liveNested =
            {
                Rec("Level", "components=[Transform] pos(0,0,0) children=(Floor,Square,Wall)", idCamera),
                Rec("Level/Floor", "components=[Transform] pos(0,0,0)", idLight),
                Rec("Level/Square", square.state, idSquare),
                Rec("Level/Wall", "components=[Transform] pos(0,2,0)", idWall)
            };

            BaselineAccept.Result moved = BaselineAccept.Apply(reparented,
                OnlyKind(Check(reparented, "roots=(Level)", liveNested), RegressionKind.Moved));

            failures += Held("accepting a re-parent moves the name from one list to the other",
                moved.acceptedCount == 1 && moved.orderLeftAloneCount == 0 &&
                reparented.settings[0].state == "roots=(Level)" &&
                reparented.objects.Any(o => o.path == "Level" &&
                                            o.state == "components=[Transform] pos(0,0,0) children=(Floor,Square,Wall)"));
            failures += Held("nothing is left over after accepting a re-parent",
                Check(reparented, "roots=(Level)", liveNested).findings.Count == 0);

            return failures;
        }

        // ── Can a baseline that needs recording actually be recorded? ────────────
        //
        // 🚨 The assertion that would have caught the 2026-08-11 defect. Dedup compared objects
        // only, so a user who reordered the Hierarchy — which changes no object's state — was
        // refused as a duplicate and could never record the baseline the tool was asking for.
        // Second time the tool demanded the one action it had made impossible.

        private static int CheckDedupCoversEverySection()
        {
            int failures = 0;

            const string idA = "GlobalObjectId_V1-2-abc-111-0";

            failures += Held("an identical candidate is a duplicate",
                BaselineStore.RecordsSameState(
                    WithSections(Setting("scene", "rootOrder", "roots=(A,B)")),
                    WithSections(Setting("scene", "rootOrder", "roots=(A,B)"))));

            failures += Held("the same objects with a changed setting is NOT a duplicate",
                !BaselineStore.RecordsSameState(
                    WithSections(Setting("scene", "rootOrder", "roots=(A,B)")),
                    WithSections(Setting("scene", "rootOrder", "roots=(B,A)"))));

            // Coverage the tool has only just learned to capture must be recordable without a
            // schema bump, or the new section can never reach a scene that already has a baseline.
            failures += Held("a section the stored baseline lacks is NOT a duplicate",
                !BaselineStore.RecordsSameState(
                    WithSections(),
                    WithSections(Setting("scene", "rootOrder", "roots=(A,B)"))));

            Baseline storedAsset = WithSections();
            storedAsset.assets = new List<BaselineAssetRecord>
            {
                new BaselineAssetRecord { path = "Assets/M.mat", type = "Material", state = "_Color=(1,1,1,1)" }
            };

            Baseline changedAsset = WithSections();
            changedAsset.assets = new List<BaselineAssetRecord>
            {
                new BaselineAssetRecord { path = "Assets/M.mat", type = "Material", state = "_Color=(1,0,0,1)" }
            };

            failures += Held("the same objects with a changed asset is NOT a duplicate",
                !BaselineStore.RecordsSameState(storedAsset, changedAsset));

            return failures;
        }

        private static BaselineSettingsRecord Setting(string scope, string group, string state) =>
            new BaselineSettingsRecord { scope = scope, group = group, state = state };

        /// <summary>A baseline with one fixed object, so only the sections under test can differ.</summary>
        private static Baseline WithSections(params BaselineSettingsRecord[] settings)
        {
            Baseline baseline = Baseline(Rec("Player", "speed=5", "GlobalObjectId_V1-2-abc-111-0"));
            baseline.settings = settings.ToList();
            baseline.assets = new List<BaselineAssetRecord>();
            return baseline;
        }

        // ── Does a path lead the reader to the right object? ────────────────────

        /// <remarks>
        /// Found on Boss Room, where 22 objects were named Imp. The sixteenth was recorded as
        /// "Entrance/Imp#15" and its own child as "Entrance/Imp/ImpGraphics#15" -- the parent segment
        /// rebuilt from the raw name, so the child read as hanging off the FIRST Imp. Paths stayed
        /// unique, so nothing failed loudly; 10.7% of that project's objects simply pointed the
        /// reader at the wrong place, and any attempt to group findings by subtree inherited the lie.
        /// </remarks>
        private static int CheckTwinChildrenHangFromTheTwin()
        {
            int failures = 0;

            var first = new GameObject("Twin");
            var second = new GameObject("Twin");
            var firstChild = new GameObject("Graphics");
            var secondChild = new GameObject("Graphics");

            try
            {
                firstChild.transform.SetParent(first.transform);
                secondChild.transform.SetParent(second.transform);

                SceneSnapshot snapshot = SceneCapture.CaptureSceneSnapshot();
                List<string> paths = snapshot.objects.Select(o => o.path).ToList();

                // The twins themselves: one plain, one suffixed. Unchanged behaviour, asserted so a
                // fix to the children cannot quietly rewrite the parents.
                failures += Held("one twin keeps the plain path", paths.Contains("Twin"));
                failures += Held("the other twin takes a suffix", paths.Contains("Twin#2"));

                // The point of the whole change: the suffix reaches the child.
                failures += Held("the second twin's child hangs from the SECOND twin",
                    paths.Contains("Twin#2/Graphics"));
                failures += Held("no child is recorded under a twin it does not belong to",
                    !paths.Contains("Twin/Graphics#2"));

                // A path is only worth printing if it leads back to the object it names.
                failures += Held("the recorded path resolves to the object it names",
                    SceneCapture.FindByRecordedPath("Twin#2/Graphics") == secondChild &&
                    SceneCapture.FindByRecordedPath("Twin/Graphics") == firstChild);

                // Every path must still be unique, or the record cannot hold two objects apart.
                failures += Held("every recorded path is unique",
                    paths.Count == paths.Distinct().Count());
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }

            return failures;
        }

        // ── Is a long report readable without becoming a dishonest one? ─────────

        /// <remarks>
        /// Measured on Boss Room: 3,022 findings, all true, and unreadable. 1,438 of 1,548 "missing"
        /// findings had a missing ANCESTOR -- a few deleted subtrees listed bone by bone -- and 597
        /// of 715 "changed" findings sat in clusters that changed the same property set. Grouping is
        /// presentation only: the count, the verdict, and every individual finding survive it.
        /// </remarks>
        private static int CheckFindingsGroupByCause()
        {
            int failures = 0;

            // A deleted subtree: a root, two children and a grandchild, plus one unrelated deletion.
            var subtree = new List<FindingRollup.Item>
            {
                RollupItem("missing", "Level/Enemy"),
                RollupItem("missing", "Level/Enemy/Graphics"),
                RollupItem("missing", "Level/Enemy/Graphics/Bone"),
                RollupItem("missing", "Level/Enemy/Collider"),
                RollupItem("missing", "Level/Torch")
            };

            List<FindingRollup.Group> groups = FindingRollup.Build(subtree);

            failures += Held("a deleted subtree is one group, and the unrelated deletion another",
                groups.Count == 2);
            failures += Held("the subtree groups under its topmost missing object",
                groups[0].Count == 4 && subtree[groups[0].Lead].Path == "Level/Enemy");
            failures += Held("an unrelated deletion is left alone",
                groups[1].Count == 1 && !groups[1].IsRolledUp);

            // A near-miss: a path that merely starts with the same TEXT is not a descendant.
            var lookalike = new List<FindingRollup.Item>
            {
                RollupItem("missing", "Level/Enemy"),
                RollupItem("missing", "Level/EnemySpawner")
            };
            failures += Held("a name that only shares a prefix is not swallowed as a child",
                FindingRollup.Build(lookalike).Count == 2);

            // Same-change clustering, at the threshold and one below it.
            failures += Held("five objects changing the same property are one group",
                FindingRollup.Build(ChangedItems(5, "pos")).Count == 1);
            failures += Held("four are still worth reading one by one",
                FindingRollup.Build(ChangedItems(4, "pos")).Count == 4);
            failures += Held("objects changing DIFFERENT properties are not grouped together",
                FindingRollup.Build(ChangedItems(5, "pos").Concat(ChangedItems(5, "mass")).ToList()).Count == 2);

            // A value carrying a colon must not become the cluster key, or nothing would cluster.
            failures += Held("the property name is read up to the FIRST colon",
                FindingRollup.KeyOf("Skill1: (Base) → (ImpBaseAttack:MeleeAction)") == "Skill1");

            // The property the whole feature rests on: grouping is not filtering.
            List<FindingRollup.Item> mixed = subtree.Concat(ChangedItems(6, "pos")).ToList();
            List<FindingRollup.Group> mixedGroups = FindingRollup.Build(mixed);

            failures += Held("every finding lands in exactly one group",
                mixedGroups.Sum(g => g.Count) == mixed.Count &&
                mixedGroups.SelectMany(g => g.Members).Distinct().Count() == mixed.Count);
            failures += Held("grouping shortened the report",
                mixedGroups.Count < mixed.Count);

            return failures;
        }

        /// <remarks>
        /// The roll-up is only worth having if the rendering it feeds is honest. A grouped report
        /// that quietly stopped printing members would look like a triumph -- 936 findings rendered
        /// as 45 lines -- while hiding 891 real regressions, which is the one failure this tool
        /// cannot survive. So this asserts on the RENDERED TEXT: the total is stated, the group is
        /// named, members are printed, and nothing claims a smaller number than was found.
        /// </remarks>
        private static int CheckGroupedReportPrintsEveryFinding()
        {
            int failures = 0;

            var recorded = new List<BaselineObjectRecord>
            {
                Rec("Level/Enemy", "state=1", "id-enemy"),
                Rec("Level/Enemy/Graphics", "state=1", "id-graphics"),
                Rec("Level/Enemy/Graphics/Bone", "state=1", "id-bone"),
                Rec("Level/Keep", "state=1", "id-keep")
            };

            // The whole subtree is gone; the unrelated object is untouched.
            var live = new List<BaselineObjectRecord> { Rec("Level/Keep", "state=1", "id-keep") };

            BaselineComparison comparison = CompareObjects(recorded, live);
            string rendered = RegressionCheck.DescribeComparison(comparison);

            failures += Held("the subtree is still three findings, not one",
                comparison.findings.Count == 3);

            failures += Held("the report states the true finding count before grouping them",
                rendered.Contains("3 finding(s), grouped into 1"));

            failures += Held("the group's root is named in full",
                rendered.Contains("MISSING  Level/Enemy"));

            failures += Held("the group says how many went with it",
                rendered.Contains("and 2 more"));

            // The members themselves, printed. Without this the count above is an unverifiable claim.
            failures += Held("the members are named, not merely counted",
                rendered.Contains("Level/Enemy/Graphics") &&
                rendered.Contains("Level/Enemy/Graphics/Bone"));

            // A clean scene must not grow a grouping line it has no use for.
            string clean = RegressionCheck.DescribeComparison(CompareObjects(
                new List<BaselineObjectRecord> { Rec("Level/Keep", "state=1", "id-keep") },
                new List<BaselineObjectRecord> { Rec("Level/Keep", "state=1", "id-keep") }));

            failures += Held("a clean comparison says nothing about grouping",
                !clean.Contains("grouped into"));

            // The markdown is the rendering a reviewer actually reads on a pull request, and it
            // groups through the same code by a different path. Asserted separately because "both
            // renderers agree" is exactly the kind of claim that stops being true silently.
            // Filled in the way a real run leaves it: RenderMarkdown is always handed a finished
            // report and reads the run-wide fields before it reaches a single finding.
            var report = new RegressionReport.Report
            {
                verdict = "regressions",
                totalFindings = comparison.findings.Count,
                scenesChecked = 1,
                baselinesCompared = 1
            };
            var scene = new RegressionReport.ReportScene { sceneName = "Level", scenePath = "Assets/Level.unity" };
            var entry = new RegressionReport.ReportBaseline { id = "b1", verdict = "regressions" };

            foreach (RegressionFinding finding in comparison.findings)
            {
                entry.findings.Add(new RegressionReport.ReportFinding
                {
                    path = finding.path,
                    kind = "missing",
                    changes = new List<string>()
                });
            }

            scene.baselines.Add(entry);
            report.scenes.Add(scene);

            string markdown = RegressionReport.RenderMarkdown(report);

            failures += Held("the markdown states the true count before grouping",
                markdown.Contains("3 findings, grouped into 1"));
            failures += Held("the markdown names the group's root",
                markdown.Contains("Level/Enemy"));
            failures += Held("the markdown names the members it grouped",
                markdown.Contains("Level/Enemy/Graphics") &&
                markdown.Contains("Level/Enemy/Graphics/Bone"));
            failures += Held("the markdown says how many went with the root",
                markdown.Contains("and 2 more"));

            return failures;
        }

        /// <remarks>
        /// Two shapes the first cut of the roll-up left alone, both found by READING a real report
        /// rather than by testing it: 43 materials each printing one identical line, and 22 sibling
        /// Imps printing one line apiece because each is the topmost missing object on its own
        /// branch. Measured on the corpus at 632 and 36 findings respectively before they were
        /// grouped -- the second is small, and it was the most repetitive thing on the page.
        /// </remarks>
        private static int CheckRepetitionTheSubtreeRuleCannotReach()
        {
            int failures = 0;

            // One shader edit landing on every material: the shape assets actually arrive in.
            var materials = new List<FindingRollup.Item>();
            for (int i = 0; i < 5; i++)
            {
                materials.Add(RollupItem("asset-changed", "Assets/FX_" + i + ".mat",
                    "renderQueue: 3000 -> 2000"));
            }

            failures += Held("five assets changing the same property are one group",
                FindingRollup.Build(materials).Count == 1);

            // The kind is part of the cause. One line covering both would name an event that
            // neither a material nor a GameObject went through.
            List<FindingRollup.Item> both = materials.Concat(ChangedItems(5, "renderQueue")).ToList();
            failures += Held("assets and objects changing the same property stay separate groups",
                FindingRollup.Build(both).Count == 2);

            // Same-named siblings deleted together. Each is its own topmost missing object, so the
            // subtree rule sees five roots and leaves every one of them on a line of its own.
            var siblings = new List<FindingRollup.Item>
            {
                RollupItem("missing", "Level/Spawners/Imp"),
                RollupItem("missing", "Level/Spawners/Imp#2"),
                RollupItem("missing", "Level/Spawners/Imp#3"),
                RollupItem("missing", "Level/Spawners/Imp#4"),
                RollupItem("missing", "Level/Spawners/Imp#5")
            };

            List<FindingRollup.Group> siblingGroups = FindingRollup.Build(siblings);

            failures += Held("five same-named siblings deleted together are one group",
                siblingGroups.Count == 1 && siblingGroups[0].Count == 5);
            failures += Held("the group names what they were, not the tool's own #n",
                siblingGroups[0].Cause == "all named Imp under the same parent");
            failures += Held("four siblings are still worth reading one by one",
                FindingRollup.Build(siblings.Take(4).ToList()).Count == 4);

            // Different parents are different deletions that happen to share a name.
            var scattered = new List<FindingRollup.Item>
            {
                RollupItem("missing", "Level/A/Imp"),
                RollupItem("missing", "Level/B/Imp"),
                RollupItem("missing", "Level/C/Imp"),
                RollupItem("missing", "Level/D/Imp"),
                RollupItem("missing", "Level/E/Imp")
            };
            failures += Held("same-named objects under different parents are not one group",
                FindingRollup.Build(scattered).Count == 5);

            // A sibling whose own subtree went with it belongs to the subtree, which is the more
            // specific claim. Nothing may be claimed twice, whichever rule got there first.
            List<FindingRollup.Item> mixed = siblings
                .Concat(new[] { RollupItem("missing", "Level/Spawners/Imp/Graphics") }).ToList();
            List<FindingRollup.Group> mixedGroups = FindingRollup.Build(mixed);

            failures += Held("every finding still lands in exactly one group",
                mixedGroups.Sum(g => g.Count) == mixed.Count &&
                mixedGroups.SelectMany(g => g.Members).Distinct().Count() == mixed.Count);

            return failures;
        }

        /// <remarks>
        /// The cap is the one part of this that takes lines off the page, so it is the one part
        /// that could turn the report into a lie. A ScriptableObject refactor reported 74 changed
        /// fields on a single asset, and a scene of 33 findings took 1,567 lines; showing eight of
        /// those 74 silently would understate the very thing the tool exists to report. Both
        /// renderings are asserted, because "they agree" stops being true quietly.
        /// </remarks>
        private static int CheckLongFindingsAreCappedHonestly()
        {
            int failures = 0;

            const int Changes = 12;
            int hidden = Changes - FindingRollup.ChangesShown;

            var before = new System.Text.StringBuilder();
            var after = new System.Text.StringBuilder();
            for (int i = 0; i < Changes; i++)
            {
                before.Append("p").Append(i).Append("(0) ");
                after.Append("p").Append(i).Append("(1) ");
            }

            BaselineComparison comparison = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Level/Obj", before.ToString().Trim(), "id-obj") },
                new List<BaselineObjectRecord> { Rec("Level/Obj", after.ToString().Trim(), "id-obj") });

            failures += Held("the finding still carries every change it found",
                comparison.findings.Count == 1 &&
                comparison.findings[0].changedSegments.Count == Changes);

            string console = RegressionCheck.DescribeComparison(comparison);

            failures += Held("the console prints changes up to the cap",
                CountOccurrences(console, "p0:") == 1 &&
                CountOccurrences(console, "p" + (FindingRollup.ChangesShown - 1) + ":") == 1);
            failures += Held("the console does not print past the cap",
                !console.Contains("p" + FindingRollup.ChangesShown + ":"));
            failures += Held("the console says how many it held back",
                console.Contains("and " + hidden + " more change(s) on this one"));

            var report = new RegressionReport.Report
            {
                verdict = "regressions",
                totalFindings = 1,
                scenesChecked = 1,
                baselinesCompared = 1
            };
            var scene = new RegressionReport.ReportScene
            {
                sceneName = "Level",
                scenePath = "Assets/Level.unity"
            };
            var entry = new RegressionReport.ReportBaseline { id = "b1", verdict = "regressions" };
            entry.findings.Add(new RegressionReport.ReportFinding
            {
                path = "Level/Obj",
                kind = "changed",
                changes = comparison.findings[0].changedSegments
            });
            scene.baselines.Add(entry);
            report.scenes.Add(scene);

            string markdown = RegressionReport.RenderMarkdown(report);

            failures += Held("the markdown does not print past the cap",
                !markdown.Contains("p" + FindingRollup.ChangesShown + ":"));
            failures += Held("the markdown says how many it held back",
                markdown.Contains("and " + hidden + " more change(s) on this one"));

            // A finding that fits says nothing about holding anything back, or every ordinary
            // report would carry a line implying there is more to see.
            BaselineComparison whole = CompareObjects(
                new List<BaselineObjectRecord> { Rec("Level/Obj", "p0(0) p1(0)", "id-obj") },
                new List<BaselineObjectRecord> { Rec("Level/Obj", "p0(1) p1(1)", "id-obj") });

            failures += Held("a short finding carries no held-back line",
                !RegressionCheck.DescribeComparison(whole).Contains("more change(s)"));

            return failures;
        }

        /// <summary>How many times a needle appears, for assertions that count rendered lines.</summary>
        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;

            for (int i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <remarks>
        /// A replay sets project-wide settings findings aside, which is the ONLY filtering anything
        /// in this package does, so it is the one place where a silent mistake would look like good
        /// news. The reason it is allowed at all is measured: Unity reads project settings once per
        /// session instead of importing them, so after a checkout the editor's tags could still be a
        /// different commit's — on Boss Room a README-only commit reported two tag findings for a tag
        /// that exists in no commit being replayed.
        ///
        /// What must stay true: only project-scope settings go, scene settings and object findings
        /// stay, the counts left behind agree with the findings left behind, and a scene emptied by
        /// the filter stops calling itself a regression.
        /// </remarks>
        private static int CheckReplaySetsAsideOnlyProjectSettings()
        {
            int failures = 0;

            var report = new RegressionReport.Report
            {
                verdict = "regressions",
                totalFindings = 4,
                scenesChecked = 2,
                baselinesCompared = 2
            };

            var mixed = new RegressionReport.ReportScene
            {
                sceneName = "Level",
                scenePath = "Assets/Level.unity",
                verdict = "regressions",
                headline = "REGRESSIONS FOUND — 3 across 1 of 1 baseline(s).",
                findingCount = 3,
                baselinesCompared = 1
            };

            var mixedBaseline = new RegressionReport.ReportBaseline { id = "b1", verdict = "regressions" };
            mixedBaseline.findings.Add(Finding("changed", "Level/Enemy"));
            mixedBaseline.findings.Add(Finding("settings-changed", "project/tags"));
            mixedBaseline.findings.Add(Finding("settings-changed", "scene/render"));
            mixed.baselines.Add(mixedBaseline);

            var settingsOnly = new RegressionReport.ReportScene
            {
                sceneName = "Menu",
                scenePath = "Assets/Menu.unity",
                verdict = "regressions",
                headline = "REGRESSIONS FOUND — 1 across 1 of 1 baseline(s).",
                findingCount = 1,
                baselinesCompared = 1
            };

            var settingsBaseline = new RegressionReport.ReportBaseline { id = "b2", verdict = "regressions" };
            settingsBaseline.findings.Add(Finding("settings-changed", "project/physics"));
            settingsOnly.baselines.Add(settingsBaseline);

            report.scenes.Add(mixed);
            report.scenes.Add(settingsOnly);

            int setAside = InvokeSetAsideProjectSettings(report);

            failures += Held("both project-scope settings findings are set aside",
                setAside == 2);
            failures += Held("an object finding is untouched",
                mixedBaseline.findings.Any(f => f.kind == "changed" && f.path == "Level/Enemy"));
            failures += Held("a SCENE-scope setting is untouched -- it lives in the scene file",
                mixedBaseline.findings.Any(f => f.path == "scene/render"));
            failures += Held("no project-scope setting survives",
                !report.scenes.SelectMany(s => s.baselines).SelectMany(b => b.findings)
                    .Any(f => f.path != null && f.path.StartsWith("project/")));

            // The counts and the findings must tell the same story, or the report is lying twice.
            failures += Held("the total matches what is actually left",
                report.totalFindings == 2 &&
                report.scenes.Sum(s => s.baselines.Sum(b => b.findings.Count)) == report.totalFindings);
            failures += Held("the scene that kept findings still counts them correctly",
                mixed.findingCount == 2 && mixed.verdict == "regressions");
            failures += Held("a scene emptied by the filter stops claiming regressions",
                settingsOnly.findingCount == 0 &&
                settingsOnly.verdict == "pass" &&
                settingsOnly.headline.StartsWith("PASS") &&
                settingsBaseline.verdict == "pass");

            // A report with nothing left must not still be a failing report.
            var onlySettings = new RegressionReport.Report
            {
                verdict = "regressions", totalFindings = 1, scenesChecked = 1, baselinesCompared = 1
            };
            var lone = new RegressionReport.ReportScene
            {
                sceneName = "Solo", verdict = "regressions", findingCount = 1, baselinesCompared = 1
            };
            var loneBaseline = new RegressionReport.ReportBaseline { id = "b3", verdict = "regressions" };
            loneBaseline.findings.Add(Finding("settings-changed", "project/tags"));
            lone.baselines.Add(loneBaseline);
            onlySettings.scenes.Add(lone);

            InvokeSetAsideProjectSettings(onlySettings);

            failures += Held("a report left with nothing is a pass, not a regression",
                onlySettings.totalFindings == 0 && onlySettings.verdict == "pass");

            return failures;
        }

        /// <remarks>
        /// Scope is the only thing in a replay report that is neither a finding nor a count of
        /// findings: it is the commit's own size, put beside the scenes' movement so a reader can
        /// tell "a big refactor moved a lot" from "two files moved a lot", which no list of
        /// findings can say. Two properties are load-bearing and neither is obvious from reading
        /// the sentence. Objects are counted ONCE however many findings they carry — the number
        /// answers "how far did this reach", not "how much is there to read", which is the
        /// roll-up's job. And a count git could not supply is not a zero: a merge lists no files
        /// of its own, and "0 file(s) changed" would be a measurement rather than an absence.
        ///
        /// The last assertion is the one to keep if the others ever go: adding context must not
        /// remove a finding. Every other part of this tool prints its count before its groups for
        /// the same reason, and scope is the first thing here that could quietly break it.
        /// </remarks>
        private static int CheckCommitScopeIsStated()
        {
            int failures = 0;

            var report = new RegressionReport.Report { verdict = "regressions", totalFindings = 5 };

            var level = new RegressionReport.ReportScene
            {
                sceneName = "Level",
                scenePath = "Assets/Level.unity",
                verdict = "regressions",
                findingCount = 4,
                baselinesCompared = 1
            };

            var levelBaseline = new RegressionReport.ReportBaseline { id = "b1", verdict = "regressions" };
            levelBaseline.findings.Add(Finding("changed", "Level/Enemy"));   // one object,
            levelBaseline.findings.Add(Finding("changed", "Level/Enemy"));   // two findings
            levelBaseline.findings.Add(Finding("asset-changed", "Assets/Enemy.mat"));
            levelBaseline.findings.Add(Finding("settings-changed", "scene/render"));
            level.baselines.Add(levelBaseline);

            var menu = new RegressionReport.ReportScene
            {
                sceneName = "Menu",
                scenePath = "Assets/Menu.unity",
                verdict = "regressions",
                findingCount = 1,
                baselinesCompared = 1
            };

            // The same path in another scene is another object, not the same one seen twice.
            var menuBaseline = new RegressionReport.ReportBaseline { id = "b2", verdict = "regressions" };
            menuBaseline.findings.Add(Finding("changed", "Level/Enemy"));
            menu.baselines.Add(menuBaseline);

            report.scenes.Add(level);
            report.scenes.Add(menu);

            var plan = new HistoryReplay.Plan { index = 0 };
            plan.fileCounts.Add(2);
            plan.dependencyFileCounts.Add(1);

            string line = InvokeScopeLine(plan, report);

            failures += Held("the line says how far the commit reached",
                line != null && line.Contains("2 file(s) changed"));
            failures += Held("and how much of that the scenes depend on",
                line != null && line.Contains("1 of them"));
            failures += Held("an object counts once however many findings it carries",
                line != null && line.Contains("2 object(s)"));
            failures += Held("assets are counted, and counted separately",
                line != null && line.Contains("1 asset(s)"));
            failures += Held("a setting is not a place a commit reached",
                line != null && !line.Contains("3 object(s)"));

            failures += Held("stating scope removes no finding",
                report.totalFindings == 5 &&
                report.scenes.Sum(s => s.baselines.Sum(b => b.findings.Count)) == 5);

            // -1 is the private UnknownCount: what git could not answer.
            var unknown = new HistoryReplay.Plan { index = 0 };
            unknown.fileCounts.Add(-1);
            unknown.dependencyFileCounts.Add(-1);

            string withoutCounts = InvokeScopeLine(unknown, report);

            failures += Held("an unknown file count is not reported as none",
                withoutCounts != null && !withoutCounts.Contains("file(s) changed"));
            failures += Held("and what did move is still said",
                withoutCounts != null && withoutCounts.Contains("2 object(s)"));

            return failures;
        }

        /// <remarks>
        /// A first-time reader met three dense block quotes before a single finding: what a replay
        /// does not compare, how commits were chosen, and what a finding is and is not. All three
        /// are owed to them and none was removed — they moved to the end. This asserts the shape
        /// that change bought, and, more importantly, that the move did not quietly lose one: a
        /// caveat deleted and a caveat relocated look identical from the top of the file.
        ///
        /// The one thing that stays at the top is how the commits were chosen, because it decides
        /// what the whole page MEANS. A reader who believes these are simply the last N commits
        /// reads "no findings" as "nothing happened in the last N commits", which is a far stronger
        /// claim than the report is making.
        /// </remarks>
        private static int CheckReplayReportLeadsWithFindings()
        {
            int failures = 0;

            string folder = Path.Combine(Path.GetTempPath(), "SceneBaselinesReport-" + System.Guid.NewGuid().ToString("N"));
            string reportPath = Path.Combine(folder, "history-replay.md");

            var plan = new HistoryReplay.Plan
            {
                reportPath = reportPath,
                filtered = true,
                scanned = 52,
                skipped = 42,
                closureSize = 1010,
                commitsReported = 1,
                findings = 3
            };

            plan.commits.Add("abc1234");
            plan.parents.Add("def5678");
            plan.subjects.Add("a subject");
            plan.scenes.Add("Assets/Scenes/Level.unity");

            try
            {
                InvokeBeginReport(plan);
                string header = File.ReadAllText(reportPath);

                failures += Held("the header does not open with the caveats",
                    !header.Contains("Project-wide settings are not compared") &&
                    !header.Contains("Objects added"));
                failures += Held("but it does say how the commits were chosen, which decides what the page means",
                    header.Contains("52") && header.Contains("42"));
                failures += Held("and it says where the rest of it went",
                    header.Contains("at the end"));

                // Whatever a real run would stream between the header and the tail.
                File.AppendAllText(reportPath, "\n## `abc1234` a subject\n\n- **CHANGED** Level/Enemy\n");

                InvokeFinishReport(plan, "complete");
                string whole = File.ReadAllText(reportPath);

                int finding = whole.IndexOf("**CHANGED** Level/Enemy", System.StringComparison.Ordinal);
                int howToRead = whole.IndexOf("## How to read this", System.StringComparison.Ordinal);
                int settings = whole.IndexOf("Project-wide settings are not compared", System.StringComparison.Ordinal);
                int chosen = whole.IndexOf("How the 1 commit(s) were chosen", System.StringComparison.Ordinal);

                failures += Held("the findings come before the notes about them",
                    finding >= 0 && howToRead > finding);

                // Relocated, not deleted -- which is the failure this test exists to catch.
                failures += Held("the project-settings caveat survived the move",
                    settings > howToRead);
                failures += Held("the full explanation of how commits were chosen survived it too",
                    chosen > howToRead);
                failures += Held("and so did what a finding is and is not",
                    whole.IndexOf("not necessarily a mistake", System.StringComparison.Ordinal) > howToRead);

                failures += Held("the run's own outcome is still stated",
                    whole.Contains("1 of 1 commit(s) reported, 3 finding(s) in total"));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }

            return failures;
        }

        private static RegressionReport.ReportFinding Finding(string kind, string path)
        {
            return new RegressionReport.ReportFinding { kind = kind, path = path };
        }

        /// <remarks>
        /// The review window can now be narrowed, and a filter is the most dangerous thing this
        /// tool has shipped: a check whose whole promise is "you will be told" acquiring a control
        /// that stops it telling you. So the assertions below are in two halves.
        ///
        /// The first half is that every filter is a FACT the finding already carries — its kind, its
        /// path, the named segments of state that differ. Nothing is inferred, nothing is scored,
        /// and there is no filter for "real regressions": that needs a classifier of intent which
        /// cannot exist, and hiding by it would fail worse than mis-ordering by it.
        ///
        /// The second half is the one that keeps the promise: NOTHING GOES MISSING QUIETLY. An
        /// empty filter is the identity, a filter only ever removes, and — the property the window
        /// leans on hardest — every finding that names a segment is reachable through a menu entry,
        /// because the menu is built out of the findings themselves rather than out of a list of
        /// categories someone wrote down. A category list is exactly how a difference ends up in no
        /// bucket at all and disappears from a window that still says it is showing everything.
        /// </remarks>
        private static int CheckReviewFilterNarrowsOnFacts()
        {
            int failures = 0;

            var moved = new RegressionFinding
            {
                path = "Level/Enemies/Grunt",
                livePath = "Level/Pool/Grunt",
                kind = RegressionKind.Moved,
                changedSegments = new List<string> { "pos: (0.0,0.0,0.0) → (3.0,0.0,0.0)" }
            };

            var missing = new RegressionFinding
            {
                path = "Level/Enemies/Boss",
                kind = RegressionKind.Missing
            };

            var trigger = new RegressionFinding
            {
                path = "Level/Walls/Wall",
                kind = RegressionKind.Changed,
                changedSegments = new List<string>
                {
                    "props:BoxCollider.isTrigger: false → true",
                    "props:BoxCollider.size: (1.0,1.0,1.0) → (2.0,1.0,1.0)"
                }
            };

            var stretched = new RegressionFinding
            {
                path = "Level/Walls/Pillar",
                kind = RegressionKind.Changed,
                changedSegments = new List<string>
                {
                    "scale: (1.0,1.0,1.0) → (1.0,4.0,1.0)",
                    "bounds.center: (0.0,0.5,0.0) → (0.0,2.0,0.0)"
                }
            };

            var all = new List<RegressionFinding> { moved, missing, trigger, stretched };
            var filter = new ReviewFilter();

            // ── An empty filter is the window as it was ──────────────────────────
            failures += Held("nothing chosen hides nothing",
                !filter.IsActive && filter.Apply(all).SequenceEqual(all));

            // ── Kind: a fact on every finding ────────────────────────────────────
            filter.kind = RegressionKind.Missing;
            List<RegressionFinding> onlyMissing = filter.Apply(all);

            failures += Held("choosing a kind shows that kind", onlyMissing.Count == 1 && onlyMissing[0] == missing);
            failures += Held("and choosing one is hiding something", filter.IsActive);

            filter.Clear();
            failures += Held("clearing puts everything back", !filter.IsActive && filter.Apply(all).Count == all.Count);

            // ── Detail: the segment the difference is actually under ─────────────
            filter.detail = "props:BoxCollider";
            List<RegressionFinding> onlyCollider = filter.Apply(all);

            failures += Held("a component's properties are one choice, not one per property",
                onlyCollider.Count == 1 && onlyCollider[0] == trigger);

            filter.detail = "bounds";
            failures += Held("a narrowed field is filed under the segment it came from",
                filter.Apply(all).SequenceEqual(new List<RegressionFinding> { stretched }));

            filter.Clear();

            // The keys themselves, since the menu is nothing but these.
            failures += Held("a repeated segment is not a second entry",
                ReviewFilter.SegmentKey("props:Sprite#2.color: white → red") == "props:Sprite");
            failures += Held("a plain segment keeps its own name",
                ReviewFilter.SegmentKey("pos: (0.0,0.0,0.0) → (3.0,0.0,0.0)") == "pos");
            failures += Held("a component reads as its properties in a menu",
                ReviewFilter.DescribeDetail("props:BoxCollider") == "BoxCollider properties");
            failures += Held("and anything else is spelled as the report spells it",
                ReviewFilter.DescribeDetail("children") == "children");

            // ── Path: what the reviewer types ────────────────────────────────────
            filter.path = "  walls  ";
            List<RegressionFinding> onlyWalls = filter.Apply(all);

            failures += Held("a path search ignores case and stray spaces",
                onlyWalls.Count == 2 && onlyWalls.Contains(trigger) && onlyWalls.Contains(stretched));

            filter.path = "Pool";
            List<RegressionFinding> byWhereItWent = filter.Apply(all);

            failures += Held("a moved object is findable where it went, not only where it was",
                byWhereItWent.Count == 1 && byWhereItWent[0] == moved);

            filter.Clear();

            // ── Nothing goes missing quietly ─────────────────────────────────────
            List<KeyValuePair<string, int>> keys = ReviewFilter.DetailKeys(all);

            failures += Held("the menu is built out of the differences themselves",
                keys.Any(k => k.Key == "pos") && keys.Any(k => k.Key == "props:BoxCollider") &&
                keys.Any(k => k.Key == "scale") && keys.Any(k => k.Key == "bounds"));

            failures += Held("a finding differing in two segments counts once under each, not half",
                keys.First(k => k.Key == "scale").Value == 1 &&
                keys.First(k => k.Key == "bounds").Value == 1);

            failures += Held("no menu entry is offered that would come back empty",
                keys.All(k => new ReviewFilter { detail = k.Key }.Apply(all).Count == k.Value));

            // The property the window's honesty rests on: no difference falls outside the menu.
            failures += Held("every finding that names a segment is reachable from the menu",
                all.Where(f => f.changedSegments.Count > 0)
                   .All(f => keys.Any(k => new ReviewFilter { detail = k.Key }.Apply(all).Contains(f))));

            // And the one that cannot be: a finding with nothing named under it is excluded by any
            // segment choice, which is why the window keeps printing the unfiltered count as well.
            failures += Held("a finding with no named difference is not silently filed somewhere",
                keys.All(k => !new ReviewFilter { detail = k.Key }.Apply(all).Contains(missing)));

            failures += Held("a filter can only ever remove",
                new ReviewFilter { kind = RegressionKind.Changed, path = "Wall" }
                    .Apply(all).All(all.Contains));

            return failures;
        }

        /// <remarks>
        /// Which commits get replayed is the difference between a tool that works on a toy project
        /// and one that works on a real repository, where the scenes are touched rarely and most
        /// commits cannot have changed them at all. The two possible mistakes are not symmetric:
        /// replaying a commit that changed nothing costs a minute, while skipping one that did
        /// costs a finding, silently, in the one place a reader will never think to look. Every
        /// rule asserted here bends the same way for that reason.
        ///
        /// The case that matters most is the commit that touches a PREFAB and no scene file at all:
        /// the scene's own diff is empty, and catching it anyway is the entire point of the tool.
        /// </remarks>
        private static int CheckCommitsAreChosenByWhatTheScenesDependOn()
        {
            int failures = 0;

            const string Record = "\u0001";
            const string Field = "\u001f";

            // Exactly what `log --format=%x01%H%x1f%P%x1f%s --name-only` hands back.
            string log =
                Record + "aaaa1111" + Field + "pppp1111" + Field + "Tidy up the docs\n" +
                "Docs/README.md\n" +
                "Docs/images/logo.png\n" +
                Record + "bbbb2222" + Field + "pppp2222" + Field + "Nudge the enemy prefab\n" +
                "Assets/Prefabs/Enemy.prefab\n" +
                Record + "cccc3333" + Field + "dddd4444 eeee5555" + Field + "Merge branch 'art'\n" +
                Record + "dddd4444" + Field + "pppp4444" + Field + "Re-import the prefab\n" +
                "Assets/Prefabs/Enemy.prefab.meta\n" +
                Record + "eeee5555" + Field + "pppp5555" + Field + "Rename a level\n" +
                "\"Assets/Sc\\303\\250nes/Level.unity\"\n" +
                Record + "ffff6666" + Field + Field + "The first commit\n" +
                "Assets/Level.unity\n";

            List<HistoryReplay.CommitRecord> records = InvokeParseLog(log);
            if (records == null)
                return 1;

            failures += Held("every commit is read, including one that lists no files",
                records.Count == 6);

            failures += Held("a merge is compared against its FIRST parent",
                records.Count > 2 && records[2].parent == "dddd4444");

            failures += Held("a merge lists no files of its own",
                records.Count > 2 && records[2].files.Count == 0);

            failures += Held("the first commit in a repository has no parent",
                records.Count > 5 && string.IsNullOrEmpty(records[5].parent));

            failures += Held("a subject survives being read",
                records.Count > 1 && records[1].subject == "Nudge the enemy prefab");

            var closure = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "Assets/Level.unity",
                "Assets/Level.unity.meta",
                "Assets/Prefabs/Enemy.prefab",
                "Assets/Prefabs/Enemy.prefab.meta"
            };

            int scanned, skipped;
            List<HistoryReplay.CommitRecord> chosen =
                InvokeSelectCommits(records, closure, 10, out scanned, out skipped);

            if (chosen == null)
                return failures + 1;

            failures += Held("a commit that touched only docs is not replayed",
                chosen.All(c => c.sha != "aaaa1111"));

            // The feature, in one assertion.
            failures += Held("a commit that touched a PREFAB is replayed, though no scene file changed",
                chosen.Any(c => c.sha == "bbbb2222"));

            failures += Held("a commit that touched only a .meta is replayed -- import settings " +
                             "change what a capture sees",
                chosen.Any(c => c.sha == "dddd4444"));

            failures += Held("a path git had to quote keeps its commit rather than risking a finding",
                chosen.Any(c => c.sha == "eeee5555"));

            failures += Held("a merge is not replayed -- the commits it merged are in the same walk",
                chosen.All(c => c.sha != "cccc3333"));

            failures += Held("the first commit in a repository is not replayed -- nothing to compare with",
                chosen.All(c => c.sha != "ffff6666"));

            failures += Held("the counts say what happened: six looked at, two set aside, three chosen",
                scanned == 6 && skipped == 2 && chosen.Count == 3);

            failures += Held("the commits stay newest first",
                chosen.Count == 3 && chosen[0].sha == "bbbb2222" && chosen[2].sha == "eeee5555");

            // Stopping early is the whole reason the scan window is safe to make large.
            chosen = InvokeSelectCommits(records, closure, 1, out scanned, out skipped);

            failures += Held("asking for one commit stops at the newest one worth replaying",
                chosen != null && chosen.Count == 1 && chosen[0].sha == "bbbb2222" && scanned == 2);

            // What -replayFilter off promises: no judgement about what a commit could have changed.
            chosen = InvokeSelectCommits(records, null, 10, out scanned, out skipped);

            failures += Held("without a dependency list every commit with a parent is replayed",
                chosen != null && chosen.Count == 5 && skipped == 0);

            return failures;
        }

        /// <summary>Reads a commit log the way the replay does. Deliberately not public API.</summary>
        private static List<HistoryReplay.CommitRecord> InvokeParseLog(string log)
        {
            MethodInfo method = typeof(HistoryReplay).GetMethod("ParseLog",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Held("HistoryReplay still has a ParseLog to test", false);
                return null;
            }

            return (List<HistoryReplay.CommitRecord>)method.Invoke(null, new object[] { log });
        }

        /// <summary>Calls the replay's commit chooser. Deliberately not public API.</summary>
        private static List<HistoryReplay.CommitRecord> InvokeSelectCommits(
            List<HistoryReplay.CommitRecord> records, HashSet<string> closure, int count,
            out int scanned, out int skipped)
        {
            scanned = 0;
            skipped = 0;

            MethodInfo method = typeof(HistoryReplay).GetMethod("SelectCommits",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Held("HistoryReplay still has a SelectCommits to test", false);
                return null;
            }

            object[] arguments = { records, closure, count, 0, 0 };
            var chosen = (List<HistoryReplay.CommitRecord>)method.Invoke(null, arguments);

            scanned = (int)arguments[3];
            skipped = (int)arguments[4];

            return chosen;
        }

        /// <summary>Calls the replay's private filter, which is deliberately not public API.</summary>
        private static int InvokeSetAsideProjectSettings(RegressionReport.Report report)
        {
            MethodInfo method = typeof(HistoryReplay).GetMethod("SetAsideProjectSettings",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Held("HistoryReplay still has a SetAsideProjectSettings to test", false);
                return -1;
            }

            return (int)method.Invoke(null, new object[] { report });
        }

        private static void InvokeBeginReport(HistoryReplay.Plan plan)
        {
            MethodInfo method = typeof(HistoryReplay).GetMethod("BeginReport",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Held("HistoryReplay still has a BeginReport to test", false);
                return;
            }

            method.Invoke(null, new object[] { plan });
        }

        private static void InvokeFinishReport(HistoryReplay.Plan plan, string reason)
        {
            MethodInfo method = typeof(HistoryReplay).GetMethod("FinishReport",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Held("HistoryReplay still has a FinishReport to test", false);
                return;
            }

            method.Invoke(null, new object[] { plan, reason });
        }

        private static string InvokeScopeLine(HistoryReplay.Plan plan, RegressionReport.Report report)
        {
            MethodInfo method = typeof(HistoryReplay).GetMethod("ScopeLine",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Held("HistoryReplay still has a ScopeLine to test", false);
                return null;
            }

            return (string)method.Invoke(null, new object[] { plan, report });
        }

        private static FindingRollup.Item RollupItem(string kind, string path, params string[] changes)
        {
            return new FindingRollup.Item(kind, path, changes);
        }

        private static List<FindingRollup.Item> ChangedItems(int count, string property)
        {
            var items = new List<FindingRollup.Item>();
            for (int i = 0; i < count; i++)
            {
                // Different VALUES on purpose: objects that each moved somewhere different still had
                // the same thing happen to them, and that is what the group is claiming.
                items.Add(RollupItem("changed", "Level/Prop" + i, property + ": 0 → " + i));
            }

            return items;
        }

        // ── Does the record hold still when nothing was changed? ─────────────────

        /// <remarks>
        /// Guards the single largest source of invented findings ever measured here. Replaying 15
        /// BossRoom commits on 2026-09-04 produced 3,022 findings, and 621 of them — a fifth of
        /// everything the tool said — were materials reporting "renderQueue: 3000 -> 2000" at
        /// commits that touched no .mat file at all. The same 41 materials said it at EVERY commit,
        /// because Material.renderQueue is computed from the shader and resolves differently
        /// depending on how far shader import has progressed. Recording the stored override instead
        /// is what makes the number evidence rather than weather.
        /// </remarks>
        private static int CheckRenderQueueRecordsTheOverride()
        {
            int failures = 0;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
            {
                Debug.Log("[Scene Baselines] SKIPPED: render queue — no stock shader to build a " +
                    "material from on this render pipeline.");
                return 0;
            }

            // In memory only: never written to the project, so no asset is created or modified.
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                string inherited = SceneCapture.DescribeAsset(material);

                failures += Held("a material that does not override its queue records that it does not",
                    inherited.Contains("renderQueue=from-shader"));

                // The heart of it: the resolved number must not reach the record at all, because
                // that is the value which moves on its own between two runs over the same project.
                failures += Held("the shader-resolved queue never reaches the record",
                    !inherited.Contains("renderQueue=" + material.renderQueue));

                material.renderQueue = 2500;
                string overridden = SceneCapture.DescribeAsset(material);

                failures += Held("an explicit queue override is recorded",
                    overridden.Contains("renderQueue=2500"));

                failures += Held("starting to override the queue changes the record",
                    overridden != inherited);

                BaselineComparison comparison = CompareAssetStates(inherited, overridden);
                failures += Held("overriding the queue is one asset finding naming renderQueue",
                    comparison.findings.Count == 1 &&
                    comparison.findings[0].kind == RegressionKind.AssetChanged &&
                    comparison.findings[0].changedSegments.Any(segment => segment.StartsWith("renderQueue")));

                material.renderQueue = -1;
                failures += Held("clearing the override returns the record to from-shader",
                    SceneCapture.DescribeAsset(material).Contains("renderQueue=from-shader"));

                // The upgrade itself must not become the noise. Baselines already on disk hold the
                // resolved NUMBER, so comparing them against a capture that now records the override
                // would accuse every material in the project at the first check after updating —
                // exactly what this change exists to stop.
                string asRecordedBefore = "shader=\"Probe\" renderQueue=3000";
                string asRecordedNow = "shader=\"Probe\" renderQueue=from-shader";

                failures += Held("an older baseline's assets are set aside, not compared",
                    CompareAssetStates(asRecordedBefore, asRecordedNow,
                        BaselineStore.AssetStateFormatSchemaVersion - 1).findings.Count == 0);

                // ...and the gate must be a migration, not a mute: at the current schema the same
                // pair is still a finding, or the section would have gone quiet for everyone.
                failures += Held("assets recorded at the current schema still compare",
                    CompareAssetStates(asRecordedBefore, asRecordedNow).findings.Count == 1);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }

            return failures;
        }

        /// <summary>
        /// A run must be able to leave the project it inspects completely untouched.
        /// </summary>
        /// <remarks>
        /// This is the guarantee that makes it safe to point the tool at a repository that is not
        /// ours. Replaying another team's history checks out commit after commit, and a baseline
        /// written under their Assets/ would both dirty their working tree and be destroyed by the
        /// next checkout — so the folder has to be movable outside the project entirely.
        ///
        /// The assertions are deliberately about the FILESYSTEM rather than about return values.
        /// The failure being guarded is a file appearing where nobody asked for one, and only
        /// looking on disk can see that; a function can return the right path and still have
        /// written somewhere else on the way.
        /// </remarks>
        private static int CheckBaselinesCanBeWrittenOutsideTheProject()
        {
            int failures = 0;

            const string id = "outside-the-project-test";

            string outside = Path.Combine(Path.GetTempPath(),
                "SceneBaselinesOutside-" + System.Guid.NewGuid().ToString("N"));

            string projectFolder = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                BaselineStore.DefaultBaselineFolder);

            int filesInProjectBefore = CountJsonFiles(projectFolder);
            string originalOverride = BaselineStore.FolderOverride;

            try
            {
                BaselineStore.FolderOverride = outside;

                Baseline baseline = Baseline(Rec("Player", "speed=5", "GlobalObjectId_V1-2-abc-111-0"));
                baseline.id = id;
                baseline.scenePath = "Assets/Scenes/NotOurs.unity";

                string written = BaselineStore.Save(baseline);

                failures += Held("a baseline saves to the folder it was pointed at",
                    File.Exists(Path.Combine(outside, id + ".json")));

                failures += Held("the returned path is the file that was actually written",
                    written != null && File.Exists(written));

                // Outside the project there is no such thing as an asset path, and every
                // AssetDatabase call is guarded on this returning null.
                failures += Held("a baseline outside the project is not treated as an asset",
                    BaselineStore.AssetPathFor(id) == null);

                failures += Held("it reads back from where it was written",
                    BaselineStore.LoadAll().Any(b => b.id == id));

                failures += Held("nothing was written into the project",
                    CountJsonFiles(projectFolder) == filesInProjectBefore);
            }
            finally
            {
                BaselineStore.FolderOverride = originalOverride;

                try { if (Directory.Exists(outside)) Directory.Delete(outside, true); }
                catch { /* a leftover temp folder is not worth failing a test over */ }
            }

            // A run that pointed the folder elsewhere must not leave the editor writing there for
            // the rest of the session.
            failures += Held("clearing the override restores the project folder",
                originalOverride != null ||
                BaselineStore.BaselineFolder == BaselineStore.DefaultBaselineFolder);

            return failures;
        }

        private static int CountJsonFiles(string folder) =>
            Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Length : 0;

        private static BaselineObjectRecord Rec(string path, string state, string id) =>
            new BaselineObjectRecord { path = path, state = state, id = id };

        private static BaselineComparison CompareObjects(
            List<BaselineObjectRecord> recorded, List<BaselineObjectRecord> live)
        {
            var baseline = new Baseline
            {
                schemaVersion = BaselineStore.SchemaVersion,
                objects = recorded
            };

            return RegressionCheck.Compare(baseline, live, scenePersistedAfterCapture: true);
        }

        // ── Is a capture repeatable? ─────────────────────────────────────────────
        //
        // This product is bought for what it says at a MERGE, which means two machines must describe
        // one unchanged scene the same way. Everything else here asks whether a capture is right;
        // these two ask whether it is the SAME right answer twice, which is a separate property and
        // was not covered by a single assertion until 2026-09-01.
        //
        // Both guard defects that were measured on this project, not imagined:
        //
        //   reopen — the scene sweep used FindObjectsSortMode.None, which Unity documents as
        //     unspecified and which really does vary: closing and reopening an untouched Level_01
        //     returned its five objects in a different order, so the same scene on the same machine
        //     serialised to a different baseline. No finding was ever wrong (objects match on
        //     identity, then path; root order is read from GetRootGameObjects) — the damage was a
        //     committed JSON file that churned for a scene nobody had edited, and two branches that
        //     conflicted over changes neither had made.
        //
        //   locale — floats are formatted through InvariantCulture, so a developer on a comma-decimal
        //     machine should not produce "0,5" and diff against the whole team. That held when it was
        //     first measured. It is asserted here because it is the kind of property that is true
        //     until someone adds one field with a bare ToString(), and then silently is not.
        //
        // Neither test writes a baseline, creates an object, or leaves the editor changed.

        /// <summary>
        /// Everything a baseline would record, flattened into one comparable string.
        /// </summary>
        /// <remarks>
        /// Deliberately the same three calls a real recording makes, rather than a convenient subset:
        /// a determinism test that only looked at objects would have passed while settings or assets
        /// drifted underneath it.
        /// </remarks>
        private static string CanonicalCapture()
        {
            var sb = new System.Text.StringBuilder();

            foreach (BaselineObjectRecord record in SceneCapture.CaptureBaselineObjects())
                sb.Append("obj|").Append(record.path).Append('|')
                  .Append(record.state).Append('|').Append(record.id).Append('\n');

            foreach (SceneCapture.SceneAssetRecord asset in SceneCapture.CaptureReferencedAssets().assets)
                sb.Append("asset|").Append(asset.path).Append('|')
                  .Append(asset.type).Append('|').Append(asset.state).Append('\n');

            foreach (SceneCapture.SceneSettingsRecord setting in SceneCapture.CaptureSettings())
                sb.Append("setting|").Append(setting.scope).Append('|')
                  .Append(setting.group).Append('|').Append(setting.state).Append('\n');

            return sb.ToString();
        }

        private static int CheckCaptureIsStableAcrossReopen()
        {
            Scene active = SceneManager.GetActiveScene();

            // Reopening is the whole method, so this cannot run against an untitled scene, and must
            // never run against unsaved work — reopening would silently discard it.
            if (!active.IsValid() || string.IsNullOrEmpty(active.path) || active.isDirty)
            {
                Debug.Log("[Scene Baselines] SKIPPED: capture stability across a reopen — needs a " +
                          "saved scene with no unsaved changes, because it reloads the scene.");
                return 0;
            }

            int failures = 0;
            string scenePath = active.path;

            string first = CanonicalCapture();

            // A reopen, rather than two captures in a row. In-session the sweep returns a stable
            // order even when the order is not guaranteed, so back-to-back captures agreed while the
            // real defect was still there — the reopen is what a second machine actually does.
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            string second = CanonicalCapture();

            failures += Held("a capture of an unchanged scene is byte-identical after reopening it",
                first == second);

            // The property a user would state: reopening a scene is not a change, so it must not read
            // as one. Only meaningful where the scene actually has baselines to compare against.
            RegressionRunResult reopened = RegressionCheck.RunForActiveScene();
            if (reopened.comparisons.Count > 0)
            {
                failures += Held("reopening an unchanged scene produces no findings",
                    reopened.comparisons.Sum(comparison => comparison.findings.Count) == 0);
            }

            return failures;
        }

        private static int CheckCaptureDoesNotDependOnLocale()
        {
            int failures = 0;

            System.Threading.Thread thread = System.Threading.Thread.CurrentThread;
            System.Globalization.CultureInfo originalCulture = thread.CurrentCulture;
            System.Globalization.CultureInfo originalUICulture = thread.CurrentUICulture;

            string asFound = CanonicalCapture();

            try
            {
                // de-DE for the decimal comma, tr-TR because it also swaps the casing of "i" — the
                // two ways a machine's locale usually reaches code that never asked for one.
                foreach (string cultureName in new[] { "de-DE", "tr-TR" })
                {
                    var culture = new System.Globalization.CultureInfo(cultureName);
                    thread.CurrentCulture = culture;
                    thread.CurrentUICulture = culture;

                    // Without this the test can pass for the wrong reason: if the culture never took
                    // effect, the capture below is trivially identical and asserts nothing at all.
                    failures += Held($"{cultureName} is really in force, so the next check means something",
                        (0.5f).ToString() == "0,5");

                    failures += Held($"a capture is byte-identical under {cultureName}",
                        CanonicalCapture() == asFound);
                }
            }
            finally
            {
                // In a finally because leaving the editor in a foreign culture would be a far worse
                // bug than the one under test, and would outlive this run.
                thread.CurrentCulture = originalCulture;
                thread.CurrentUICulture = originalUICulture;
            }

            return failures;
        }

        private static string Capture(GameObject go) => SceneCapture.DescribeObjectState(go);

        /// <summary>
        /// A throwaway object that never touches the open scene.
        /// </summary>
        /// <remarks>
        /// HideAndDontSave keeps the user's scene clean and undirtied — a free test that silently
        /// modified the scene it ran in would be a worse bug than the one it guards against.
        /// </remarks>
        private static GameObject New(List<GameObject> created, string name)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            created.Add(go);
            return go;
        }

        /// <summary>
        /// Records one assertion. Returns a count rather than throwing, so a failure does not hide
        /// the assertions after it — <see cref="Verify"/> reports the whole collected list at once.
        /// </summary>
        private static int Held(string what, bool held)
        {
            if (held)
                return 0;

            Failures.Add(what);
            Debug.LogWarning($"[Scene Baselines] FAILED: {what}");
            return 1;
        }

        private static readonly List<string> Failures = new List<string>();
    }
}
