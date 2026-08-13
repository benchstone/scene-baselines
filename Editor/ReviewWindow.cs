using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SceneBaselines
{
    // ─── Regression baselines: reviewing findings (step 4 of 4) ───────────────────
    //
    // The half that turns a printed diff into a decision. Recording, checking and accepting all
    // existed as code before this; what did not exist was anywhere for a human to stand while
    // judging — the console prints a wall of text with no way to act on any single line of it.
    //
    // Two things here are the whole pitch, and both are deliberately cheap:
    //
    //   SELECT — one click from "this object changed" to that object highlighted in the Hierarchy.
    //   The customer problem is not the bug, it is the hours spent hunting for what changed; this is
    //   the shortest possible path from a finding to the thing itself.
    //
    //   ACCEPT — per finding, never all-or-nothing. Re-recording the scene would adopt every other
    //   difference in it too, including the ones nobody read.
    //
    // A scene accumulates baselines, and the check compares against all of them. Listing one section
    // per baseline was the first thing a real user got wrong: the same moved object was reported
    // three times, the oldest section led with a two-day-old date, and it was dismissed as stale
    // data. So the newest comparable baseline is the headline — it is what the scene is supposed to
    // match — repeats collapse into a count on it, and older baselines get a section only for what
    // they alone still catch. Nothing is dropped; it is said once.
    //
    // 🚨 No Reject button, on purpose: walking away already rejects. Nothing is written, the check
    // stays red, and it stays red until the scene actually matches. Silence must mean "not accepted",
    // because the alternative is a button whose only use is to silence a finding without fixing it.
    //
    // No model call anywhere: this is the free product's review surface.

    public class ReviewWindow : EditorWindow
    {
        private RegressionRunResult result;
        private Vector2 scroll;

        // Reference identity, so a checkbox follows its finding without needing an index that a
        // re-check would invalidate.
        private readonly HashSet<RegressionFinding> selected = new HashSet<RegressionFinding>();

        [MenuItem("Scene Baselines/Review Findings")]
        public static void Open()
        {
            ReviewWindow window = GetWindow<ReviewWindow>("Baseline Review");
            window.minSize = new Vector2(520f, 320f);
            window.Recheck();
        }

        private void Recheck()
        {
            result = RegressionCheck.RunForActiveScene();
            selected.Clear();
        }

        private void OnGUI()
        {
            DrawToolbar();

            // Findings do not survive a domain reload, and inventing them back would be worse than
            // asking: a stale finding accepted as intentional writes a state nobody looked at.
            if (result == null)
            {
                EditorGUILayout.HelpBox(
                    "Press Re-check to compare the open scene against its baselines.",
                    MessageType.Info);
                return;
            }

            if (result.sceneWasUnsaved)
            {
                EditorGUILayout.HelpBox(
                    "This scene has never been saved, so it has no identity to look baselines up by. " +
                    "Save it first.", MessageType.Warning);
                return;
            }

            if (!result.HasBaselines)
            {
                EditorGUILayout.HelpBox(
                    "No comparable baseline for this scene. Record one with " +
                    "Scene Baselines ▸ Record Baseline for Open Scene.\n\n" +
                    "Note this is NOT a pass: nothing was compared.", MessageType.Warning);
                return;
            }

            // Newest first, which is the order BaselineStore.LoadForScene guarantees. The order is
            // load-bearing here: the newest comparable baseline is the one the reviewer is being
            // asked to judge against, and everything else is context.
            List<BaselineComparison> comparable = result.comparisons.Where(c => c.IsComparable).ToList();
            List<BaselineComparison> incomparable = result.comparisons.Where(c => !c.IsComparable).ToList();

            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (comparable.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No baseline for this scene can be compared — see below. Nothing was checked, " +
                    "which is NOT a pass.", MessageType.Warning);
            }
            else
            {
                List<BaselineComparison> older = comparable.Skip(1).ToList();

                DrawCurrent(comparable[0], older);
                DrawOnlyInOlder(comparable[0], older);
            }

            DrawIncomparable(incomparable);

            EditorGUILayout.EndScrollView();

            DrawAcceptBar();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Re-check", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                Recheck();

            GUILayout.FlexibleSpace();

            if (result != null)
            {
                GUILayout.Label(result.TotalFindings == 0
                    ? $"{result.sceneName} — nothing differs"
                    : $"{result.sceneName} — {result.TotalFindings} finding(s)");
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// The newest comparable baseline: the state the scene is currently supposed to match.
        /// </summary>
        private void DrawCurrent(BaselineComparison current, List<BaselineComparison> older)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                "CURRENT KNOWN-GOOD — recorded " + current.baseline?.DescribeAge(),
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField("   " + current.baseline?.id, EditorStyles.miniLabel);

            DrawCaveats(current);

            if (current.findings.Count == 0)
            {
                EditorGUILayout.LabelField("   everything this baseline records still holds");
                return;
            }

            foreach (RegressionFinding finding in current.findings)
                DrawFinding(finding, EchoCount(finding, older));
        }

        /// <summary>
        /// Findings that only OLDER baselines report — the ones the current known-good has stopped
        /// protecting, because it was recorded when they were already true.
        /// </summary>
        /// <remarks>
        /// This section is the reason the check compares against every baseline instead of only the
        /// newest, and it earned its place on the first real use: re-recording a scene that had
        /// already lost an object adopted "that object does not exist" as known-good, and only a
        /// two-day-old baseline still knew otherwise. Under a newest-only check that regression
        /// would have disappeared silently, which is the one outcome this tool may not produce.
        ///
        /// Repeats are folded into the current section as a count instead of being restated here.
        /// Saying the same thing once per baseline is how a report teaches its reader to skim it.
        /// </remarks>
        private void DrawOnlyInOlder(BaselineComparison current, List<BaselineComparison> older)
        {
            if (older.Count == 0)
                return;

            var currentKeys = new HashSet<string>(current.findings.Select(Key));
            var shown = new HashSet<string>();
            var extras = new List<KeyValuePair<BaselineComparison, RegressionFinding>>();

            foreach (BaselineComparison comparison in older)
            {
                foreach (RegressionFinding finding in comparison.findings)
                {
                    string key = Key(finding);

                    // Newest-first order means the first baseline to report something is the most
                    // recent one that still knows about it — the most useful one to name.
                    if (currentKeys.Contains(key) || !shown.Add(key))
                        continue;

                    extras.Add(new KeyValuePair<BaselineComparison, RegressionFinding>(comparison, finding));
                }
            }

            if (extras.Count == 0)
                return;

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField(
                $"ALSO BROKEN AGAINST OLDER BASELINES — {extras.Count} finding(s)",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "The current known-good does not report these: it was recorded when they were " +
                "already true, so it cannot see them. An older baseline still can.",
                EditorStyles.wordWrappedMiniLabel);

            foreach (KeyValuePair<BaselineComparison, RegressionFinding> extra in extras)
            {
                DrawFinding(extra.Value, 0);
                EditorGUILayout.LabelField(
                    "      from " + extra.Key.baseline?.DescribeAge(), EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// Baselines too old in format to compare, as one line. They are still stated: a baseline
        /// that was silently ignored looks exactly like a baseline that passed.
        /// </summary>
        private void DrawIncomparable(List<BaselineComparison> incomparable)
        {
            if (incomparable.Count == 0)
                return;

            EditorGUILayout.Space(10f);
            EditorGUILayout.HelpBox(
                $"{incomparable.Count} older baseline(s) NOT COMPARED — " +
                incomparable[0].incomparableReason,
                MessageType.Info);
        }

        // Every caveat the console report states is stated here too. A reviewer deciding what is
        // intentional needs to know how much this record is worth BEFORE they start ticking boxes.
        private void DrawCaveats(BaselineComparison comparison)
        {
            if (comparison.baseline?.IsManuallyRecorded == true)
                EditorGUILayout.LabelField("   recorded by hand — captures what the scene WAS, not that it was correct");

            if (comparison.baseline?.HasAcceptedFindings == true)
                EditorGUILayout.LabelField($"   {comparison.baseline.acceptedFindingCount} finding(s) " +
                    "accepted into this baseline since it was recorded");

            if (comparison.baseline?.RecordsObjectIds != true)
                EditorGUILayout.LabelField("   matched by hierarchy path only — a renamed or " +
                    "re-parented object shows as MISSING");

            if (!comparison.stateReachedDisk)
            {
                EditorGUILayout.HelpBox(
                    "Cannot prove breakage: " + comparison.untrustworthyReason +
                    ". Accepting from here records state that never reached disk.",
                    MessageType.Warning);
            }

            if (comparison.newObjectCount > 0)
            {
                EditorGUILayout.LabelField($"   {comparison.newObjectCount} object(s) added since — " +
                    "not regressions, and NOT covered by this baseline until it is re-recorded");

                // Named as well as counted: a count cannot be checked against what the reviewer
                // thinks they built, which is the only way to notice one they did not.
                EditorGUILayout.LabelField(
                    "      " + RegressionCheck.DescribeAddedPaths(comparison.newObjectPaths),
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>How many OLDER baselines report the same thing as this finding.</summary>
        private static int EchoCount(RegressionFinding finding, List<BaselineComparison> older)
        {
            string key = Key(finding);
            return older.Count(c => c.findings.Any(f => string.Equals(Key(f), key, StringComparison.Ordinal)));
        }

        /// <summary>
        /// What makes two findings "the same finding" across baselines: the kind of problem and the
        /// object it is about. Deliberately NOT the recorded state — two baselines can disagree
        /// about what an object used to be while agreeing completely about what is wrong with it now.
        /// </summary>
        private static string Key(RegressionFinding finding)
        {
            return finding.kind + "|" + (finding.path ?? "");
        }

        private void DrawFinding(RegressionFinding finding, int echoCount)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool wasSelected = selected.Contains(finding);
            bool nowSelected = EditorGUILayout.ToggleLeft(
                $"{KindLabel(finding.kind)}  {finding.path}", wasSelected);

            if (nowSelected != wasSelected)
                SetSelected(finding, nowSelected);

            // Disabled rather than hidden for the kinds that have nothing to show: a button that
            // silently does nothing is worse than one that visibly cannot.
            using (new EditorGUI.DisabledScope(!CanSelectInScene(finding)))
            {
                if (GUILayout.Button("Select", GUILayout.Width(60f)))
                    SelectInScene(finding);
            }

            EditorGUILayout.EndHorizontal();

            if (echoCount > 0)
            {
                EditorGUILayout.LabelField(
                    $"      also differs from {echoCount} older baseline(s) — accepting covers all of them",
                    EditorStyles.miniLabel);
            }

            if (finding.kind == RegressionKind.Moved)
                EditorGUILayout.LabelField($"      now at {finding.livePath}");

            if (finding.kind == RegressionKind.Missing)
                EditorGUILayout.LabelField("      recorded as known-good, not in the scene now");

            if (finding.changedSegments.Count > 0)
            {
                foreach (string segment in finding.changedSegments)
                    EditorGUILayout.LabelField("      " + segment, EditorStyles.wordWrappedMiniLabel);
            }
            else if (!string.Equals(finding.baselineState, finding.liveState, StringComparison.Ordinal))
            {
                // Segment diffing does not always find a difference it can name, and a finding with
                // nothing under it is unactionable — the reviewer is told something changed and given
                // no way to see what. The console report has always fallen back to the raw states;
                // this window must too, or it is the weaker surface of the two.
                EditorGUILayout.LabelField("      was:  " + Shorten(finding.baselineState),
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("      now:  " + Shorten(finding.liveState),
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Ticks a finding and every twin of it in the other baselines.
        /// </summary>
        /// <remarks>
        /// A reviewer judges a CHANGE, not a row in a table: "the pickup was meant to move" is true
        /// against every baseline that disagrees about where it sits. Without this, accepting the
        /// visible finding would leave its twins unaccepted, and the next check would surface the
        /// same change again from an older baseline — the tool arguing with a decision the user
        /// already made, which is how a review surface loses trust.
        /// </remarks>
        private void SetSelected(RegressionFinding finding, bool value)
        {
            string key = Key(finding);

            foreach (BaselineComparison comparison in result.comparisons)
            {
                foreach (RegressionFinding candidate in comparison.findings)
                {
                    if (!string.Equals(Key(candidate), key, StringComparison.Ordinal))
                        continue;

                    if (value)
                        selected.Add(candidate);
                    else
                        selected.Remove(candidate);
                }
            }
        }

        /// <summary>
        /// Distinct CHANGES ticked, not rows. The button must count what the user thinks they
        /// selected; "Accept 3" after ticking one box reads as the tool doing something extra.
        /// </summary>
        private int SelectedChangeCount => selected.Select(Key).Distinct().Count();

        private void DrawAcceptBar()
        {
            EditorGUILayout.Space(4f);

            // Stated next to the button rather than buried in docs, because it is the one thing about
            // this window a user could get badly wrong.
            EditorGUILayout.LabelField(
                "Accepting records the current state as known-good. It never changes your scene — " +
                "use undo or version control to put something back.", EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUI.DisabledScope(selected.Count == 0))
            {
                if (GUILayout.Button($"Accept {SelectedChangeCount} checked as intentional",
                        GUILayout.Height(26f)))
                {
                    AcceptSelected();
                }
            }
        }

        private void AcceptSelected()
        {
            int accepted = 0;
            int skipped = 0;
            int changes = SelectedChangeCount;
            int baselinesAmended = 0;
            var messages = new List<string>();

            // Per baseline, because each baseline is its own file and its own known-good record.
            foreach (BaselineComparison comparison in result.comparisons)
            {
                List<RegressionFinding> mine = comparison.findings
                    .Where(f => selected.Contains(f))
                    .ToList();

                if (mine.Count == 0)
                    continue;

                BaselineAccept.Result outcome =
                    BaselineAccept.Accept(comparison.baseline, mine);

                accepted += outcome.acceptedCount;
                skipped += outcome.skippedCount;
                baselinesAmended++;
                messages.Add(outcome.message);
            }

            foreach (string message in messages)
                Debug.Log("[Scene Baselines] " + message);

            // Re-checked immediately so the window shows the consequence of the decision rather than
            // the state that led to it — an accepted finding must visibly stop being a finding.
            Recheck();

            // Counted in changes rather than in records rewritten, because one change accepted across
            // three baselines is still one decision the user made.
            EditorUtility.DisplayDialog("Accept findings",
                accepted == 0
                    ? "Nothing was accepted — see the console for why."
                    : $"Accepted {changes} change(s) as intentional, across {baselinesAmended} baseline(s)." +
                      (skipped > 0 ? $" {skipped} could not be matched and were left alone." : "") +
                      "\n\nYour scene was not modified.",
                "OK");
        }

        // ── Getting to the object ────────────────────────────────────────────────

        private static bool CanSelectInScene(RegressionFinding finding)
        {
            switch (finding.kind)
            {
                // Nothing to select: the object is gone, which is what the finding says.
                case RegressionKind.Missing:
                    return false;

                // A setting belongs to no object — sending the user to the Hierarchy would mislead.
                case RegressionKind.SettingsChanged:
                    return false;

                default:
                    return true;
            }
        }

        private static void SelectInScene(RegressionFinding finding)
        {
            if (finding.kind == RegressionKind.AssetChanged)
            {
                // Sub-assets are recorded as "path::name"; the file is what can be pinged.
                string assetPath = (finding.path ?? "").Split(new[] { "::" }, StringSplitOptions.None)[0];
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);

                if (asset == null)
                {
                    Debug.LogWarning($"[Scene Baselines] '{assetPath}' could not be loaded — it may " +
                                     "have been moved or deleted since the check ran.");
                    return;
                }

                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return;
            }

            GameObject target = ResolveObject(finding);

            if (target == null)
            {
                Debug.LogWarning($"[Scene Baselines] '{finding.livePath ?? finding.path}' could not be " +
                                 "found in the open scene — re-check to refresh the findings.");
                return;
            }

            Selection.activeGameObject = target;
            EditorGUIUtility.PingObject(target);
        }

        /// <summary>
        /// The live object a finding points at: by identity when there is one, by path otherwise.
        /// </summary>
        private static GameObject ResolveObject(RegressionFinding finding)
        {
            if (!string.IsNullOrEmpty(finding.liveId) &&
                GlobalObjectId.TryParse(finding.liveId, out GlobalObjectId id))
            {
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
                if (resolved != null)
                    return resolved;
            }

            // A moved object is at its NEW path; the recorded one is where it used to be.
            return SceneCapture.FindByRecordedPath(finding.livePath ?? finding.path);
        }

        /// <summary>
        /// A state string cut to something a window can show. Recorded states run past a thousand
        /// characters, and a label that long is silently clipped — which reads as a UI bug and hides
        /// the very value the reviewer is being asked to judge.
        /// </summary>
        private static string Shorten(string state)
        {
            const int limit = 240;

            if (string.IsNullOrEmpty(state))
                return "(nothing)";

            return state.Length <= limit
                ? state
                : state.Substring(0, limit) + $"… (+{state.Length - limit} more, full text in the Console report)";
        }

        private static string KindLabel(RegressionKind kind)
        {
            switch (kind)
            {
                case RegressionKind.Missing:         return "MISSING";
                case RegressionKind.Moved:           return "MOVED  ";
                case RegressionKind.Added:           return "ADDED  ";
                case RegressionKind.AssetChanged:    return "ASSET  ";
                case RegressionKind.SettingsChanged: return "SETTING";
                default:                             return "CHANGED";
            }
        }
    }
}
