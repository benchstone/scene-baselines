using UnityEngine;

namespace SceneBaselines
{
    // Fixtures for PropertyCaptureTest. A user's own MonoBehaviour is the case that
    // matters most for property capture — Unity's built-in components can be special-cased, but
    // the speed field on someone's PlayerController cannot — and it cannot be exercised with a
    // bare GameObject.
    //
    // In the runtime assembly because AddComponent needs them there, and deliberately inert:
    // nothing references them outside the test.

    /// <summary>A tuned script, of the shape a designer actually edits in the Inspector.</summary>
    public class PropertyFixture : MonoBehaviour
    {
        public float speed = 5f;

        // The case reflection over public fields would miss entirely. Most Unity code stores
        // tuned values exactly like this, so missing it would leave the coverage gap half open.
        [SerializeField] private float hiddenTuning = 1f;

        // A reference going null is one of the failures studios already write their own asserts
        // for, so capture has to be able to see it.
        public GameObject linkedTarget;

        // Not serialized, so it must NOT be recorded: it is not state anyone authored, and
        // recording it would report changes nobody made.
        [System.NonSerialized] public float runtimeOnly;

        public void SetHiddenTuning(float value) => hiddenTuning = value;
    }

    /// <summary>
    /// More serialized fields than a single component is allowed to record, so truncation can be
    /// proven to announce itself rather than quietly narrowing what the baseline covers.
    /// </summary>
    public class ManyPropertyFixture : MonoBehaviour
    {
        public float f01, f02, f03, f04, f05, f06, f07, f08, f09, f10;
        public float f11, f12, f13, f14, f15, f16, f17, f18, f19, f20;
        public float f21, f22, f23, f24, f25, f26, f27, f28, f29, f30;
    }
}
