using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Linq;
using System.Collections.Generic;

namespace SceneBaselines
{
    // ─── Scene capture: the one producer of recorded state ───────────────────────
    //
    // Kept separate from any editor window so the baseline/regression
    // feature can ship WITHOUT the agent. Everything here is deliberately free of
    // EditorWindow, chat state, and any model call: capture is a pure read of the
    // open scene, which is what lets it run from a menu item, from -batchmode CI,
    // and from a headless build with no editor window ever opened.
    //
    // Dependency direction is one-way and must stay that way: the window calls into
    // this file, this file never calls back. That rule is the whole point of the
    // split — anything that reaches back into the window drags the agent into the
    // free package with it.
    //
    // The state string built here IS the comparison key for every baseline ever
    // recorded. Changing its format invalidates existing baselines, so treat edits
    // to the string shape as a schema change, not a tidy-up.

    /// <summary>One object's recorded state, as it is stored in a baseline.</summary>
    [Serializable]
    public class SceneObjectRecord
    {
        public string path;   // hierarchy path; "#n" suffix disambiguates same-named siblings
        public string state;  // components + transform + bounds, culture-invariant

        /// <summary>
        /// Unity's own stable identity for this object, or empty when it has none worth trusting.
        /// </summary>
        /// <remarks>
        /// The MATCHING key; <see cref="path"/> stays the DISPLAY key. A path is not an identity:
        /// renaming an object or dragging it onto a different parent changes it, and the old path
        /// then reports MISSING — the report claiming something was deleted when it is still there,
        /// which is the false finding that teaches users to stop reading reports.
        ///
        /// Empty rather than absent when unusable (a never-saved object has no file id yet), because
        /// comparison falls back to the path in that case and a made-up id would match nothing.
        /// </remarks>
        public string id;
    }

    /// <summary>The whole open scene, in the exact shape baselines are stored in.</summary>
    [Serializable]
    public class SceneSnapshot
    {
        public List<SceneObjectRecord> objects = new List<SceneObjectRecord>();
    }

    /// <summary>
    /// Reads the open scene into a comparable record. No window, no model, no side effects.
    /// </summary>
    public static class SceneCapture
    {
        // "x,y,z" in the exact shape TryParseVector3 reads back. Invariant culture is required,
        // not cosmetic: under a comma-decimal locale the default formatting emits "0,5" and
        // silently turns a 3-component vector into a 6-component one.
        public static string FormatVec3(Vector3 v)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###},{1:0.###},{2:0.###}", v.x, v.y, v.z);
        }

        public static string FormatVec2(Vector2 v)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###},{1:0.###}", v.x, v.y);
        }

        // World-space size of an object, which is what relative placement actually needs:
        // scale alone can't answer "how tall is this?" (a capsule at scale 1 is 2 units tall).
        // Renderer bounds are what you see; colliders cover invisible geometry like triggers.
        public static bool TryGetWorldBounds(GameObject go, out Bounds bounds)
        {
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                bounds = renderer.bounds;
                return true;
            }

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                bounds = collider.bounds;
                return true;
            }

            bounds = default;
            return false;
        }

        public static string GetHierarchyPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>An object's place in the hierarchy, as a chain of sibling indices that sorts.</summary>
        /// <remarks>
        /// Only used to order same-named siblings. They share a hierarchy path by definition, so the
        /// path cannot separate them and something else has to, or their order — and with it the
        /// "#n" suffix each one gets — falls back to whatever order the scene sweep happened to
        /// return.
        ///
        /// Indices are zero-padded because these are compared as text, where "10" sorts before "2".
        /// </remarks>
        private static string SiblingIndexChain(Transform t)
        {
            var parts = new List<string>();
            for (Transform cur = t; cur != null; cur = cur.parent)
                parts.Add(cur.GetSiblingIndex()
                             .ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            parts.Reverse();
            return string.Join("/", parts);
        }

        // A RectTransform carries the whole meaning of a UI element's placement, and none of it
        // shows up in pos/rot/scale — which is why "anchored to the top-right corner" came back
        // `unverifiable`: the judge had no field to read it from. Emitted only when the transform
        // IS a RectTransform, so non-UI objects are byte-identical to before.
        public static string DescribeRectTransform(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null)
                return "";

            return $" rect(anchorMin=({FormatVec2(rt.anchorMin)}) anchorMax=({FormatVec2(rt.anchorMax)})" +
                   $" pivot=({FormatVec2(rt.pivot)}) anchoredPos=({FormatVec2(rt.anchoredPosition)})" +
                   $" sizeDelta=({FormatVec2(rt.sizeDelta)}))";
        }

        /// <summary>
        /// True when this object's placement is computed from the screen/Game View size rather
        /// than authored.
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-02 on the live scene, and the reason this check exists: two regression
        /// reports on an UNTOUCHED scene disagreed (PASS, then 2 findings) purely because the Game
        /// View had been resized between them. A ScreenSpaceOverlay canvas with a CanvasScaler
        /// recomputes scaleFactor from the viewport, and every descendant's world position moves
        /// with it.
        ///
        /// Checking RectTransform.drivenByObject alone is NOT enough and it is worth saying why:
        /// the driven child in that scene reported drivenByObject == null and drivenProperties ==
        /// None, yet its world position still moved — because its PARENT rescaled. Ancestry is the
        /// property that actually predicts screen dependence.
        ///
        /// The root canvas decides: nested canvases inherit its render mode, and a WorldSpace
        /// canvas is genuinely positioned in the scene, so its values are real state and are kept.
        /// </remarks>
        public static bool IsUnderScreenSpaceCanvas(GameObject go)
        {
            Canvas canvas = go.GetComponentInParent<Canvas>(true);
            if (canvas == null)
                return false;

            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            return root.renderMode != RenderMode.WorldSpace;
        }

        // Static so the regression checker can capture live scene state without a window open —
        // a regression check must be runnable from a menu item or CI, not only mid-conversation.
        public static SceneSnapshot CaptureSceneSnapshot()
        {
            var snapshot = new SceneSnapshot();
            var seen = new Dictionary<string, int>();

            // Collected first, then described, so identities can be fetched for the whole scene in
            // ONE call. GlobalObjectId's per-object overload is named "Slow" for a reason and capture
            // already walks every object in the scene; paying that cost per object would make a check
            // slow enough to stop being run, which costs more coverage than it adds.
            List<GameObject> objects = SweepActiveSceneObjects();

            List<string> ids = CaptureObjectIds(objects);

            for (int i = 0; i < objects.Count; i++)
            {
                GameObject go = objects[i];
                string path = GetHierarchyPath(go.transform);

                // Same-named siblings still collide on path, so one of them takes a "#n" suffix. It is
                // assigned walking the list in the order the sweep fixed — by hierarchy path, then by
                // sibling index — so the same twin keeps the same suffix across captures. It used to
                // follow the raw sweep, which meant the DISPLAY name of a twin could move between two
                // captures of an unchanged scene.
                //
                // The pairing does not rest on it either way: identity comes from the id below, so
                // twins match each other correctly even where a suffix does move.
                if (seen.TryGetValue(path, out int count))
                {
                    seen[path] = count + 1;
                    path += "#" + (count + 1);
                }
                else
                {
                    seen[path] = 1;
                }

                snapshot.objects.Add(new SceneObjectRecord
                {
                    path = path,
                    state = DescribeObjectState(go),
                    id = ids[i]
                });
            }

            return snapshot;
        }

        /// <summary>
        /// Every GameObject in the active scene, inactive included, in a stable, repeatable order.
        /// </summary>
        /// <remarks>
        /// ONE method, because two callers have to agree exactly: capture assigns the "#n" suffixes
        /// walking this list, and <see cref="FindByRecordedPath"/> re-derives them to resolve one. Two
        /// separate sweeps could disagree about which twin is "#2" and quietly hand back the wrong
        /// object — which looks like it worked, and is worse than returning nothing.
        ///
        /// <para>FindObjectsInactive.Include is load-bearing. The default overload returns only ACTIVE
        /// objects, which left two holes: deactivating an object made it vanish from the record and
        /// get reported as "not in the scene now", which is false — it is still there; and, far worse,
        /// anything already inactive when a baseline was recorded was never covered at all. Real
        /// projects are full of those — disabled level sections, closed UI panels, pooled enemies,
        /// alternate spawn sets — and every one was a place changes went unnoticed while the report
        /// said PASS.</para>
        ///
        /// <para>One baseline describes ONE scene. FindObjectsByType sweeps every LOADED scene, so on
        /// an additive setup a baseline for Level_01 silently swallowed the objects of every other open
        /// scene — and the next check, run with a different set loaded, reported all of them MISSING. A
        /// wall of red for a scene nobody touched, which is the failure that gets a check switched off
        /// for good. Filtering here rather than merging scenes keeps a baseline portable: it matches
        /// its own scenePath, and a second scene is covered by its own baseline instead of being
        /// smuggled into this one.</para>
        ///
        /// <para>🚨 The sort is not tidiness. FindObjectsSortMode.None is documented as unspecified and
        /// behaves like it: closing and reopening an unmodified scene returns the same objects in a
        /// different order, so the same scene on the same machine produced byte-different baselines.
        /// Nothing was ever mis-REPORTED by that — objects match by identity and then by path, and root
        /// order is read from GetRootGameObjects — so a check stayed correct. The cost was to the FILE.
        /// A baseline is committed JSON, so a scrambled array meant a large diff for a scene nobody had
        /// touched, and two branches that both re-recorded conflicted over changes neither had made. In
        /// a tool whose whole claim is a legible diff, that is the wrong artifact to hand a team.</para>
        ///
        /// <para>Path first, so a diff moves only what was renamed and a hierarchy reorder — already
        /// reported by rootOrder and childOrder — does not churn the file as well. Sibling index breaks
        /// the tie for same-named siblings, which share a path by definition.</para>
        /// </remarks>
        private static List<GameObject> SweepActiveSceneObjects()
        {
            Scene activeScene = SceneManager.GetActiveScene();

            // Keys are built once rather than inside the comparison: sorting calls a comparer
            // O(n log n) times, and rebuilding a hierarchy path per call would put a walk up the tree,
            // allocating a string at every level, on that multiplier for every object in the scene.
            var ordered = new List<KeyValuePair<string, GameObject>>();

            foreach (GameObject go in GameObject.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go == null || !go.scene.IsValid() || go.scene != activeScene)
                    continue;

                // \0 separates the two keys and sorts below every character a name can contain, so
                // comparing the joined string is the same as comparing path first, then sibling index.
                ordered.Add(new KeyValuePair<string, GameObject>(
                    GetHierarchyPath(go.transform) + "\0" + SiblingIndexChain(go.transform), go));
            }

            ordered.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));

            var objects = new List<GameObject>(ordered.Count);
            foreach (KeyValuePair<string, GameObject> entry in ordered)
                objects.Add(entry.Value);

            return objects;
        }

        /// <summary>
        /// The live GameObject a recorded path refers to, or null when nothing matches.
        /// </summary>
        /// <remarks>
        /// Rebuilds paths with the SAME sweep and the same "#n" suffixing that produced them, rather
        /// than parsing the string and walking by name. Parsing would have to re-derive the suffix
        /// rule, and any drift between the two would quietly select the wrong twin — worse than
        /// selecting nothing, because it looks like it worked.
        ///
        /// "The same sweep" is now literal: both walk <see cref="SweepActiveSceneObjects"/>, so they
        /// cannot disagree about which twin is "#2" even in principle. They used to be two copies of
        /// the same loop over an order Unity does not guarantee.
        ///
        /// Only for baselines with no usable identity: an id resolves exactly and needs none of this.
        /// </remarks>
        public static GameObject FindByRecordedPath(string recordedPath)
        {
            if (string.IsNullOrEmpty(recordedPath))
                return null;

            var seen = new Dictionary<string, int>();

            foreach (GameObject go in SweepActiveSceneObjects())
            {
                string path = GetHierarchyPath(go.transform);

                if (seen.TryGetValue(path, out int count))
                {
                    seen[path] = count + 1;
                    path += "#" + (count + 1);
                }
                else
                {
                    seen[path] = 1;
                }

                if (string.Equals(path, recordedPath, StringComparison.Ordinal))
                    return go;
            }

            return null;
        }

        /// <summary>
        /// Unity's stable identity for each object, in the order given, empty where unusable.
        /// </summary>
        /// <remarks>
        /// Never throws: identity is an improvement to matching, so a scene it cannot describe must
        /// degrade to path matching rather than fail a capture that would otherwise have succeeded.
        /// </remarks>
        private static List<string> CaptureObjectIds(List<GameObject> objects)
        {
            var ids = new List<string>(objects.Count);

            try
            {
                var asObjects = new UnityEngine.Object[objects.Count];
                for (int i = 0; i < objects.Count; i++)
                    asObjects[i] = objects[i];

                // Pre-allocated, NOT an out parameter — the signature carries [Out] on an array that
                // the call fills in place, which reflection reports as "out GlobalObjectId[]".
                var globalIds = new GlobalObjectId[objects.Count];
                GlobalObjectId.GetGlobalObjectIdsSlow(asObjects, globalIds);

                for (int i = 0; i < globalIds.Length; i++)
                    ids.Add(UsableId(globalIds[i]) ? globalIds[i].ToString() : "");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scene Baselines] Object identities could not be read, so this " +
                                 "capture matches by hierarchy path only (a rename or re-parent may " +
                                 "report as MISSING): " + e.Message);

                ids.Clear();
                for (int i = 0; i < objects.Count; i++)
                    ids.Add("");
            }

            return ids;
        }

        /// <summary>
        /// Whether an identity is stable enough to match on.
        /// </summary>
        /// <remarks>
        /// A zero target id means the object has no entry in the scene file yet, so the value would
        /// change the moment the scene is saved — matching on it would invent a MISSING finding for
        /// an object nobody touched. Type 0 is Unity's own "null identity".
        /// </remarks>
        private static bool UsableId(GlobalObjectId id)
        {
            return id.identifierType != 0 && id.targetObjectId != 0;
        }

        /// <summary>
        /// One object's full recorded state. Public and side-effect free so it can be exercised
        /// on a throwaway object, without a scene, by the free property-capture test.
        /// </summary>
        public static string DescribeObjectState(GameObject go)
        {
            if (go == null)
                return "";

            var componentNames = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name);

            Transform t = go.transform;
            var rt = t as RectTransform;

            bool screenSpaceUI = IsUnderScreenSpaceCanvas(go);
            bool driven = rt != null && rt.drivenByObject != null;

            // activeSelf, not activeInHierarchy: this records what was AUTHORED on this object. A
            // parent being switched off already shows up as that parent's own change, and deriving
            // from the hierarchy would report one edit as a finding on every descendant.
            string state = $"active={(go.activeSelf ? "true" : "false")}";

            state += $" components=[{string.Join(",", componentNames)}]";

            // World position under a screen-space canvas is recomputed from the Game View
            // size, so it differs between two captures of a scene nobody touched — and
            // differs again on CI, where the resolution is different from every developer's.
            state += screenSpaceUI ? " pos(screen-driven)" : $" pos({FormatVec3(t.position)})";

            if (driven)
            {
                // Unity computes every value on this RectTransform, so none of it is authored
                // state. Record WHAT drives it instead: that changing is a real difference,
                // while the numbers changing is just a window being resized.
                state += $" transform(driven-by:{rt.drivenByObject.GetType().Name})";
            }
            else
            {
                state += $" rot({FormatVec3(t.localEulerAngles)}) scale({FormatVec3(t.localScale)})";
                state += DescribeRectTransform(t);
            }

            // Bounds are world-space, so they inherit the same screen dependence as position.
            if (!screenSpaceUI && TryGetWorldBounds(go, out Bounds bounds))
                state += $" bounds(center=({FormatVec3(bounds.center)}) size=({FormatVec3(bounds.size)}))";

            state += DescribeChildOrder(t);
            state += DescribeComponentProperties(go);

            return state;
        }

        /// <summary>Names of an object's children, in order.</summary>
        /// <remarks>
        /// Sibling order is not decoration: under a Canvas it IS draw order, so swapping two
        /// panels changes what the player sees while every transform, component and property
        /// stays identical.
        ///
        /// Recorded as one list on the PARENT rather than as an index on each child, which is the
        /// difference between a usable report and a wall of noise. With per-child indices,
        /// deleting the second of ten children reports one MISSING plus eight renumbered
        /// siblings; as a list it is one changed parent plus the one MISSING child. Attribution
        /// again: what happened is "this parent's children changed", once.
        ///
        /// Skipped below two children, where order cannot differ.
        /// </remarks>
        private static string DescribeChildOrder(Transform t)
        {
            if (t == null || t.childCount < 2)
                return "";

            var names = new List<string>();
            int shown = Math.Min(t.childCount, MaxRecordedChildren);

            for (int i = 0; i < shown; i++)
                names.Add(Sanitise(t.GetChild(i).name));

            string more = t.childCount > shown ? ",+" + (t.childCount - shown) + "-more" : "";
            return $" children=({string.Join(",", names)}{more})";
        }

        /// <summary>Children listed by name before the list is cut short.</summary>
        public const int MaxRecordedChildren = 30;

        /// <summary>The scene's top-level objects, in hierarchy order.</summary>
        /// <remarks>
        /// The one level <see cref="DescribeChildOrder"/> cannot reach. Child order is recorded on the
        /// PARENT, and root objects have no parent record — so until this existed, dragging a root
        /// object up or down the Hierarchy changed the saved scene file (Unity stores it in
        /// SceneRoots.m_Roots) and every baseline still matched byte for byte. Found by the first
        /// person to try it, who reasonably concluded the tool was broken.
        ///
        /// It matters for the same reason child order does: two screen-space Canvases at the same
        /// sorting order draw in hierarchy order, so swapping them changes what the player sees while
        /// every transform, component and property stays identical.
        ///
        /// Recorded as a scene SETTING rather than on an object, because it belongs to no object —
        /// same attribution rule as the layer collision matrix. Baselines recorded before this
        /// simply have no such group, and a group the baseline never recorded is skipped rather than
        /// reported, so nothing already on disk is invalidated.
        /// </remarks>
        private static string DescribeRootOrder()
        {
            Scene scene = SceneManager.GetActiveScene();

            if (!scene.IsValid())
                return "roots=()";

            GameObject[] roots = scene.GetRootGameObjects();
            var names = new List<string>();
            int shown = Math.Min(roots.Length, MaxRecordedChildren);

            for (int i = 0; i < shown; i++)
                names.Add(Sanitise(roots[i].name));

            string more = roots.Length > shown ? ",+" + (roots.Length - shown) + "-more" : "";
            return $"roots=({string.Join(",", names)}{more})";
        }

        // ── Component properties ─────────────────────────────────────────────────
        //
        // Until 2026-08-07 the record held component TYPE NAMES and nothing else, so it could
        // answer "does this object still have a BoxCollider" but never "is that BoxCollider
        // still a solid wall". Every value a designer tunes — isTrigger, mass, a speed field, a
        // light's intensity, a missing reference — was invisible, which is precisely the class
        // of breakage the interviews described: someone nudges an Inspector value, nothing
        // errors, and it surfaces after a merge. It was caught by the 08-06 demo, where a
        // flipped isTrigger went unreported and the demo had to be broken a different way.

        /// <summary>
        /// Properties recorded per component before the rest are dropped.
        /// </summary>
        /// <remarks>
        /// A cap exists because the whole pitch over `git diff` on the scene YAML is legibility.
        /// Terrains, particle systems and big serialized arrays would otherwise bury a changed
        /// isTrigger under thousands of values nobody reads. Truncation is RECORDED rather than
        /// silent — a record that quietly covers less than it appears to is the exact failure
        /// this layer exists to remove.
        /// </remarks>
        public const int MaxPropertiesPerComponent = 24;

        /// <summary>How deep into nested serialized structures to descend.</summary>
        public const int MaxPropertyDepth = 2;

        /// <summary>Longest string value recorded, before it is cut short.</summary>
        public const int MaxStringValueLength = 64;

        /// <summary>
        /// Serialized properties that are identity or bookkeeping rather than authored state.
        /// </summary>
        private static readonly HashSet<string> IgnoredPropertyPaths = new HashSet<string>
        {
            // Which script/prefab this is, not what it was configured to do. The component type
            // name already appears in components=[...], so recording it again only adds noise.
            "m_Script",
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_EditorHideFlags",
            "m_EditorClassIdentifier",
        };

        /// <summary>
        /// Properties that Unity recomputes, keyed "ComponentType/propertyPath".
        /// </summary>
        /// <remarks>
        /// Same trap as the screen-space canvas exclusion above, in property form: a CanvasScaler
        /// writes Canvas.m_ScaleFactor from the Game View size, so recording it would make an
        /// untouched scene report a regression every time the window is resized — and report a
        /// different answer on CI, where the resolution matches nobody's machine. A check that
        /// cries wolf gets switched off, which costs more than the coverage it buys.
        /// </remarks>
        private static readonly HashSet<string> ComputedProperties = new HashSet<string>
        {
            "Canvas/m_ScaleFactor",
        };

        /// <summary>
        /// Lightmap, GI and ray-tracing bookkeeping, dropped from every component that has it.
        /// </summary>
        /// <remarks>
        /// Measured on Level_01 2026-08-07: a stock MeshRenderer emitted 24 properties and
        /// announced 7 more dropped, of which the overwhelming majority were baking flags at
        /// their defaults — m_RayTracingAccelStructBuildFlags, m_IgnoreNormalsForChartDetection
        /// and friends. That is a double cost. It buries the handful of values a designer
        /// actually tunes, against a product whose whole claim over `git diff` on the scene YAML
        /// is legibility; and because it consumes the per-component budget first, it pushes REAL
        /// state past the cap and out of the record entirely.
        ///
        /// These are written by the lighting and build pipelines rather than authored as
        /// gameplay, so a change to one is not the kind of breakage this layer reports. Kept
        /// deliberately narrow: sorting order, rendering layer mask, shadow casting and probe
        /// usage are all still recorded, because those DO change how a scene plays and looks.
        /// </remarks>
        private static readonly HashSet<string> BakingProperties = new HashSet<string>
        {
            "m_ScaleInLightmap",
            "m_ReceiveGI",
            "m_StitchLightmapSeams",
            "m_PreserveUVs",
            "m_IgnoreNormalsForChartDetection",
            "m_ImportantGI",
            "m_AutoUVMaxDistance",
            "m_AutoUVMaxAngle",
            "m_MinimumChartSize",
            "m_LightmapParameters",
            "m_SelectedEditorRenderState",
            "m_SelectedWireframeHidden",
            "m_SmallMeshCulling",
            "m_ForceMeshLod",
            "m_MeshLodSelectionBias",
        };

        /// <summary>Ray-tracing settings share a prefix and are all pipeline bookkeeping.</summary>
        private const string RayTracingPrefix = "m_RayTrac";

        private static string DescribeComponentProperties(GameObject go)
        {
            var sb = new System.Text.StringBuilder();

            foreach (Component component in go.GetComponents<Component>())
            {
                // A missing script serialises as a null component. Worth knowing about, and
                // already visible: GetComponents keeps the slot, so components=[...] shows it.
                if (component == null)
                    continue;

                // The transform is already recorded above, in a form that survives a resized
                // Game View. Recording it again here would undo that care.
                if (component is Transform)
                    continue;

                string properties = DescribeProperties(component);
                if (properties.Length > 0)
                    sb.Append($" props:{component.GetType().Name}({properties})");
            }

            return sb.ToString();
        }

        /// <summary>
        /// One component's serialized values, as space-separated `path=value` pairs.
        /// </summary>
        /// <remarks>
        /// SerializedObject rather than reflection over fields, for three reasons. It sees
        /// [SerializeField] private fields, which are how most Unity code actually stores tuned
        /// values. It shows exactly what the Inspector shows, so a user's own MonoBehaviour is
        /// covered without maintaining a list of known component types. And it reads the stored
        /// data rather than invoking C# properties — reading Renderer.material through reflection
        /// would INSTANTIATE a material clone, mutating the scene that capture is supposed to
        /// observe without touching.
        /// </remarks>
        public static string DescribeProperties(Component component)
        {
            return DescribeSerializedProperties(component, MaxPropertiesPerComponent, MaxPropertyDepth);
        }

        /// <summary>
        /// Any serialized object's values, as space-separated `path=value` pairs. Shared by
        /// components and by asset types whose data is plain serialized fields.
        /// </summary>
        public static string DescribeSerializedProperties(
            UnityEngine.Object target, int maxProperties, int maxDepth)
        {
            if (target == null)
                return "";

            var sb = new System.Text.StringBuilder();
            int recorded = 0;
            int skipped = 0;
            string typeName = target.GetType().Name;

            using (var serialized = new SerializedObject(target))
            {
                // Recorded explicitly because NextVisible does NOT return it: the enabled tick is
                // drawn in the component header rather than in the property list. Without this a
                // renderer switched off — an object that silently stopped being visible — reads as
                // completely unchanged, which is the exact breakage this file exists to catch.
                SerializedProperty enabledProperty = serialized.FindProperty("m_Enabled");
                if (enabledProperty != null && enabledProperty.propertyType == SerializedPropertyType.Boolean)
                {
                    sb.Append("enabled=").Append(enabledProperty.boolValue ? "true" : "false");
                    recorded++;
                }

                SerializedProperty property = serialized.GetIterator();

                // NextVisible honours Inspector visibility, so Unity's internal bookkeeping is
                // excluded before the ignore list is consulted at all.
                bool enterChildren = true;
                while (property.NextVisible(enterChildren))
                {
                    enterChildren = false;

                    if (IgnoredPropertyPaths.Contains(property.propertyPath) ||
                        BakingProperties.Contains(property.propertyPath) ||
                        property.propertyPath.StartsWith(RayTracingPrefix, StringComparison.Ordinal) ||
                        ComputedProperties.Contains(typeName + "/" + property.propertyPath))
                        continue;

                    if (property.depth > maxDepth)
                        continue;

                    if (!TryDescribeValue(property, out string value))
                    {
                        // A container has no value of its own; its children carry the meaning.
                        enterChildren = property.depth < maxDepth;
                        continue;
                    }

                    if (recorded >= maxProperties)
                    {
                        skipped++;
                        continue;
                    }

                    if (sb.Length > 0)
                        sb.Append(' ');

                    sb.Append(property.propertyPath).Append('=').Append(value);
                    recorded++;
                }
            }

            if (skipped > 0)
                sb.Append($" truncated=({skipped} more not recorded)");

            return sb.ToString();
        }

        /// <summary>
        /// One property as a comparable string, or false when it is a container to descend into.
        /// </summary>
        private static bool TryDescribeValue(SerializedProperty property, out string value)
        {
            value = null;

            // An array holds no value of its own. Descending records its size as Array.size and
            // its entries as Array.data[i], so a material list losing an entry is reported even
            // when every remaining element still matches.
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
                return false;

            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    value = property.boolValue ? "true" : "false";
                    return true;

                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.LayerMask:
                    value = property.intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Float:
                    value = FormatFloat(property.floatValue);
                    return true;

                case SerializedPropertyType.String:
                    value = Quote(property.stringValue);
                    return true;

                case SerializedPropertyType.Enum:
                    // The name, not the index: reordering an enum in code would otherwise read as
                    // every object using it having changed.
                    value = property.enumValueIndex >= 0 &&
                            property.enumDisplayNames != null &&
                            property.enumValueIndex < property.enumDisplayNames.Length
                        ? Quote(property.enumDisplayNames[property.enumValueIndex])
                        : property.enumValueIndex.ToString();
                    return true;

                case SerializedPropertyType.ObjectReference:
                    value = DescribeObjectReference(property.objectReferenceValue);
                    return true;

                case SerializedPropertyType.Color:
                    Color c = property.colorValue;
                    value = $"({FormatFloat(c.r)},{FormatFloat(c.g)},{FormatFloat(c.b)},{FormatFloat(c.a)})";
                    return true;

                case SerializedPropertyType.Vector2:
                    value = $"({FormatVec2(property.vector2Value)})";
                    return true;

                case SerializedPropertyType.Vector3:
                    value = $"({FormatVec3(property.vector3Value)})";
                    return true;

                case SerializedPropertyType.Vector4:
                    Vector4 v4 = property.vector4Value;
                    value = $"({FormatFloat(v4.x)},{FormatFloat(v4.y)},{FormatFloat(v4.z)},{FormatFloat(v4.w)})";
                    return true;

                case SerializedPropertyType.Quaternion:
                    Quaternion q = property.quaternionValue;
                    value = $"({FormatFloat(q.x)},{FormatFloat(q.y)},{FormatFloat(q.z)},{FormatFloat(q.w)})";
                    return true;

                case SerializedPropertyType.Vector2Int:
                    Vector2Int v2i = property.vector2IntValue;
                    value = $"({v2i.x},{v2i.y})";
                    return true;

                case SerializedPropertyType.Vector3Int:
                    Vector3Int v3i = property.vector3IntValue;
                    value = $"({v3i.x},{v3i.y},{v3i.z})";
                    return true;

                case SerializedPropertyType.Rect:
                    Rect r = property.rectValue;
                    value = $"({FormatFloat(r.x)},{FormatFloat(r.y)},{FormatFloat(r.width)},{FormatFloat(r.height)})";
                    return true;

                case SerializedPropertyType.Bounds:
                    Bounds b = property.boundsValue;
                    value = $"(center=({FormatVec3(b.center)}) size=({FormatVec3(b.size)}))";
                    return true;

                case SerializedPropertyType.Character:
                    value = property.intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.AnimationCurve:
                    // The curve's shape has no short stable text form. Its key count still catches
                    // a curve being cleared or replaced, which is the change worth reporting.
                    AnimationCurve curve = property.animationCurveValue;
                    value = $"(keys={(curve != null ? curve.length : 0)})";
                    return true;

                default:
                    // Generic structs and nested classes hold no value themselves — descend.
                    return false;
            }
        }

        /// <summary>
        /// A reference recorded by NAME and TYPE, never by instance ID.
        /// </summary>
        /// <remarks>
        /// Instance IDs are regenerated on every domain reload, so recording one would make every
        /// baseline report a scene-wide regression after the next script compile. Name and type
        /// are stable across reloads and across machines, which is what a committed baseline and
        /// a CI run both need. The cost is that swapping one asset for another of the same name
        /// reads as unchanged — a narrow blind spot next to a check that fails constantly.
        ///
        /// Null is recorded explicitly: a reference going missing is one of the failures studios
        /// already write their own asserts for.
        /// </remarks>
        private static string DescribeObjectReference(UnityEngine.Object reference)
        {
            if (reference == null)
                return "none";

            return Quote(reference.name + ":" + reference.GetType().Name);
        }

        private static string FormatFloat(float f)
        {
            // Same rounding as the vector formatters. Physics settles to values that wobble in the
            // last decimal places, and recording that noise would report regressions on a scene
            // nobody touched.
            return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Wraps a free-text value so it cannot break the state string's own structure.
        /// </summary>
        /// <remarks>
        /// A state string is split on spaces at bracket depth zero, so an unwrapped label reading
        /// "Game Over" would parse as two segments and corrupt every later one. Brackets inside
        /// the text are replaced rather than escaped, which means two labels differing ONLY in
        /// bracket characters record identically. That is a real, narrow blind spot, accepted
        /// because the alternative is an escaping scheme the diff reader has to understand too.
        /// </remarks>
        private static string Quote(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "()";

            string cleaned = raw
                .Replace('(', '_').Replace(')', '_')
                .Replace('[', '_').Replace(']', '_')
                .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            if (cleaned.Length > MaxStringValueLength)
                cleaned = cleaned.Substring(0, MaxStringValueLength) + "…";

            return "(" + cleaned + ")";
        }

        // ── Referenced assets ────────────────────────────────────────────────────
        //
        // A scene is not only its objects. Recording that an object points at
        // "Wall:Material" says nothing about what that material IS, so recolouring it,
        // swapping its shader or clearing its texture all passed a check untouched — while
        // looking, on screen, exactly like the breakage this layer claims to find. The
        // reference name was never the point; the asset's contents are.
        //
        // Attribution matters as much as detection. A material used by forty objects must
        // report as ONE changed material, not forty changed objects: the second is both
        // unreadable and actively misleading about what someone did.

        /// <summary>One asset's recorded contents.</summary>
        public class SceneAssetRecord
        {
            public string path;   // asset path, "::name" suffixed for a sub-asset
            public string type;   // asset class name, e.g. Material
            public string state;  // contents, in the same key=value shape objects use
        }

        /// <summary>What a sweep of the scene's referenced assets found.</summary>
        public class SceneAssetCapture
        {
            public List<SceneAssetRecord> assets = new List<SceneAssetRecord>();

            /// <summary>
            /// Referenced project assets whose CONTENTS this tool cannot read.
            /// </summary>
            /// <remarks>
            /// Counted rather than listed, and reported rather than hidden. Textures, meshes,
            /// audio and animator controllers are all referenced by a normal scene and none of
            /// them can be dumped into a legible line, so a baseline covers strictly less than
            /// "the scene and everything it touches". Saying how much less is the difference
            /// between a known limit and a false claim — and listing two hundred uncovered
            /// textures would help nobody, so the number carries it.
            /// </remarks>
            public int notChecked;
        }

        /// <summary>Assets whose contents can be recorded, keyed by how they get read.</summary>
        private static bool IsCheckableAsset(UnityEngine.Object asset)
        {
            return asset is Material || asset is ScriptableObject || asset is PhysicsMaterial;
        }

        /// <summary>Properties recorded per asset, higher than a component's because a material
        /// legitimately exposes dozens and truncating them loses the tuned values.</summary>
        public const int MaxPropertiesPerAsset = 60;

        /// <summary>
        /// Every project asset the open scene depends on, with contents where readable.
        /// </summary>
        /// <remarks>
        /// EditorUtility.CollectDependencies rather than the references seen during property
        /// capture: it follows nested references (a material's texture, a ScriptableObject's
        /// links) that a single pass over components would never reach.
        ///
        /// Built-in assets are skipped deliberately. They live outside Assets/ in Unity's own
        /// resource files and cannot be edited, so recording them adds bulk to every baseline
        /// and can never produce a finding.
        /// </remarks>
        public static SceneAssetCapture CaptureReferencedAssets()
        {
            var capture = new SceneAssetCapture();
            var seen = new HashSet<string>();

            // Active scene only, for the same reason the object sweep is: assets reached through
            // another loaded scene are that scene's dependencies, and recording them here would
            // make this baseline report changes to material it does not own.
            //
            // The same sweep the objects use, so what is fed to CollectDependencies is in a fixed
            // order too. The recorded list is sorted by path further down and would come out stable
            // either way — this is about not leaving an unspecified order in the middle of a capture
            // whose whole job is to be repeatable.
            GameObject[] roots = SweepActiveSceneObjects().ToArray();

            UnityEngine.Object[] dependencies;
            try
            {
                dependencies = EditorUtility.CollectDependencies(roots);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scene Baselines] Could not collect scene dependencies: " + e.Message);
                return capture;
            }

            foreach (UnityEngine.Object dependency in dependencies)
            {
                if (dependency == null || dependency is GameObject || dependency is Component)
                    continue;

                string assetPath = AssetDatabase.GetAssetPath(dependency);

                // No path means it lives in the scene itself, not in an asset file — an embedded
                // material or mesh, already covered by the object that owns it.
                if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                    continue;

                string key = AssetDatabase.IsMainAsset(dependency)
                    ? assetPath
                    : assetPath + "::" + dependency.name;

                if (!seen.Add(key))
                    continue;

                if (!IsCheckableAsset(dependency))
                {
                    capture.notChecked++;
                    continue;
                }

                capture.assets.Add(new SceneAssetRecord
                {
                    path = key,
                    type = dependency.GetType().Name,
                    state = DescribeAsset(dependency)
                });
            }

            capture.assets.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            return capture;
        }

        /// <summary>One asset's contents, in the same shape object state uses.</summary>
        public static string DescribeAsset(UnityEngine.Object asset)
        {
            var material = asset as Material;
            if (material != null)
                return DescribeMaterial(material);

            return DescribeSerializedProperties(asset, MaxPropertiesPerAsset, MaxPropertyDepth);
        }

        /// <summary>
        /// A material through its SHADER's property list rather than its serialized fields.
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-07, and the reason this is not the generic dump: a material's tuned
        /// values live at depth 3-5 inside m_SavedProperties, behind an entry for every texture
        /// slot the shader declares. A depth-limited generic walk reaches m_TexEnvs scale/offset
        /// boilerplate and exhausts its budget long before m_Colors — recording pages of nothing
        /// and missing the colour, which is the single most likely thing to change.
        ///
        /// The shader API gives the same properties the Inspector shows, under the names a human
        /// uses (_BaseColor, _Metallic), with no serialization plumbing in between.
        ///
        /// The shader itself is recorded by name because a material losing its shader is the
        /// classic "everything turned pink" regression, and it must not read as unchanged.
        /// </remarks>
        private static string DescribeMaterial(Material material)
        {
            Shader shader = material.shader;

            var sb = new System.Text.StringBuilder();
            sb.Append("shader=").Append(shader != null ? Quote(shader.name) : "none");
            sb.Append(" renderQueue=").Append(material.renderQueue);

            if (shader == null)
                return sb.ToString();

            int count = shader.GetPropertyCount();
            int recorded = 0;
            int skipped = 0;

            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);

                if (recorded >= MaxPropertiesPerAsset)
                {
                    skipped++;
                    continue;
                }

                string value;
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        Color c = material.GetColor(name);
                        value = $"({FormatFloat(c.r)},{FormatFloat(c.g)},{FormatFloat(c.b)},{FormatFloat(c.a)})";
                        break;

                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = FormatFloat(material.GetFloat(name));
                        break;

                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        value = material.GetInteger(name)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;

                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        Vector4 v = material.GetVector(name);
                        value = $"({FormatFloat(v.x)},{FormatFloat(v.y)},{FormatFloat(v.z)},{FormatFloat(v.w)})";
                        break;

                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        value = DescribeObjectReference(material.GetTexture(name));
                        break;

                    default:
                        continue;
                }

                sb.Append(' ').Append(name).Append('=').Append(value);
                recorded++;
            }

            if (skipped > 0)
                sb.Append($" truncated=({skipped} more not recorded)");

            return sb.ToString();
        }

        // ── Scene and project settings ───────────────────────────────────────────
        //
        // Neither of these is a GameObject or an asset, so nothing above sees them — yet both
        // decide how a scene behaves. Halving gravity, switching off a layer collision pair,
        // deleting a tag or changing the fixed timestep all break gameplay everywhere at once
        // while every object and every asset still records exactly as before.
        //
        // Project settings are recorded INTO a scene baseline on purpose. A baseline says "this
        // scene was known good", and a physics scene that worked at gravity -9.81 is not
        // evidence of anything at -2: the project state is part of what made it work. The cost
        // is that in a multi-scene CI run one gravity change reports once per scene, which is
        // repetitive but not wrong — each scene genuinely no longer matches its own record.

        /// <summary>One group of settings, recorded like any other state.</summary>
        public class SceneSettingsRecord
        {
            public string scope;  // "scene" (belongs to this scene) or "project" (shared)
            public string group;  // e.g. render, physics, tags
            public string state;
        }

        /// <summary>
        /// Names of the other scenes that were loaded alongside the active one.
        /// </summary>
        /// <remarks>
        /// Context for a reader, never a comparison key. The active scene's objects are captured
        /// on their own, so a baseline stays valid whatever else is open — but on an additive
        /// setup what made the scene work may partly live next door, and "known good, with
        /// UI_Overlay also loaded" is a more honest claim than "known good". Comparing it would
        /// reintroduce the false regressions this change removes: opening one more scene is not
        /// a regression in this one.
        /// </remarks>
        /// <summary>How a never-saved scene is named in the loaded-alongside context.</summary>
        public const string UnsavedSceneName = "(unsaved scene)";

        public static List<string> OtherLoadedScenes()
        {
            var names = new List<string>();
            Scene active = SceneManager.GetActiveScene();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded || scene == active)
                    continue;

                // A scene that has never been saved reports an empty name, which would print as a
                // blank in the report and read as a bug. Say what it actually is.
                names.Add(string.IsNullOrEmpty(scene.name) ? UnsavedSceneName : scene.name);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>Scene render/lighting settings plus the project settings gameplay rests on.</summary>
        public static List<SceneSettingsRecord> CaptureSettings()
        {
            return new List<SceneSettingsRecord>
            {
                new SceneSettingsRecord { scope = "scene",   group = "render",  state = DescribeRenderSettings() },
                new SceneSettingsRecord { scope = "scene",   group = "lighting", state = DescribeLightingSettings() },
                new SceneSettingsRecord { scope = "scene",   group = "rootOrder", state = DescribeRootOrder() },
                new SceneSettingsRecord { scope = "project", group = "physics", state = DescribePhysicsSettings() },
                new SceneSettingsRecord { scope = "project", group = "layers",  state = DescribeLayerCollisions() },
                new SceneSettingsRecord { scope = "project", group = "layers2D", state = DescribeLayerCollisions2D() },
                new SceneSettingsRecord { scope = "project", group = "tags",    state = DescribeTagsAndLayers() },
                new SceneSettingsRecord { scope = "project", group = "time",    state = DescribeTimeSettings() },
                new SceneSettingsRecord { scope = "project", group = "defines", state = DescribeScriptingDefines() },
                new SceneSettingsRecord { scope = "project", group = "build",   state = DescribeBuildScenes() },
                new SceneSettingsRecord { scope = "project", group = "input",   state = DescribeInputAxes() },
            };
        }

        /// <summary>
        /// Which symbols the project compiles with, and for which platform.
        /// </summary>
        /// <remarks>
        /// A define decides which code exists. Adding or removing one silently changes behaviour
        /// everywhere without touching a scene, an asset or a serialized value — the same shape as
        /// gravity, and just as invisible. The active build target is recorded with them because
        /// defines are per-target: the same project compiles differently after a platform switch.
        /// </remarks>
        private static string DescribeScriptingDefines()
        {
            var sb = new System.Text.StringBuilder();

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);

            sb.Append("activeBuildTarget=").Append(Quote(target.ToString()));

            try
            {
                string[] defines;
                PlayerSettings.GetScriptingDefineSymbols(
                    UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group), out defines);

                var sorted = new List<string>(defines ?? new string[0]);

                // Sorted because the ORDER of defines has no meaning to the compiler; recording it
                // would report a reordering as a change nobody made.
                sorted.Sort(StringComparer.Ordinal);

                sb.Append(" defines=(").Append(string.Join(",", sorted.Select(Sanitise))).Append(')');
            }
            catch (Exception e)
            {
                sb.Append(" defines=(unreadable:").Append(Sanitise(e.GetType().Name)).Append(')');
            }

            return sb.ToString();
        }

        /// <summary>
        /// The build scene list, in order, with each scene's enabled state.
        /// </summary>
        /// <remarks>
        /// Order is recorded because code loads scenes BY INDEX, so reordering the list silently
        /// sends the player somewhere else. Removing or disabling a scene breaks LoadScene at
        /// runtime while every scene file on disk is untouched.
        /// </remarks>
        private static string DescribeBuildScenes()
        {
            var sb = new System.Text.StringBuilder();
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];

            sb.Append("count=").Append(scenes.Length);

            for (int i = 0; i < scenes.Length; i++)
            {
                sb.Append(' ').Append(i.ToString("00")).Append('=')
                  .Append('(').Append(scenes[i].enabled ? "on" : "off").Append(' ')
                  .Append(Sanitise(scenes[i].path)).Append(')');
            }

            return sb.ToString();
        }

        /// <summary>
        /// The legacy Input Manager axes, by index, with their bindings.
        /// </summary>
        /// <remarks>
        /// Code reaches these by STRING name, so renaming or deleting an axis breaks input with no
        /// compile error and nothing to see in any scene. Keyed by index rather than name because
        /// Unity ships duplicate names on purpose (a keyboard "Horizontal" and a joystick one), and
        /// a name-keyed record could not tell them apart.
        ///
        /// The new Input System needs nothing here: its actions live in an InputActionAsset, which
        /// is a ScriptableObject and is already covered by asset capture whenever the scene
        /// references it.
        /// </remarks>
        private static string DescribeInputAxes()
        {
            UnityEngine.Object[] assets;
            try
            {
                assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset");
            }
            catch (Exception e)
            {
                return "axes=(unreadable:" + Sanitise(e.GetType().Name) + ")";
            }

            if (assets == null || assets.Length == 0 || assets[0] == null)
                return "axes=none";

            var sb = new System.Text.StringBuilder();

            using (var serialized = new SerializedObject(assets[0]))
            {
                SerializedProperty axes = serialized.FindProperty("m_Axes");
                if (axes == null || !axes.isArray)
                    return "axes=none";

                sb.Append("count=").Append(axes.arraySize);

                for (int i = 0; i < axes.arraySize; i++)
                {
                    SerializedProperty axis = axes.GetArrayElementAtIndex(i);

                    sb.Append(' ').Append("axis").Append(i.ToString("00")).Append("=(")
                      .Append(Sanitise(Field(axis, "m_Name")))
                      .Append(' ').Append("pos:").Append(Sanitise(Field(axis, "positiveButton")))
                      .Append(' ').Append("neg:").Append(Sanitise(Field(axis, "negativeButton")))
                      .Append(' ').Append("alt:").Append(Sanitise(Field(axis, "altPositiveButton")))
                      .Append(' ').Append("type:").Append(Field(axis, "type"))
                      .Append(')');
                }
            }

            return sb.ToString();
        }

        /// <summary>A child property as text, empty rather than throwing when absent.</summary>
        private static string Field(SerializedProperty parent, string name)
        {
            SerializedProperty property = parent?.FindPropertyRelative(name);
            if (property == null)
                return "";

            switch (property.propertyType)
            {
                case SerializedPropertyType.String:  return property.stringValue;
                case SerializedPropertyType.Enum:    return property.enumValueIndex.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:   return FormatFloat(property.floatValue);
                default:                             return property.intValue.ToString();
            }
        }

        // Named properties rather than a serialized dump, for the same reason materials use the
        // shader API: these have well-known names a human recognises, and "fog=true" in a report
        // is worth more than the field Unity happens to store it in.
        private static string DescribeRenderSettings()
        {
            var sb = new System.Text.StringBuilder();

            sb.Append("fog=").Append(RenderSettings.fog ? "true" : "false");
            sb.Append(" fogMode=").Append(Quote(RenderSettings.fogMode.ToString()));
            sb.Append(" fogColor=").Append(FormatColor(RenderSettings.fogColor));
            sb.Append(" fogDensity=").Append(FormatFloat(RenderSettings.fogDensity));
            sb.Append(" fogStart=").Append(FormatFloat(RenderSettings.fogStartDistance));
            sb.Append(" fogEnd=").Append(FormatFloat(RenderSettings.fogEndDistance));

            sb.Append(" ambientMode=").Append(Quote(RenderSettings.ambientMode.ToString()));
            sb.Append(" ambientLight=").Append(FormatColor(RenderSettings.ambientLight));
            sb.Append(" ambientSky=").Append(FormatColor(RenderSettings.ambientSkyColor));
            sb.Append(" ambientEquator=").Append(FormatColor(RenderSettings.ambientEquatorColor));
            sb.Append(" ambientGround=").Append(FormatColor(RenderSettings.ambientGroundColor));
            sb.Append(" ambientIntensity=").Append(FormatFloat(RenderSettings.ambientIntensity));

            sb.Append(" skybox=").Append(DescribeObjectReference(RenderSettings.skybox));
            sb.Append(" sun=").Append(DescribeObjectReference(RenderSettings.sun));

            sb.Append(" reflectionIntensity=").Append(FormatFloat(RenderSettings.reflectionIntensity));
            sb.Append(" reflectionBounces=").Append(RenderSettings.reflectionBounces);
            sb.Append(" haloStrength=").Append(FormatFloat(RenderSettings.haloStrength));
            sb.Append(" flareStrength=").Append(FormatFloat(RenderSettings.flareStrength));

            return sb.ToString();
        }

        private static string DescribeLightingSettings()
        {
            var sb = new System.Text.StringBuilder();

            // Quoted because this is a FLAGS enum: it stringifies as "Single, Dual", and an
            // unwrapped space would split one value into two segments and corrupt every segment
            // after it. Every enum here is wrapped for the same reason, whether or not today's
            // values happen to contain a space.
            sb.Append("lightmapsMode=").Append(Quote(LightmapSettings.lightmapsMode.ToString()));
            sb.Append(" lightmapCount=").Append(LightmapSettings.lightmaps?.Length ?? 0);
            // On UnityEditor.Lightmapping, not LightmapSettings — the runtime class does not expose it.
            sb.Append(" lightingData=").Append(DescribeObjectReference(Lightmapping.lightingDataAsset));
            sb.Append(" lightProbes=").Append(DescribeObjectReference(LightmapSettings.lightProbes));
            return sb.ToString();
        }

        private static string DescribePhysicsSettings()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("gravity=(").Append(FormatVec3(Physics.gravity)).Append(')');
            sb.Append(" defaultSolverIterations=").Append(Physics.defaultSolverIterations);
            sb.Append(" defaultSolverVelocityIterations=").Append(Physics.defaultSolverVelocityIterations);
            sb.Append(" bounceThreshold=").Append(FormatFloat(Physics.bounceThreshold));
            sb.Append(" defaultContactOffset=").Append(FormatFloat(Physics.defaultContactOffset));
            sb.Append(" sleepThreshold=").Append(FormatFloat(Physics.sleepThreshold));
            sb.Append(" queriesHitTriggers=").Append(Physics.queriesHitTriggers ? "true" : "false");
            sb.Append(" queriesHitBackfaces=").Append(Physics.queriesHitBackfaces ? "true" : "false");
            // 2D gets the same depth as 3D. The first version recorded only gravity2D and
            // queriesHitTriggers2D, which meant a 2D project — this one included — had its physics
            // barely covered while a 3D project had it fully covered.
            sb.Append(" gravity2D=(").Append(FormatVec2(Physics2D.gravity)).Append(')');
            sb.Append(" velocityIterations2D=").Append(Physics2D.velocityIterations);
            sb.Append(" positionIterations2D=").Append(Physics2D.positionIterations);
            sb.Append(" bounceThreshold2D=").Append(FormatFloat(Physics2D.bounceThreshold));
            sb.Append(" defaultContactOffset2D=").Append(FormatFloat(Physics2D.defaultContactOffset));
            sb.Append(" simulationMode2D=").Append(Quote(Physics2D.simulationMode.ToString()));
            sb.Append(" queriesHitTriggers2D=").Append(Physics2D.queriesHitTriggers ? "true" : "false");
            sb.Append(" queriesStartInColliders2D=").Append(Physics2D.queriesStartInColliders ? "true" : "false");
            sb.Append(" callbacksOnDisable2D=").Append(Physics2D.callbacksOnDisable ? "true" : "false");
            sb.Append(" reuseCollisionCallbacks2D=").Append(Physics2D.reuseCollisionCallbacks ? "true" : "false");
            sb.Append(" autoSyncTransforms2D=").Append(Physics2D.autoSyncTransforms ? "true" : "false");
            return sb.ToString();
        }

        /// <summary>
        /// The layer collision matrix, as the pairs each layer does NOT collide with.
        /// </summary>
        /// <remarks>
        /// Recorded by NAME rather than as 32 bitmasks, because the point is to be readable: a
        /// finding that says "Player: (Water) → (Water,Enemy)" tells someone what broke, while
        /// "Player: 0xfffffff7 → 0xffffffe7" tells them to go and decode hex. Switching off one
        /// pair in this matrix stops collisions everywhere in the game and touches no object, no
        /// asset and no scene file — exactly the invisible, project-wide change this is for.
        ///
        /// Layers with nothing ignored are omitted so an untouched project records almost
        /// nothing; the first ignored pair then appears as a new entry, which the diff reports.
        /// </remarks>
        private static string DescribeLayerCollisions()
        {
            return DescribeLayerCollisions(Physics.GetIgnoreLayerCollision);
        }

        /// <summary>
        /// The 2D collision matrix, which is a SEPARATE matrix from the 3D one.
        /// </summary>
        /// <remarks>
        /// Missed in the first version of settings capture: only Physics.GetIgnoreLayerCollision
        /// was read, so a 2D project's collision matrix — the one that actually governs its
        /// gameplay — was not covered at all. Its own group rather than merged into the 3D one, so
        /// a finding says which physics world changed.
        /// </remarks>
        private static string DescribeLayerCollisions2D()
        {
            return DescribeLayerCollisions(Physics2D.GetIgnoreLayerCollision);
        }

        private static string DescribeLayerCollisions(Func<int, int, bool> ignores)
        {
            var entries = new System.Text.StringBuilder();
            int pairs = 0;

            for (int layer = 0; layer < 32; layer++)
            {
                string name = LayerMask.LayerToName(layer);
                if (string.IsNullOrEmpty(name))
                    continue;

                var ignored = new List<string>();

                // Only one direction per pair. The matrix is symmetric, so listing both would
                // report every change twice and say the same thing the second time.
                for (int other = layer + 1; other < 32; other++)
                {
                    string otherName = LayerMask.LayerToName(other);
                    if (string.IsNullOrEmpty(otherName))
                        continue;

                    if (ignores(layer, other))
                        ignored.Add(otherName);
                }

                if (ignored.Count == 0)
                    continue;

                pairs += ignored.Count;
                entries.Append(' ').Append(Sanitise(name))
                    .Append("=(").Append(string.Join(",", ignored.Select(Sanitise))).Append(')');
            }

            // The count leads, and is emitted even at zero. Without it a project that ignored
            // nothing recorded an empty string, and the first ignored pair then read as the whole
            // group appearing from nowhere rather than as a count going up.
            return "ignoredPairs=" + pairs + entries;
        }

        /// <summary>
        /// Tags and layer names. Deleting either breaks references that nothing else records.
        /// </summary>
        /// <remarks>
        /// Layers are recorded WITH their index, because objects store the index and not the
        /// name: renaming layer 8 leaves every object on layer 8 pointing at different meaning,
        /// and recording bare names would miss a reorder entirely.
        /// </remarks>
        private static string DescribeTagsAndLayers()
        {
            var sb = new System.Text.StringBuilder();

            string[] tags = UnityEditorInternal.InternalEditorUtility.tags ?? new string[0];
            sb.Append("tags=(").Append(string.Join(",", tags.Select(Sanitise))).Append(')');

            var layers = new List<string>();
            for (int layer = 0; layer < 32; layer++)
            {
                string name = LayerMask.LayerToName(layer);
                if (!string.IsNullOrEmpty(name))
                    layers.Add(layer + ":" + Sanitise(name));
            }

            sb.Append(" layers=(").Append(string.Join(",", layers)).Append(')');
            return sb.ToString();
        }

        private static string DescribeTimeSettings()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("fixedDeltaTime=").Append(FormatFloat(Time.fixedDeltaTime));
            sb.Append(" maximumDeltaTime=").Append(FormatFloat(Time.maximumDeltaTime));
            sb.Append(" maximumParticleDeltaTime=").Append(FormatFloat(Time.maximumParticleDeltaTime));

            // Deliberately NOT Time.timeScale: it is runtime state a play session leaves behind,
            // not an authored setting, and recording it would report a regression because someone
            // paused the game.
            return sb.ToString();
        }

        private static string FormatColor(Color c)
        {
            return $"({FormatFloat(c.r)},{FormatFloat(c.g)},{FormatFloat(c.b)},{FormatFloat(c.a)})";
        }

        /// <summary>Removes the characters that would break the state string's own structure.</summary>
        private static string Sanitise(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "_";

            return raw
                .Replace('(', '_').Replace(')', '_')
                .Replace('[', '_').Replace(']', '_')
                .Replace(',', '_').Replace(' ', '_');
        }

        /// <summary>
        /// The live scene in the exact shape baselines are stored in.
        /// </summary>
        /// <remarks>
        /// Both writing a baseline and later checking against one go through here on purpose.
        /// The state string IS the comparison key, so if capture and re-check ever built it
        /// differently every stored baseline would report false regressions on untouched
        /// objects — the single worst failure this feature could have. One producer, no drift.
        /// </remarks>
        public static List<BaselineObjectRecord> CaptureBaselineObjects()
        {
            return ToBaselineObjects(CaptureSceneSnapshot());
        }

        public static List<BaselineObjectRecord> ToBaselineObjects(SceneSnapshot snapshot)
        {
            if (snapshot?.objects == null)
                return new List<BaselineObjectRecord>();

            return snapshot.objects
                .Select(o => new BaselineObjectRecord { path = o.path, state = o.state, id = o.id })
                .ToList();
        }
    }
}
