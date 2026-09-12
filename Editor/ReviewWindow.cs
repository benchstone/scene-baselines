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
    // A long list is narrowed with the controls under the toolbar, and every one of them filters on
    // a fact the finding already carries — see ReviewFilter, which also says why there is no button
    // labelled "regressions" and why every count on screen is printed beside the count it came from.
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

        // What the reviewer has narrowed the list to. Survives a re-check on purpose: someone who
        // filtered to "MISSING under Enemies/", fixed one and pressed Re-check is asking whether the
        // rest are still missing, not to be dropped back into the full list. See ReviewFilter for
        // the two rules every control here obeys.
        private readonly ReviewFilter filter = new ReviewFilter();

        // The findings actually put on screen this frame, by key, so the Accept bar can say when
        // something ticked is behind a filter rather than leave its count unexplained.
        private readonly HashSet<string> drawnKeys = new HashSet<string>(StringComparer.Ordinal);

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
            drawnKeys.Clear();

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
                DrawAdditions(comparable[0]);
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
                GUILayout.Label(DescribeCount());

            EditorGUILayout.EndHorizontal();

            DrawFilterBar();
        }

        /// <summary>
        /// What this window is showing, and out of how much.
        /// </summary>
        /// <remarks>
        /// The total is printed whether or not a filter is on. A reviewer who cannot see the number
        /// being filtered out cannot tell a clean scene from a narrow filter, and that confusion is
        /// the whole risk of having filters at all.
        ///
        /// Counted over what this window LISTS — the newest baseline's findings plus what only older
        /// baselines still catch — rather than over every finding in the run. Those differ: the same
        /// change reported by three baselines is three findings in the result and one line here, and
        /// a header that says 45 above a list of 17 reads as a bug in the tool.
        /// </remarks>
        private string DescribeCount()
        {
            // Not "nothing differs": nothing was compared, which is the one thing this tool may
            // never let read as a pass.
            if (result.sceneWasUnsaved || !result.HasBaselines)
                return $"{result.sceneName} — nothing compared";

            List<RegressionFinding> listed = ListedFindings();
            string added = DescribeAdded();

            if (listed.Count == 0)
                return $"{result.sceneName} — nothing differs{added}";

            return filter.IsActive
                ? $"{result.sceneName} — showing {filter.Apply(listed).Count} of {listed.Count} finding(s){added}"
                : $"{result.sceneName} — {listed.Count} finding(s){added}";
        }

        /// <summary>
        /// How many objects this window is offering to adopt, as a suffix for the count line.
        /// </summary>
        /// <remarks>
        /// On the headline and not only in its own section, because "nothing differs" is the one
        /// phrase a reviewer reads without scrolling — and a window that says that to someone who
        /// just built a new object reads as a tool that missed it. It has not missed it; it is
        /// declining to call new work breakage. That distinction has to survive being skimmed.
        /// </remarks>
        private string DescribeAdded()
        {
            BaselineComparison current = result.comparisons.FirstOrDefault(c => c.IsComparable);
            int added = current == null ? 0 : current.newObjects.Count;

            return added > 0 ? $" · {added} added" : "";
        }

        /// <summary>
        /// The narrowing controls, and the one place a filter is chosen.
        /// </summary>
        /// <remarks>
        /// Every option is built from the findings in front of the reviewer and carries its own
        /// count, so a choice can never come back empty and no bucket is offered that the findings
        /// do not fill. There is deliberately no "only regressions" control — see ReviewFilter.
        /// </remarks>
        private void DrawFilterBar()
        {
            if (result == null || result.sceneWasUnsaved || !result.HasBaselines)
                return;

            List<RegressionFinding> listed = ListedFindings();

            // A row of dead menus over an empty list is noise, and a filter cannot help anyone read
            // a list of nothing.
            if (listed.Count == 0)
                return;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Show", EditorStyles.miniLabel, GUILayout.Width(34f));

            DrawKindMenu(listed);
            DrawDetailMenu(listed);

            GUILayout.Label("path", EditorStyles.miniLabel, GUILayout.Width(28f));
            filter.path = EditorGUILayout.TextField(
                filter.path ?? "", EditorStyles.toolbarSearchField, GUILayout.MinWidth(70f));

            using (new EditorGUI.DisabledScope(!filter.IsActive))
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(46f)))
                {
                    filter.Clear();
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawKindMenu(List<RegressionFinding> listed)
        {
            var kinds = new List<RegressionKind>();

            foreach (RegressionFinding finding in listed)
            {
                if (!kinds.Contains(finding.kind))
                    kinds.Add(finding.kind);
            }

            kinds.Sort();

            // A chosen kind stays on the menu even when a re-check leaves none of it — which is the
            // moment it matters most, because "0 of 12" is the reviewer's answer that the thing they
            // were watching is fixed. Dropping them back to every kind would answer a different
            // question and look like the filter failing.
            if (filter.kind.HasValue && !kinds.Contains(filter.kind.Value))
                kinds.Add(filter.kind.Value);

            var labels = new List<string> { $"All kinds ({listed.Count})" };

            foreach (RegressionKind kind in kinds)
                labels.Add($"{KindLabel(kind).Trim()} ({listed.Count(f => f.kind == kind)})");

            int chosen = filter.kind.HasValue ? kinds.IndexOf(filter.kind.Value) + 1 : 0;
            int picked = EditorGUILayout.Popup(chosen, labels.ToArray(),
                EditorStyles.toolbarPopup, GUILayout.Width(118f));

            filter.kind = picked <= 0 ? (RegressionKind?)null : kinds[picked - 1];
        }

        private void DrawDetailMenu(List<RegressionFinding> listed)
        {
            List<KeyValuePair<string, int>> details = ReviewFilter.DetailKeys(listed);

            if (filter.detail != null && !details.Any(d => string.Equals(d.Key, filter.detail, StringComparison.Ordinal)))
                details.Add(new KeyValuePair<string, int>(filter.detail, 0));

            // MISSING and ADDED findings name no segment, so a scene that only lost objects has
            // nothing to offer here. An empty menu would be a control that cannot be used.
            if (details.Count == 0)
                return;

            var labels = new List<string> { "Any change" };

            foreach (KeyValuePair<string, int> detail in details)
                labels.Add($"{ReviewFilter.DescribeDetail(detail.Key)} ({detail.Value})");

            int chosen = filter.detail == null
                ? 0
                : details.FindIndex(d => string.Equals(d.Key, filter.detail, StringComparison.Ordinal)) + 1;

            int picked = EditorGUILayout.Popup(chosen, labels.ToArray(),
                EditorStyles.toolbarPopup, GUILayout.Width(168f));

            filter.detail = picked <= 0 ? null : details[picked - 1].Key;
        }

        /// <summary>
        /// Every finding this window would list with no filter on: the current known-good's, plus
        /// the ones only an older baseline still reports. Deduplicated exactly as the sections are.
        /// </summary>
        private List<RegressionFinding> ListedFindings()
        {
            var listed = new List<RegressionFinding>();

            if (result == null)
                return listed;

            List<BaselineComparison> comparable = result.comparisons.Where(c => c.IsComparable).ToList();

            if (comparable.Count == 0)
                return listed;

            listed.AddRange(comparable[0].findings);
            listed.AddRange(OnlyInOlder(comparable[0], comparable.Skip(1).ToList()).Select(e => e.Value));

            return listed;
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

            List<RegressionFinding> visible = filter.Apply(current.findings);

            // Never "still holds" when a filter is the only reason there is nothing to show. A
            // window that reads as clean because of a menu setting is the one failure this feature
            // is not allowed to have.
            if (visible.Count == 0)
            {
                EditorGUILayout.LabelField(
                    $"   {current.findings.Count} finding(s) here, none of them matching the filter");
                return;
            }

            if (visible.Count < current.findings.Count)
            {
                EditorGUILayout.LabelField(
                    $"   showing {visible.Count} of {current.findings.Count} finding(s)",
                    EditorStyles.miniLabel);
            }

            foreach (RegressionFinding finding in visible)
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
            List<KeyValuePair<BaselineComparison, RegressionFinding>> extras = OnlyInOlder(current, older);

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

            List<KeyValuePair<BaselineComparison, RegressionFinding>> visible =
                extras.Where(e => filter.Matches(e.Value)).ToList();

            // The heading is printed above whatever the filter leaves, including nothing. A section
            // that vanishes under a filter takes its count with it, and the count is the point.
            if (visible.Count < extras.Count)
            {
                EditorGUILayout.LabelField(
                    visible.Count == 0
                        ? "   none of them matching the filter"
                        : $"   showing {visible.Count} of {extras.Count}",
                    EditorStyles.miniLabel);
            }

            foreach (KeyValuePair<BaselineComparison, RegressionFinding> extra in visible)
            {
                DrawFinding(extra.Value, 0);
                EditorGUILayout.LabelField(
                    "      from " + extra.Key.baseline?.DescribeAge(), EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// The findings only OLDER baselines still report, each paired with the newest baseline
        /// that reports it. Deduplicated, so one lost object is one entry however many baselines
        /// remember it.
        /// </summary>
        private static List<KeyValuePair<BaselineComparison, RegressionFinding>> OnlyInOlder(
            BaselineComparison current, List<BaselineComparison> older)
        {
            var extras = new List<KeyValuePair<BaselineComparison, RegressionFinding>>();

            if (older == null || older.Count == 0)
                return extras;

            var currentKeys = new HashSet<string>(current.findings.Select(Key));
            var shown = new HashSet<string>();

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

            return extras;
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
                // Stated up here as well as listed below, because the findings between the two can
                // run to a whole screen, and "how much of my scene is this baseline actually
                // watching" is a question the reviewer needs answered before they start ticking.
                EditorGUILayout.LabelField($"   {comparison.newObjectCount} object(s) added since — " +
                    "not regressions, and not covered until adopted; see ADDED SINCE below");
            }
        }

        /// <summary>
        /// Objects this baseline has never seen — offered for adoption, never as breakage.
        /// </summary>
        /// <remarks>
        /// This section exists because the alternative was re-recording the whole scene. Adding a
        /// GameObject is the most ordinary thing a developer does, and it used to leave them with
        /// two bad options: leave the object permanently unwatched, or re-record and thereby adopt
        /// every other difference in the scene at once, including the ones nobody read. Per-finding
        /// accept was built to kill that second option everywhere else; additions were the last
        /// place it survived.
        ///
        /// Worded as an offer, not an accusation, and it says "not regressions" before it says
        /// anything else. A list of the developer's own new work under a red heading is how a tool
        /// teaches its user that its findings are noise — and the next line they skip is a real one.
        ///
        /// No filter is applied here. The filters narrow FINDINGS, and these are not findings; the
        /// section always shows every added object, so nothing it holds can be hidden by a menu.
        /// </remarks>
        private void DrawAdditions(BaselineComparison comparison)
        {
            if (comparison.newObjects.Count == 0)
                return;

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField(
                $"ADDED SINCE — {comparison.newObjects.Count} object(s), not regressions",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "This baseline has never seen these, so nothing here is broken — but nothing here is " +
                "protected either. Tick one to record it as known-good from now on. Everything else " +
                "in the baseline is left exactly as it was recorded.",
                EditorStyles.wordWrappedMiniLabel);

            foreach (RegressionFinding addition in comparison.newObjects)
                DrawFinding(addition, 0);
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
            drawnKeys.Add(Key(finding));

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
            else if (finding.kind == RegressionKind.Added)
            {
                // An object the baseline never held has no "was" to differ from, so the was/now
                // fallback below would print "was: (nothing)" and dress new work up as a change.
                EditorGUILayout.LabelField(
                    "      not recorded yet — accepting starts covering it, and changes nothing else");
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
                foreach (RegressionFinding candidate in Rows(comparison))
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
        /// Everything in one comparison a reviewer can tick: the findings against it, and the
        /// objects it does not yet cover.
        /// </summary>
        /// <remarks>
        /// One list wherever selection and accept are concerned, two lists wherever breakage is
        /// counted. An adoptable addition is a decision the reviewer makes in this window exactly
        /// like a finding — same tick, same Accept button, same trip through
        /// <see cref="BaselineAccept"/> — but it is not breakage, so it stays out of the finding
        /// counts, the report and the CI exit code.
        /// </remarks>
        private static IEnumerable<RegressionFinding> Rows(BaselineComparison comparison)
        {
            return comparison.findings.Concat(comparison.newObjects);
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

            // A filter changes what is on screen and nothing else: a finding ticked before the
            // filter narrowed is still ticked and will still be accepted. Said out loud, because the
            // alternative is a button whose number does not match the list above it — and the way
            // that gets "fixed" by a user is by accepting a change they can no longer see.
            int hidden = selected.Select(Key).Distinct().Count(k => !drawnKeys.Contains(k));

            if (hidden > 0)
            {
                EditorGUILayout.LabelField(
                    $"{hidden} checked finding(s) are hidden by the filter — Accept still covers them. " +
                    "Clear the filter to see them.", EditorStyles.wordWrappedMiniLabel);
            }

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
                List<RegressionFinding> mine = Rows(comparison)
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
