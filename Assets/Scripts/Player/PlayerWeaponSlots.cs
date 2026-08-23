using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// What weapon is in the melee slot, and what happens when a new one is picked up.
    ///
    /// SEPARATE FROM <see cref="PlayerLoadout"/> ON PURPOSE. The loadout answers "which slot is
    /// active" and arbitrates input between the slot scripts; this answers "what is IN the melee
    /// slot". Folding them together would mean the component that decides whether the bow may
    /// read the mouse also spawns prefabs and throws swords on the floor.
    ///
    /// THE SOCKET IS JUST A PARENT — the hand transform is a perfectly good one, and nothing
    /// needs listing in PlayerLoadout.meleeViewmodels. An earlier design required the socket to
    /// BE the entry in that array so hiding the socket hid its contents; that only works if the
    /// authored weapon is ALSO re-parented under the socket, and when it was not, this component
    /// spawned a sword the loadout had never heard of — visible in every slot, so you held a
    /// torch and a sword at once. A rule that must be satisfied in two places to work will be
    /// half-satisfied, so the spawner REPORTS its viewmodel to the loadout instead
    /// (PlayerLoadout.SetMeleeViewmodel) and the loadout stays the single owner of visibility.
    ///
    /// PICKUP IS DELIBERATE — a WeaponPickup is an IInteractable you look at and press E on.
    /// Nothing is ever taken or dropped by walking.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerWeaponSlots : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("What weapon viewmodels are parented to. The sword HAND is fine — it does not need to be a dedicated empty, and it does NOT need listing in PlayerLoadout.meleeViewmodels; the spawner reports each new weapon to the loadout itself.\n\nOnly the FIRST child is treated as the weapon and replaced on pickup, so keep lights, VFX and the shield out of it.")]
        public Transform weaponSocket;
        [Tooltip("Left blank, found on this GameObject.")]
        public PlayerLoadout loadout;
        public PlayerMelee melee;
        public MeleeAttack meleeAttack;
        [Tooltip("Needed so a viewmodel spawned at runtime lands on the Viewmodel layer. Without it the new weapon renders through the BASE camera: it clips through walls and takes world post-processing, while looking otherwise correct.")]
        public ViewmodelCamera viewmodelCamera;

        [Header("Starting weapon")]
        [Tooltip("Equipped at spawn. Leave EMPTY to keep whatever sword is already authored under the socket — which is what you want until every weapon has a definition asset.")]
        public WeaponDefinition startingWeapon;

        [Header("Dropping")]
        [Tooltip("Spawn the replaced weapon at the WEAPON VIEWMODEL's live pose, so it leaves the hand where you were holding it instead of appearing out of the player's chest. Off falls back to a fixed spot in front of the player.\n\nStill corrected when the hand pose would land inside the capsule — see DropCurrent.")]
        public bool dropFromViewmodel = true;
        [Tooltip("Fallback distance in front of the player, used when Drop From Viewmodel is off or there is no viewmodel instance. Measured along the HORIZONTAL facing, and clamped up to clear the capsule radius.")]
        public float dropForwardDistance = 0.8f;
        [Tooltip("Fallback height above the player's feet, used with Drop Forward Distance.")]
        public float dropHeight = 1.0f;
        [Tooltip("Gentle toss so a dropped weapon settles beside you instead of landing exactly on your toes. Not a throw — that is PlayerCarry's job.")]
        public float dropTossSpeed = 1.5f;
        [Tooltip("MINIMUM seconds a freshly dropped weapon ignores the player's own capsule. Covers the spawn frame, when the two are closest and a resolved overlap would shove you.")]
        public float dropIgnoreTime = 0.4f;
        [Tooltip("CAP on that ignore. Past the minimum the ignore is held until the weapon is genuinely clear of the capsule, so a timer cannot hand the shove back while the two still overlap — but never indefinitely: a settled weapon should behave like any other prop rather than being uniquely walk-through.")]
        public float dropIgnoreMaxTime = 4f;
        [Tooltip("Depenetration clamp applied to the dropped body (m/s). The project default is 1; a dropped weapon is the one prop that reliably spawns near the player, so it gets its own low value rather than inheriting whatever the project setting happens to be.")]
        public float dropMaxDepenetration = 0.5f;

        [Header("Equip feel")]
        [Tooltip("Seconds with EMPTY HANDS after the old weapon is dropped, before the new one starts rising. The pause is what sells the swap as two actions — letting go, then drawing — instead of one weapon becoming another.")]
        public float equipDelay = 0.22f;
        [Tooltip("Seconds for the new weapon to rise into its rest pose. Eased out, so it settles rather than snapping.")]
        public float equipRaiseTime = 0.35f;
        [Tooltip("Where the weapon starts, in CAMERA space — straight down the screen by default. MUST sit below the frame: the weapon is genuinely present during the delay, just out of view, so an offset tuned too small parks it at the bottom of the screen instead of reading as empty hands.\n\nCamera space, and no rotation. Local to the viewmodel sends the sword, shield and bow three different ways, because their authored orientations disagree; world space ignores pitch, so looking up brings the weapon into your face and looking down buries it.")]
        public Vector3 equipLowerOffset = new Vector3(0f, -0.7f, 0f);

        [Tooltip("Log equips and drops.")]
        public bool debugWeapons = false;

        /// <summary>What is in the melee slot, or null if the slot holds an unauthored sword.</summary>
        public WeaponDefinition CurrentMelee { get; private set; }

        /// <summary>The weapon leaving your hands, fired the instant you let go.</summary>
        public event System.Action<WeaponDefinition> OnWeaponReleased;

        /// <summary>
        /// The weapon being drawn, fired when it STARTS RISING — after equipDelay, not at the
        /// input. The sound belongs to the motion you can see, the same reasoning that moved the
        /// throw grunt off the wind-up and onto the heave: played at the press it covers the
        /// pause and the lift itself lands silent.
        /// </summary>
        public event System.Action<WeaponDefinition> OnWeaponDrawn;

        GameObject currentViewmodel;
        ViewmodelHolster raise;
        Coroutine drawAnnounce;
        Camera cam;
        CharacterController cc;

        /// <summary>
        /// A held weapon's weight, asked for every frame it is in hand.
        ///
        /// HERE RATHER THAN IN `WeaponDefinition.ApplyTo`, and the distinction is the one that
        /// entry already draws: `ApplyTo` pushes what describes the BLADE — damage, reach, sweep
        /// geometry — into `MeleeAttack`. How fast the PLAYER walks is not a property of the
        /// weapon's swing, and pushing it through the attack component would put a movement
        /// concern inside the thing that decides what a hit does.
        ///
        /// CONTINUOUS, NOT ON EQUIP, because it is a request rather than a setting: the controller
        /// composes it with the backpedal penalty, a drawn bow and a charging heavy swing, and
        /// none of them need to know about each other. It also means dropping the weapon, dying or
        /// swapping mid-swing restores speed with nothing to remember to reset.
        ///
        /// ONLY WHILE THE MELEE SLOT IS ACTIVE. `CurrentMelee` stays set while the bow is out —
        /// it is what you return TO — so weighing the player down for a greatsword on their back
        /// while they hold a bow would be wrong, and would silently stack with the bow's own draw
        /// penalty.
        /// </summary>
        void Update()
        {
            if (CurrentMelee == null || CurrentMelee.moveSpeedMultiplier >= 1f) return;
            if (loadout != null && loadout.Current != PlayerLoadout.Slot.Melee) return;
            if (controller == null) controller = GetComponentInParent<FirstPersonController>();
            if (controller != null) controller.RequestMoveScale(CurrentMelee.moveSpeedMultiplier);
        }
        FirstPersonController controller;

        void Awake()
        {
            if (loadout == null) loadout = GetComponent<PlayerLoadout>();
            if (melee == null) melee = GetComponent<PlayerMelee>();
            if (meleeAttack == null) meleeAttack = GetComponent<MeleeAttack>();
            if (viewmodelCamera == null) viewmodelCamera = GetComponentInChildren<ViewmodelCamera>(true);
            cam = GetComponentInChildren<Camera>(true);
            cc = GetComponentInParent<CharacterController>();

            if (weaponSocket == null)
            {
                Debug.LogWarning("[PlayerWeaponSlots] No weaponSocket assigned — weapon pickups " +
                                 "will have nowhere to go and are refused.", this);
                return;
            }

            // ADOPT the authored weapon rather than planning to bulldoze the socket later.
            // Destroying every socket child on the next equip took out anything else parented
            // there — a torch, a light, an effect — which is how a Light the player torch was
            // driving got destroyed mid-run and threw a MissingReferenceException every frame
            // afterwards. Only ever destroy the ONE object identified as the current weapon.
            if (weaponSocket.childCount > 0)
                currentViewmodel = weaponSocket.GetChild(0).gameObject;
            if (weaponSocket.childCount > 1)
                Debug.LogWarning($"[PlayerWeaponSlots] weaponSocket '{weaponSocket.name}' has " +
                                 $"{weaponSocket.childCount} children. Only the FIRST is treated as " +
                                 "the weapon and replaced on pickup; the socket should hold nothing " +
                                 "else, so park lights and effects outside it.", this);

            // The raise holder is created AFTER the adoption above, or it would be child 0 and
            // get adopted as the weapon itself. Spawned weapons go inside it; an authored
            // starting weapon stays where it is and is never raised, which is right — you began
            // the run holding it.
            var holderGo = new GameObject("WeaponRaise");
            holderGo.transform.SetParent(weaponSocket, false);
            raise = holderGo.AddComponent<ViewmodelHolster>();
            raise.enabled = false;

            // An authored sword is left alone when no starting weapon is set. The current
            // player keeps working untouched: pickups still function, and the first one simply
            // replaces whatever was there — with nothing to drop, since a weapon with no
            // definition has no world form to spawn.
            if (startingWeapon != null) Equip(startingWeapon, dropCurrent: false);
        }

        public void Equip(WeaponDefinition next) => Equip(next, dropCurrent: true);

        void Equip(WeaponDefinition next, bool dropCurrent)
        {
            if (next == null || weaponSocket == null) return;

            if (dropCurrent) DropCurrent();

            // ONLY the object we know to be the weapon — never a sweep of the socket's
            // children. Awake adopts an authored weapon into this same reference, so the first
            // pickup replaces exactly that and nothing else parented nearby.
            if (currentViewmodel != null) Destroy(currentViewmodel);
            currentViewmodel = null;

            if (next.viewmodelPrefab != null)
            {
                // Into the RAISE HOLDER, not straight onto the socket. The holder is the
                // transform the lift animates; the weapon's own ViewmodelSway owns its local
                // pose underneath and the two compose instead of overwriting each other.
                Transform parent = raise != null ? raise.transform : weaponSocket;
                currentViewmodel = Instantiate(next.viewmodelPrefab, parent);
                currentViewmodel.transform.localPosition = Vector3.zero;
                currentViewmodel.transform.localRotation = Quaternion.identity;

                // Spawned after ViewmodelCamera.Awake, so the layer sweep has already run and
                // cannot have seen this. Skipping it fails silently and looks like clipping.
                if (viewmodelCamera != null) viewmodelCamera.AdoptViewmodel(currentViewmodel.transform);

                // PlayerMelee poses the blade through a DIRECT ViewmodelSway reference, so the
                // swap has to rebind it or every swing after the first pickup animates a sword
                // that no longer exists — silently, since a destroyed reference just stops
                // doing anything.
                var sway = currentViewmodel.GetComponentInChildren<ViewmodelSway>(true);
                if (melee != null && sway != null) melee.swordSway = sway;
                else if (melee != null)
                    Debug.LogWarning($"[PlayerWeaponSlots] '{next.Label}' viewmodel has no " +
                                     "ViewmodelSway — it will not sway, bob or animate a swing.", this);
            }
            else
            {
                Debug.LogWarning($"[PlayerWeaponSlots] '{next.Label}' has no viewmodelPrefab — " +
                                 "equipped for stats only, with nothing in hand.", this);
            }

            // TELL THE LOADOUT WHAT IT NOW OWNS. Without this the spawned weapon is visible in
            // every slot — the loadout hides the authored sword listed in meleeViewmodels and
            // has no idea a replacement exists, so you end up holding a torch and a sword at
            // once. Reported every equip, including the startup one, since Awake order between
            // these two components is undefined.
            if (loadout != null) loadout.SetMeleeViewmodel(currentViewmodel);

            // Raise it into view — but only on a real swap. The startup equip has nothing to
            // drop and no swap to sell, so lifting the weapon you spawned holding would just be
            // an animation nobody asked for on every load.
            if (raise != null)
            {
                if (dropCurrent) raise.Raise(equipDelay, equipRaiseTime, equipLowerOffset);
                else raise.Finish();
            }

            // Announce the draw when the lift STARTS, not now. A swap with no delay fires
            // immediately; otherwise the wait is restarted so a fast second pickup cannot leave
            // an orphaned announcement from the weapon it already replaced.
            if (dropCurrent)
            {
                if (drawAnnounce != null) StopCoroutine(drawAnnounce);
                drawAnnounce = equipDelay > 0f
                    ? StartCoroutine(AnnounceDraw(next, equipDelay))
                    : null;
                if (drawAnnounce == null) OnWeaponDrawn?.Invoke(next);
            }

            next.ApplyTo(meleeAttack);
            CurrentMelee = next;

            if (debugWeapons) Debug.Log($"[PlayerWeaponSlots] equipped {next.Label}.", this);
        }

        /// <summary>
        /// Put the currently slotted weapon back into the world.
        ///
        /// WHERE IT LANDS DEPENDS ON WHETHER IT WAS IN HAND, which is the rule the pickup
        /// design turns on: replacing the sword you are holding drops it from the hand, while
        /// replacing a sword that was stowed (because you are carrying the torch) drops it at
        /// your feet. You never lose what you are actually holding by picking something up.
        /// </summary>
        public void DropCurrent()
        {
            if (CurrentMelee == null) return;
            if (CurrentMelee.worldPrefab == null)
            {
                if (debugWeapons)
                    Debug.Log($"[PlayerWeaponSlots] {CurrentMelee.Label} has no worldPrefab — " +
                              "replaced without dropping anything.", this);
                CurrentMelee = null;
                return;
            }

            bool inHand = loadout == null || loadout.Current == PlayerLoadout.Slot.Melee;

            Vector3 flat = cam != null ? cam.transform.forward : transform.forward;
            flat.y = 0f;
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : transform.forward;

            float clearance = cc != null ? cc.radius + 0.35f : 0.6f;
            Vector3 feet = transform.position - Vector3.up * (cc != null ? cc.height * 0.5f - cc.center.y : 0f);

            // FROM THE HAND, so it reads as the player letting go rather than a sword being
            // born out of their chest. The viewmodel's transform is a real world pose — it
            // lives just in front of the camera — and it is the live, swayed, bobbing pose, so
            // the weapon leaves exactly where it visually was. It stays valid while the slot is
            // STOWED too: an inactive GameObject keeps its transform, so a sword replaced while
            // you hold the torch still drops from where the sword hangs.
            //
            // NB the overlay camera keeps its own FOV, so the viewmodel's on-screen position is
            // not a perfect match for where that world point projects through the base camera.
            // Close enough to read correctly, and not worth unprojecting for.
            Vector3 pos;
            Quaternion rot;
            string posSource;
            if (dropFromViewmodel && currentViewmodel != null)
            {
                pos = currentViewmodel.transform.position;
                rot = currentViewmodel.transform.rotation;
                posSource = $"viewmodel '{currentViewmodel.name}'";
            }
            else
            {
                pos = feet + flat * Mathf.Max(dropForwardDistance, clearance) + Vector3.up * dropHeight;
                rot = Quaternion.LookRotation(flat, Vector3.up);

                // NAME WHICH of the two reasons fired. "It drops from my chest" has exactly two
                // causes and they need opposite fixes — a serialized bool that predates the
                // field and so came back false (tick it in the inspector), versus no viewmodel
                // instance to drop from at all (the weapon has no viewmodelPrefab, or Awake
                // adopted nothing because the socket was empty). Reporting "fell back" without
                // saying which is the diagnostic that sends you looking in the wrong place.
                posSource = !dropFromViewmodel
                    ? "FALLBACK — dropFromViewmodel is OFF. If you never unticked it, the field " +
                      "was added after this component was serialized and came back false: tick it"
                    : "FALLBACK — no currentViewmodel. Either the equipped weapon has no " +
                      "viewmodelPrefab, or nothing was adopted from weaponSocket at Awake";
            }

            // NUDGE IT OUT OF THE CAPSULE, MINIMALLY — pitch must still not put the weapon under
            // your feet ("press E and ride into the ceiling"), because CharacterController.Move
            // ALWAYS resolves whatever overlap it finds itself in regardless of requested motion
            // (the quirk behind NpcLocomotion.RejectUnwantedPush).
            //
            // BUT THIS GUARD IS WHAT MADE EVERY DROP LAND IN FRONT OF THE PLAYER. It used
            // radius + 0.35 (~0.7m) as the threshold and snapped to `flat` — and a held
            // viewmodel sits 0.4-0.6m ahead BY DESIGN, inside that radius, so it fired on every
            // single drop and overrode the hand pose one line after it was computed. The `if`
            // above was innocent.
            //
            // So: the floor is the capsule radius plus a skin, nothing more, and the push runs
            // along the offset's OWN direction so a right-hand pose stays to the right instead
            // of being snapped dead ahead. The real protection is the collision ignore below;
            // this is only a sanity floor.
            float minHoriz = (cc != null ? cc.radius : 0.3f) + 0.05f;
            Vector3 offset = pos - feet;
            offset.y = 0f;
            if (offset.magnitude < minHoriz)
            {
                Vector3 outDir = offset.sqrMagnitude > 1e-4f ? offset.normalized : flat;
                pos = feet + outDir * minHoriz + Vector3.up * (pos.y - feet.y);
            }
            if (pos.y < feet.y + 0.05f) pos.y = feet.y + 0.05f;

            var dropped = Instantiate(CurrentMelee.worldPrefab, pos, rot);

            // Make sure the thing we just spawned knows what it is. The prefab should already
            // carry a WeaponPickup pointing at this definition, but a prefab authored for one
            // weapon and reused for another would silently become the wrong sword on the floor.
            var pickup = dropped.GetComponent<WeaponPickup>();
            if (pickup == null) pickup = dropped.AddComponent<WeaponPickup>();
            pickup.weapon = CurrentMelee;

            var body = dropped.GetComponent<Rigidbody>();
            if (body != null)
            {
                if (dropTossSpeed > 0f)
                    body.linearVelocity = (flat + Vector3.up * 0.3f).normalized * dropTossSpeed;

                // Belt and braces against the same launch from the other side: even a clean
                // spawn can end up overlapping something, and an unclamped body ejects at up to
                // maxDepenetrationVelocity. The project default was dropped 10 -> 1 for exactly
                // this class of bug; a weapon spawned an arm's length from the player earns its
                // own lower value.
                if (dropMaxDepenetration > 0f) body.maxDepenetrationVelocity = dropMaxDepenetration;
            }

            // AND IGNORE THE PLAYER'S OWN CAPSULE WHILE IT CLEARS. The geometry above stops the
            // weapon spawning under you; this covers the case where you walk into it in the
            // same breath, or drop against a wall that bounces it back into your feet. Same
            // tool PlayerCarry uses to stop the carry force and the push force fighting.
            if (cc != null && dropIgnoreTime > 0f)
                StartCoroutine(IgnorePlayerBriefly(dropped));

            if (debugWeapons)
                Debug.Log($"[PlayerWeaponSlots] dropped {CurrentMelee.Label} " +
                          $"(it was {(inHand ? "wielded" : "stowed")}) at {pos} from {posSource}.", this);

            OnWeaponReleased?.Invoke(CurrentMelee);
            CurrentMelee = null;
        }

        System.Collections.IEnumerator AnnounceDraw(WeaponDefinition drawn, float wait)
        {
            yield return new WaitForSeconds(wait);
            drawAnnounce = null;
            OnWeaponDrawn?.Invoke(drawn);
        }

        /// <summary>
        /// Let a freshly dropped weapon pass through the player for a moment, then restore
        /// normal collision.
        ///
        /// TEMPORARY, NOT PERMANENT. A weapon on the floor should behave like any other prop
        /// once it has settled — you can kick it, it blocks nothing. Ignoring forever would
        /// make dropped weapons uniquely ghostly, and the ignore is only needed to survive the
        /// instant of the spawn.
        /// </summary>
        System.Collections.IEnumerator IgnorePlayerBriefly(GameObject dropped)
        {
            var cols = dropped.GetComponentsInChildren<Collider>(true);
            foreach (var col in cols)
                if (col != null) Physics.IgnoreCollision(col, cc, true);

            // HELD UNTIL IT IS ACTUALLY CLEAR, not for a fixed span. Dropping from the hand
            // spawns the weapon much closer to the capsule than the old fixed spot did, so a
            // timer that expires while the two still overlap simply hands the shove back. The
            // timer survives as a FLOOR (cover the spawn frame) and a CAP (never ignore
            // forever — a weapon wedged under a shelf must not stay walk-through, and a
            // settled weapon should behave like any other prop).
            float elapsed = 0f;
            while (elapsed < dropIgnoreMaxTime)
            {
                yield return null;
                elapsed += Time.deltaTime;
                if (dropped == null || cc == null) break;
                if (elapsed < dropIgnoreTime) continue;

                Vector3 feet = transform.position - Vector3.up * (cc.height * 0.5f - cc.center.y);
                Vector3 d = dropped.transform.position - feet;
                float horiz = new Vector2(d.x, d.z).magnitude;
                bool overlapping = horiz < cc.radius + 0.15f && d.y > -0.2f && d.y < cc.height + 0.2f;
                if (!overlapping) break;
            }

            // Both sides can be gone by now — the weapon picked back up and destroyed, or the
            // whole player torn down on a regenerate. Physics.IgnoreCollision throws on a
            // destroyed collider, and this coroutine outliving either is the normal case rather
            // than the exception.
            if (cc == null) yield break;
            foreach (var col in cols)
                if (col != null) Physics.IgnoreCollision(col, cc, false);
        }
    }
}
