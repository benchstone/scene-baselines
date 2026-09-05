using System;
using System.Collections.Generic;

namespace SceneBaselines
{
    // Findings are true one at a time and unreadable in bulk. Replaying 15 Boss Room commits
    // produced 3,022 of them; one commit produced 1,736 and one scene 936, and every one of those
    // was correct. A report nobody can read is not a report, so the findings are grouped by the
    // thing that CAUSED them before they are rendered.
    //
    // This groups; it never filters. The count a run reports, and the verdict it reaches, are the
    // same before and after — every finding is still in exactly one group, and a group states how
    // many it holds. Hiding a finding to shorten a report would be the tool lying about what it
    // found, which is the one thing it cannot do and stay worth running.
    //
    // Rules earn their place by measurement, not by taste. On the Boss Room corpus:
    //   - 1,438 of 1,548 "missing" findings (92.9%) had a missing ANCESTOR. Nobody deleted 1,438
    //     objects; they deleted a few subtrees and the report listed every bone in them.
    //   - 597 of 715 "changed" findings sat in clusters of 5+ that changed the SAME property set,
    //     the largest being 199 objects whose only change was "pos".
    //   - 632 of 713 "asset-changed" findings (88.6%) sat in the same kind of cluster, which the
    //     first cut of this file did not group at all: 43 materials with one identical line each
    //     opened the biggest report in the corpus.
    //   - 36 "missing" findings were same-named SIBLINGS the subtree rule cannot reach, because
    //     each is the topmost missing object on its own branch. Small, and the single most
    //     repetitive thing in the report it was read in: 22 consecutive lines differing by "#n".
    // Everything else is left exactly as it was, because nothing was measured to justify touching it.
    //
    // One thing grouping does NOT fix, so the renderers do it instead: the number of change lines
    // UNDER a finding. 27 of 3,022 findings carried more than eight, and those 27 alone were a
    // third of every detail line in the corpus — see ChangesShown.
    public static class FindingRollup
    {
        /// <summary>
        /// How many findings must share a change before they are worth one line instead of many.
        /// </summary>
        /// <remarks>
        /// Five, from the corpus: clusters of five or more held 597 of 715 changed findings, while
        /// the clusters below it were ones and twos where a roll-up line would say less than the
        /// findings it replaced. A threshold of one would "group" every lone finding and turn a
        /// two-line report into a two-line report with headings.
        /// </remarks>
        public const int SameChangeThreshold = 5;

        /// <summary>How many members a rolled-up group names before it stops listing and counts.</summary>
        public const int MembersNamed = 3;

        /// <summary>
        /// How many change lines a single finding shows before the rest are counted instead.
        /// </summary>
        /// <remarks>
        /// Eight, from the corpus, and the number is where two populations separate rather than a
        /// preference. Capping at 8 truncates 27 of 3,022 findings (0.9%) and removes 33.5% of all
        /// change lines; raising it to 10, 15 or even 30 still truncates 25-26 of the same findings,
        /// because the long ones are a distinct handful — a ScriptableObject refactor reporting 74
        /// moved fields, an array reporting element by element — and everything else is short.
        /// Below 8 the cap starts cutting ordinary findings for very little: 5 truncates 46 and 3
        /// truncates 148, to save 2.6 and 9 more points of length.
        ///
        /// The renderers print the remainder as a count. A finding that quietly showed eight of its
        /// 74 changes would be the tool understating what it found, which is the same lie as
        /// dropping the finding outright.
        /// </remarks>
        public const int ChangesShown = 8;

        /// <summary>
        /// One finding, reduced to what grouping needs. Callers pass indexes into their OWN list and
        /// get indexes back, so this knows nothing about either finding type and cannot drift from
        /// them.
        /// </summary>
        public readonly struct Item
        {
            public readonly string Kind;
            public readonly string Path;
            public readonly IReadOnlyList<string> ChangeKeys;

            public Item(string kind, string path, IReadOnlyList<string> changeKeys)
            {
                Kind = kind ?? "";
                Path = path ?? "";
                ChangeKeys = changeKeys ?? Array.Empty<string>();
            }
        }

        /// <summary>
        /// A cause, and the findings it produced. A group of one is the ordinary case and renders
        /// exactly as a finding always has.
        /// </summary>
        public sealed class Group
        {
            /// <summary>Indexes into the caller's finding list, in the caller's own order.</summary>
            public readonly List<int> Members = new List<int>();

            /// <summary>What these findings share, or null when this is a single finding.</summary>
            public string Cause;

            /// <summary>The finding rendered in full; the rest are named or counted beneath it.</summary>
            public int Lead => Members[0];

            public int Count => Members.Count;

            public bool IsRolledUp => Cause != null && Members.Count > 1;
        }

        /// <summary>
        /// Groups findings by cause, preserving the order they were reported in. Every index in
        /// <paramref name="items"/> appears in exactly one group.
        /// </summary>
        public static List<Group> Build(IReadOnlyList<Item> items)
        {
            var groups = new List<Group>();
            if (items == null || items.Count == 0)
                return groups;

            var owner = new Dictionary<int, Group>();

            RollUpLostSubtrees(items, groups, owner);
            RollUpLostSiblings(items, groups, owner);
            RollUpSharedChanges(items, groups, owner);

            // Whatever no rule claimed is its own group, so the caller can render every finding by
            // walking groups alone and never has to remember the leftovers.
            for (int i = 0; i < items.Count; i++)
            {
                if (owner.ContainsKey(i))
                    continue;

                var single = new Group();
                single.Members.Add(i);
                owner[i] = single;
                groups.Add(single);
            }

            groups.Sort((a, b) => a.Lead.CompareTo(b.Lead));
            return groups;
        }

        /// <summary>
        /// A deleted subtree reports once, naming how much went with it.
        /// </summary>
        /// <remarks>
        /// The root is the TOPMOST missing ancestor, not the immediate parent: deleting a character
        /// removes its graphics, which removes its bones, and reporting each level separately would
        /// trade one long list for three shorter ones.
        ///
        /// This is only sound because a child's path is built from its parent's disambiguated path.
        /// While the suffix was appended to a finished path instead, 22 objects named Imp all looked
        /// like one, and this rule would have announced a single Imp losing 805 descendants.
        /// </remarks>
        private static void RollUpLostSubtrees(IReadOnlyList<Item> items, List<Group> groups,
            Dictionary<int, Group> owner)
        {
            var missingAt = new Dictionary<string, int>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Kind == RegressionKinds.Missing && !missingAt.ContainsKey(items[i].Path))
                    missingAt[items[i].Path] = i;
            }

            if (missingAt.Count == 0)
                return;

            var roots = new Dictionary<int, Group>();

            foreach (KeyValuePair<string, int> entry in missingAt)
            {
                int rootIndex = TopmostMissingAncestor(entry.Key, missingAt);
                if (rootIndex == entry.Value)
                    continue;

                if (!roots.TryGetValue(rootIndex, out Group group))
                {
                    group = new Group();
                    group.Members.Add(rootIndex);
                    roots[rootIndex] = group;
                    owner[rootIndex] = group;
                    groups.Add(group);
                }

                group.Members.Add(entry.Value);
                owner[entry.Value] = group;
            }

            foreach (Group group in roots.Values)
            {
                group.Members.Sort();
                group.Cause = "gone with everything beneath it";
            }
        }

        /// <summary>
        /// Same-named siblings deleted together report as one line.
        /// </summary>
        /// <remarks>
        /// The subtree rule cannot reach these and never will: 23 objects named Imp under one
        /// parent are 23 separate roots, each the topmost missing object on its own branch. Their
        /// descendants rolled up neatly beneath them while the roots themselves printed 23 nearly
        /// identical lines — the reader saw "#2, #3, #4 ..." and learned nothing from any of them.
        ///
        /// Keyed on the parent and the name with the disambiguating suffix removed, because "#7" is
        /// this tool's own bookkeeping for twins, not something the project named. Siblings under
        /// DIFFERENT parents are left alone: those are separate deletions that happen to share a
        /// name, and one line covering both would claim a cause that was never measured.
        /// </remarks>
        private static void RollUpLostSiblings(IReadOnlyList<Item> items, List<Group> groups,
            Dictionary<int, Group> owner)
        {
            var clusters = new Dictionary<string, List<int>>();
            var names = new Dictionary<string, string>();

            for (int i = 0; i < items.Count; i++)
            {
                if (owner.ContainsKey(i) || items[i].Kind != RegressionKinds.Missing)
                    continue;

                string path = items[i].Path;
                int cut = path.LastIndexOf('/');
                string parent = cut > 0 ? path.Substring(0, cut) : "";
                string name = BaseName(cut >= 0 ? path.Substring(cut + 1) : path);

                if (name.Length == 0)
                    continue;

                // Length-prefixed rather than joined with a separator, because a separator has to be a
                // character no path or name can contain, and there is no such character.
                string key = parent.Length + ":" + parent + name;

                if (!clusters.TryGetValue(key, out List<int> members))
                {
                    clusters[key] = members = new List<int>();
                    names[key] = name;
                }

                members.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> cluster in clusters)
            {
                if (cluster.Value.Count < SameChangeThreshold)
                    continue;

                var group = new Group();
                group.Members.AddRange(cluster.Value);
                group.Cause = $"all named {names[cluster.Key]} under the same parent";

                foreach (int index in cluster.Value)
                    owner[index] = group;

                groups.Add(group);
            }
        }

        /// <summary>An object's name without the "#n" this tool appends to tell twins apart.</summary>
        private static string BaseName(string name)
        {
            int hash = name.LastIndexOf('#');
            if (hash <= 0 || hash == name.Length - 1)
                return name;

            for (int i = hash + 1; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                    return name;
            }

            return name.Substring(0, hash);
        }

        /// <summary>The highest missing object this path hangs from, or its own index when it is the root.</summary>
        private static int TopmostMissingAncestor(string path, Dictionary<string, int> missingAt)
        {
            int found = missingAt[path];
            int cut = path.Length;

            while (true)
            {
                cut = path.LastIndexOf('/', cut - 1);
                if (cut <= 0)
                    return found;

                if (missingAt.TryGetValue(path.Substring(0, cut), out int ancestor))
                    found = ancestor;
            }
        }

        /// <summary>
        /// Objects and assets that changed the same thing report as one line naming the thing.
        /// </summary>
        /// <remarks>
        /// Keyed on the SET of changed property names rather than their values, because the reader's
        /// question is "what happened", and 199 objects that each moved to a different position had
        /// one thing happen to them. The values are still there on the members.
        ///
        /// Assets cluster the same way and were missed on the first cut: one shader edit, one
        /// re-import or one upgraded material property lands on every material at once, and 43 of
        /// them each printing an identical line was the first thing a reader met in the biggest
        /// report in the corpus. The KIND is part of the key, though — a material whose renderQueue
        /// moved and a GameObject whose renderQueue moved are two different events, and one line
        /// claiming both would name a cause that fits neither.
        /// </remarks>
        private static void RollUpSharedChanges(IReadOnlyList<Item> items, List<Group> groups,
            Dictionary<int, Group> owner)
        {
            var clusters = new Dictionary<string, List<int>>();
            var causes = new Dictionary<string, string>();

            for (int i = 0; i < items.Count; i++)
            {
                if (owner.ContainsKey(i))
                    continue;

                Item item = items[i];
                if (!GroupsByChange(item.Kind) || item.ChangeKeys.Count == 0)
                    continue;

                string changed = ChangeSetKey(item.ChangeKeys);
                string key = item.Kind.Length + ":" + item.Kind + changed;

                if (!clusters.TryGetValue(key, out List<int> members))
                {
                    clusters[key] = members = new List<int>();
                    causes[key] = changed;
                }

                members.Add(i);
            }

            foreach (KeyValuePair<string, List<int>> cluster in clusters)
            {
                if (cluster.Value.Count < SameChangeThreshold)
                    continue;

                var group = new Group();
                group.Members.AddRange(cluster.Value);
                group.Cause = "all changed " + causes[cluster.Key];

                foreach (int index in cluster.Value)
                    owner[index] = group;

                groups.Add(group);
            }
        }

        /// <summary>
        /// Whether a kind's findings are worth clustering by what they changed.
        /// </summary>
        /// <remarks>
        /// Objects and assets, measured. Not settings: 27 of them across the whole corpus, one line
        /// each, and nothing to gain. Not missing, moved or added — those have their own shape and
        /// no measurement asked for this.
        /// </remarks>
        private static bool GroupsByChange(string kind)
        {
            return kind == RegressionKinds.Changed || kind == RegressionKinds.AssetChanged;
        }

        /// <summary>The changed property names, deduplicated and ordered, as one comparable string.</summary>
        private static string ChangeSetKey(IReadOnlyList<string> changeKeys)
        {
            var names = new List<string>();

            foreach (string key in changeKeys)
            {
                string name = KeyOf(key);
                if (name.Length > 0 && !names.Contains(name))
                    names.Add(name);
            }

            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names);
        }

        /// <summary>
        /// The property name out of a "name: was → now" line.
        /// </summary>
        /// <remarks>
        /// Split at the FIRST colon only. Values carry colons of their own — an action reference
        /// reads "(ImpBaseAttack:MeleeAction)" — and splitting at the last one would key the cluster
        /// on the value, which is the one thing every member of it differs in.
        /// </remarks>
        public static string KeyOf(string change)
        {
            if (string.IsNullOrEmpty(change))
                return "";

            int colon = change.IndexOf(':');
            return (colon < 0 ? change : change.Substring(0, colon)).Trim();
        }
    }

    /// <summary>
    /// The kind tokens the roll-up matches on, spelled once.
    /// </summary>
    /// <remarks>
    /// Both renderers reach the roll-up from different finding types — one holds a RegressionKind
    /// enum, the other the string already written into the JSON report — so the shared step needs a
    /// spelling both can produce. These are the report's tokens, because those are the ones already
    /// fixed by a published schema and cannot be renamed on a whim.
    /// </remarks>
    public static class RegressionKinds
    {
        public const string Missing = "missing";
        public const string Changed = "changed";
        public const string AssetChanged = "asset-changed";
    }
}
