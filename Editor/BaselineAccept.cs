using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBaselines
{
    // ─── Regression baselines: accepting findings (step 3 of 4) ───────────────────
    //
    // Recording says what was good; checking says what differs. Neither closes the loop, because
    // until now a difference could only be printed. A human who looked at a finding and decided
    // "yes, I meant that" had no way to say so, and the only way forward was to re-record the whole
    // scene — which also adopts every OTHER difference in it, including the ones nobody looked at.
    // That is why accept is per-finding: the unit of judgement is one difference, not one scene.
    //
    // 🚨 There is deliberately NO REJECT, and deliberately NO REVERT.
    //
    // No reject, because walking away already IS rejecting: nothing is written, the difference is
    // still there, and the next check still reports it. A reject button would only add a way to
    // silence a finding without fixing it, which is the one outcome this tool must not make easy.
    //
    // No revert, because a baseline is a FINGERPRINT, not a backup. The stored state is a lossy
    // summary — it can prove a BoxCollider went missing, but it holds nothing that could put the
    // collider back: not its size, not its material, not the serialized fields of the scripts beside
    // it. "Restoring" from it would produce a wrong-but-plausible scene, which is worse than doing
    // nothing, because the developer would believe the damage was undone. Undoing is the job of the
    // version control the team already owns; this tool's job is to point at the damage precisely.
    //
    // Free of any LLM call, like recording and checking: deciding is the human's half of the loop,
    // and asking a model to judge would reintroduce the agreeableness the whole layer exists to
    // remove.

    public static class BaselineAccept
    {
        public class Result
        {
            /// <summary>Findings written into the baseline.</summary>
            public int acceptedCount;

            /// <summary>
            /// Findings that could not be matched back to a record, so nothing was written for them.
            /// </summary>
            /// <remarks>
            /// Counted and reported rather than ignored. A silent skip would leave the user believing
            /// a difference had been accepted while the next check still reports it — they would
            /// conclude accept is broken, and they would be right.
            /// </remarks>
            public int skippedCount;

            /// <summary>
            /// Accepted objects whose record was written but whose name the recorded Hierarchy order
            /// could not be brought in step with, because that would have meant guessing.
            /// </summary>
            /// <remarks>
            /// Counted for the same reason as <see cref="skippedCount"/>. The object itself IS
            /// accepted, but the order is not: an added or moved object can then be dragged up or down
            /// the Hierarchy without a report, and a deleted one leaves its name behind to be reported
            /// on its own. A reviewer who was not told either would reasonably believe otherwise.
            /// </remarks>
            public int orderLeftAloneCount;

            public string message;
            public string assetPath;

            public bool Succeeded => acceptedCount > 0 && !string.IsNullOrEmpty(assetPath);
        }

        /// <summary>
        /// Writes the given findings into the baseline as the new known-good, and saves it in place.
        /// </summary>
        /// <remarks>
        /// Takes the findings rather than re-reading the scene on purpose: what gets written is the
        /// state the reviewer actually SAW and agreed to. If the scene moved on while they were
        /// reading, the next check reports that as a fresh difference — which is correct, and far
        /// safer than silently adopting a state nobody looked at.
        ///
        /// Never throws: this is a menu action on a user's committed file, so a bad finding must fail
        /// as a message rather than as an exception halfway through a rewrite.
        /// </remarks>
        public static Result Accept(Baseline baseline, List<RegressionFinding> findings)
        {
            Result result = Apply(baseline, findings);

            if (result.acceptedCount == 0)
                return result;

            // The accepted state came from the scene as it is NOW, so if that scene is dirty this
            // baseline now contains state that never reached disk. Recorded so later checks keep
            // saying "unconfirmed" instead of claiming they can prove breakage against it. Only ever
            // set, never cleared: one accepted-from-dirty record is enough to weaken the whole file,
            // and nothing here can tell which records are still disk-backed.
            if (SceneManager.GetActiveScene().isDirty)
                baseline.capturedFromUnsavedScene = true;

            result.assetPath = BaselineStore.Save(baseline);

            if (string.IsNullOrEmpty(result.assetPath))
            {
                result.message = "Findings could not be written — see the console for the storage " +
                                 "error. The baseline on disk is unchanged.";
                return result;
            }

            string skipped = result.skippedCount > 0
                ? $" {result.skippedCount} could not be matched and were left alone."
                : "";

            string unplaced = result.orderLeftAloneCount > 0
                ? $" For {result.orderLeftAloneCount} of them the recorded Hierarchy order was left as it " +
                  "was, because updating it would have meant guessing — where those objects sit is not " +
                  "covered until the baseline is re-recorded."
                : "";

            result.message = $"Accepted {result.acceptedCount} finding(s) as intentional in " +
                             $"'{baseline.sceneName}'.{skipped} This baseline now records them as " +
                             $"known-good, so future checks will not report them.{unplaced}";

            return result;
        }

        /// <summary>
        /// Rewrites the baseline's records in memory, without saving. Touches no files and no scene.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="Accept"/> so the decision rules can be exercised by the free test
        /// suite. A test that had to save would leave real baseline files in a user's project, and a
        /// test whose side effects need cleaning up eventually gets deleted instead of fixed.
        /// </remarks>
        public static Result Apply(Baseline baseline, List<RegressionFinding> findings)
        {
            var result = new Result();

            if (baseline == null)
            {
                result.message = "Nothing accepted: no baseline was given.";
                return result;
            }

            if (findings == null || findings.Count == 0)
            {
                result.message = "Nothing accepted: no findings were selected.";
                return result;
            }

            // Removals are collected and applied after the loop. Deleting from baseline.objects while
            // matching against it would shift the list under the iteration and skip records.
            var toRemove = new List<BaselineObjectRecord>();

            foreach (RegressionFinding finding in findings)
            {
                if (finding == null)
                {
                    result.skippedCount++;
                    continue;
                }

                bool applied;

                switch (finding.kind)
                {
                    case RegressionKind.Missing:
                        applied = AcceptMissing(baseline, finding, toRemove, result);
                        break;

                    case RegressionKind.Moved:
                        applied = AcceptMoved(baseline, finding, toRemove, result);
                        break;

                    case RegressionKind.Changed:
                        applied = AcceptChanged(baseline, finding);
                        break;

                    case RegressionKind.Added:
                        applied = AcceptAdded(baseline, finding, toRemove, result);
                        break;

                    case RegressionKind.AssetChanged:
                        applied = AcceptAssetChanged(baseline, finding);
                        break;

                    case RegressionKind.SettingsChanged:
                        applied = AcceptSettingsChanged(baseline, finding);
                        break;

                    default:
                        applied = false;
                        break;
                }

                if (applied)
                    result.acceptedCount++;
                else
                    result.skippedCount++;
            }

            foreach (BaselineObjectRecord record in toRemove)
                baseline.objects.Remove(record);

            if (result.acceptedCount == 0)
            {
                result.message = "Nothing accepted: none of the selected findings could be matched " +
                                 "back to a record in this baseline. It may have been re-recorded " +
                                 "since the check ran — re-check and try again.";
                return result;
            }

            baseline.acceptedFindingCount += result.acceptedCount;
            baseline.lastAcceptedUtc = BaselineStore.CreateTimestampUtc();

            return result;
        }

        // ── One kind at a time ───────────────────────────────────────────────────

        /// <summary>
        /// Accepting a deletion drops the record, and the object's name from the order it sat in;
        /// the object is meant to be gone.
        /// </summary>
        private static bool AcceptMissing(Baseline baseline, RegressionFinding finding,
            List<BaselineObjectRecord> toRemove, Result result)
        {
            BaselineObjectRecord record = FindObjectRecord(baseline, finding);
            if (record == null)
                return false;

            // Before it joins toRemove: it is still one of its same-named siblings until then.
            if (!RemoveFromRecordedOrder(baseline, record, toRemove))
                result.orderLeftAloneCount++;

            toRemove.Add(record);
            return true;
        }

        /// <summary>
        /// Accepting a move rewrites the path the object is expected at — and its state, because
        /// re-parenting also changes world-space values recorded alongside it — and moves its name
        /// from the order it left to the order it joined.
        /// </summary>
        /// <remarks>
        /// A rename is a move too, and needs the same: the parent's list still says the old name,
        /// so without this the parent would go on reporting a changed child list for an edit the
        /// reviewer has just accepted.
        /// </remarks>
        private static bool AcceptMoved(Baseline baseline, RegressionFinding finding,
            List<BaselineObjectRecord> toRemove, Result result)
        {
            BaselineObjectRecord record = FindObjectRecord(baseline, finding);
            if (record == null)
                return false;

            bool orderKept = RemoveFromRecordedOrder(baseline, record, toRemove);

            record.path = finding.livePath ?? record.path;
            record.state = finding.liveState ?? record.state;

            // Not short-circuited: a name that could not be taken out of the old list can still be
            // put into the new one.
            orderKept &= PlaceInRecordedOrder(baseline, finding, record, toRemove);

            if (!orderKept)
                result.orderLeftAloneCount++;

            return true;
        }

        private static bool AcceptChanged(Baseline baseline, RegressionFinding finding)
        {
            BaselineObjectRecord record = FindObjectRecord(baseline, finding);
            if (record == null)
                return false;

            record.state = finding.liveState ?? record.state;
            return true;
        }

        /// <summary>
        /// Adopts an added object into the baseline, so it stops being reported and starts being
        /// COVERED — the only accept that grows what the baseline protects rather than amending it.
        /// </summary>
        /// <remarks>
        /// Accepting an ADDED finding says "I meant to build this", and the useful consequence of
        /// meaning it is that the object is now watched like everything else. Writing a record is
        /// therefore the right outcome rather than merely silencing the line: silencing would leave
        /// the object permanently unprotected while looking as though it had been dealt with.
        ///
        /// Refuses when a record already claims that path. Two records for one object would both be
        /// matched against it on the next check, and one of them would report MISSING forever.
        /// </remarks>
        private static bool AcceptAdded(Baseline baseline, RegressionFinding finding,
            List<BaselineObjectRecord> toRemove, Result result)
        {
            if (baseline.objects == null)
                return false;

            string path = finding.livePath ?? finding.path ?? "";

            if (string.IsNullOrEmpty(path))
                return false;

            bool alreadyRecorded = baseline.objects.Any(o =>
                string.Equals(o.path ?? "", path, StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(finding.liveId) &&
                 string.Equals(o.id ?? "", finding.liveId, StringComparison.Ordinal)));

            if (alreadyRecorded)
                return false;

            var record = new BaselineObjectRecord
            {
                path = path,
                state = finding.liveState ?? "",
                id = finding.liveId ?? ""
            };

            baseline.objects.Add(record);

            if (!PlaceInRecordedOrder(baseline, finding, record, toRemove))
                result.orderLeftAloneCount++;

            return true;
        }

        // ── Keeping the recorded order in step ───────────────────────────────────
        //
        // Order is recorded as LISTS OF NAMES — the scene's root order, and each parent's children —
        // not on the objects themselves. So every accept that adds, removes or renames an object
        // has a second place to write: the object's record, and its name in the list it sits in.
        // Writing only the first leaves the two disagreeing, and each way round fails differently:
        //
        // - added, name missing from the list → dragging the object anywhere is silent, because the
        //   names the list does know are still in sequence;
        // - deleted, name left in the list → the list goes on reporting a difference for a deletion
        //   the reviewer just accepted, as a second finding they have to find and tick.
        //
        // Each helper returns false only when a recorded list EXISTS and could not be updated
        // without guessing. Nothing recorded to keep in step is not a failure.

        /// <summary>One recorded ordered list and where it lives.</summary>
        private sealed class RecordedOrder
        {
            /// <summary>Set for the scene's root order.</summary>
            public BaselineSettingsRecord roots;

            /// <summary>Set for one parent's children.</summary>
            public BaselineObjectRecord parent;

            /// <summary>The names, or null when they cannot be read safely (a list cut short).</summary>
            public List<string> names;
        }

        /// <summary>
        /// The recorded list owned by <paramref name="ownerPath"/> (null for the scene's roots), or
        /// null when the baseline records no such list.
        /// </summary>
        /// <param name="exclude">A record that is not (yet) one of the owner's children.</param>
        private static RecordedOrder FindRecordedOrder(Baseline baseline, string ownerPath, string ownerId,
            BaselineObjectRecord exclude, List<BaselineObjectRecord> toRemove)
        {
            if (ownerPath == null)
            {
                BaselineSettingsRecord roots = baseline.settings?.FirstOrDefault(s =>
                    RegressionCheck.SettingsKey(s.scope, s.group) == RegressionCheck.RootOrderKey);

                // A baseline recorded before root order was: nothing to keep in step.
                if (roots == null)
                    return null;

                return new RecordedOrder { roots = roots, names = Readable(RegressionCheck.ListedNames(roots.state)) };
            }

            BaselineObjectRecord parent = null;

            if (!string.IsNullOrEmpty(ownerId))
                parent = baseline.objects.FirstOrDefault(o =>
                    string.Equals(o.id ?? "", ownerId, StringComparison.Ordinal));

            if (parent == null)
                parent = baseline.objects.FirstOrDefault(o =>
                    string.Equals(o.path ?? "", ownerPath, StringComparison.Ordinal));

            // A parent the baseline has never seen has no recorded list either.
            if (parent == null)
                return null;

            var order = new RecordedOrder { parent = parent };
            string segment = RegressionCheck.StateSegment(parent.state, "children");

            if (segment != null)
            {
                order.names = Readable(RegressionCheck.ListedNames(segment));
                return order;
            }

            // No list, because capture writes none below two children. With zero or one child the
            // list is fully known from the child records; with more, something other than child
            // count kept it off the record, and rebuilding it would be a guess.
            List<BaselineObjectRecord> children = baseline.objects.Where(o =>
            {
                if (o == exclude || toRemove.Contains(o))
                    return false;

                RegressionCheck.SplitPath(o.path, out string owner, out _, out _);
                return string.Equals(owner, parent.path ?? "", StringComparison.Ordinal);
            }).ToList();

            order.names = children.Count < 2
                ? children.Select(c =>
                {
                    RegressionCheck.SplitPath(c.path, out _, out string name, out _);
                    return SceneCapture.Sanitise(name);
                }).ToList()
                : null;

            return order;
        }

        private static List<string> Readable(List<string> names)
        {
            return names.Any(RegressionCheck.IsTruncationMarker) ? null : names;
        }

        /// <summary>Writes names back to where the list lives, in exactly the shape capture writes.</summary>
        private static void WriteRecordedOrder(RecordedOrder order, List<string> names)
        {
            if (order.roots != null)
            {
                order.roots.state = "roots=(" + string.Join(",", names) + ")";
                return;
            }

            string state = order.parent.state ?? "";
            int start = RegressionCheck.FindStateSegment(state, key => key == "children", out int length);

            // Capture writes " children=(...)" only from two children up, straight after bounds and
            // before any component properties. Anything else and the record would mismatch an
            // untouched parent on the very next check.
            if (names.Count < 2)
            {
                if (start < 0)
                    return;

                int from = start > 0 ? start - 1 : start;
                int to = start > 0 ? start + length : Math.Min(state.Length, start + length + 1);
                order.parent.state = state.Remove(from, to - from);
                return;
            }

            string list = "children=(" + string.Join(",", names) + ")";

            if (start >= 0)
            {
                order.parent.state = state.Substring(0, start) + list + state.Substring(start + length);
                return;
            }

            int props = RegressionCheck.FindStateSegment(state,
                key => key.StartsWith("props:", StringComparison.Ordinal), out _);

            order.parent.state = props > 0
                ? state.Insert(props - 1, " " + list)
                : state + " " + list;
        }

        /// <summary>
        /// Gives an added or moved object its place in the order it sits in now: the scene's root
        /// order for a top-level object, its parent's child list otherwise.
        /// </summary>
        /// <remarks>
        /// Without this, adopting "Square" into roots=(MainCamera,Light) writes Square's record and
        /// leaves the order as it was. Every later check still passes the order — MainCamera, Light
        /// are in sequence whether Square is first or last — so dragging Square to the top of the
        /// Hierarchy is never reported, for an object the reviewer was just told is now covered.
        /// </remarks>
        private static bool PlaceInRecordedOrder(Baseline baseline, RegressionFinding finding,
            BaselineObjectRecord self, List<BaselineObjectRecord> toRemove)
        {
            if (string.IsNullOrEmpty(finding.liveOrderOwner))
                return true;

            string ownerPath = finding.liveOrderOwner == RegressionCheck.RootOrderKey
                ? null
                : finding.liveOrderOwner;

            RecordedOrder order = FindRecordedOrder(baseline, ownerPath, finding.liveOrderOwnerId, self, toRemove);

            if (order == null)
                return true;

            if (order.names == null)
                return false;

            // The live parent has no list either, i.e. this object is its only child. Then the
            // recorded side must be empty, and stays without a list.
            if (finding.liveOrderNames.Count == 0)
                return order.names.Count == 0;

            List<string> placed = RegressionCheck.InsertIntoOrder(
                order.names, finding.liveOrderNames, finding.liveOrderIndex);

            if (placed == null)
                return false;

            WriteRecordedOrder(order, placed);
            return true;
        }

        /// <summary>
        /// Takes an object's name out of the order it was recorded in, before its record is dropped
        /// or moved.
        /// </summary>
        /// <remarks>
        /// Which entry is this object's is worked out from its "#n" suffix, which capture numbers in
        /// sibling order — ranked among the same-named siblings still recorded, because a twin
        /// removed earlier in the same accept has already left the list. When the list and the
        /// records disagree about how many there are, which one to remove would be a guess.
        /// </remarks>
        private static bool RemoveFromRecordedOrder(Baseline baseline, BaselineObjectRecord record,
            List<BaselineObjectRecord> toRemove)
        {
            RegressionCheck.SplitPath(record.path, out string ownerPath, out string name, out int twin);

            RecordedOrder order = FindRecordedOrder(baseline, ownerPath, null, null, toRemove);

            if (order == null)
                return true;

            if (order.names == null)
                return false;

            var twins = new List<int>();

            foreach (BaselineObjectRecord other in baseline.objects)
            {
                if (toRemove.Contains(other))
                    continue;

                RegressionCheck.SplitPath(other.path, out string otherOwner, out string otherName, out int otherTwin);

                if (string.Equals(otherOwner, ownerPath, StringComparison.Ordinal) &&
                    string.Equals(otherName, name, StringComparison.Ordinal))
                    twins.Add(otherTwin);
            }

            twins.Sort();

            List<int> positions = RegressionCheck.PositionsOf(order.names, SceneCapture.Sanitise(name));

            // Already gone — e.g. the parent's own change was accepted first, taking its live list
            // with it. Only certain when there was just the one.
            if (positions.Count == 0 && twins.Count == 1)
                return true;

            int rank = twins.IndexOf(twin);

            if (rank < 0 || positions.Count != twins.Count)
                return false;

            order.names.RemoveAt(positions[rank]);
            WriteRecordedOrder(order, order.names);
            return true;
        }

        private static bool AcceptAssetChanged(Baseline baseline, RegressionFinding finding)
        {
            BaselineAssetRecord record = baseline.assets?
                .FirstOrDefault(a => string.Equals(a.path ?? "", finding.path ?? "", StringComparison.Ordinal));

            if (record == null)
                return false;

            record.state = finding.liveState ?? record.state;
            return true;
        }

        private static bool AcceptSettingsChanged(Baseline baseline, RegressionFinding finding)
        {
            BaselineSettingsRecord record = baseline.settings?
                .FirstOrDefault(s => string.Equals(
                    RegressionCheck.SettingsKey(s.scope, s.group), finding.path ?? "",
                    StringComparison.Ordinal));

            if (record == null)
                return false;

            record.state = finding.liveState ?? record.state;
            return true;
        }

        /// <summary>
        /// The record a finding came from, matched the same way the check matched it.
        /// </summary>
        /// <remarks>
        /// A finding's <see cref="RegressionFinding.path"/> is the RECORDED path, which capture makes
        /// unique per baseline via its "#n" suffixing — so it identifies the record even for a move,
        /// where the live path is different. The id is tried first anyway, for the same reason the
        /// check prefers it: it is the identity, and the path is a label.
        /// </remarks>
        private static BaselineObjectRecord FindObjectRecord(Baseline baseline,
            RegressionFinding finding)
        {
            if (baseline.objects == null)
                return null;

            if (!string.IsNullOrEmpty(finding.liveId))
            {
                BaselineObjectRecord byId = baseline.objects.FirstOrDefault(
                    o => string.Equals(o.id ?? "", finding.liveId, StringComparison.Ordinal));

                if (byId != null)
                    return byId;
            }

            return baseline.objects.FirstOrDefault(
                o => string.Equals(o.path ?? "", finding.path ?? "", StringComparison.Ordinal));
        }
    }
}
