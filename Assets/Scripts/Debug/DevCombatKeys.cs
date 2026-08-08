using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Dev-only combat pokes, for tuning without setting up a real fight.
    /// Put on the player. Remove (or disable) for real builds.
    ///   K — damage the nearest NPC (test stagger/knockback/death/topple)
    ///   L — damage YOURSELF (test the player Health pipeline)
    /// </summary>
    [DisallowMultipleComponent]
    public class DevCombatKeys : MonoBehaviour
    {
        public KeyCode damageNearestNpcKey = KeyCode.K;
        public KeyCode damageSelfKey = KeyCode.L;
        public float amount = 10f;
        public float searchRadius = 30f;

        void Update()
        {
            if (Input.GetKeyDown(damageNearestNpcKey)) DamageNearestNpc();
            if (Input.GetKeyDown(damageSelfKey)) DamageSelf();
        }

        void DamageNearestNpc()
        {
            Health best = null;
            float bestSq = searchRadius * searchRadius;
            foreach (var h in FindObjectsOfType<Health>())
            {
                if (h.IsDead || h.transform.root == transform.root) continue;
                float sq = (h.transform.position - transform.position).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = h; }
            }
            if (best == null) { Debug.Log("[Dev] No living NPC in range to damage."); return; }

            Vector3 dir = (best.transform.position - transform.position).normalized;
            best.TakeDamage(new DamageInfo
            {
                amount = amount, point = best.transform.position + Vector3.up,
                direction = dir, instigator = gameObject,
                type = DamageType.Environment, impulse = 4f,
            });
        }

        void DamageSelf()
        {
            var h = GetComponentInParent<Health>();
            if (h == null) { Debug.LogWarning("[Dev] Player has no Health component."); return; }
            h.TakeDamage(new DamageInfo
            {
                amount = amount, point = transform.position + Vector3.up,
                direction = transform.forward, instigator = null,
                type = DamageType.Environment, impulse = 0f,
            });
        }
    }
}
