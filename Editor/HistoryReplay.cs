using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SceneBaselines
{
    /// <summary>
    /// Replays a repository's own history to show what this tool would have caught, newest commit
    /// first, streaming each commit's findings as it reaches them.
    /// </summary>
    /// <remarks>
    /// A healthy project reports nothing, which is the correct answer and a useless demonstration:
    /// nobody can tell "it works and there is nothing wrong" from "it does not work". Replaying
    /// history answers that from the project's own past — on Boss Room it found regressions that
    /// entered through a PREFAB, where the scene's own diff shows nothing at all.
    ///
    /// ONE EDITOR CANNOT DO THIS, and the attempt is recorded here so nobody spends the day
    /// discovering it twice. Checking commits out underneath a live editor is far faster on paper,
    /// and it crashes Unity: a domain reload serializes every live object, swaps assemblies, then
    /// restores them, and objects with [SerializeReference] cannot survive being read back by
    /// assemblies that did not write them. Unity runs off the end of its own backup stream and dies
    /// with "the file 'MemoryStream' is corrupted", exit 21, inside
    /// SerializableManagedRefsUtilities::RestoreBackups. Three runs, three identical crashes, and
    /// neither a gentler refresh nor closing every scene first avoided it. Keeping one editor only
    /// for commits that touch no code would apply to under a fifth of them: 18 of the last 22 Boss
    /// Room commits touch .cs, .asmdef or Packages/.
    ///
    /// So the editor is relaunched per step, which the phase-2a sweep already measured at about a
    /// minute per commit. That forces one structural rule: THE CHECKOUT MUST HAPPEN WHILE NO EDITOR
    /// IS RUNNING, so a small external driver owns checkouts. Everything else stays here, where it
    /// can be tested — this decides what to do next and names the commit the driver should check
    /// out, and the driver only ever does "check out what it says, run the editor again, repeat".
    /// </remarks>
    public static class HistoryReplay
    {
        private const string LogPrefix = "[Scene Baselines]";

        /// <summary>How many commits back a run goes when the caller does not say.</summary>
        /// <remarks>
        /// Small on purpose. The point is the first finding, not completeness: a commit costs about
        /// a minute, so ten is a coffee break and a hundred is an afternoon nobody will wait through
        /// to find out whether the tool is worth installing.
        /// </remarks>
        public const int DefaultCommitCount = 10;

        /// <summary>How far back the planner looks for those commits.</summary>
        /// <remarks>
        /// Only commits that changed something the scenes depend on are replayed, so the planner
        /// has to read further back than it reports. This bounds that reading: on a project where
        /// the scenes are touched rarely, a run says how far it looked and finds what it found,
        /// rather than walking a hundred thousand commits to fill a quota nobody asked for.
        /// </remarks>
        public const int DefaultScanDepth = 500;

        /// <summary>Nothing left to do; the driver stops.</summary>
        public const int ExitComplete = 0;

        /// <summary>One step done: check out <c>nextCheckout</c> and call again.</summary>
        public const int ExitContinue = 7;

        /// <summary>Refused to start, or cannot carry on.</summary>
        public const int ExitRefused = 2;

        /// <summary>A count git could not supply. Distinct from a count of zero, which is a fact.</summary>
        private const int UnknownCount = -1;

        // ── The plan ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole run, in a file, because nothing else outlives a step.
        /// </summary>
        /// <remarks>
        /// It lives outside the repository being replayed. Writing it inside would put the run's own
        /// memory in the path of the checkouts it is driving — the next commit would revert or
        /// delete it — quite apart from the promise that a run leaves the inspected project alone.
        /// </remarks>
        [Serializable]
        public class Plan
        {
            /// <summary>Branch to return to, or a sha when the run started detached.</summary>
            public string restoreRef;
            public bool restoreIsBranch;

            /// <summary>Commits to replay, newest first. Commit i is compared with parents[i].</summary>
            /// <remarks>
            /// Deliberately NOT assumed to be adjacent to one another. With the dependency filter
            /// on, the commits in between changed nothing these scenes depend on and are never
            /// opened at all — but each commit here is still compared with its OWN parent, so a
            /// finding belongs to one commit rather than to a range nobody can pick apart.
            /// </remarks>
            public List<string> commits = new List<string>();
            public List<string> parents = new List<string>();
            public List<string> subjects = new List<string>();
            public List<string> scenes = new List<string>();

            /// <summary>
            /// How many files each commit changed, and how many of those the scenes depend on.
            /// </summary>
            /// <remarks>
            /// Read while planning, because by the time a commit is reported the repository has
            /// moved and asking git again would answer about a different checkout.
            /// <see cref="UnknownCount"/> where git said nothing useful: a merge lists no files of
            /// its own, and "0 file(s) changed" would read as a measurement rather than an absence.
            /// </remarks>
            public List<int> fileCounts = new List<int>();
            public List<int> dependencyFileCounts = new List<int>();

            /// <summary>How the commits were chosen. The report states all of it.</summary>
            public bool filtered;
            public int scanned;
            public int skipped;
            public int closureSize;

            /// <summary>Which pair is being worked on.</summary>
            public int index;

            /// <summary>The parent's baselines are captured, so the child is what comes next.</summary>
            public bool recorded;

            /// <summary>What the driver must check out before the next step. The way back when done.</summary>
            public string nextCheckout;

            public bool done;
            public int findings;

            /// <summary>Scene checks across the run that compared nothing. Never folded into a pass.</summary>
            public int scenesNotChecked;
            public int commitsReported;
            public string reportPath;
            public string startedUtc;
            public string stoppedReason;

            /// <summary>Root the per-pair baseline folders hang from. Outside the repository.</summary>
            public string baselineRoot;

            /// <summary>Files Unity had rewritten before the run, copied aside and put back after.</summary>
            public string backupDir;
            public List<string> backedUp = new List<string>();
        }

        public static Plan Load(string path)
        {
            return File.Exists(path) ? JsonUtility.FromJson<Plan>(File.ReadAllText(path)) : null;
        }

        private static void Save(Plan plan, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));

            // Written whole, then moved into place. A state file caught half-written by an exiting
            // editor would resume as a plan that parses to nothing, having already moved the
            // repository somewhere that plan no longer records.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(plan, true), new UTF8Encoding(false));

            if (File.Exists(path))
                File.Delete(path);

            File.Move(temporary, path);
        }

        // ── One step per launch ──────────────────────────────────────────────────

        /// <summary>
        /// Does one step of a replay and exits, naming what the driver should check out next.
        /// </summary>
        /// <remarks>
        /// -executeMethod SceneBaselines.HistoryReplay.RunStep, with -replayState &lt;file&gt; and,
        /// on the first call, -replayCommits &lt;n&gt; and optional repeated -replayScene &lt;path&gt;.
        /// Exits <see cref="ExitContinue"/> while there is more to do, <see cref="ExitComplete"/>
        /// when the run is over, and <see cref="ExitRefused"/> when it will not or cannot go on.
        /// Never pass -quit: this exits by itself, with the code the driver reads.
        /// </remarks>
        public static void RunStep()
        {
            string statePath = ArgumentValue("-replayState");

            if (string.IsNullOrEmpty(statePath))
            {
                Debug.LogError($"{LogPrefix} -replayState <file> is required: it is where a replay keeps " +
                               "its plan between steps.");
                EditorApplication.Exit(ExitRefused);
                return;
            }

            try
            {
                Plan plan = Load(statePath);

                if (plan == null)
                {
                    // The first launch only plans. It does not capture anything, because the tree is
                    // still on the user's own commit and there is nothing yet to compare it with.
                    plan = Create();

                    if (plan == null)
                    {
                        EditorApplication.Exit(ExitRefused);
                        return;
                    }
                }
                else if (plan.done)
                {
                    EditorApplication.Exit(ExitComplete);
                    return;
                }
                else if (!Step(plan))
                {
                    Save(plan, statePath);
                    EditorApplication.Exit(ExitRefused);
                    return;
                }

                Save(plan, statePath);
                EditorApplication.Exit(plan.done ? ExitComplete : ExitContinue);
            }
            catch (Exception e)
            {
                Debug.LogError($"{LogPrefix} Replay step failed: {e}");
                EditorApplication.Exit(ExitRefused);
            }
        }

        /// <summary>Builds the plan and asks for the first checkout, capturing nothing.</summary>
        private static Plan Create()
        {
            string refusal = WhyNotSafeToStart(out List<string> machineWritten);
            if (refusal != null)
            {
                Debug.LogError($"{LogPrefix} Not starting a replay: {refusal}");
                return null;
            }

            var plan = new Plan
            {
                startedUtc = DateTime.UtcNow.ToString("o"),
                scenes = ArgumentValues("-replayScene")
            };

            if (plan.scenes.Count == 0)
                plan.scenes = AllScenes();

            if (plan.scenes.Count == 0)
            {
                Debug.LogError($"{LogPrefix} Not starting a replay: the project has no scenes to check.");
                return null;
            }

            // The way back, read before anything moves. A run that cannot say where it came from has
            // no business checking anything out.
            string branch = Git("rev-parse --abbrev-ref HEAD", out _)?.Trim();
            if (branch == null)
            {
                Debug.LogError($"{LogPrefix} Not starting a replay: this is not a git repository, or git " +
                               "is not on PATH.");
                return null;
            }

            plan.restoreIsBranch = branch != "HEAD";
            plan.restoreRef = plan.restoreIsBranch ? branch : Git("rev-parse HEAD", out _)?.Trim();

            int count = DefaultCommitCount;
            string requested = ArgumentValue("-replayCommits");
            if (!string.IsNullOrEmpty(requested) && !int.TryParse(requested, out count))
            {
                Debug.LogError($"{LogPrefix} -replayCommits must be a number, got '{requested}'.");
                return null;
            }

            if (count < 1)
            {
                Debug.LogError($"{LogPrefix} -replayCommits must be at least 1, got {count}.");
                return null;
            }

            int scan = DefaultScanDepth;
            string depth = ArgumentValue("-replayScan");
            if (!string.IsNullOrEmpty(depth) && !int.TryParse(depth, out scan))
            {
                Debug.LogError($"{LogPrefix} -replayScan must be a number, got '{depth}'.");
                return null;
            }

            plan.filtered = !string.Equals(ArgumentValue("-replayFilter"), "off",
                StringComparison.OrdinalIgnoreCase);

            HashSet<string> closure = null;
            if (plan.filtered)
            {
                closure = DependencyClosure(plan.scenes);
                plan.closureSize = closure.Count;
            }

            // %P is the parent list, and reading it here rather than asking git once per commit is
            // what lets the plan skip commits without losing track of what each one is compared
            // with. --name-only over the scan window is the expensive half of planning, and it is
            // paid ONCE: filtering with a git pathspec instead would put the whole dependency list
            // on a command line, which a real project overflows.
            string format = "--format=%x01%H%x1f%P%x1f%s";
            string log = plan.filtered
                ? Git($"-c core.quotepath=false log {format} --name-only -n {scan} HEAD", out _)
                : Git($"log {format} -n {Math.Max(1, count)} HEAD", out _);

            if (string.IsNullOrEmpty(log))
            {
                Debug.LogError($"{LogPrefix} Not starting a replay: could not read the commit log.");
                return null;
            }

            int scanned, skipped;
            List<CommitRecord> chosen = SelectCommits(ParseLog(log), closure, count, out scanned, out skipped);

            plan.scanned = scanned;
            plan.skipped = skipped;

            foreach (CommitRecord record in chosen)
            {
                plan.commits.Add(record.sha);
                plan.parents.Add(record.parent);
                plan.subjects.Add(record.subject);

                // Planning with the filter off reads the log without --name-only, so the file list
                // is asked for here instead: once per commit that will actually be replayed, which
                // is nothing beside the editor launch each one costs.
                List<string> files = record.files.Count > 0 ? record.files : FilesChangedBy(record.sha);

                plan.fileCounts.Add(files.Count > 0 ? files.Count : UnknownCount);
                plan.dependencyFileCounts.Add(closure == null ? UnknownCount : CountIn(closure, files));
            }

            if (plan.commits.Count == 0)
            {
                // Nothing to look at is not the same as something being broken, and the reader's
                // first guess will be the second one, so both ways out are named here.
                Debug.LogError($"{LogPrefix} Not starting a replay: none of the last {scanned} commit(s) " +
                               (plan.filtered
                                   ? $"changed any of the {plan.closureSize} file(s) these scene(s) depend on. " +
                                     "Look further back with -replayScan <n>, or replay every commit with " +
                                     "-replayFilter off."
                                   : "has a parent to be compared with."));
                return null;
            }

            plan.reportPath = Path.Combine(RegressionReport.ResolveOutputFolder(ArgumentValue("-reportDir")),
                "history-replay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");

            // Beside the state file when nobody says otherwise, because the default baseline folder
            // is INSIDE the project — which a replay is checking out over, and promised to leave
            // alone. A baseline written there would be reverted by the next commit anyway.
            plan.baselineRoot = ArgumentValue("-baselineDir");
            if (string.IsNullOrEmpty(plan.baselineRoot))
            {
                plan.baselineRoot = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(ArgumentValue("-replayState"))), "baselines");
            }

            BackUpMachineWrittenFiles(plan, machineWritten);
            BeginReport(plan);

            // The first thing the driver must do: put the tree on the parent of the newest commit,
            // so that commit has a known-good state to be compared against.
            plan.nextCheckout = plan.parents[0];

            Debug.Log($"{LogPrefix} Replaying {plan.commits.Count} commit(s) over {plan.scenes.Count} " +
                      $"scene(s), newest first. Returning to '{plan.restoreRef}' at the end. " +
                      $"Report: {plan.reportPath}");

            if (plan.filtered)
            {
                Debug.Log($"{LogPrefix} Of the last {plan.scanned} commit(s), {plan.skipped} changed nothing " +
                          $"these scene(s) depend on and will not be opened. The dependency list holds " +
                          $"{plan.closureSize} file(s).");
            }

            return plan;
        }

        /// <summary>Does the one thing this launch is for. False means the run cannot continue.</summary>
        private static bool Step(Plan plan)
        {
            if (plan.index >= plan.commits.Count)
            {
                Complete(plan, "finished");
                return true;
            }

            string child = plan.commits[plan.index];
            string parent = plan.parents[plan.index];
            string expected = plan.recorded ? child : parent;

            // The driver owns checkouts, so a mismatch means the two have lost track of each other.
            // Capturing anyway would file findings against the wrong commit, which is worse than
            // stopping and saying so.
            string head = Git("rev-parse HEAD", out _)?.Trim();
            if (head == null || !head.StartsWith(expected.Substring(0, 8), StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"{LogPrefix} Expected the tree at {Short(expected)}, found {Short(head ?? "unknown")}. " +
                               "Stopping rather than reporting findings against the wrong commit.");

                plan.stoppedReason = "the working tree was not at the expected commit";
                plan.nextCheckout = plan.restoreRef;
                plan.done = true;
                return false;
            }

            // A commit whose scripts do not compile cannot be captured at all: Unity refuses to run
            // editor code, so a check here would report an empty project as a mass deletion. Six of
            // 22 Boss Room commits are in that state, because pre-release packages they pinned are no
            // longer served. It belongs in the report as a stated limit, not as invented findings.
            if (EditorUtility.scriptCompilationFailed)
            {
                AppendReport(plan, $"\n## Cannot replay past `{Short(expected)}`\n\n" +
                    "The project's scripts do not compile at this commit, so Unity cannot run a capture " +
                    "here. Everything newer than it was replayed; nothing older can be.\n");

                Complete(plan, $"stopped at {Short(expected)}: its scripts do not compile");
                return true;
            }

            UsePairFolder(plan);

            if (!plan.recorded)
            {
                RecordAt(plan, parent);
                plan.recorded = true;
                plan.nextCheckout = child;
                return true;
            }

            CheckAt(plan, child, parent);
            plan.index++;
            plan.recorded = false;

            if (plan.index >= plan.commits.Count)
                Complete(plan, "finished");
            else
                plan.nextCheckout = plan.parents[plan.index];

            return true;
        }

        private static void Complete(Plan plan, string reason)
        {
            plan.done = true;
            plan.stoppedReason = reason;

            // The last checkout the driver performs is the way back. Restoring is part of the plan
            // rather than something the driver is trusted to remember, so a run that ends in any way
            // still ends with the repository where it was found.
            plan.nextCheckout = plan.restoreRef;

            RestoreMachineWrittenFiles(plan);
            FinishReport(plan, reason);

            Debug.Log($"{LogPrefix} Replay {reason}: {plan.commitsReported} commit(s) reported, " +
                      $"{plan.findings} finding(s) in total" +
                      (plan.scenesNotChecked > 0 ? $", {plan.scenesNotChecked} scene check(s) NOT CHECKED" : "") +
                      $". Report: {plan.reportPath}");
        }

        /// <summary>
        /// Sends this pair's baselines to a folder of their own.
        /// </summary>
        /// <remarks>
        /// Found by reading the first working run's report: with one shared folder the baselines
        /// pile up, so the third commit was compared against its own parent AND against a baseline
        /// recorded two commits earlier. That second comparison is not wrong so much as meaningless
        /// — it answers "what changed across three commits" while the section it is printed under
        /// says "compared against its parent" — and it reported the same finding again at every
        /// commit that followed.
        ///
        /// A pair is a question about two commits, so it gets a folder holding exactly the answer
        /// to that question.
        /// </remarks>
        private static void UsePairFolder(Plan plan)
        {
            BaselineStore.FolderOverride = Path.Combine(plan.baselineRoot,
                "pair-" + plan.index.ToString("D4"));
        }

        private static void RecordAt(Plan plan, string parent)
        {
            int captured = 0;

            foreach (string scene in plan.scenes)
            {
                if (!File.Exists(ToAbsolute(scene)))
                    continue;               // the scene does not exist this far back; not an error

                EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
                RecordBaseline.Result result = RecordBaseline.Record();

                // A duplicate is a success: most scenes do not change between adjacent commits, so
                // "this state is already the latest baseline" is the expected answer, and the
                // existing baseline stays the one the check compares against.
                if (result.Succeeded || result.outcome == RecordBaseline.Outcome.Duplicate)
                    captured++;
                else
                    Debug.LogWarning($"{LogPrefix} Could not capture '{scene}' at {Short(parent)}: {result.message}");
            }

            Debug.Log($"{LogPrefix} Replay: captured {captured} scene(s) at {Short(parent)}.");
        }

        private static void CheckAt(Plan plan, string child, string parent)
        {
            // Every scene that exists now, AND every scene that was recorded at the parent but is gone
            // now. Checking only the scenes present here made a commit that DELETED a scene report
            // "no findings" for it — the scene was simply never asked about.
            var toCheck = new List<string>();
            foreach (string scene in plan.scenes)
            {
                if (File.Exists(ToAbsolute(scene)) || BaselineStore.LoadForScene(scene).Count > 0)
                    toCheck.Add(scene);
            }

            string subject = plan.subjects.Count > plan.index ? plan.subjects[plan.index] : "";

            var section = new StringBuilder();
            section.AppendLine();
            section.AppendLine($"## `{Short(child)}` {subject}");
            section.AppendLine();

            plan.commitsReported++;

            if (toCheck.Count == 0)
            {
                plan.scenesNotChecked += plan.scenes.Count;

                Debug.Log($"{LogPrefix} Replay {plan.commitsReported}/{plan.commits.Count}: " +
                          $"{Short(child)} {subject} — NOT CHECKED, none of the scene(s) exist here.");

                section.AppendLine($"**Not checked** against its parent `{Short(parent)}` — none of the " +
                                   "replayed scenes exist at this commit or its parent, so nothing was compared.");
                AppendReport(plan, section.ToString());
                return;
            }

            RegressionReport.Report report = RegressionReport.BuildForScenes(toCheck);
            int setAside = SetAsideProjectSettings(report);

            // A scene that compared nothing has no findings either, so "no findings" alone would read
            // as a pass for it. Named separately, every time.
            List<RegressionReport.ReportScene> notChecked = report.scenes
                .Where(s => s.verdict == RegressionCheck.VerdictToken(RegressionVerdict.NotChecked))
                .ToList();

            plan.findings += report.totalFindings;
            plan.scenesNotChecked += notChecked.Count;

            // Streamed, not saved for the end: the number that decides whether this is usable is
            // time to the FIRST finding, and a reader watching a console learns nothing from a run
            // that stays silent until it is done.
            Debug.Log($"{LogPrefix} Replay {plan.commitsReported}/{plan.commits.Count}: " +
                      $"{Short(child)} {subject} — {report.totalFindings} finding(s)" +
                      (notChecked.Count > 0 ? $", {notChecked.Count} scene(s) NOT CHECKED." : "."));

            string outcome = report.totalFindings > 0
                ? $"**{report.totalFindings} finding(s)**"
                : notChecked.Count > 0 ? "no findings in what could be compared" : "no findings";

            section.AppendLine($"Compared against its parent `{Short(parent)}` — {outcome}.");
            section.AppendLine();

            if (notChecked.Count > 0)
            {
                section.AppendLine($"**{notChecked.Count} scene(s) not checked here** — nothing about them is " +
                                   "verified at this commit:");
                section.AppendLine();

                foreach (RegressionReport.ReportScene scene in notChecked)
                    section.AppendLine($"- `{scene.scenePath}` — {DescribeNotChecked(scene)}");

                section.AppendLine();
            }

            string scope = ScopeLine(plan, report);
            if (scope != null)
            {
                section.AppendLine(scope);
                section.AppendLine();
            }

            // Said per commit as well as once at the top, because a reader who scrolls to one
            // commit's section must be able to see that something was not compared there.
            if (setAside > 0)
            {
                section.AppendLine($"*{setAside} project-settings finding(s) set aside here — a replay " +
                                   "cannot attribute those to a commit; see \"How to read this\" at the end.*");
                section.AppendLine();
            }

            if (report.totalFindings > 0)
            {
                // Rendered through the ordinary report renderer, so a replayed commit reads exactly
                // like a checked one: same grouping, same cap, same counts stated before the groups.
                string markdown = RegressionReport.RenderMarkdown(report);
                int cut = markdown.IndexOf("## Scene ", StringComparison.Ordinal);
                section.AppendLine(cut >= 0 ? markdown.Substring(cut) : markdown);
            }

            AppendReport(plan, section.ToString());
        }

        /// <summary>Why a scene compared nothing, in terms of the two commits being compared.</summary>
        private static string DescribeNotChecked(RegressionReport.ReportScene scene)
        {
            switch (scene.notCheckedReason)
            {
                case "no baselines recorded for this scene":
                    return "nothing was recorded for it at the parent: the scene is new in this commit, " +
                           "or its capture failed there (see the console)";

                case "the scene file no longer exists at this path":
                    return "the scene was deleted or moved in this commit";

                default:
                    return string.IsNullOrEmpty(scene.notCheckedReason) ? scene.headline : scene.notCheckedReason;
            }
        }

        /// <summary>
        /// One line saying how far the commit reached, beside how far the scenes moved.
        /// </summary>
        /// <remarks>
        /// The one thing a reader cannot get from the findings themselves. "45 object(s) differ"
        /// means nothing until it sits next to "2 file(s) changed": a large refactor that moves a
        /// lot of scene is ordinary, and a two-file commit that moves a lot of scene is the shape
        /// of a change nobody knew they were making. On Boss Room the regression worth catching
        /// arrived through a prefab, where the scene's own diff is empty.
        ///
        /// 🚨 CONTEXT, DELIBERATELY NOT A SCORE, and that is measured rather than cautious.
        /// Over 15 replayed Boss Room commits, findings per file changed ranks the one known true
        /// regression NINTH of fifteen, and "findings in a scene whose own file the commit never
        /// touched" holds for 59 of 85 scene sections — in a prefab-heavy project that is
        /// the normal case, not an alarm. So both numbers are printed and the reader judges.
        /// Ordering findings by this would bury the one that matters under the ones that look big.
        /// </remarks>
        private static string ScopeLine(Plan plan, RegressionReport.Report report)
        {
            int files = plan.index < plan.fileCounts.Count ? plan.fileCounts[plan.index] : UnknownCount;
            int dependencies = plan.index < plan.dependencyFileCounts.Count
                ? plan.dependencyFileCounts[plan.index]
                : UnknownCount;

            int objects, assets;
            CountScope(report, out objects, out assets);

            var moved = new List<string>();
            if (objects > 0)
                moved.Add($"{objects} object(s)");
            if (assets > 0)
                moved.Add($"{assets} asset(s)");

            string reached = null;
            if (files != UnknownCount)
            {
                reached = $"{files} file(s) changed";
                if (dependencies != UnknownCount)
                    reached += $", {dependencies} of them file(s) these scene(s) depend on";
            }

            string differs = moved.Count == 0
                ? "no object or asset differs"
                : string.Join(" and ", moved.ToArray()) + " differ";

            if (reached == null)
                return moved.Count == 0 ? null : $"Scope: {differs}.";

            return $"Scope: {reached} — {differs}.";
        }

        /// <summary>
        /// How many distinct things a report says differ, as against how many findings it holds.
        /// </summary>
        /// <remarks>
        /// Two different numbers, for the reason the roll-up exists: one object with eleven changed
        /// properties is eleven findings and one surprise. Scope is about how far a commit reached,
        /// so an object counts once however much of it moved, and the same path in two scenes
        /// counts twice because those are two objects. Settings are left out — they belong
        /// to a scene or to the project, not to a place the commit reached.
        /// </remarks>
        private static void CountScope(RegressionReport.Report report, out int objects, out int assets)
        {
            string assetKind = RegressionReport.FindingKindToken(RegressionKind.AssetChanged);
            string settingKind = RegressionReport.FindingKindToken(RegressionKind.SettingsChanged);

            var seenObjects = new HashSet<string>(StringComparer.Ordinal);
            var seenAssets = new HashSet<string>(StringComparer.Ordinal);

            foreach (RegressionReport.ReportScene scene in report.scenes)
            {
                foreach (RegressionReport.ReportBaseline baseline in scene.baselines)
                {
                    foreach (RegressionReport.ReportFinding finding in baseline.findings)
                    {
                        if (finding.kind == assetKind)
                            seenAssets.Add(finding.path);
                        else if (finding.kind != settingKind)
                            seenObjects.Add(scene.scenePath + "|" + finding.path);
                    }
                }
            }

            objects = seenObjects.Count;
            assets = seenAssets.Count;
        }

        /// <summary>
        /// Drops project-wide settings findings from a replayed comparison, and returns how many.
        /// </summary>
        /// <remarks>
        /// 🚨 Measured, not assumed, and the reason is Unity rather than this tool. Project settings
        /// are not assets: they are read once into the editor, and a checkout that changes
        /// ProjectSettings/*.asset underneath a stopped editor does not reliably reach the next
        /// session's view of them. On the Boss Room corpus a replay recorded 20 tags including
        /// "Heavy" at two commits whose committed TagManager.asset holds 19 and no "Heavy" at all —
        /// the tag belongs to a commit outside the replayed range entirely. The next commit then
        /// "lost" it, and a commit that changed nothing but README.md reported two findings.
        ///
        /// So a replay does not compare them. This is filtering, which everything else in this
        /// codebase refuses to do, and it is only tolerable because it is stated twice: once in the
        /// report header and once in the section of every commit where something was set aside.
        /// Scene-scope settings stay: those live in the scene file, which Unity does re-import.
        ///
        /// An ordinary check is unaffected. There the editor's view and the working tree are the
        /// same thing, which is exactly the assumption a replay breaks.
        /// </remarks>
        private static int SetAsideProjectSettings(RegressionReport.Report report)
        {
            int removed = 0;

            foreach (RegressionReport.ReportScene scene in report.scenes)
            {
                int removedHere = 0;

                foreach (RegressionReport.ReportBaseline baseline in scene.baselines)
                {
                    int before = baseline.findings.Count;

                    baseline.findings.RemoveAll(f =>
                        f.kind == "settings-changed" &&
                        f.path != null &&
                        f.path.StartsWith("project/", StringComparison.Ordinal));

                    int dropped = before - baseline.findings.Count;
                    if (dropped == 0)
                        continue;

                    removedHere += dropped;
                    scene.findingCount -= dropped;

                    // A baseline left holding nothing is a pass, and must read as one: leaving the
                    // verdict at "regressions" over an empty list would be the report contradicting
                    // itself in the one place a reader looks to decide whether to worry.
                    if (baseline.findings.Count == 0)
                        baseline.verdict = "pass";
                }

                // 🚨 Only a scene this filter actually touched is re-judged. This used to rewrite
                // every scene with no findings as a PASS — including scenes that were never compared,
                // which have no findings precisely BECAUSE nothing was compared: the commit that adds
                // a scene, a parent whose capture failed. "Not checked" read as "passed", which is
                // the one answer this tool may never give.
                if (removedHere == 0)
                    continue;

                removed += removedHere;
                RejudgeScene(scene);
            }

            // The run's verdict is the worst of its scenes, as it was when the report was built —
            // never "pass" merely because no findings are left, which a not-checked scene also has.
            RegressionReport.ApplyWorstVerdict(report);

            return removed;
        }

        /// <summary>
        /// A scene's verdict and headline, worked out again from its baselines after findings were
        /// set aside. Reworded in the shape the ordinary renderer uses, so a replayed scene and a
        /// checked one do not describe the same situation two different ways.
        /// </summary>
        private static void RejudgeScene(RegressionReport.ReportScene scene)
        {
            scene.findingCount = Math.Max(0, scene.findingCount);

            var compared = scene.baselines.Where(b => b.verdict != "not-checked").ToList();
            int failing = compared.Count(b => b.findings.Count > 0);

            if (compared.Count == 0)
            {
                // Findings were removed, so something was compared; kept for safety all the same.
                scene.verdict = "not-checked";
                return;
            }

            if (scene.findingCount == 0)
            {
                scene.verdict = "pass";
                scene.headline = $"PASS — {scene.baselinesCompared} baseline(s) still hold.";
            }
            else if (compared.Any(b => b.findings.Count > 0 && b.verdict == "regressions"))
            {
                scene.verdict = "regressions";
                scene.headline = $"REGRESSIONS FOUND — {scene.findingCount} across {failing} of " +
                                 $"{scene.baselinesCompared} baseline(s).";
            }
            else
            {
                scene.verdict = "unconfirmed";
                scene.headline = $"UNCONFIRMED — {scene.findingCount} difference(s) across {failing} of " +
                                 $"{scene.baselinesCompared} baseline(s), but no affected baseline can prove " +
                                 "its recorded state ever reached disk, so these may be unsaved work reverting " +
                                 "rather than regressions.";
            }
        }

        // ── Choosing the commits ─────────────────────────────────────────────────

        /// <summary>One commit as the planner reads it: what it is, what it came from, what it touched.</summary>
        public class CommitRecord
        {
            public string sha;
            public string parent;
            public string subject;
            public List<string> files = new List<string>();
        }

        /// <summary>
        /// Every file the given scenes depend on, spelled the way git spells paths.
        /// </summary>
        /// <remarks>
        /// Deliberately not "the scene files". A scene's own diff is empty for the regressions this
        /// tool exists to catch — the prefab that moved, the material whose queue changed — so
        /// choosing commits by whether they touched a scene file would hide precisely the findings
        /// worth having. What a scene depends on is the smallest set that cannot hide one.
        ///
        /// .meta files are added on top: they are not dependencies, but import settings live in
        /// them and import settings change what a capture sees. Paths outside the repository
        /// (built-in resources, registry packages) stay in the set and simply never match anything
        /// git reports, which costs a string comparison and no correctness.
        /// </remarks>
        internal static HashSet<string> DependencyClosure(List<string> scenes)
        {
            var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = RepositoryPrefix();

            foreach (string scene in scenes)
                AddToClosure(closure, prefix, scene);

            foreach (string dependency in AssetDatabase.GetDependencies(scenes.ToArray(), true))
                AddToClosure(closure, prefix, dependency);

            return closure;
        }

        private static void AddToClosure(HashSet<string> closure, string prefix, string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            string spelled = prefix + path.Replace('\\', '/');
            closure.Add(spelled);
            closure.Add(spelled + ".meta");
        }

        /// <summary>
        /// What git puts in front of a project path when the project is not the repository root.
        /// </summary>
        /// <remarks>
        /// A Unity project is often a subdirectory of the repository holding it, and git reports
        /// paths from the repository root. Comparing "Assets/Foo.prefab" against
        /// "game/Assets/Foo.prefab" matches nothing, and matching nothing does not look like a
        /// failure here — it looks like a history in which no commit ever touched the scenes. The
        /// filter would quietly skip everything, so this is worth getting right.
        /// </remarks>
        private static string RepositoryPrefix()
        {
            string top = Git("rev-parse --show-toplevel", out _);
            if (string.IsNullOrEmpty(top))
                return "";

            string root = Path.GetFullPath(top.Trim()).Replace('\\', '/').TrimEnd('/');
            string project = Path.GetFullPath(ProjectRoot()).Replace('\\', '/').TrimEnd('/');

            if (project.Length <= root.Length ||
                !project.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return project.Substring(root.Length).Trim('/') + "/";
        }

        /// <summary>Reads `log --format=%x01%H%x1f%P%x1f%s --name-only` into records.</summary>
        /// <remarks>
        /// Records start with U+0001 rather than being split on blank lines, so a subject can hold
        /// anything at all, and a commit that lists no files stays a record rather than becoming a
        /// gap between two others.
        /// </remarks>
        internal static List<CommitRecord> ParseLog(string log)
        {
            var records = new List<CommitRecord>();

            if (string.IsNullOrEmpty(log))
                return records;

            foreach (string chunk in log.Split('\u0001'))
            {
                if (chunk.Trim().Length == 0)
                    continue;

                string[] lines = chunk.Split('\n');
                string[] head = lines[0].TrimEnd('\r').Split('\u001f');

                var record = new CommitRecord
                {
                    sha = head[0].Trim(),
                    parent = head.Length > 1 ? FirstParent(head[1]) : null,
                    subject = head.Length > 2 ? head[2].Trim() : ""
                };

                for (int i = 1; i < lines.Length; i++)
                {
                    string file = lines[i].TrimEnd('\r').Trim();
                    if (file.Length > 0)
                        record.files.Add(file);
                }

                if (record.sha.Length > 0)
                    records.Add(record);
            }

            return records;
        }

        /// <summary>The files one commit changed, as git spells them.</summary>
        private static List<string> FilesChangedBy(string sha)
        {
            var files = new List<string>();
            string listed = Git("-c core.quotepath=false show --name-only --format= " + sha, out _);

            foreach (string line in (listed ?? "").Split('\n'))
            {
                string file = line.TrimEnd('\r').Trim();
                if (file.Length > 0)
                    files.Add(file);
            }

            return files;
        }

        /// <summary>How many of the given files the scenes depend on.</summary>
        private static int CountIn(HashSet<string> closure, List<string> files)
        {
            int hits = 0;

            foreach (string file in files)
            {
                if (closure.Contains(file))
                    hits++;
            }

            return hits;
        }

        private static string FirstParent(string parents)
        {
            string[] shas = parents.Trim().Split(' ');
            return shas.Length > 0 && shas[0].Length > 0 ? shas[0] : null;
        }

        /// <summary>
        /// Picks the commits worth launching an editor for, newest first.
        /// </summary>
        /// <remarks>
        /// A pure function of what git said, so that which commits get replayed can be tested
        /// without a repository to replay. A null closure takes commits as they come.
        ///
        /// The bar for skipping a commit is that it CANNOT have changed how the scenes look, never
        /// that it seems unlikely to have. Two consequences worth stating: a path git had to quote
        /// is unreadable here, so that commit is kept rather than judged; and a merge lists no
        /// files of its own, so it is skipped while the commits it merged — which are in this same
        /// walk — are not. Skipping a real finding to save a minute is the wrong trade for a tool
        /// whose entire claim is that it does not miss things.
        /// </remarks>
        internal static List<CommitRecord> SelectCommits(List<CommitRecord> records,
            HashSet<string> closure, int count, out int scanned, out int skipped)
        {
            var chosen = new List<CommitRecord>();

            scanned = 0;
            skipped = 0;

            foreach (CommitRecord record in records)
            {
                if (chosen.Count >= count)
                    break;

                scanned++;

                // The first commit in a repository has nothing to be compared against.
                if (string.IsNullOrEmpty(record.parent))
                    continue;

                if (closure != null && !Touches(record, closure))
                {
                    skipped++;
                    continue;
                }

                chosen.Add(record);
            }

            return chosen;
        }

        private static bool Touches(CommitRecord record, HashSet<string> closure)
        {
            foreach (string file in record.files)
            {
                // git quotes a path it had to escape. Rather than guess what it spells, keep the
                // commit: a needless replay costs a minute, a wrongly skipped one costs a finding.
                if (file.StartsWith("\"", StringComparison.Ordinal))
                    return true;

                if (closure.Contains(file))
                    return true;
            }

            return false;
        }

        // ── Refusing to destroy things ───────────────────────────────────────────

        /// <summary>Why a replay must not start right now, or null when it may.</summary>
        internal static string WhyNotSafeToStart(out List<string> machineWritten)
        {
            machineWritten = new List<string>();

            if (EditorApplication.isPlaying)
                return "Play Mode is running.";

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                    return "an open scene has unsaved changes, and replaying would discard them. Save first.";
            }

            string status = Git("status --porcelain", out string error);
            if (error != null)
                return "git could not read the working tree: " + error;

            var mine = new List<string>();
            int untracked = 0;

            foreach (string line in status.Split('\n'))
            {
                if (line.Trim().Length == 0)
                    continue;

                if (line.StartsWith("??", StringComparison.Ordinal))
                {
                    untracked++;
                    continue;
                }

                // Porcelain is "XY <path>", and a rename carries "old -> new".
                string path = line.Length > 3 ? line.Substring(3).Trim().Trim('"') : "";
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                    path = path.Substring(arrow + 4);

                if (IsMachineWritten(path))
                    machineWritten.Add(path);
                else
                    mine.Add(path);
            }

            if (mine.Count > 0)
            {
                return $"the working tree has {mine.Count} uncommitted change(s) to tracked files, starting " +
                       $"with '{mine[0]}'. Replaying force-checks-out other commits over them, which would " +
                       "destroy that work, so it will not run until the tree is clean.";
            }

            // Untracked files are warned about, never refused. A force checkout does not delete them,
            // and refusing would make the tool refuse to run on account of ITSELF: a package dropped
            // into someone's project is untracked there, as are the files Unity generates on first
            // import. What they can do is be present at a commit that predates them, so they are
            // worth a line in the log rather than silence.
            if (untracked > 0)
            {
                Debug.LogWarning($"{LogPrefix} {untracked} untracked path(s) in the working tree. They stay " +
                                 "put through every checkout, so whatever they hold is present at every " +
                                 "commit replayed — including commits from before they existed.");
            }

            return null;
        }

        /// <summary>
        /// Whether a path is one Unity rewrites on its own, rather than something a person edited.
        /// </summary>
        /// <remarks>
        /// Measured rather than guessed: opening the Boss Room corpus in a fresh editor dirtied
        /// exactly these before any of this code ran — the package manifest and its lock file are
        /// re-resolved, ProjectVersion.txt records the editor that opened it, and the per-package
        /// ProjectSettings folder is written by whatever tooling owns it. Refusing on these would
        /// mean refusing on every project that has ever been opened, which is all of them.
        ///
        /// This decides only whether a run may START. Everything on the list is still copied aside
        /// and put back, because "Unity usually writes this" is not proof that the user did not.
        /// </remarks>
        internal static bool IsMachineWritten(string path)
        {
            return path == "Packages/manifest.json"
                || path == "Packages/packages-lock.json"
                || path == "ProjectSettings/ProjectVersion.txt"
                || path.StartsWith("ProjectSettings/Packages/", StringComparison.Ordinal);
        }

        private static void BackUpMachineWrittenFiles(Plan plan, List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return;

            plan.backupDir = Path.Combine(Path.GetTempPath(),
                "SceneBaselines-replay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            foreach (string path in paths)
            {
                string source = ToAbsolute(path);
                if (!File.Exists(source))
                    continue;

                string destination = Path.Combine(plan.backupDir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, true);
                plan.backedUp.Add(path);
            }

            if (plan.backedUp.Count > 0)
            {
                Debug.Log($"{LogPrefix} {plan.backedUp.Count} file(s) Unity had rewritten were copied to " +
                          $"{plan.backupDir} and will be put back when the run ends.");
            }
        }

        private static void RestoreMachineWrittenFiles(Plan plan)
        {
            if (plan.backedUp == null || plan.backedUp.Count == 0 || string.IsNullOrEmpty(plan.backupDir))
                return;

            int restored = 0;

            foreach (string path in plan.backedUp)
            {
                string source = Path.Combine(plan.backupDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                    continue;

                try
                {
                    File.Copy(source, ToAbsolute(path), true);
                    restored++;
                }
                catch (Exception e)
                {
                    // Named out loud: a file this run moved and could not move back is the user's
                    // problem now, and they can only act on it if they are told which.
                    Debug.LogError($"{LogPrefix} Could not restore '{path}' from {plan.backupDir}: {e.Message}");
                }
            }

            if (restored > 0)
                Debug.Log($"{LogPrefix} Restored {restored} file(s) Unity had rewritten before the run.");
        }

        // ── Odds and ends ────────────────────────────────────────────────────────

        /// <summary>Runs git in the project's repository; returns stdout, or null on failure.</summary>
        private static string Git(string arguments, out string error)
        {
            return Git(ProjectRoot(), arguments, out error);
        }

        /// <summary>Runs git in the given directory; returns stdout, or null on failure.</summary>
        /// <remarks>
        /// 🚨 Read as UTF-8, explicitly. git writes paths as UTF-8 (core.quotepath=false says not to
        /// escape them), but a redirected stream is decoded with the CONSOLE's encoding unless told
        /// otherwise — IBM437 in the Windows editor. "Assets/კარი.prefab" then came back as 26
        /// characters of mojibake, matched nothing in the dependency list, and every commit that
        /// touched it was skipped as unrelated to the scenes. Skipping a real finding silently is the
        /// one thing the commit filter promises never to do.
        /// </remarks>
        private static string Git(string workingDirectory, string arguments, out string error)
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        error = string.IsNullOrWhiteSpace(stderr) ? "git exited " + process.ExitCode : stderr.Trim();
                        return null;
                    }

                    // TrimEnd, never Trim: porcelain status puts a SPACE in column one for a file
                    // modified but not staged, so trimming the front shifts the first line's path by
                    // one character and every path check downstream asks about a file that does not
                    // exist.
                    error = null;
                    return output.TrimEnd();
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static string ProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static string ToAbsolute(string projectPath)
        {
            return Path.Combine(ProjectRoot(), projectPath);
        }

        private static string Short(string sha)
        {
            return sha != null && sha.Length > 8 ? sha.Substring(0, 8) : sha;
        }

        /// <summary>
        /// Every scene in the project.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT the scenes a commit touched. The finding this feature exists to show is
        /// the one that arrives through a prefab or an asset, where the scene file itself is
        /// untouched and its own diff is empty; filtering by touched scenes would hide exactly that.
        ///
        /// Which is not the same thing as replaying every commit: <see cref="DependencyClosure"/>
        /// skips commits that touched nothing any of these scenes REFERS to, and the prefab in that
        /// example is referred to. Every scene, and every commit that could have changed one.
        /// </remarks>
        private static List<string> AllScenes()
        {
            var scenes = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:Scene"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Assets/", StringComparison.Ordinal))
                    scenes.Add(path);
            }

            scenes.Sort(StringComparer.Ordinal);
            return scenes;
        }

        private static string ArgumentValue(string name)
        {
            List<string> values = ArgumentValues(name);
            return values.Count > 0 ? values[values.Count - 1] : null;
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

        // ── The report, written as the run goes ──────────────────────────────────

        /// <summary>
        /// Opens the report: what this is, the one fact that decides what it means, and nothing else.
        /// </summary>
        /// <remarks>
        /// The caveats a replay owes its reader are real and none of them were removed — they moved
        /// to <see cref="AppendHowToRead"/> at the end. A first-time reader met three dense block
        /// quotes before a single finding, which is a strange way to sell a report whose entire
        /// value is the finding. Only the choosing of commits stays here, because it is the one
        /// thing that changes what the whole page MEANS: a reader who thinks these are simply the
        /// last N commits reads "no findings" as "nothing happened in the last N commits", which is
        /// a far stronger claim than the one being made. One sentence carries that; the rest waits.
        /// </remarks>
        private static void BeginReport(Plan plan)
        {
            var header = new StringBuilder();
            header.AppendLine("# History replay");
            header.AppendLine();
            header.AppendLine($"What this tool would have reported at {plan.commits.Count} commit(s) of this " +
                              $"project's own history, over {plan.scenes.Count} scene(s), newest first. Each " +
                              "commit is compared against its own parent: the parent is captured as " +
                              "known-good, then the commit is checked against it.");
            header.AppendLine();

            if (plan.filtered)
            {
                header.AppendLine($"These are the {plan.commits.Count} commit(s), out of the last " +
                                  $"{plan.scanned} looked at, that changed something these scene(s) depend " +
                                  $"on. The other {plan.skipped} cannot have changed how the scenes look and " +
                                  "were never opened.");
                header.AppendLine();
            }

            header.AppendLine("*How to read this, and what a replay does not compare, is at the end.*");
            header.AppendLine();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(plan.reportPath)));
            File.WriteAllText(plan.reportPath, header.ToString(), new UTF8Encoding(false));
        }

        /// <summary>The caveats, at the end, where they are not standing in front of the findings.</summary>
        private static void AppendHowToRead(Plan plan, StringBuilder tail)
        {
            tail.AppendLine();
            tail.AppendLine("## How to read this");
            tail.AppendLine();
            tail.AppendLine("> These are findings this tool WOULD have reported at the time. Objects added " +
                            "since a baseline are not regressions, and this checks scene state, not runtime " +
                            "behaviour. A finding is a real difference, not necessarily a mistake: most " +
                            "changes in a history were meant, and no tool can know which.");
            tail.AppendLine();

            if (plan.filtered)
            {
                tail.AppendLine($"> **How the {plan.commits.Count} commit(s) were chosen.** {plan.skipped} of " +
                                $"the last {plan.scanned} changed none of the {plan.closureSize} file(s) these " +
                                "scene(s) depend on, so they cannot have changed what the scenes look like and " +
                                "were never opened. The dependencies are the ones Unity reports for these " +
                                "scenes — the scene files, and the prefabs, materials, ScriptableObjects and " +
                                "scripts they reference, plus the .meta file of each. That is why a commit " +
                                "that never touched a scene file can still appear here: the regressions worth " +
                                "catching arrive through a prefab, where the scene's own diff is empty.");
                tail.AppendLine();
                tail.AppendLine("> Two limits of choosing them that way. The dependency list is read at the " +
                                "commit this run started from, so a file the scenes used to reference and no " +
                                "longer do is not noticed. And merge commits are not replayed: git attributes " +
                                "their changes to the commits they merge, which are replayed instead. Pass " +
                                "`-replayFilter off` to replay every commit regardless.");
                tail.AppendLine();
            }

            tail.AppendLine("> **Project-wide settings are not compared in a replay.** Unity reads them once " +
                            "per session rather than importing them like assets, so after a checkout the " +
                            "editor's view of tags, layers, physics and quality can still belong to a " +
                            "different commit — measured here, not assumed. Findings about them would say " +
                            "more about the editor than about the commit, so they are set aside and counted " +
                            "rather than reported. Scene settings, which live in the scene file, are compared " +
                            "as usual, and an ordinary (non-replay) check compares everything.");
            tail.AppendLine();
        }

        private static void AppendReport(Plan plan, string text)
        {
            File.AppendAllText(plan.reportPath, text, new UTF8Encoding(false));
        }

        private static void FinishReport(Plan plan, string reason)
        {
            var tail = new StringBuilder();
            tail.AppendLine();
            tail.AppendLine("---");
            tail.AppendLine();
            tail.AppendLine($"Replay {reason}: {plan.commitsReported} of {plan.commits.Count} commit(s) " +
                            $"reported, {plan.findings} finding(s) in total" +
                            (plan.scenesNotChecked > 0
                                ? $", and {plan.scenesNotChecked} scene check(s) that compared nothing — " +
                                  "see the sections marked \"not checked\"."
                                : "."));

            AppendHowToRead(plan, tail);

            try
            {
                AppendReport(plan, tail.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix} Could not finish the replay report: {e.Message}");
            }
        }
    }
}
