using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Optional per-object control over how a prop responds to being shoved.
    ///
    /// You do NOT need this to make a prop pushable — any non-kinematic Rigidbody
    /// already gets a default shove, and Rigidbody.mass is the first and best
    /// knob (the push is mass-aware, so heavy things genuinely resist). Add this
    /// only when mass isn't the lever you want: e.g. a light-looking crate that
    /// should still feel stubborn, or a bucket that should skitter further than
    /// its weight suggests.
    ///
    /// Crucially, tuning happens HERE, per object — the player's push settings
    /// stay untouched, so nothing you do to a barrel can un-tune the doors.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PushableProp : MonoBehaviour, IPushable
    {
        [Tooltip("Scales the player's push for THIS object only. 1 = the player's full force. <1 = stubborn (a crate you lean on and barely move). >1 = skittish (an empty bucket that scoots).")]
        [SerializeField] private float pushMultiplier = 1f;

        [Tooltip("Stop adding force once the prop is already moving this fast (m/s). Without a cap you can walk into something repeatedly and accelerate it to absurd speed.")]
        [SerializeField] private float maxPushSpeed = 3f;

        [Tooltip("ON: push at the contact point, so props tip and spin realistically when you catch them high or off-centre. OFF: push at the centre of mass, so they slide flat and never topple (good for things that shouldn't fall over).")]
        [SerializeField] private bool pushAtContactPoint = true;

        private Rigidbody body;

        private void Awake() => body = GetComponent<Rigidbody>();

        public void Push(Vector3 contactPoint, Vector3 pushDirection, float force)
        {
            if (body == null || body.isKinematic) return;

            Vector3 v = body.linearVelocity;
            if (new Vector3(v.x, 0f, v.z).magnitude >= maxPushSpeed) return;

            Vector3 impulse = pushDirection.normalized * (force * pushMultiplier);

            if (pushAtContactPoint)
                body.AddForceAtPosition(impulse, contactPoint, ForceMode.Impulse);
            else
                body.AddForce(impulse, ForceMode.Impulse);
        }
    }
}
