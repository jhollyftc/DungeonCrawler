using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The single spawner for a surface impact: instantiate the surface's VFX and
    /// play its SFX at the hit point. Every hit source — melee (MeleeHitEffects),
    /// thrown props, future projectiles — routes through here, so "what an impact
    /// looks/sounds like" is defined once (in SurfaceLibrary) and never duplicated.
    /// </summary>
    public static class SurfaceImpact
    {
        /// <param name="lib">The shared surface table.</param>
        /// <param name="surface">What was struck (Surface.Of(collider)).</param>
        /// <param name="point">World contact point.</param>
        /// <param name="dir">Blow direction; the VFX faces back along it (out of the wound).</param>
        /// <param name="sfxSource">Optional source for PITCHED playback; null = a positioned one-shot at the point.</param>
        /// <param name="playSfx">False = spawn the VFX only (e.g. a thrown prop that already has its own ImpactAudio for sound).</param>
        public static void Spawn(SurfaceLibrary lib, SurfaceType surface, Vector3 point, Vector3 dir, AudioSource sfxSource = null, bool playSfx = true)
        {
            if (lib == null) return;
            var e = lib.For(surface);
            if (e == null) return;

            if (e.vfx != null)
            {
                Quaternion rot = dir.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(-dir) : e.vfx.transform.rotation;
                GameObject fx = Object.Instantiate(e.vfx, point, rot);
                if (e.vfxLifetime > 0f) Object.Destroy(fx, e.vfxLifetime);
            }

            if (playSfx && e.sfx != null && e.sfx.Length > 0)
            {
                AudioClip clip = e.sfx[e.sfx.Length == 1 ? 0 : Random.Range(0, e.sfx.Length)];
                if (clip == null) return;
                float pitch = Random.Range(e.pitchRange.x, e.pitchRange.y);
                if (sfxSource != null)
                {
                    sfxSource.pitch = pitch;
                    sfxSource.PlayOneShot(clip, e.volume);
                }
                else
                {
                    // Was AudioSource.PlayClipAtPoint, which spawns a hidden temporary source
                    // nothing can reach — so those hits bypassed the mixer entirely: no bus,
                    // no volume control, and no DUCKING, despite a surface impact being the
                    // very event the ducking design keys off. The pool is routable, and it
                    // gets pitch variation too, which PlayClipAtPoint could not do.
                    OneShotAudioPool.Play(clip, point, e.volume, pitch, lib.impactMixerGroup);
                }
            }
        }
    }
}
