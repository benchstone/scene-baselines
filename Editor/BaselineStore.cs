using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SceneBaselines
{
    // ─── Regression baselines: storage (step 1 of 4) ─────────────────────────────
    //
    // A baseline is a known-good state of a scene, captured at the moment verification
    // passed cleanly. Later runs diff the live scene against it to answer the question
    // no agent session can answer on its own: "did a change break something that used
    // to work?" — an agent remembers what it BELIEVED happened; a baseline records what
    // DID happen, and only the second one can be diffed.
    //
    // Why the on-disk types are declared here and not reused from the verify loop:
    // SceneCapture.SceneSnapshot is an in-memory runtime type that is free to
    // change shape. A persisted format is not — old baselines must stay readable after
    // the loop is refactored, or the accumulated history (the whole point) is lost. The
    // small mapping cost at the boundary buys that independence, so keep these types
    // dumb and additive: add fields, never rename or repurpose one, and bump
    // SchemaVersion when the meaning of an existing field changes.
    //
    // Baselines live under Assets/ deliberately: they must be committed with the code,
    // diffable in review, and readable by CI and by teammates. A baseline that lives in
    // one developer's machine-local folder is a memory, not a baseline.

    [Serializable]
    public class BaselineObjectRecord
    {
        public string path;   // hierarchy path, "#n"-suffixed for same-named siblings — DISPLAY only
        public string state;  // components + transform + bounds, culture-invariant
        public string id;     // GlobalObjectId, the MATCHING key; empty when unusable or pre-v10
    }

    // An asset the scene depends on, recorded by CONTENTS rather than by name. Separate
    // from the objects that reference it so that one edited material reports as one
    // changed material, not as a change to each of the forty objects using it — which
    // would be unreadable and would blame the wrong thing.
    [Serializable]
    public class BaselineAssetRecord
    {
        public string path;   // asset path, "::name"-suffixed for a sub-asset
        public string type;   // asset class name, e.g. Material
        public string state;  // contents, in the same key=value shape objects use
    }

    // Settings belong to neither an object nor an asset, yet decide how everything behaves:
    // gravity, the layer collision matrix, fog, the tag list, the fixed timestep. Changing one
    // breaks gameplay project-wide while every object and asset still records identically.
    [Serializable]
    public class BaselineSettingsRecord
    {
        public string scope;  // "scene" — belongs to this scene; "project" — shared by all scenes
        public string group;  // render, lighting, physics, layers, tags, time
        public string state;
    }

    // What verification actually confirmed when this baseline was taken. Kept as text
    // because its job is to tell a human (or a report) what this baseline guarantees.
    [Serializable]
    public class BaselineCheck
    {
        public string description;
        public string evidence;
    }

    [Serializable]
    public class Baseline
    {
        public int schemaVersion = BaselineStore.SchemaVersion;
        public string id;
        public string createdUtc;       // ISO-8601 round-trip, invariant culture
        public string unityVersion;
        public string sceneName;
        public string scenePath;        // scene identity for lookup; name alone is ambiguous
        public string originalRequest;  // what was being built when this became known-good

        /// <summary>
        /// The scene had unsaved changes when this was captured, so the state recorded here was
        /// never written to disk.
        /// </summary>
        /// <remarks>
        /// This is the normal case, not an anomaly: a scene is usually dirty at
        /// the exact moment verification passes. It matters because if the editor closes without
        /// saving, everything recorded here reverts and the next check reports those objects
        /// MISSING — a false regression manufactured by the tool. Recorded so the check can say
        /// which it is instead of guessing.
        /// </remarks>
        public bool capturedFromUnsavedScene;

        /// <summary>
        /// How this baseline came to exist: "manual" when a human recorded it directly, absent
        /// when a passing verification filed it.
        /// </summary>
        /// <remarks>
        /// Reading an ABSENT value as agent-verified is correct here, and it is worth saying why,
        /// because the identical reasoning was rejected for capturedFromUnsavedScene: until this
        /// field existed the verify loop was the only code that could write a baseline at all, so
        /// every record without it genuinely is verified. Absent means verified because nothing
        /// else could have produced it — not because absent is being rounded up charitably.
        ///
        /// It exists because a manual baseline records that a scene WAS a certain way, never that
        /// the scene was CORRECT. A report that blurs those two rebuilds exactly the false
        /// confidence this layer was built to remove, so the grade travels with the record.
        /// </remarks>
        public string recordedBy;

        /// <summary>
        /// How many findings a human has accepted into this baseline since it was recorded.
        /// </summary>
        /// <remarks>
        /// Recorded for the same reason as <see cref="recordedBy"/>: the grade travels with the
        /// record. Once a finding is accepted, this file is no longer purely "what the scene was at
        /// createdUtc" — parts of it are "what a human later decided was fine", which is a weaker
        /// claim, and a record that hides the difference is the false confidence this layer exists to
        /// remove. Zero is unambiguous for older files, so this needed no schema bump.
        /// </remarks>
        public int acceptedFindingCount;

        /// <summary>When a finding was last accepted into this baseline. Empty if never.</summary>
        public string lastAcceptedUtc;

        /// <summary>True when part of this baseline was agreed to after the fact rather than recorded.</summary>
        public bool HasAcceptedFindings => acceptedFindingCount > 0;

        /// <summary>True when a human recorded this by hand, with nothing verifying the scene was right.</summary>
        public bool IsManuallyRecorded =>
            string.Equals(recordedBy, BaselineStore.ManualProvenance,
                StringComparison.OrdinalIgnoreCase);

        public List<BaselineCheck> verifiedChecks = new List<BaselineCheck>();
        public List<BaselineObjectRecord> objects = new List<BaselineObjectRecord>();

        /// <summary>Assets the scene depends on, recorded by contents.</summary>
        public List<BaselineAssetRecord> assets = new List<BaselineAssetRecord>();

        /// <summary>
        /// Referenced project assets whose contents could not be read, and are therefore NOT
        /// covered by this baseline.
        /// </summary>
        /// <remarks>
        /// Stored so a report can state the limit instead of implying the baseline covers the
        /// scene and everything it touches. Textures, meshes, audio and animator controllers are
        /// all normal scene dependencies and none of them reduces to a legible line. A known,
        /// stated gap is survivable; a gap the record quietly papers over is the failure this
        /// whole layer exists to remove.
        /// </remarks>
        public int uncheckedAssetCount;

        /// <summary>Scene and project settings as they stood when this was known good.</summary>
        public List<BaselineSettingsRecord> settings = new List<BaselineSettingsRecord>();

        /// <summary>
        /// Other scenes that were loaded alongside this one when the baseline was taken.
        /// </summary>
        /// <remarks>
        /// Context, NEVER a comparison key. Only the active scene's objects are recorded, so this
        /// baseline holds whatever else is open — but on an additive setup part of what made the
        /// scene work may live next door, and a reader deserves to know. Comparing it would make
        /// "opened one more scene" a regression, which is precisely the false alarm that
        /// restricting capture to the active scene exists to stop.
        /// </remarks>
        public List<string> otherLoadedScenes = new List<string>();

        // Properties, not fields, so JsonUtility never persists them — these are conclusions
        // drawn from the record, not part of it.

        /// <summary>
        /// This baseline predates provenance recording, so whether its state ever reached disk
        /// is unknowable.
        /// </summary>
        /// <remarks>
        /// JsonUtility cannot distinguish "field absent" from "field false", so a baseline
        /// written before capturedFromUnsavedScene existed deserialises as though it had been
        /// captured from a SAVED scene — silently upgrading a record that knows nothing about
        /// its own provenance into a trusted one. The schema version is the only honest signal
        /// available, so it is what gets used.
        /// </remarks>
        public bool ProvenanceUnknown => schemaVersion < BaselineStore.ProvenanceSchemaVersion;

        /// <summary>
        /// Whether differences against this baseline may be called regressions at all.
        /// </summary>
        /// <param name="scenePersistedAfterCapture">
        /// Whether the scene file was written to disk after this baseline was captured. Supplied by
        /// the caller rather than read here, so this stays free of IO and directly testable.
        /// </param>
        /// <remarks>
        /// capturedFromUnsavedScene alone is NOT the answer, and treating it as the answer is what
        /// made this rule useless: a capture usually follows an edit immediately, so
        /// the scene is dirty at every capture and the flag is true on every baseline it will ever
        /// write. Read literally, no baseline could ever prove a regression — only ever report
        /// "unconfirmed", which is most of the feature's value gone.
        ///
        /// The question that matters is not "was the scene dirty when this was captured" but "did
        /// this state ever reach disk", and a scene file written AFTER the capture answers yes.
        ///
        /// That is a heuristic with a real limit worth stating: reverting the scene file to an older
        /// revision also updates its write time, so this can say yes where a stricter check would
        /// not. It is the safe direction to be loose in — the alternative was a rule that could
        /// never say yes at all.
        /// </remarks>
        public bool StateReachedDisk(bool scenePersistedAfterCapture)
        {
            return !ProvenanceUnknown && (!capturedFromUnsavedScene || scenePersistedAfterCapture);
        }

        /// <summary>
        /// Whether this baseline's recorded states can be compared against a freshly captured
        /// scene at all. False for baselines written before the state format changed.
        /// </summary>
        public bool StateFormatComparable =>
            schemaVersion >= BaselineStore.StateFormatSchemaVersion;

        /// <summary>
        /// Whether this baseline recorded referenced assets at all. False for older records,
        /// whose empty asset list means "never looked", not "nothing to look at".
        /// </summary>
        public bool RecordsAssets =>
            schemaVersion >= BaselineStore.AssetSchemaVersion;

        /// <summary>
        /// Whether this baseline's recorded ASSET states can be compared against freshly captured
        /// ones. False for baselines written before the material render-queue record changed, whose
        /// materials would otherwise all report a queue nobody touched.
        /// </summary>
        public bool AssetStateComparable =>
            schemaVersion >= BaselineStore.AssetStateFormatSchemaVersion;

        /// <summary>
        /// Whether this baseline recorded scene and project settings. False for older records,
        /// whose empty settings list means "never looked", not "nothing to look at".
        /// </summary>
        public bool RecordsSettings =>
            schemaVersion >= BaselineStore.SettingsSchemaVersion;

        /// <summary>
        /// Whether this baseline recorded stable object identities. False for older records, which
        /// can only be matched by hierarchy path — so a rename or re-parent still reports MISSING
        /// against them, and the report says so rather than implying identity coverage it lacks.
        /// </summary>
        public bool RecordsObjectIds =>
            schemaVersion >= BaselineStore.IdentitySchemaVersion;

        /// <summary>Why this baseline cannot be trusted to prove breakage, or null if it can.</summary>
        public string UntrustworthyReason(bool scenePersistedAfterCapture)
        {
            if (ProvenanceUnknown)
                return "it was written before this tool recorded whether the scene had been saved, " +
                       "so there is no way to tell whether the state below ever reached disk";

            if (capturedFromUnsavedScene && !scenePersistedAfterCapture)
                return "it was captured from an unsaved scene and that scene has not been saved " +
                       "since, so the state below was never written to disk";

            return null;
        }

        /// <summary>
        /// Age in words, with the exact date after it: "2 days ago (2026-08-07 22:53)".
        /// </summary>
        /// <remarks>
        /// A bare timestamp is read as staleness. The first person to use the review window took
        /// "2026-08-07 22:53" to mean the findings under it were old data left over from a previous
        /// session, and stopped reading — so a correct report was dismissed as noise, which is the
        /// failure this whole layer exists to avoid. The words say how old; the date stays for
        /// anyone matching a baseline to a commit.
        /// </remarks>
        public string DescribeAge()
        {
            if (!DateTime.TryParse(createdUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime parsed))
                return DescribeCreated();

            TimeSpan age = DateTime.UtcNow - parsed.ToUniversalTime();

            string words;

            // Negative ages (a clock behind the one that recorded it) fall into the first branch
            // rather than printing "-3 hours ago", which reads as a bug in the tool.
            if (age.TotalSeconds < 90)
                words = "just now";
            else if (age.TotalMinutes < 90)
                words = $"{(int)age.TotalMinutes} minutes ago";
            else if (age.TotalHours < 36)
                words = $"{(int)age.TotalHours} hours ago";
            else
                words = $"{(int)age.TotalDays} days ago";

            return $"{words} ({DescribeCreated()})";
        }

        /// <summary>Local-time, human-readable establish date for report lines.</summary>
        public string DescribeCreated()
        {
            if (DateTime.TryParse(createdUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime parsed))
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            return string.IsNullOrWhiteSpace(createdUtc) ? "(unknown date)" : createdUtc;
        }
    }

    public static class BaselineStore
    {
        public const int SchemaVersion = 11;

        /// <summary>
        /// The first schema that recorded capturedFromUnsavedScene. Anything older cannot report
        /// its own provenance, and a missing bool is indistinguishable from a false one.
        /// </summary>
        public const int ProvenanceSchemaVersion = 2;

        /// <summary>
        /// The first schema that recorded referenced assets by contents.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT a state-format bump: assets are a new section beside the objects, and
        /// object state strings are byte-identical across the change, so an older baseline still
        /// compares its objects correctly and nobody is forced to re-record.
        ///
        /// It needs its own version anyway, because an empty asset list is ambiguous — it means
        /// either "this scene references no checkable assets" or "this baseline was taken before
        /// assets were recorded at all", and reporting the second as the first would let a
        /// baseline claim coverage it never had.
        /// </remarks>
        public const int AssetSchemaVersion = 6;

        /// <summary>
        /// The first schema that recorded scene and project settings.
        /// </summary>
        /// <remarks>
        /// Additive like assets, and for the same reason: object state strings do not change, so
        /// existing baselines keep comparing and nobody is forced to re-record. It needs its own
        /// version because an empty settings list is otherwise ambiguous between "recorded before
        /// settings were covered" and "recorded and found nothing", and only the second would be
        /// safe to report as clean.
        /// </remarks>
        public const int SettingsSchemaVersion = 7;

        /// <summary>
        /// The first schema that recorded each object's <see cref="BaselineObjectRecord.id"/>.
        /// </summary>
        /// <remarks>
        /// Additive, like assets and settings, and deliberately NOT a state-format bump: state strings
        /// are byte-identical across the change and matching falls back to the hierarchy path when a
        /// record carries no id, so every existing baseline keeps comparing exactly as before and
        /// nobody is forced to re-record. Forcing a re-record here would have been especially
        /// self-defeating: the re-record is done from the CURRENT scene, so it would bake today's
        /// state in as "known good" and destroy the very history the ids exist to track.
        ///
        /// It needs its own version because an empty id is ambiguous — either "recorded before
        /// identities existed" or "this object has no usable identity" — and only the report can say
        /// which, by naming how the object was matched.
        /// </remarks>
        public const int IdentitySchemaVersion = 10;

        /// <summary>
        /// The first schema whose ASSET state strings record a material's stored render-queue
        /// override rather than the value Unity resolves from the shader.
        /// </summary>
        /// <remarks>
        /// Narrower than <see cref="StateFormatSchemaVersion"/> on purpose: object state strings are
        /// byte-identical across this change, so objects, settings and identity all keep comparing
        /// against an older baseline exactly as before, and only the asset section is set aside.
        ///
        /// It needs a version at all because the old records hold a NUMBER where the new ones hold
        /// "from-shader", so every material in an older baseline would report a changed queue at the
        /// first check after upgrading — the precise noise this change exists to remove. Measured on
        /// BossRoom: the resolved value produced 621 of 3,022 findings across a 15-commit replay,
        /// all of them invented, because it resolves differently depending on how far shader import
        /// has progressed. Older asset records are therefore refused rather than compared, and the
        /// report says the section was not covered instead of implying it was clean.
        /// </remarks>
        public const int AssetStateFormatSchemaVersion = 11;

        /// <summary>
        /// The first schema whose state strings record inactive objects and their active state.
        /// </summary>
        /// <remarks>
        /// The state string is the comparison key, so changing how it is built makes every older
        /// baseline mismatch on every object at once. Comparing across the change would report a
        /// scene-wide regression that never happened, so older baselines are refused rather than
        /// compared, and re-recording is the only way forward. Bumping this is therefore a real
        /// cost to anyone holding baselines — do it for coverage that cannot be added any other
        /// way, not for tidiness.
        ///
        /// v3 removed screen-derived values, which changed when the Game View was resized.
        /// v4 added component properties: before it, the record held component TYPE NAMES only,
        /// so a flipped isTrigger, a changed mass or a reference gone null were all invisible.
        /// v5 added inactive objects, which capture had been skipping entirely, and the object's
        /// own active state. It changes both the state string AND which objects appear at all.
        /// v8 restricted capture to the ACTIVE scene. Older baselines taken with more than one
        /// scene loaded contain objects belonging to the others, and nothing in the record says
        /// which — so those objects would now report MISSING forever. There is no way to tell a
        /// polluted baseline from a clean one after the fact, which is why every older baseline is
        /// refused rather than a subset repaired.
        /// v9 added each object's child ORDER, which under a Canvas is draw order.
        /// </remarks>
        public const int StateFormatSchemaVersion = 9;
        public const string DefaultBaselineFolder = "Assets/SceneBaselines";

        /// <summary>
        /// Where baselines are read and written. Defaults to <see cref="DefaultBaselineFolder"/>
        /// inside the project; -baselineDir on the command line moves it anywhere, including
        /// outside the project entirely.
        /// </summary>
        /// <remarks>
        /// The override exists so a run can leave the project it inspects untouched. Replaying
        /// another team's history means checking out commit after commit in their repository:
        /// anything written under their Assets/ would dirty the working tree and then be
        /// destroyed by the next checkout, and a tool that writes into a repository it was only
        /// asked to read is one no studio should install. Writing outside the project is what
        /// makes inspecting a repository that is not ours a read-only act.
        /// </remarks>
        public static string BaselineFolder => FolderOverride ?? DefaultBaselineFolder;

        /// <summary>Value of <see cref="Baseline.recordedBy"/> for a hand-recorded baseline.</summary>
        public const string ManualProvenance = "manual";

        // ── Paths ────────────────────────────────────────────────────────────────

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        private static string AbsoluteFolder
        {
            get
            {
                string folder = BaselineFolder;
                return Path.IsPathRooted(folder) ? folder : Path.Combine(ProjectRoot, folder);
            }
        }

        /// <summary>
        /// The project-relative asset path for a baseline, or null when baselines are being
        /// written outside the project and are therefore not assets at all. Every AssetDatabase
        /// call must be guarded on this: asking the AssetDatabase about a path outside the
        /// project does not fail loudly, it just quietly does nothing.
        /// </summary>
        public static string AssetPathFor(string id)
        {
            string root = Normalize(ProjectRoot);
            string full = Normalize(AbsolutePathFor(id));

            return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length + 1)
                : null;
        }

        private static string AbsolutePathFor(string id) => Path.Combine(AbsoluteFolder, id + ".json");

        /// <summary>Full path, forward slashes, no trailing slash — comparable across both.</summary>
        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/'); }
            catch { return path.Replace('\\', '/').TrimEnd('/'); }
        }

        // -- Output location override --------------------------------------------

        private static string _folderOverride;
        private static bool _folderOverrideResolved;

        /// <summary>
        /// Overrides <see cref="BaselineFolder"/>; null means the default. Read from
        /// -baselineDir on the command line the first time it is used, so batch runs need no
        /// code change. Assigning null restores the default and is how a test cleans up.
        /// </summary>
        public static string FolderOverride
        {
            get
            {
                if (!_folderOverrideResolved)
                {
                    _folderOverride = ReadFolderOverrideArgument();
                    _folderOverrideResolved = true;
                }

                return _folderOverride;
            }
            set
            {
                _folderOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                _folderOverrideResolved = true;
            }
        }

        private static string ReadFolderOverrideArgument()
        {
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-baselineDir", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(args[i + 1]) ? null : args[i + 1].Trim();
            }

            return null;
        }

        // ── Save ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a baseline and returns its asset path, or null if it was skipped or failed.
        /// Never throws: a storage problem must not break the verification pass that called it.
        /// </summary>
        public static string Save(Baseline baseline)
        {
            if (baseline == null || baseline.objects == null || baseline.objects.Count == 0)
                return null;

            try
            {
                if (string.IsNullOrWhiteSpace(baseline.id))
                    baseline.id = BuildId(baseline.sceneName);

                Directory.CreateDirectory(AbsoluteFolder);

                // prettyPrint so a baseline is reviewable in a pull request. These files are
                // meant to be read by humans during code review, not just by the loop.
                File.WriteAllText(AbsolutePathFor(baseline.id),
                    JsonUtility.ToJson(baseline, true), new UTF8Encoding(false));

                string assetPath = AssetPathFor(baseline.id);
                if (assetPath == null)
                    return AbsolutePathFor(baseline.id);   // written outside the project

                // Import just this file. A full AssetDatabase.Refresh() is heavier and can fire
                // in the middle of a verification pass, which is exactly where this gets called.
                AssetDatabase.ImportAsset(assetPath);
                return assetPath;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scene Baselines] Could not write baseline: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// True when the most recent baseline for this scene already records an identical
        /// object set. Re-running the same request otherwise files a fresh near-duplicate
        /// every time and buries the baselines that represent real milestones.
        /// </summary>
        /// <remarks>
        /// An out-of-date schema is never a duplicate, however identical its objects look. Found
        /// 2026-08-07 as a dead end a user could not escape: after a schema change the report
        /// tells them to re-record, and this then refused with "already matches its latest
        /// baseline" — because dedup compares objects, and a schema bump can change what is
        /// recorded ABOUT them or, as with assets, add a section objects say nothing about. The
        /// tool asked for the one action it had made impossible.
        /// </remarks>
        /// <remarks>
        /// 🚨 Takes the FULLY BUILT candidate, not just its objects, and that is the whole point.
        /// It compared objects alone until 2026-08-11, when a user reordered the Hierarchy, was
        /// told nothing had changed, recorded a fresh baseline to pick up the new root-order
        /// coverage — and was refused as a duplicate, because moving an object in the Hierarchy
        /// changes no object's state. The tool had made the one action it needed impossible, for
        /// the second time and by a new route: the 08-07 fix guarded the SCHEMA version, and this
        /// coverage arrived without a schema bump.
        ///
        /// So the rule is now the general one the earlier fix was a special case of: a candidate is
        /// a duplicate only when EVERYTHING the baseline records is identical — objects, settings
        /// and assets alike. A settings group or an asset the candidate records and the stored one
        /// does not is a reason to file, since that is exactly how a scene picks up coverage the
        /// tool only just learned to capture.
        /// </remarks>
        public static bool MatchesLatestForScene(Baseline candidate)
        {
            if (candidate?.objects == null)
                return false;

            Baseline latest = LoadForScene(candidate.scenePath).FirstOrDefault();
            if (latest == null || latest.schemaVersion < SchemaVersion)
                return false;

            return RecordsSameState(latest, candidate);
        }

        /// <summary>
        /// Whether two baselines record the same state in every section. Pure: no files, no scene.
        /// </summary>
        /// <remarks>
        /// Split from <see cref="MatchesLatestForScene"/> for the same reason BaselineAccept.Apply is
        /// split from Accept: the decision rule can then be exercised by the free suite without
        /// leaving baseline files in a user's project, and a test whose side effects need cleaning up
        /// eventually gets deleted instead of fixed. The dedup defect of 2026-08-11 was invisible
        /// precisely because nothing could reach this rule without touching disk.
        /// </remarks>
        public static bool RecordsSameState(Baseline stored, Baseline candidate)
        {
            if (stored?.objects == null || candidate?.objects == null)
                return false;

            return SameObjects(stored, candidate)
                && SameSettings(stored, candidate)
                && SameAssets(stored, candidate);
        }

        private static bool SameObjects(Baseline latest, Baseline candidate)
        {
            if (latest.objects == null || latest.objects.Count != candidate.objects.Count)
                return false;

            var previous = new Dictionary<string, string>(latest.objects.Count);
            foreach (BaselineObjectRecord record in latest.objects)
                previous[record.path ?? ""] = record.state ?? "";

            foreach (BaselineObjectRecord record in candidate.objects)
            {
                if (!previous.TryGetValue(record.path ?? "", out string state))
                    return false;
                if (!string.Equals(state, record.state ?? "", StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static bool SameSettings(Baseline latest, Baseline candidate)
        {
            List<BaselineSettingsRecord> now = candidate.settings ?? new List<BaselineSettingsRecord>();
            List<BaselineSettingsRecord> before = latest.settings ?? new List<BaselineSettingsRecord>();

            if (now.Count != before.Count)
                return false;

            var previous = new Dictionary<string, string>(before.Count);
            foreach (BaselineSettingsRecord record in before)
                previous[(record.scope ?? "") + "/" + (record.group ?? "")] = record.state ?? "";

            foreach (BaselineSettingsRecord record in now)
            {
                if (!previous.TryGetValue((record.scope ?? "") + "/" + (record.group ?? ""), out string state))
                    return false;
                if (!string.Equals(state, record.state ?? "", StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static bool SameAssets(Baseline latest, Baseline candidate)
        {
            List<BaselineAssetRecord> now = candidate.assets ?? new List<BaselineAssetRecord>();
            List<BaselineAssetRecord> before = latest.assets ?? new List<BaselineAssetRecord>();

            if (now.Count != before.Count)
                return false;

            var previous = new Dictionary<string, string>(before.Count);
            foreach (BaselineAssetRecord record in before)
                previous[record.path ?? ""] = record.state ?? "";

            foreach (BaselineAssetRecord record in now)
            {
                if (!previous.TryGetValue(record.path ?? "", out string state))
                    return false;
                if (!string.Equals(state, record.state ?? "", StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Whether this baseline's scene file was written to disk after the baseline was captured,
        /// which means the captured state was subsequently saved.
        /// </summary>
        /// <remarks>
        /// This is the fact that rescues capturedFromUnsavedScene from being permanently true —
        /// see Baseline.StateReachedDisk for why that matters. Kept here, next to the other
        /// file access, so the comparison logic can stay pure.
        ///
        /// Returns false on any doubt (missing file, unparseable timestamp, IO error): the caller
        /// treats false as "cannot prove the state reached disk", which downgrades a regression
        /// claim to unconfirmed. Failing that way round never invents breakage.
        /// </remarks>
        public static bool ScenePersistedAfterCapture(Baseline baseline)
        {
            if (baseline == null || string.IsNullOrWhiteSpace(baseline.scenePath))
                return false;

            if (!DateTime.TryParse(baseline.createdUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime captured))
                return false;

            try
            {
                string absolute = Path.Combine(
                    Directory.GetParent(Application.dataPath).FullName, baseline.scenePath);

                if (!File.Exists(absolute))
                    return false;

                return File.GetLastWriteTimeUtc(absolute) > captured.ToUniversalTime();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scene Baselines] Could not read scene write time for baseline '" +
                    baseline.id + "': " + e.Message);
                return false;
            }
        }

        // ── Load ─────────────────────────────────────────────────────────────────

        /// <summary>All baselines, newest first. A corrupt file is skipped, never fatal.</summary>
        public static List<Baseline> LoadAll()
        {
            var results = new List<Baseline>();

            if (!Directory.Exists(AbsoluteFolder))
                return results;

            string[] files;
            try { files = Directory.GetFiles(AbsoluteFolder, "*.json", SearchOption.TopDirectoryOnly); }
            catch (Exception e)
            {
                Debug.LogWarning("[Scene Baselines] Could not list baselines: " + e.Message);
                return results;
            }

            foreach (string file in files)
            {
                Baseline baseline = ReadFile(file);
                if (baseline != null)
                    results.Add(baseline);
            }

            // Sort on the stored timestamp rather than file mtime: a checkout, a copy or a
            // merge rewrites mtime, and "when was this known good" must survive all three.
            return results
                .OrderByDescending(b => ParseCreated(b.createdUtc))
                .ToList();
        }

        /// <summary>Baselines for one scene, newest first.</summary>
        public static List<Baseline> LoadForScene(string scenePath)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
                return new List<Baseline>();

            return LoadAll()
                .Where(b => string.Equals(b.scenePath, scenePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static Baseline Load(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string path = AbsolutePathFor(id);
            return File.Exists(path) ? ReadFile(path) : null;
        }

        public static bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            string assetPath = AssetPathFor(id);
            if (assetPath != null && AssetDatabase.DeleteAsset(assetPath))
                return true;

            // Not imported as an asset (hand-copied in, or written while Unity was closed).
            try
            {
                string absolute = AbsolutePathFor(id);
                if (!File.Exists(absolute))
                    return false;

                File.Delete(absolute);
                AssetDatabase.Refresh();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scene Baselines] Could not delete baseline '" + id + "': " + e.Message);
                return false;
            }
        }

        // ── Internals ────────────────────────────────────────────────────────────

        private static Baseline ReadFile(string absolutePath)
        {
            try
            {
                var baseline = JsonUtility.FromJson<Baseline>(File.ReadAllText(absolutePath));
                if (baseline == null)
                    return null;

                // A file written by a NEWER schema may mean something different by the same
                // field names. Refuse to guess — a wrong baseline is worse than a missing one.
                if (baseline.schemaVersion > SchemaVersion)
                {
                    Debug.LogWarning($"[Scene Baselines] Skipping baseline '{Path.GetFileName(absolutePath)}': " +
                        $"schema v{baseline.schemaVersion} is newer than this tool understands (v{SchemaVersion}).");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(baseline.id))
                    baseline.id = Path.GetFileNameWithoutExtension(absolutePath);

                return baseline;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Scene Baselines] Skipping unreadable baseline " +
                    $"'{Path.GetFileName(absolutePath)}': {e.Message}");
                return null;
            }
        }

        private static DateTime ParseCreated(string createdUtc)
        {
            return DateTime.TryParse(createdUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;
        }

        private static string BuildId(string sceneName)
        {
            string safeScene = Sanitize(string.IsNullOrWhiteSpace(sceneName) ? "scene" : sceneName);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string id = $"{safeScene}--{stamp}";

            // Same-second collisions are possible when several baselines are written in one
            // pass; never silently overwrite an existing known-good record.
            int suffix = 2;
            while (File.Exists(AbsolutePathFor(id)))
                id = $"{safeScene}--{stamp}-{suffix++}";

            return id;
        }

        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');

            return sb.ToString().Trim('-');
        }

        public static string CreateTimestampUtc() =>
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    }
}
