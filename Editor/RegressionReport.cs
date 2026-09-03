using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SceneBaselines
{
    // ─── Regression baselines: reporting (step 3 of 4) ───────────────────────────
    //
    // Steps 1 and 2 record a known-good scene and diff the live scene against it. Both
    // of them end at Debug.Log, which means the answer exists only inside one editor
    // session, on one machine, in a console somebody has to be watching. This step turns
    // that answer into an artifact: a file on disk, a machine-readable sibling, and a
    // process exit code — the three things that let something OTHER than the person who
    // ran it act on the result.
    //
    // That is the entire point of the layer. A verify loop that only convinces the agent
    // which ran it is a demo. Evidence a reviewer, a teammate, or CI can read WITHOUT
    // re-running anything, and without trusting the agent's account of its own work, is
    // the product.
    //
    // Two renderings, one source of truth. Markdown is for a human reading a pull
    // request; JSON is for anything parsing the outcome. Both are projections of the same
    // RegressionRunResult and both take their verdict from RegressionCheck.
    // VerdictFor. A renderer that decided for itself whether findings counted as breakage
    // would eventually disagree with the check it claims to report, and "you can trust
    // this without re-running it" is the only thing being sold here.
    //
    // Reports are deliberately NOT written under Assets/ and NOT committed — the opposite
    // of baselines. A baseline is evidence and must survive in version control; a report
    // is derived from a baseline plus the current scene, so committing it would add churn
    // to every review while adding nothing that cannot be regenerated in a second.
    //
    // CI usage:
    //   Unity.exe -batchmode -projectPath <path> \
    //             -executeMethod RegressionReport.RunBatch
    //
    //   Optional args:  -reportDir <dir>     where to write (default SceneBaselineReports/)
    //                   -baselineScene <Assets/...>  check only this scene; repeatable
    //                   -baselineDir <dir>   where baselines live (default Assets/SceneBaselines).
    //                                        Point it outside the project to inspect a repository
    //                                        without writing a single file into it.
    //
    //   Do NOT pass -quit: this method calls EditorApplication.Exit itself so it can
    //   return a meaningful code, and -quit would race it to a 0.

    public static class RegressionReport
    {
        // v2 widened ReportFinding.kind with "asset-changed"; v3 with "settings-changed"; v4 with
        // "moved", which also added ReportFinding.livePath (set for that kind only). v5 with
        // "added" — an object that is not in the baseline and does not look deliberate — which
        // also sets livePath (an added object has no recorded path to differ from) and leaves
        // baselineState empty, since the baseline has never seen it.
        // Every other field is unchanged.
        public const int SchemaVersion = 5;

        public const string DefaultFolder = "SceneBaselineReports";
        public const string MarkdownFileName = "regression-report.md";
        public const string JsonFileName = "regression-report.json";

        // ── Exit codes ───────────────────────────────────────────────────────────
        //
        // 2 is a separate code from 1 on purpose. "Regressions found" and "I could not
        // tell you" are different facts and a build owner will want to treat them
        // differently — but BOTH are non-zero, because a check that compared nothing
        // must never be able to turn into a green build.

        public const int ExitPass        = 0;
        public const int ExitRegressions = 1;
        public const int ExitNotChecked  = 2;

        // ── On-disk report model ─────────────────────────────────────────────────
        //
        // Declared separately from the check's own types for the same reason the baseline
        // store declares its own: a consumed format is a contract. RegressionRunResult is
        // free to change shape; anything parsing regression-report.json is not. Keep these
        // dumb and additive — add fields, never rename or repurpose one, and bump
        // SchemaVersion when the meaning of an existing field changes.

        [Serializable]
        public class ReportFinding
        {
            public string path;

            // "missing" | "changed" | "moved" | "asset-changed" | "settings-changed". Widened in
            // schema v2, again in v3, and again in v4 ("moved"). Only "missing", "changed" and
            // "moved" carry a hierarchy path: "asset-changed" carries an asset path and
            // "settings-changed" a scope/group key, so anything resolving these against the scene
            // must branch rather than assume a GameObject.
            public string kind;

            // Where the object is NOW; set only for "moved", where `path` is where it USED to be.
            // A consumer resolving a moved object against the live scene must use this one.
            public string livePath;

            public List<string> changes = new List<string>();
            public string baselineState;
            public string liveState;
        }

        [Serializable]
        public class ReportBaseline
        {
            public string id;
            public string verdict;         // pass | regressions | unconfirmed
            public string established;     // local time, human readable
            public string establishedUtc;  // round-trip, for machines
            public string originalRequest;
            public int recordedObjectCount;
            public int newObjectCount;
            public int verifiedBehaviourCount;
            public bool capturedFromUnsavedScene;

            /// <summary>False when findings against this baseline cannot be called regressions.</summary>
            public bool stateReachedDisk;

            /// <summary>Why not, or empty when the baseline is trustworthy.</summary>
            public string untrustworthyReason;

            /// <summary>Why this baseline was not compared at all, or empty when it was.</summary>
            public string incomparableReason;

            public List<ReportFinding> findings = new List<ReportFinding>();
        }

        [Serializable]
        public class ReportScene
        {
            public string sceneName;
            public string scenePath;
            public string verdict;
            public string headline;
            public string notCheckedReason;   // set only when the scene could not be checked
            public bool sceneHadUnsavedChanges;
            public int baselinesCompared;

            /// <summary>Baselines found but not compared — unreadable, not clean.</summary>
            public int baselinesSkipped;

            public int findingCount;
            public List<ReportBaseline> baselines = new List<ReportBaseline>();
        }

        [Serializable]
        public class Report
        {
            public int schemaVersion = SchemaVersion;
            public string generatedUtc;
            public string unityVersion;
            public string verdict;         // worst verdict across every scene
            public int exitCode;
            public int scenesChecked;
            public int baselinesCompared;
            public int baselinesSkipped;
            public int totalFindings;
            public List<ReportScene> scenes = new List<ReportScene>();
        }

        /// <summary>Where a report was written. Paths are absolute.</summary>
        public class WrittenReport
        {
            public string markdownPath;
            public string jsonPath;
        }

        // ── Entry points ─────────────────────────────────────────────────────────

        [MenuItem("Scene Baselines/Write Regression Report")]
        public static void WriteReportMenu()
        {
            // The active scene only. Checking every baselined scene means OPENING them, which
            // would silently discard whatever the user has unsaved — never acceptable as the
            // side effect of a menu click. Batch mode has no such user to lose work.
            Report report = BuildForActiveScene();
            WrittenReport written = Write(report, ResolveOutputFolder(null));

            if (written == null)
            {
                Debug.LogWarning("[Scene Baselines] Regression report could not be written — see the error above.");
                return;
            }

            var message = new StringBuilder();
            message.AppendLine($"Scene baseline report — {report.verdict.ToUpperInvariant()}");
            message.AppendLine(written.markdownPath);
            message.AppendLine(written.jsonPath);

            // Coverage the menu path deliberately did not check. Left unsaid, a clean report on
            // one scene reads as a clean project, which is the same false confidence this whole
            // feature exists to prevent.
            int otherScenes = ScenePathsWithBaselines()
                .Count(p => !string.Equals(p, report.scenes.FirstOrDefault()?.scenePath, StringComparison.OrdinalIgnoreCase));

            if (otherScenes > 0)
            {
                message.AppendLine($"NOTE: {otherScenes} other scene(s) have baselines and were NOT checked. " +
                    "Run in batch mode to cover every scene.");
            }

            if (report.exitCode == ExitRegressions)
                Debug.LogWarning(message.ToString().TrimEnd());
            else
                Debug.Log(message.ToString().TrimEnd());
        }

        /// <summary>
        /// Batch-mode entry point. Checks every scene that has a baseline, writes the report, and
        /// exits with a code CI can branch on.
        /// </summary>
        public static void RunBatch()
        {
            // Refuse outside batch mode. This path OPENS scenes to check them, which discards
            // unsaved work without a prompt — acceptable on a CI machine with nothing to lose,
            // never acceptable in a live editor session where a mis-click would destroy hours.
            if (!Application.isBatchMode)
            {
                Debug.LogError("[Scene Baselines] RunBatch is batch-mode only: it opens every baselined scene " +
                    "and would discard unsaved work. Use the 'Scene Baselines/Write Regression Report' menu " +
                    "item to check the active scene instead.");
                return;
            }

            int exitCode = ExitNotChecked;

            try
            {
                List<string> requested = ArgumentValues("-baselineScene");
                List<string> scenePaths = requested.Count > 0 ? requested : ScenePathsWithBaselines();

                Report report = BuildForScenes(scenePaths);
                WrittenReport written = Write(report, ResolveOutputFolder(ArgumentValue("-reportDir")));

                exitCode = report.exitCode;

                // Batch-mode CI often keeps only the process output, so the headline goes to the
                // log as well as into the file. A failing build should not require fetching an
                // artifact to find out what broke.
                Debug.Log(RenderMarkdown(report));

                if (written != null)
                    Debug.Log($"[Scene Baselines] Report written:\n{written.markdownPath}\n{written.jsonPath}");
            }
            catch (Exception e)
            {
                // An exception here means the check itself failed, which is emphatically not a
                // pass. Fall through to a non-zero exit rather than letting the build go green
                // on a crashed regression check.
                Debug.LogError("[Scene Baselines] Regression report failed: " + e);
                exitCode = ExitNotChecked;
            }

            EditorApplication.Exit(exitCode);
        }

        // ── Building ─────────────────────────────────────────────────────────────

        public static Report BuildForActiveScene()
        {
            return Finish(new Report
            {
                scenes = { BuildScene(RegressionCheck.RunForActiveScene()) }
            });
        }

        /// <summary>
        /// Opens and checks each scene in turn. DESTRUCTIVE: opening a scene discards unsaved
        /// changes to the current one, so only call this where there is no work to lose — which
        /// in practice means batch mode. See the guard in <see cref="RunBatch"/>.
        /// </summary>
        public static Report BuildForScenes(List<string> scenePaths)
        {
            var report = new Report();

            if (scenePaths == null || scenePaths.Count == 0)
            {
                report.scenes.Add(new ReportScene
                {
                    sceneName = "(none)",
                    verdict = RegressionCheck.VerdictToken(RegressionVerdict.NotChecked),
                    headline = "NOT CHECKED — no baselines exist in this project, so nothing could be " +
                               "compared. This is not a pass.",
                    notCheckedReason = "no baselines recorded"
                });

                return Finish(report);
            }

            foreach (string scenePath in scenePaths)
            {
                string absolute = ToAbsoluteProjectPath(scenePath);

                if (!File.Exists(absolute))
                {
                    // A baseline outliving its scene is worth surfacing rather than skipping: it
                    // usually means the scene was renamed or deleted, and the recorded history is
                    // now orphaned.
                    report.scenes.Add(NotCheckedScene(scenePath,
                        "the scene file no longer exists at this path"));
                    continue;
                }

                try
                {
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                }
                catch (Exception e)
                {
                    report.scenes.Add(NotCheckedScene(scenePath, "the scene could not be opened: " + e.Message));
                    continue;
                }

                report.scenes.Add(BuildScene(RegressionCheck.RunForActiveScene()));
            }

            return Finish(report);
        }

        private static ReportScene NotCheckedScene(string scenePath, string reason)
        {
            return new ReportScene
            {
                sceneName = Path.GetFileNameWithoutExtension(scenePath),
                scenePath = scenePath,
                verdict = RegressionCheck.VerdictToken(RegressionVerdict.NotChecked),
                headline = "NOT CHECKED — " + reason + ". This is not a pass.",
                notCheckedReason = reason
            };
        }

        private static ReportScene BuildScene(RegressionRunResult result)
        {
            var scene = new ReportScene
            {
                sceneName = result.sceneName,
                scenePath = result.scenePath,
                verdict = RegressionCheck.VerdictToken(RegressionCheck.VerdictFor(result)),
                headline = RegressionCheck.DescribeVerdict(result),
                sceneHadUnsavedChanges = result.sceneHadUnsavedChanges,
                baselinesCompared = result.comparisons.Count(c => c.IsComparable),
                baselinesSkipped = result.comparisons.Count(c => !c.IsComparable),
                findingCount = result.TotalFindings
            };

            if (result.sceneWasUnsaved)
                scene.notCheckedReason = "the scene has never been saved";
            else if (result.comparisons.Count == 0)
                scene.notCheckedReason = "no baselines recorded for this scene";
            else if (!result.HasBaselines)
                scene.notCheckedReason = "every baseline for this scene was unreadable";

            foreach (BaselineComparison comparison in result.comparisons)
                scene.baselines.Add(BuildBaseline(comparison));

            return scene;
        }

        private static ReportBaseline BuildBaseline(BaselineComparison comparison)
        {
            Baseline baseline = comparison.baseline;

            var entry = new ReportBaseline
            {
                id = baseline?.id,
                verdict = RegressionCheck.VerdictToken(RegressionCheck.VerdictFor(comparison)),
                established = baseline?.DescribeCreated() ?? "(unknown date)",
                establishedUtc = baseline?.createdUtc,
                originalRequest = baseline?.originalRequest,
                recordedObjectCount = comparison.recordedObjectCount,
                newObjectCount = comparison.newObjectCount,
                verifiedBehaviourCount = baseline?.verifiedChecks?.Count ?? 0,
                capturedFromUnsavedScene = baseline?.capturedFromUnsavedScene ?? false,
                stateReachedDisk = comparison.stateReachedDisk,
                untrustworthyReason = comparison.untrustworthyReason,
                incomparableReason = comparison.incomparableReason
            };

            foreach (RegressionFinding finding in comparison.findings)
            {
                entry.findings.Add(new ReportFinding
                {
                    path = finding.path,
                    kind = FindingKindToken(finding.kind),
                    livePath = finding.kind == RegressionKind.Moved || finding.kind == RegressionKind.Added
                        ? finding.livePath
                        : null,
                    changes = new List<string>(finding.changedSegments ?? new List<string>()),

                    // Full states are kept only where something actually differs, as the fallback
                    // when segment diffing produced nothing. Repeating them for MISSING would
                    // double the file size to restate what "missing" already says.
                    baselineState = finding.kind != RegressionKind.Missing ? finding.baselineState : null,
                    liveState = finding.kind != RegressionKind.Missing ? finding.liveState : null
                });
            }

            return entry;
        }

        /// <summary>Stable token for a finding kind. Part of the consumed contract — do not rename.</summary>
        private static string FindingKindToken(RegressionKind kind)
        {
            switch (kind)
            {
                case RegressionKind.Missing:         return "missing";
                case RegressionKind.Moved:           return "moved";
                case RegressionKind.Added:           return "added";
                case RegressionKind.AssetChanged:    return "asset-changed";
                case RegressionKind.SettingsChanged: return "settings-changed";
                default:                             return "changed";
            }
        }

        /// <summary>Fills in the run-wide totals and the aggregate verdict.</summary>
        private static Report Finish(Report report)
        {
            report.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report.unityVersion = Application.unityVersion;
            report.scenesChecked = report.scenes.Count;
            report.baselinesCompared = report.scenes.Sum(s => s.baselinesCompared);
            report.baselinesSkipped = report.scenes.Sum(s => s.baselinesSkipped);
            report.totalFindings = report.scenes.Sum(s => s.findingCount);

            // Worst verdict wins. A project is not passing because most of its scenes are.
            RegressionVerdict worst = RegressionVerdict.Pass;
            foreach (ReportScene scene in report.scenes)
            {
                RegressionVerdict verdict = ParseVerdict(scene.verdict);
                if (Severity(verdict) > Severity(worst))
                    worst = verdict;
            }

            report.verdict = RegressionCheck.VerdictToken(worst);
            report.exitCode = ExitCodeFor(worst);
            return report;
        }

        private static int Severity(RegressionVerdict verdict)
        {
            switch (verdict)
            {
                case RegressionVerdict.Pass:        return 0;
                case RegressionVerdict.NotChecked:  return 1;
                case RegressionVerdict.Unconfirmed: return 2;
                default:                            return 3;   // Regressions
            }
        }

        public static int ExitCodeFor(RegressionVerdict verdict)
        {
            switch (verdict)
            {
                case RegressionVerdict.Pass:        return ExitPass;
                case RegressionVerdict.Regressions: return ExitRegressions;
                default:                            return ExitNotChecked;  // NotChecked, Unconfirmed
            }
        }

        private static RegressionVerdict ParseVerdict(string token)
        {
            switch (token)
            {
                case "pass":        return RegressionVerdict.Pass;
                case "regressions": return RegressionVerdict.Regressions;
                case "unconfirmed": return RegressionVerdict.Unconfirmed;
                default:            return RegressionVerdict.NotChecked;
            }
        }

        // ── Markdown rendering ───────────────────────────────────────────────────

        public static string RenderMarkdown(Report report)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Scene baseline report");
            sb.AppendLine();
            sb.AppendLine($"**{report.verdict.ToUpperInvariant()}** — {report.totalFindings} finding(s) across " +
                $"{report.baselinesCompared} baseline(s) in {report.scenesChecked} scene(s)." +
                (report.baselinesSkipped > 0
                    ? $" {report.baselinesSkipped} baseline(s) could not be read and were NOT checked."
                    : ""));
            sb.AppendLine();

            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Generated | {DescribeUtc(report.generatedUtc)} |");
            sb.AppendLine($"| Unity | {report.unityVersion} |");
            sb.AppendLine($"| Exit code | `{report.exitCode}` |");
            sb.AppendLine();

            // What this report does and does not prove, stated up front. A reader who takes a
            // static-state comparison for a behavioural guarantee has been misled by the report
            // whether or not any single line in it is wrong.
            sb.AppendLine("> Compares the current scene against states recorded when verification " +
                "last passed cleanly. Deterministic string comparison — no model is consulted. " +
                "Objects added since a baseline are not regressions. This checks scene state, " +
                "not runtime behaviour.");
            sb.AppendLine();

            foreach (ReportScene scene in report.scenes)
                RenderScene(sb, scene);

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static void RenderScene(StringBuilder sb, ReportScene scene)
        {
            sb.AppendLine($"## Scene {Code(scene.sceneName)}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(scene.scenePath))
            {
                sb.AppendLine(Code(scene.scenePath));
                sb.AppendLine();
            }

            sb.AppendLine(scene.headline);
            sb.AppendLine();

            if (scene.sceneHadUnsavedChanges)
            {
                sb.AppendLine("> The scene had unsaved changes, so this reflects the editor's current " +
                    "state, not what is committed on disk.");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(scene.notCheckedReason))
            {
                sb.AppendLine("A baseline is recorded automatically the next time a request verifies " +
                    "fully clean.");
                sb.AppendLine();
            }

            // Still render the baselines even when the scene as a whole could not be checked: the
            // per-baseline reason is the actionable part, and hiding it leaves a reader with a
            // failure and no way to tell what to do about it.
            foreach (ReportBaseline baseline in scene.baselines)
                RenderBaseline(sb, baseline);
        }

        private static void RenderBaseline(StringBuilder sb, ReportBaseline baseline)
        {
            sb.AppendLine($"### {baseline.verdict.ToUpperInvariant()} — baseline {Code(baseline.id)}");
            sb.AppendLine();

            // The establish date is the load-bearing line of the whole report: "this worked on
            // 2026-08-01" is what turns a diff into evidence.
            sb.AppendLine($"- Established **{baseline.established}**");
            sb.AppendLine($"- Recorded while building: {Code(baseline.originalRequest ?? "(no request recorded)")}");
            sb.AppendLine($"- Covers {baseline.recordedObjectCount} object(s)" +
                (baseline.newObjectCount > 0
                    ? $"; {baseline.newObjectCount} added since (not regressions)"
                    : ""));

            if (baseline.verifiedBehaviourCount > 0)
                sb.AppendLine($"- Guaranteed {baseline.verifiedBehaviourCount} verified behaviour(s) at that time");

            sb.AppendLine();

            // "Could not compare" is not "nothing changed", and the report must not let the two
            // look alike — a reader skimming for green would otherwise bank an unread baseline as
            // a pass.
            if (!string.IsNullOrEmpty(baseline.incomparableReason))
            {
                sb.AppendLine($"> **Not compared:** {baseline.incomparableReason}. Nothing here was " +
                    "verified. Re-record this baseline from a fresh clean pass.");
                sb.AppendLine();
                return;
            }

            if (baseline.findings.Count == 0)
            {
                sb.AppendLine("Every recorded object still matches.");
                sb.AppendLine();
                return;
            }

            // Findings against a baseline that never reached disk are ambiguous, and saying so is
            // the entire point: the objects may have reverted because the scene was closed without
            // saving rather than because anything broke. Calling that a regression would be the
            // tool inventing breakage, which costs more trust than reporting nothing at all.
            if (!string.IsNullOrEmpty(baseline.untrustworthyReason))
            {
                sb.AppendLine($"> **Cannot prove breakage:** {baseline.untrustworthyReason}. If the scene " +
                    "was closed without saving, these findings are that revert — not regressions.");
                sb.AppendLine();
            }

            foreach (ReportFinding finding in baseline.findings)
            {
                if (finding.kind == "missing")
                {
                    sb.AppendLine($"- **MISSING** {Code(finding.path)} — recorded as known-good, not in the scene now");
                    continue;
                }

                // The object is intact and matched by its own identity, so this says where it went
                // instead of implying something broke at the old path.
                if (finding.kind == "moved")
                {
                    sb.AppendLine($"- **MOVED** {Code(finding.path)} — renamed or re-parented, now at " +
                                  $"{Code(finding.livePath)}");

                    if (finding.changes.Count > 0)
                    {
                        foreach (string change in finding.changes)
                            sb.AppendLine($"  - {Code(change)}");
                    }
                    else if (!string.Equals(finding.baselineState, finding.liveState, StringComparison.Ordinal))
                    {
                        sb.AppendLine($"  - was: {Code(finding.baselineState)}");
                        sb.AppendLine($"  - now: {Code(finding.liveState)}");
                    }

                    continue;
                }

                if (finding.kind == "added")
                {
                    sb.AppendLine($"- **ADDED** {Code(finding.path)} — not in the baseline, and does " +
                                  "not look deliberate");

                    foreach (string change in finding.changes)
                        sb.AppendLine($"  - {Code(change)}");

                    continue;
                }

                if (finding.kind == "asset-changed")
                    sb.AppendLine($"- **ASSET** {Code(finding.path)} — the asset's contents changed");
                else if (finding.kind == "settings-changed")
                    sb.AppendLine($"- **SETTING** {Code(finding.path)} — a scene or project setting changed");
                else
                    sb.AppendLine($"- **CHANGED** {Code(finding.path)}");

                if (finding.changes.Count > 0)
                {
                    foreach (string change in finding.changes)
                        sb.AppendLine($"  - {Code(change)}");
                }
                else
                {
                    sb.AppendLine($"  - was: {Code(finding.baselineState)}");
                    sb.AppendLine($"  - now: {Code(finding.liveState)}");
                }
            }

            sb.AppendLine();
        }

        /// <summary>
        /// Renders a value as inline code. Object names and state strings are arbitrary text that
        /// would otherwise be interpreted as markdown, so the fence is sized to the content rather
        /// than assumed — a name containing a backtick must not be able to break the document.
        /// </summary>
        private static string Code(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "`(none)`";

            // Newlines cannot survive inside inline code; a name containing one would split the
            // list item and silently drop the rest of the finding.
            value = value.Replace("\r", " ").Replace("\n", " ");

            int longestRun = 0;
            int run = 0;
            foreach (char c in value)
            {
                run = c == '`' ? run + 1 : 0;
                if (run > longestRun)
                    longestRun = run;
            }

            string fence = new string('`', longestRun + 1);
            string pad = value.StartsWith("`") || value.EndsWith("`") ? " " : "";

            return fence + pad + value + pad + fence;
        }

        private static string DescribeUtc(string utc)
        {
            return DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTime parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : utc;
        }

        // ── Writing ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes both renderings and returns their absolute paths, or null on failure.
        /// Never throws: a storage problem must not be able to change the verdict.
        /// </summary>
        public static WrittenReport Write(Report report, string folder)
        {
            if (report == null)
                return null;

            try
            {
                Directory.CreateDirectory(folder);

                string markdownPath = Path.Combine(folder, MarkdownFileName);
                string jsonPath = Path.Combine(folder, JsonFileName);

                // No BOM: these are read by CI tooling and diffed by git, and a BOM breaks both
                // in small, hard-to-see ways.
                var encoding = new UTF8Encoding(false);

                File.WriteAllText(markdownPath, RenderMarkdown(report), encoding);
                File.WriteAllText(jsonPath, JsonUtility.ToJson(report, true), encoding);

                return new WrittenReport { markdownPath = markdownPath, jsonPath = jsonPath };
            }
            catch (Exception e)
            {
                Debug.LogError("[Scene Baselines] Could not write regression report: " + e.Message);
                return null;
            }
        }

        // ── Paths and arguments ──────────────────────────────────────────────────

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        private static string ToAbsoluteProjectPath(string projectRelative)
        {
            return Path.IsPathRooted(projectRelative)
                ? projectRelative
                : Path.Combine(ProjectRoot, projectRelative);
        }

        /// <summary>Fixed file names in a stable folder: CI needs a path it can hard-code.</summary>
        public static string ResolveOutputFolder(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return Path.Combine(ProjectRoot, DefaultFolder);

            return Path.IsPathRooted(requested) ? requested : Path.Combine(ProjectRoot, requested);
        }

        private static List<string> ScenePathsWithBaselines()
        {
            return BaselineStore.LoadAll()
                .Select(b => b.scenePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ArgumentValue(string name)
        {
            return ArgumentValues(name).FirstOrDefault();
        }

        private static List<string> ArgumentValues(string name)
        {
            var values = new List<string>();
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    values.Add(args[i + 1]);
            }

            return values;
        }
    }
}
