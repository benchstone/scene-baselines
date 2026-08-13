using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBaselines
{
    // ─── Regression baselines: recording by hand ─────────────────────────────────
    //
    // Until now a baseline could only be filed by a passing verification, which meant the
    // regression feature was unreachable without running (and paying for) the agent. That
    // is backwards for the free package: a studio's first question is "record what my scene
    // looks like now", and the honest answer had to be "first buy a model call".
    //
    // Deliberately free of any LLM call, like the check and the report it feeds. Recording
    // is a read of the open scene plus a file write; nothing here needs judgement, and
    // anything that asked a model for one would make the free package cost money.
    //
    // The grade travels with the record. A hand-recorded baseline proves the scene WAS a
    // certain way; it never claims the scene was CORRECT, because nothing checked. Blurring
    // those two rebuilds the false confidence this whole layer exists to remove — see
    // Baseline.recordedBy.

    public static class RecordBaseline
    {
        public enum Outcome
        {
            /// <summary>A new baseline was written.</summary>
            Recorded,

            /// <summary>The scene already matches its latest baseline, so nothing was filed.</summary>
            Duplicate,

            /// <summary>The scene has never been saved, so it has no identity to attach a record to.</summary>
            SceneNeverSaved,

            /// <summary>No objects were captured — an empty scene records nothing useful.</summary>
            NothingToRecord,

            /// <summary>Storage refused the write.</summary>
            WriteFailed
        }

        public class Result
        {
            public Outcome outcome;
            public string message;
            public string assetPath;
            public Baseline baseline;

            public bool Succeeded => outcome == Outcome.Recorded;
        }

        [MenuItem("Scene Baselines/Record Baseline for Open Scene")]
        private static void RecordFromMenu()
        {
            Scene scene = SceneManager.GetActiveScene();

            // Offer the save rather than doing it silently: writing a user's scene to disk
            // because they clicked "record" is a bigger action than they asked for, and a
            // baseline captured from unsaved work can only ever report "unconfirmed" later.
            if (scene.isDirty && !string.IsNullOrEmpty(scene.path) && !Application.isBatchMode)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Record baseline",
                    $"'{scene.name}' has unsaved changes.\n\n" +
                    "A baseline captured from an unsaved scene records state that never reached " +
                    "disk, so later checks can only call differences unconfirmed — they cannot " +
                    "prove a regression.",
                    "Save, then record", "Cancel", "Record anyway");

                if (choice == 1)
                    return;

                if (choice == 0)
                    EditorSceneManager.SaveScene(scene);
            }

            Result result = Record();

            if (result.outcome == Outcome.Recorded)
                Debug.Log("[Scene Baselines] " + result.message);
            else
                Debug.LogWarning("[Scene Baselines] " + result.message);

            // Batch mode has no one to read a dialog, and blocking CI on one would hang the run.
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Record baseline", result.message, "OK");
        }

        /// <summary>
        /// Records the open scene as a hand-made baseline. Never throws and never modifies the
        /// scene: callable from a menu item, a test, or -batchmode CI.
        /// </summary>
        public static Result Record()
        {
            Scene scene = SceneManager.GetActiveScene();

            // An unsaved scene has no asset path, so a baseline against it could never be looked
            // up again — and would not be committed with the project either.
            if (string.IsNullOrEmpty(scene.path))
            {
                return new Result
                {
                    outcome = Outcome.SceneNeverSaved,
                    message = "No baseline recorded: this scene has never been saved, so there is " +
                              "no stable identity to attach a known-good state to. Save the scene first."
                };
            }

            var objects = SceneCapture.CaptureBaselineObjects();

            if (objects.Count == 0)
            {
                return new Result
                {
                    outcome = Outcome.NothingToRecord,
                    message = $"No baseline recorded: '{scene.name}' contains no objects to record."
                };
            }

            var baseline = new Baseline
            {
                createdUtc = BaselineStore.CreateTimestampUtc(),
                unityVersion = Application.unityVersion,
                sceneName = scene.name,
                scenePath = scene.path,
                recordedBy = BaselineStore.ManualProvenance,

                // Left empty on purpose. originalRequest means "what was being built when this
                // became known-good", and nothing was being built here — inventing a description
                // would be the record claiming provenance it does not have.
                originalRequest = "",

                capturedFromUnsavedScene = scene.isDirty,
                objects = objects
            };

            SceneCapture.SceneAssetCapture assets = SceneCapture.CaptureReferencedAssets();
            baseline.assets = assets.assets
                .Select(a => new BaselineAssetRecord { path = a.path, type = a.type, state = a.state })
                .ToList();
            baseline.uncheckedAssetCount = assets.notChecked;

            baseline.settings = SceneCapture.CaptureSettings()
                .Select(s => new BaselineSettingsRecord { scope = s.scope, group = s.group, state = s.state })
                .ToList();

            baseline.otherLoadedScenes = SceneCapture.OtherLoadedScenes();

            // Same rule as the verified path: near-duplicates bury the baselines that mark real
            // milestones, and a silent skip is indistinguishable from a broken write.
            //
            // Checked here rather than before the capture above, because a duplicate is only
            // knowable once the WHOLE candidate exists: settings and assets can differ while every
            // object is byte-identical, and deciding from objects alone refused recordings the user
            // urgently needed.
            if (BaselineStore.MatchesLatestForScene(baseline))
            {
                return new Result
                {
                    outcome = Outcome.Duplicate,
                    message = $"No baseline recorded: '{scene.name}' already matches its latest " +
                              "baseline in every recorded object, setting and asset, so no new one " +
                              "was filed."
                };
            }

            string assetPath = BaselineStore.Save(baseline);

            if (string.IsNullOrEmpty(assetPath))
            {
                return new Result
                {
                    outcome = Outcome.WriteFailed,
                    baseline = baseline,
                    message = "Baseline could not be written — see the console for the storage error."
                };
            }

            string caveat = scene.isDirty
                ? " Captured from an unsaved scene, so differences against it will be reported as " +
                  "unconfirmed until the scene is saved."
                : "";

            return new Result
            {
                outcome = Outcome.Recorded,
                baseline = baseline,
                assetPath = assetPath,
                message = $"Baseline recorded for '{scene.name}' — {objects.Count} object(s) at " +
                          $"{assetPath}. This records what the scene WAS, not that it is correct: " +
                          $"nothing verified it." + caveat
            };
        }
    }
}
