using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneBaselines
{
    // ─── Regression baselines: narrowing a long review (part of step 4) ───────────
    //
    // A check on a real scene reports more findings than anyone reads in one column. The roll-up
    // already fixed the LENGTH of the console report; this is the review window's half of the same
    // problem — "only what is missing", "only what touched a collider", "only the objects under
    // Enemies/". Nothing new is captured, computed or judged: every question above is a fact the
    // finding is already carrying.
    //
    // Two rules this type exists to hold, both paid for elsewhere in this tool:
    //
    //   EVERY FILTER IS A FACT. The kind of problem, the path of the object, the named segment of
    //   state that differs. In particular there is no "only real regressions" filter and there
    //   cannot be one: that needs a classifier of INTENT, which cannot exist, and a filter fails
    //   worse than a ranking would — a mis-ranked finding is three lines further down the page, a
    //   mis-classified one is behind a button nobody clicks.
    //
    //   HIDING IS SAID OUT LOUD. Filtering decides which findings are LISTED and never what a
    //   listed finding is allowed to say. The window prints how many findings exist beside how
    //   many it is showing, a section emptied by a filter says so rather than saying nothing is
    //   wrong, and a ticked finding stays ticked and stays counted when a filter scrolls it out of
    //   sight. A filter that can quietly turn a broken scene into a clean-looking window would be
    //   worth less than no filter at all.
    //
    // The menus are built FROM the findings (see <see cref="DetailKeys"/>), not from a list of
    // categories written here. That is what makes the second rule cheap to keep: a difference
    // cannot fall outside the menu, because the menu is made of the differences.

    /// <summary>
    /// What a reviewer has narrowed the finding list down to. Empty by default, which is the
    /// unfiltered window: <see cref="Apply"/> then returns exactly what it was given, in order.
    /// </summary>
    public class ReviewFilter
    {
        /// <summary>Only findings of this kind; every kind when null.</summary>
        public RegressionKind? kind;

        /// <summary>
        /// Only findings that differ under this state segment — <c>pos</c>, <c>children</c>,
        /// <c>props:BoxCollider</c> — or every finding when null. Findings that name no segment at
        /// all (a MISSING object has no properties left to differ in) are excluded by any choice
        /// here, which is why the window keeps printing the unfiltered count next to this one.
        /// </summary>
        public string detail;

        /// <summary>Only findings whose object path contains this text, compared case-insensitively.</summary>
        public string path = "";

        /// <summary>Whether anything is being hidden — the window says so wherever this is true.</summary>
        public bool IsActive => kind.HasValue || !string.IsNullOrEmpty(detail) || Search.Length > 0;

        private string Search
        {
            get { return (path ?? "").Trim(); }
        }

        public void Clear()
        {
            kind = null;
            detail = null;
            path = "";
        }

        public bool Matches(RegressionFinding finding)
        {
            if (finding == null)
                return false;

            if (kind.HasValue && finding.kind != kind.Value)
                return false;

            if (!string.IsNullOrEmpty(detail) && !SegmentKeys(finding).Contains(detail))
                return false;

            string search = Search;

            if (search.Length > 0 &&
                !Contains(finding.path, search) &&
                !Contains(finding.livePath, search))
            {
                // Both paths, because a moved object is the one case where the reviewer may be
                // hunting for where it went rather than for where it was recorded.
                return false;
            }

            return true;
        }

        /// <summary>The findings that survive this filter, in the order they were given in.</summary>
        public List<RegressionFinding> Apply(IEnumerable<RegressionFinding> findings)
        {
            if (findings == null)
                return new List<RegressionFinding>();

            return findings.Where(Matches).ToList();
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── What a difference is about ───────────────────────────────────────────

        /// <summary>
        /// The state segment a difference line is about: <c>pos: (0,0,0) → (1,0,0)</c> is about
        /// <c>pos</c>, and <c>props:BoxCollider.isTrigger: false → true</c> is about
        /// <c>props:BoxCollider</c>.
        /// </summary>
        /// <remarks>
        /// Narrowed to the component rather than to the property on purpose. A menu listing every
        /// property name that differs anywhere in a scene is as long as the finding list it was
        /// meant to shorten, and "which component changed" is the question a reviewer actually
        /// asks — the property is right there on the line once the finding is on screen.
        /// </remarks>
        public static string SegmentKey(string segment)
        {
            if (string.IsNullOrEmpty(segment))
                return "";

            // Every difference line is written as "<key>: <was> → <now>", and a key never contains
            // a space, so the first ": " ends it. The colon inside "props:BoxCollider" does not.
            int colon = segment.IndexOf(": ", StringComparison.Ordinal);
            string key = colon > 0 ? segment.Substring(0, colon) : segment;

            // "props:BoxCollider.isTrigger" narrows to the component; "bounds.center" to "bounds".
            int dot = key.IndexOf('.');
            if (dot > 0)
                key = key.Substring(0, dot);

            // A state that repeats a key disambiguates the second one as "key#2". The reviewer is
            // asking about the component, not about which of two identical ones this was.
            int hash = key.IndexOf('#');
            if (hash > 0)
                key = key.Substring(0, hash);

            return key.Trim();
        }

        /// <summary>The distinct segments one finding differs in.</summary>
        private static List<string> SegmentKeys(RegressionFinding finding)
        {
            var keys = new List<string>();

            if (finding == null || finding.changedSegments == null)
                return keys;

            foreach (string segment in finding.changedSegments)
            {
                string key = SegmentKey(segment);

                if (key.Length > 0 && !keys.Contains(key))
                    keys.Add(key);
            }

            return keys;
        }

        /// <summary>
        /// Every segment the given findings actually differ in, commonest first, each with how many
        /// findings it would show.
        /// </summary>
        /// <remarks>
        /// A finding that differs in three segments is counted under all three: it is one finding
        /// that would be listed under any of them, not a third of a finding each time. So these
        /// counts sum to more than the finding count, and none of them is ever the total — which is
        /// why the window prints the real total itself rather than deriving it from here.
        /// </remarks>
        public static List<KeyValuePair<string, int>> DetailKeys(IEnumerable<RegressionFinding> findings)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (RegressionFinding finding in findings ?? Enumerable.Empty<RegressionFinding>())
            {
                foreach (string key in SegmentKeys(finding))
                {
                    int existing;
                    counts[key] = counts.TryGetValue(key, out existing) ? existing + 1 : 1;
                }
            }

            return counts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>A segment key as a menu should read it.</summary>
        public static string DescribeDetail(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";

            const string prefix = "props:";

            // Everything else is left exactly as the report spells it. A reviewer who has read
            // "pos" in the console report should not have to learn a second word for it here.
            return key.StartsWith(prefix, StringComparison.Ordinal)
                ? key.Substring(prefix.Length) + " properties"
                : key;
        }
    }
}
