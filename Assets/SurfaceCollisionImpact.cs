using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Spawns a surface-appropriate impact effect when this rigidbody hits
    /// something hard — a thrown barrel bursting a goblin (flesh), sparking off a
    /// wall (stone), or cracking another crate (wood). It reads what was STRUCK
    /// (Surface.Of the other collider) and routes through SurfaceImpact, so it
    /// reuses the same shared SurfaceLibrary as melee.
    ///
    /// Fires on ANY hard collision, not just armed throws — a prop slammed into a
    /// wall should spark whether it was thrown or shoved. It's the SURFACE's
    /// reaction, and it stands alongside the prop's own two collision responses
    /// without overlapping them: ThrownDamage (does it HURT — armed only) and
    /// ImpactAudio (the PROP's own material sound). Gated like ImpactAudio (speed
    /// floor + retrigger) so a bouncing, settling prop doesn't machine-gun bursts.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class SurfaceCollisionImpact : MonoBehaviour
    {
        [Tooltip("Shared surface table (the same asset melee uses).")]
        public SurfaceLibrary surfaceLibrary;
        [Tooltip("Impact speed (m/s) below which nothing spawns — stops a rolling/settling prop from puffing constantly. Match ImpactAudio's silence floor.")]
        public float minImpactSpeed = 3f;
        [Tooltip("Minimum seconds between spawns — a bouncing prop re-contacts many times; this keeps it to one burst per real impact.")]
        public float retriggerInterval = 0.1f;
        [Tooltip("ON: also play the SURFACE'S sound (stone scrape, flesh splat). Turn OFF if this prop's own ImpactAudio already covers the sound and you only want the surface VFX — avoids doubling.")]
        public bool playSurfaceSfx = true;
        [Tooltip("Optional source for pitched surface SFX; empty = a positioned one-shot at the point.")]
        public AudioSource sfxSource;

        [Tooltip("Log each spawned surface impact with the resolved surface + speed.")]
        public bool debugImpact = false;

        float nextTime;

        void OnCollisionEnter(Collision collision)
        {
            if (surfaceLibrary == null || Time.time < nextTime) return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minImpactSpeed) return;
            nextTime = Time.time + retriggerInterval;

            // The STRUCK surface — walk up from the other collider; untagged world = Stone.
            SurfaceType surface = Surface.Of(collision.collider, SurfaceType.Stone);

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
            // Direction INTO the surface (the prop's travel), so the burst faces
            // back out of it. relativeVelocity points roughly along the prop's path.
            Vector3 dir = collision.relativeVelocity.sqrMagnitude > 0.01f
                ? collision.relativeVelocity.normalized
                : (point - transform.position).normalized;

            SurfaceImpact.Spawn(surfaceLibrary, surface, point, dir, sfxSource, playSurfaceSfx);

            if (debugImpact)
                Debug.Log($"[SurfaceImpact] {name} hit '{collision.collider.name}' = {surface} at {speed:0.0} m/s.", this);
        }
    }
}
