using System.Collections.Generic;
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

        // ── A scene of the suite's own ───────────────────────────────────────────
        //
        // Three groups need a real, SAVED scene. The additive-scene group cannot even start without
        // one, because Unity refuses to add a scene alongside one that has never been saved; the
        // root-order group has nothing to describe in an empty scene; and the reopen group has
        // nothing to reopen.
        //
        // That used to mean they were SKIPPED under the Test Runner, which opens a fresh untitled
        // scene for the run — so the menu covered them and CI did not. Three of nineteen groups, and
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
                // GetInstanceID rather than GetEntityId: the latter only exists on very recent
                // Unity 6 editors, and this assertion only needs SOME id that the capture must
                // never contain. GetInstanceID exists on every supported version.
                !state.Contains(target.GetInstanceID().ToString()));
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

            return failures;
        }

        private static BaselineComparison CompareAssetStates(string recorded, string live)
        {
            var baseline = new Baseline
            {
                schemaVersion = BaselineStore.SchemaVersion,
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
