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

            result.message = $"Accepted {result.acceptedCount} finding(s) as intentional in " +
                             $"'{baseline.sceneName}'.{skipped} This baseline now records them as " +
                             "known-good, so future checks will not report them.";

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
                        applied = AcceptMissing(baseline, finding, toRemove);
                        break;

                    case RegressionKind.Moved:
                        applied = AcceptMoved(baseline, finding);
                        break;

                    case RegressionKind.Changed:
                        applied = AcceptChanged(baseline, finding);
                        break;

                    case RegressionKind.Added:
                        applied = AcceptAdded(baseline, finding);
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

        /// <summary>Accepting a deletion drops the record; the object is meant to be gone.</summary>
        private static bool AcceptMissing(Baseline baseline, RegressionFinding finding,
            List<BaselineObjectRecord> toRemove)
        {
            BaselineObjectRecord record = FindObjectRecord(baseline, finding);
            if (record == null)
                return false;

            toRemove.Add(record);
            return true;
        }

        /// <summary>
        /// Accepting a move rewrites the path the object is expected at — and its state, because
        /// re-parenting also changes world-space values recorded alongside it.
        /// </summary>
        private static bool AcceptMoved(Baseline baseline, RegressionFinding finding)
        {
            BaselineObjectRecord record = FindObjectRecord(baseline, finding);
            if (record == null)
                return false;

            record.path = finding.livePath ?? record.path;
            record.state = finding.liveState ?? record.state;
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
        private static bool AcceptAdded(Baseline baseline, RegressionFinding finding)
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

            baseline.objects.Add(new BaselineObjectRecord
            {
                path = path,
                state = finding.liveState ?? "",
                id = finding.liveId ?? ""
            });

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
