using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The grate over a crawlway mouth: press E to wrench it off, once, permanently.
    ///
    /// IT ALWAYS FALLS INTO THE OPEN CELL, whichever side you break it from. Standing in the
    /// room it drops toward you; standing in the bore it is shoved out ahead of you. Those read
    /// as opposite actions and are the same motion, which is worth stating because the obvious
    /// implementation — push it away from the player — is WRONG: from inside the pipe that
    /// drives the grate into the room correctly, but from the room it drives a heavy iron
    /// grating INTO the 1.5m passage you are about to crawl down, where it fits badly, blocks
    /// the way and cannot be pushed aside because you have no room to work. The bore is the one
    /// place it must never end up, so direction is a property of the MOUTH, not of the player.
    ///
    /// TIER: the mouth is placed as <see cref="PropTier.FullGameObject"/> because of this
    /// component. An instanced tier bakes the mesh into a static matrix and
    /// InstancedDungeonRenderer has NO REMOVAL PATH (§8), so a detached grate's mesh would stay
    /// welded across the opening while its collider fell away — the same rule carryables and
    /// destructibles follow. Mouths number in single digits per run, so the batching loss is
    /// nothing.
    ///
    /// THE FRAME IS NOT THE GRATE. The mouth prefab also carries the ring of collision that
    /// replaces the suppressed 3m wall quad (§5) — destroy that and you open a hole in the world
    /// either side of the opening. Only the child assigned to <see cref="grate"/> ever detaches.
    /// </summary>
    public class CrawlwayGrate : MonoBehaviour, IInteractable
    {
        [Header("Parts")]
        [Tooltip("The moving part ONLY — the bars that fall away. NOT the frame: the frame carries the collision that stands in for the 3m wall quad this mouth suppressed, and detaching it opens a hole in the rock either side of the opening.\n\nLeave empty to use this GameObject, which is right when the component sits on the grate child itself.")]
        public Transform grate;

        [Tooltip("Collider that blocks the passage while the grate is INTACT — typically a concave MeshCollider following the bars, so arrows fly through the gaps. Disabled when it breaks.")]
        public Collider blockingCollider;

        [Tooltip("Collider used once the grate is LOOSE. Author a disabled BoxCollider roughly the size of the bars and put it here.\n\nWHY IT MUST BE A DIFFERENT COLLIDER: PhysX rejects a concave MeshCollider on a non-kinematic Rigidbody, so the shape that lets arrows through the bars cannot be the shape that falls. Leaving it null auto-fits a box from the renderer bounds, which is fine — but authoring one is better, because you can inset it so the grate lies flat rather than balancing on a bounding box that includes the frame lugs.\n\nDELIBERATELY NOT 'flip the MeshCollider to convex at runtime', which is the obvious fix and is a BUILD-ONLY TRAP: cooking a hull needs mesh DATA, so a grate mesh without Read/Write Enabled cooks fine in the editor and fails in a player build. DestructibleProp carries the same warning after learning it the hard way. A primitive needs no cooking at all.")]
        public Collider brokenCollider;

        [Header("Grip and strain")]
        [Tooltip("Hold the interact key and WORK the grate loose instead of popping it off with one press. Off = a single press breaks it (the original behaviour).")]
        public bool requireStrain = true;

        [Tooltip("Metres of accumulated hauling needed to break it free. This is DISTANCE TRAVELLED along the mouth's axis, not time — so pulling back counts, shoving forward counts, and WIGGLING counts fastest because you are always moving. That is what makes the motion the player invents match the motion the mechanic rewards, with nothing to explain.\n\nKeep it short. Resistance is characterful once and a chore the fifth time; ~2m is about a second and a half of real hauling.")]
        public float strainToBreak = 2f;

        [Tooltip("Strain bled off per second WHILE THE PLAYER IS NOT PUSHING OR PULLING, so letting go costs you and a nudge-and-wait does nothing.\n\nIt is deliberately NOT applied while they are working. As a constant it was silently a SPEED FLOOR — anything above the slowest speed the player can apply makes the mechanic impossible, and from inside a bore they are crouched by necessity at 1 m/s. A decay of 1.2 outran that at every framerate, so pushing perfectly on-axis still made no progress, with nothing in either system hinting the two numbers were related.")]
        public float strainDecayPerSecond = 1.2f;

        [Tooltip("Let go if the player gets this far from the grate. Must comfortably EXCEED PlayerInteractor.range, because Interact() hands over the interactor's own transform — which may sit at the player's feet while the cast that reached the grate came from the camera. Size it under that and the grip ends on the frame it begins, which looks exactly like the interaction doing nothing.")]
        public float gripBreakDistance = 4f;

        [Tooltip("How far from the grate you can get before it starts hauling you back. Inside this you move freely; past it you are on the leash.")]
        public float tetherSlack = 0.6f;
        [Tooltip("How hard the leash pulls, per metre past the slack. Size it so a full walk BARELY makes headway — that is the whole feel: you are hanging off an iron grating, not strolling away from it. Too low and the grip is decorative; too high and you cannot pull at all and no strain accumulates.")]
        public float tetherPull = 9f;
        [Tooltip("Hard ceiling on the leash speed, so a player who somehow gets far out is reeled in firmly rather than catapulted.")]
        public float tetherMaxSpeed = 6f;

        [Tooltip("How much feedback a grip shows before you have hauled at all, 0-1.\n\nNOT COSMETIC. Strain only accumulates while you MOVE, so pressing the key and standing still is genuinely zero progress — and with every channel scaled by strain, zero progress rendered as zero feedback and gripping was indistinguishable from the interaction failing. This is the floor that says 'you have hold of it, now pull'.")]
        [Range(0f, 1f)] public float gripBaseline = 0.35f;

        [Tooltip("Prompt while gripped FROM THE ROOM, where the grate comes toward you.")]
        public string strainPrompt = "Pull it loose!";
        [Tooltip("Prompt while gripped FROM INSIDE THE BORE, where it goes away from you.\n\nA separate string because the required input is genuinely the opposite instruction: the world direction is the same on both sides (+outward), but told to 'pull' while crouched in a tunnel a player backs away from the grate, which is the one input that does nothing there.")]
        public string strainPromptInside = "Push it open!";

        [Tooltip("Prompt on an unopened FLOOR COVER.")]
        public string coverPrompt = "Grab the cover";
        [Tooltip("Prompt while heaving a FLOOR COVER. Says LOOK UP because that is literally the input: gripping drags your gaze down onto the cover and you win by hauling your head back against it.")]
        public string strainPromptCover = "Look up — heave it!";

        [Tooltip("Log grip state every frame while hauling, and say why a grip ended. 'Nothing happens when I interact' has several causes that all look identical — the grip never started, it ended on the frame it began, or it is working and you are simply not moving.")]
        public bool debugGrip = false;

        [Tooltip("How far the grate visibly shifts along its axis at full strain. Small — this is the whole feedback channel for 'it's giving', and without it the mechanic reads as a delay rather than as work.")]
        public float strainVisualShift = 0.06f;
        [Tooltip("Rattle at full strain, in metres. Sells stone grinding on iron.\n\nOnly shakes while you are actually pushing or pulling, and scales with STRAIN rather than with the grip baseline — a grate rattling while you stand still holding the key claims work nobody is doing, and one nearly out of its seating should shake far more than one that has not shifted yet.")]
        public float strainRattle = 0.012f;
        [Tooltip("Camera lean toward the grate at full strain, in degrees. Sustained rather than a kick, so it holds while you haul and eases home the moment you let go.")]
        public float strainCameraLean = 2.5f;

        /// <summary>Being hauled on right now.</summary>
        public bool IsGripped { get; private set; }
        /// <summary>Progress toward breaking free, 0-1. The hook an audio layer follows.</summary>
        /// <summary>The threshold for THIS mode. Degrees of look for a cover, metres of haul for
        /// a wall grate — different quantities on different axes, so they cannot share a field.</summary>
        float BreakThreshold => mode == GrateMode.FloorCover ? coverStrainToBreak : strainToBreak;
        float DecayPerSecond => mode == GrateMode.FloorCover ? coverStrainDecay : strainDecayPerSecond;

        public float Strain01 => BreakThreshold > 0f ? Mathf.Clamp01(strain / BreakThreshold) : 0f;

        float strain;
        Transform gripper;
        FirstPersonController controller;
        PlayerCarry gripperCarry;
        CameraKick gripperKick;
        ViewmodelCamera gripperViewmodel;
        KeyCode gripKey = KeyCode.E;
        float lastAxial;   // displacement fallback only, when no controller is found
        bool gripFromOutside;
        Vector3 gripAnchor;
        // FloorCover only: the smoothed direction the player has been dragging, which decides
        // where the cover ends up. Smoothed so a moment of strafing does not throw it sideways.
        Vector3 dragDir;
        Vector3 restLocalPos;
        bool restCaptured;

        [Header("Break")]
        [Tooltip("Mass of the freed grate. Iron bars: heavy enough to land with a thud and not skitter.")]
        public float mass = 30f;
        [Tooltip("Shove along the mouth's outward direction (into the open cell) as it comes free, in m/s.")]
        public float outwardSpeed = 1.6f;
        [Tooltip("Extra tumble, in m/s, so it lands on a corner rather than sliding away flat.")]
        public float tumble = 2.2f;
        [Tooltip("Seconds before the freed grate is allowed to sleep. Purely so it settles rather than jittering against the frame it just left.")]
        public float settleDelay = 4f;

        [Header("Once loose")]
        [Tooltip("Make the freed grate a Carryable — pick it up, move it, throw it.\n\nTHE POINT IS THAT WHERE IT LANDS STOPS MATTERING. A 30kg grating tumbling into a tight space sometimes came to rest across the very passage it was covering, and no amount of tuning the shove reliably prevents that: it is a physics roll on every break. Being able to pick it up turns an unrecoverable bad outcome into a two-second inconvenience.\n\nEverything else comes free because a carryable IS just a heavy prop — mass-driven encumbrance, the deepened head bob, the throw heave, and ImpactAudio when it lands.")]
        public bool makeCarryable = true;

        [Tooltip("Name in the interaction prompt once it is loose.")]
        public string carryDisplayName = "Grate";
        [Tooltip("Metres in front of the eye it floats while carried. A grate is WIDE, so it wants more room than a barrel or it clips the near plane — which is exactly why Carryable keeps this per-object.")]
        public float carryHoldDistance = 1.6f;
        [Tooltip("Throw speed (m/s). Lower than a barrel's: this is an awkward slab of iron, not something you hurl.")]
        public float carryThrowSpeed = 6.5f;
        [Tooltip("Spin (rad/s) on release. A grate tumbling flat reads as thrown rather than slid.")]
        public float carryThrowSpin = 3.5f;

        [Header("Noise")]
        [Tooltip("How loud breaking it is to NPCs, 0-1. Wrenching an iron grate out of stone is one of the loudest things the player can deliberately do, and it SHOULD carry — a secret route you open by making a racket is a real trade, not a free one.")]
        [Range(0f, 1f)] public float breakLoudness = 0.85f;

        [Tooltip("Prompt shown by PlayerInteractor.")]
        public string prompt = "Wrench off the grate";

        /// <summary>Which kind of opening this covers. See <see cref="mode"/>.</summary>
        public enum GrateMode
        {
            /// <summary>A grate in a wall. Comes free toward the room it faces.</summary>
            WallGrate,
            /// <summary>A manhole cover in a floor. Heaved ASIDE across the floor it sits in.</summary>
            FloorCover,
        }

        [Header("Kind")]
        [Tooltip("WALL GRATE comes off toward the room it faces. FLOOR COVER is heaved aside across the floor.\n\nONE COMPONENT RATHER THAN TWO because everything that makes this feel like anything — the grip, the strain, the leash, the audio, the noise event, becoming carryable — is identical. Only three things differ: which way effort is measured, where the piece ends up, and the fact that a cover must never end up back over its own hole.")]
        public GrateMode mode = GrateMode.WallGrate;

        [Tooltip("Pitch the camera is dragged toward while heaving a floor cover, in degrees down from level. This is what you are FIGHTING — grip the cover and the game pulls your gaze down onto it, and you get it open by hauling your head back up.\n\nWHY LOOK RATHER THAN WALK: IntendedVelocity is horizontal BY CONSTRUCTION (the controller zeroes Y), so there is no vertical movement input to measure at all — the strain has to come from an axis the player actually has. Look pitch is that axis, and heaving upward against a weight that keeps pulling your head down is a far better read of lifting something heavy than shuffling sideways was.")]
        public float coverLookPitch = 62f;
        [Tooltip("How hard the camera is dragged back down, degrees per second. Size it so a steady upward pull BARELY wins — too low and the tether is decorative, too high and no amount of looking up makes progress.")]
        public float coverTetherPull = 95f;
        [Tooltip("Degrees of accumulated UPWARD look needed to pop a floor cover free.\n\nDEGREES, not the metres strainToBreak uses for a wall grate — the two mechanics measure different quantities on different axes, so they cannot share a number however similar they look.")]
        public float coverStrainToBreak = 260f;
        [Tooltip("Degrees of strain bled off per second while the player is NOT pulling up. Same rule as the wall grate's decay: it punishes letting go, and is not applied while they are working.")]
        public float coverStrainDecay = 150f;
        [Tooltip("How fast a freed cover pops upward, m/s. The lift is the payoff for the fight — it should visibly jump rather than slide.")]
        public float coverPopSpeed = 3.2f;

        [Tooltip("How far a floor cover slides off the hole when it comes free, in metres.\n\nIT IS MOVED RATHER THAN THROWN, and that is the one invariant a cover has: the shaft is directly beneath it, so a cover that tumbles freely will sometimes drop straight down the hole it was covering — blocking the passage and looking like a bug. Placing it clear of the opening first and only then handing it to physics makes that unrepresentable. Same reasoning as a wall grate never falling into the bore, one axis over.")]
        public float coverSlideDistance = 1.35f;

        /// <summary>Set by DungeonKitPlacer: world direction from the mouth into the OPEN cell.</summary>
        public Vector3 OutwardDirection { get; set; } = Vector3.forward;

        public bool IsOpen { get; private set; }

        // The Carryable this becomes once loose. Cached so the forwarding below costs nothing.
        Carryable loose;

        /// <summary>
        /// ONE INTERACTABLE PER OBJECT, WHICH IS WHY THIS FORWARDS. Once broken, the grate
        /// carries both this component and a `Carryable`, and both implement `IInteractable` —
        /// but `PlayerInteractor` resolves with `GetComponentInParent<IInteractable>()`, which
        /// returns the FIRST match. This one is authored on the prefab and the Carryable is
        /// added at runtime, so this would win forever and the freed grate could never be picked
        /// up: the prompt would simply go blank and E would do nothing, with both components
        /// perfectly configured.
        ///
        /// Delegating rather than destroying this component keeps `IsOpen` readable, which
        /// DungeonNavBaker needs to tell an intact grate (exclude from the bake) from a loose one
        /// lying on the floor (bake it, like any other prop).
        /// </summary>
        public string Prompt => IsOpen ? loose?.Prompt
                              : IsGripped ? (mode == GrateMode.FloorCover ? strainPromptCover
                                                                          : gripFromOutside ? strainPrompt : strainPromptInside)
                              : (mode == GrateMode.FloorCover ? coverPrompt : prompt);

        public void Interact(Transform interactor)
        {
            if (IsOpen) { loose?.Interact(interactor); return; }
            if (!requireStrain) { Break(HandsFreeSideOf(interactor)); return; }
            BeginGrip(interactor);
        }

        void BeginGrip(Transform interactor)
        {
            if (IsGripped || interactor == null) return;

            // MEASURE THE BODY, NOT WHATEVER TRANSFORM WAS HANDED OVER. PlayerInteractor calls
            // Interact(transform) with ITS OWN transform, and there is no rule about which
            // GameObject in the player rig that component sits on — if it is not one that moves
            // with the capsule, every position read here is a constant and strain can never
            // accumulate no matter which way the player walks. Resolving the controller makes
            // this independent of that authoring choice, the same reason PlayerFov.Ensure
            // resolves its owner rather than trusting a serialized reference.
            var controller = interactor.GetComponentInParent<FirstPersonController>();
            if (controller == null) controller = interactor.GetComponentInChildren<FirstPersonController>();
            if (controller == null && interactor.root != null)
                controller = interactor.root.GetComponentInChildren<FirstPersonController>();

            this.controller = controller;
            gripper = controller != null ? controller.transform : interactor;

            // Which side you took hold from is fixed AT GRIP TIME, not re-evaluated per frame.
            // The leash drags you around, so a live test would flip the moment it pulled you
            // through the plane and the mechanic would fight itself.
            gripFromOutside = HandsFreeSideOf(gripper);
            gripAnchor = (grate != null ? grate : transform).position;

            // Hands full — both of them are on the grate, same as carrying a prop. Resolved from
            // the interactor rather than cached, because the player rig is rebuilt on regenerate.
            gripperViewmodel = interactor.GetComponentInParent<ViewmodelCamera>();
            if (gripperViewmodel == null && interactor.root != null)
                gripperViewmodel = interactor.root.GetComponentInChildren<ViewmodelCamera>(true);
            if (gripperViewmodel != null) gripperViewmodel.SetViewmodelVisible(false);
            gripperCarry = interactor.GetComponentInParent<PlayerCarry>();
            if (gripperCarry == null && interactor.root != null)
                gripperCarry = interactor.root.GetComponentInChildren<PlayerCarry>();
            gripperKick = interactor.GetComponentInParent<CameraKick>();
            if (gripperKick == null && interactor.root != null)
                gripperKick = interactor.root.GetComponentInChildren<CameraKick>();

            // Which key to watch is the INTERACTOR'S, not a constant here — rebinding E must not
            // leave the one hold-to-use interaction in the game stuck on the old key.
            var pi = interactor.GetComponentInParent<PlayerInteractor>();
            if (pi != null) gripKey = pi.key;

            if (!restCaptured)
            {
                Transform part = grate != null ? grate : transform;
                restLocalPos = part.localPosition;
                restCaptured = true;
            }

            lastAxial = AxialOf(gripper.position);
            IsGripped = true;

            if (debugGrip)
                Debug.Log($"[Grate] grip STARTED on '{name}' — key={gripKey} " +
                          $"tracking '{gripper.name}' at {gripper.position.ToString("0.00")} " +
                          $"(interactor handed over '{interactor.name}'{(gripper == interactor ? "" : " — resolved to the controller instead")}) " +
                          $"dist={Vector3.Distance(gripper.position, (grate != null ? grate : transform).position):0.00} " +
                          $"(breaks at {gripBreakDistance}) outward={OutwardDirection.normalized.ToString("0.00")} " +
                          $"carry={(gripperCarry != null ? "found" : "MISSING")} " +
                          $"kick={(gripperKick != null ? "found" : "MISSING")}. " +
                          "Now MOVE — strain accumulates from travel along `outward`, not from holding still.", this);
        }

        void EndGrip(string why = null)
        {
            if (debugGrip && IsGripped && why != null)
                Debug.Log($"[Grate] grip ENDED on '{name}': {why} (strain {strain:0.00}/{strainToBreak:0.00})", this);

            // TWO OWNERS OF ONE PIECE OF STATE, so restoring blindly is wrong. A successful haul
            // runs Break() first, which may hand the grate straight to PlayerCarry — and
            // PlayerCarry stows the viewmodel for the same reason we did. Un-stowing here would
            // put the sword back in a hand that is holding a grate, and only on the success
            // path, which is the version you would ship without noticing.
            if (gripperViewmodel != null &&
                (gripperCarry == null || !gripperCarry.IsCarrying))
                gripperViewmodel.SetViewmodelVisible(true);
            gripperViewmodel = null;

            IsGripped = false;
            gripper = null;
            gripperCarry = null;
            gripperKick = null;

            if (restCaptured && !IsOpen)
            {
                Transform part = grate != null ? grate : transform;
                part.localPosition = restLocalPos;
            }
        }

        /// <summary>Distance along the mouth's outward axis — the one number the whole mechanic
        /// is built on.</summary>
        float AxialOf(Vector3 worldPos) => Vector3.Dot(worldPos, OutwardDirection.normalized);

        /// <summary>
        /// Is the player on the OPEN side (the room), rather than crouched in the bore?
        ///
        /// Decides whether they end up holding the grate. From the room they have leverage and
        /// somewhere to put it. From inside they are on their knees in a 1.5m tube with no room
        /// to work — and handing them a carried grate there is actively harmful, because
        /// PlayerCarry holds at 1.3m in front, the grate jams instantly, and breakDistance drops
        /// it INSIDE the bore, which is the one outcome this whole feature exists to prevent.
        /// </summary>
        bool HandsFreeSideOf(Transform interactor)
        {
            if (interactor == null) return false;
            return Vector3.Dot(interactor.position - transform.position,
                               OutwardDirection.normalized) > 0f;
        }

        void Update()
        {
            if (!IsGripped) return;

            Transform part = grate != null ? grate : transform;

            // Any of these means the grip is over: the player let go, walked off, or the rig
            // they were using went away (loadout swap, death, regenerate).
            if (gripper == null) { EndGrip("the gripper transform went away"); return; }
            if (!Input.GetKey(gripKey)) { EndGrip($"{gripKey} released"); return; }

            float dist = Vector3.Distance(gripper.position, part.position);
            if (dist > gripBreakDistance)
            {
                EndGrip($"too far ({dist:0.00} > gripBreakDistance {gripBreakDistance:0.00}) — " +
                        "NB Interact() hands over the interactor's transform, which may be at the " +
                        "player's feet while the cast came from the camera, so this wants to be " +
                        "comfortably larger than PlayerInteractor.range");
                return;
            }

            // INTENT, NOT ACHIEVED DISPLACEMENT — the same rule doors already taught (§10), and
            // this is the case that proves it generalises. You haul on a grate from arm's length
            // with your face at the wall, so the capsule is BLOCKED: measuring how far the body
            // actually travelled reads ~0 exactly when the player is doing the thing hardest.
            // Measured live it was 0.000 m/frame while walking straight into the wall.
            //
            // Intent has no such problem — pressing forward into stone registers as fully as
            // backing away does. So shoving counts, hauling counts, and wiggling counts fastest
            // because you are always demanding motion along the axis. Falls back to displacement
            // only if there is no controller to ask, which is better than nothing.
            Vector3 outwardDir = OutwardDirection.normalized;

            // THE REQUIRED DIRECTION IS +OUTWARD ON BOTH SIDES, which is the neat part. Outside,
            // hauling the grate toward you means backing away from the wall — +outward. Inside,
            // shoving it out means driving toward the room — also +outward. One signed test
            // covers both, and it reads as "pull" or "push" purely from where you are standing.
            //
            // SIGNED, NOT ABS. Taking the magnitude let shoving INTO the wall from outside count
            // as hauling, which is backwards — and was why the only thing that worked was
            // pressing yourself flat against the grate and holding W.
            float axialEffort;
            if (controller != null)
            {
                Vector3 intent = controller.IntendedVelocity;
                if (mode == GrateMode.FloorCover)
                {
                    // A TUG OF WAR WITH THE CAMERA. Gripping drags your gaze down onto the
                    // cover; you get it open by hauling your head back up against that pull.
                    // The strain is the UPWARD look input, in degrees — measured as intent, so
                    // a player straining against a tether that is winning still makes progress.
                    controller.SetPitchTether(coverLookPitch, coverTetherPull);
                    axialEffort = Mathf.Max(0f, -controller.LookPitchDelta);

                    // Which way it ends up is now the way the player is FACING, since they are
                    // no longer dragging it anywhere. Flattened, because they are looking down.
                    Vector3 facing = gripper.forward;
                    facing.y = 0f;
                    if (facing.sqrMagnitude > 0.0001f) dragDir = facing.normalized;
                }
                else
                {
                    axialEffort = Mathf.Max(0f, Vector3.Dot(intent, outwardDir)) * Time.deltaTime;
                }
            }
            else
            {
                float axialNow = AxialOf(gripper.position);
                axialEffort = Mathf.Max(0f, axialNow - lastAxial);
                lastAxial = axialNow;
            }
            strain += axialEffort;

            // DECAY ONLY WHILE THE PLAYER IS NOT WORKING. It exists to punish letting go, not to
            // set a speed floor — and as a constant it silently WAS a speed floor, which made
            // the mechanic impossible from inside the bore. There you are crouched by necessity,
            // so the fastest you can push is crouchSpeed (1 m/s), and a decay of 1.2/s outran
            // that at every framerate: effort 0.0033 against decay 0.0039, forever, while the
            // player pushed perfectly on-axis at dot 0.99.
            //
            // Coupling a feel constant to another system's speed constant like that is invisible
            // from either end. Gating on effort removes the coupling entirely: any genuine push
            // counts, and stopping still costs you.
            if (axialEffort <= 0f)
                strain = Mathf.Max(0f, strain - DecayPerSecond * Time.deltaTime);

            // THE LEASH. Holding the key means holding ON: past the slack you are hauled back
            // toward the grate, so walking away is a struggle rather than a stroll and letting
            // go of the direction drags you in again. That is what makes the grip feel like
            // gripping, and it is also what stops the old failure where backing off simply ended
            // the grip at gripBreakDistance before the strain ever completed.
            //
            // Radial rather than axial, so sidling along the wall is leashed too — "held within
            // distance of the grate" is a distance, not an axis.
            //
            // Only from OUTSIDE: from inside the bore you are shoving against the grate itself
            // and the geometry already resists you. A leash there would fight the tube.
            // WALL GRATES ONLY. A cover is tethered by the CAMERA, not by the body — you are
            // standing over it heaving upward, not hanging off it and leaning away, so dragging
            // the player back to a spot fights their footing for no reason and makes lining up
            // the shot at the drain worse. The two mechanics tether different things because
            // they are different actions; only the wall grate is a tug-of-war you can walk out
            // of. The gripBreakDistance check above still applies to both — walk far enough and
            // you have simply let go.
            if (mode == GrateMode.WallGrate && gripFromOutside && controller != null)
            {
                Vector3 toAnchor = gripAnchor - gripper.position;
                float over = toAnchor.magnitude - tetherSlack;
                if (over > 0f)
                    controller.SetSustainedVelocity(
                        toAnchor.normalized * Mathf.Min(over * tetherPull, tetherMaxSpeed));
            }

            float t = Strain01;

            // FEEDBACK RUNS FROM A FLOOR, NOT FROM ZERO. Strain only accumulates while the
            // player MOVES, so gripping and standing still is honestly no progress — and
            // scaling every channel by strain meant that state rendered as no feedback at all,
            // making a working grip look identical to an interaction that failed. The baseline
            // is what says "you have hold of it"; the rise above it is what says "keep going".
            float feel = Mathf.Lerp(gripBaseline, 1f, t);

            // THE SHIFT AND THE RATTLE SAY DIFFERENT THINGS, so only one of them takes the
            // baseline. The static shift means "you have hold of it" and should be there the
            // moment you grip — that is what the baseline exists for. The rattle means "it is
            // MOVING", and a grate that shakes while you stand still holding a key is claiming
            // work nobody is doing.
            //
            // So the rattle is gated on actual effort and scaled by STRAIN rather than by the
            // baselined `feel`: it starts almost still and builds as the thing works loose,
            // which is also the honest direction — a grate nearly out of its seating rattles far
            // more than one that has not shifted yet.
            // A COVER STRAINS UPWARD, matching the input: you are heaving your gaze up against a
            // weight, so the weight should visibly rise and rock in its seating as you win. A
            // wall grate still comes toward the room it faces. (`dragDir` on a cover is the
            // player's facing and only decides where it lands — it is not the strain axis.)
            Vector3 shiftAxis = mode == GrateMode.FloorCover ? Vector3.up : outwardDir;
            Vector3 shift = shiftAxis * (feel * strainVisualShift);
            if (strainRattle > 0f && axialEffort > 0f)
                shift += Random.insideUnitSphere * (strainRattle * t);
            // localPosition is in PARENT space, so the world-space shift has to be converted
            // there — not into the part's own space, which is already rotated by the mouth.
            Vector3 localShift = part.parent != null
                ? part.parent.InverseTransformVector(shift) : shift;
            part.localPosition = restLocalPos + localShift;

            // Frame-stamped, so letting go eases the camera home with no explicit clear — the
            // contract that stops a lean surviving a death or a regenerate (§10).
            if (gripperKick != null && strainCameraLean > 0f)
                gripperKick.SetSustained(new Vector3(-strainCameraLean * feel, 0f, 0f));

            if (debugGrip)
            {
                Vector3 intent = controller != null ? controller.IntendedVelocity : Vector3.zero;
                float decay = strainDecayPerSecond * Time.deltaTime;

                // EFFORT AGAINST DECAY is the pair that matters, because a player pressing a
                // direction that barely projects onto the axis loses ground and the raw numbers
                // would not say so.
                float signed = controller != null ? Vector3.Dot(intent, outwardDir) : 0f;
                string verdict =
                    controller == null
                        ? "NO FirstPersonController FOUND — falling back to displacement, which reads ~0 while you are against the wall"
                    : intent.sqrMagnitude < 1e-6f
                        ? "no movement input"
                    : signed < 0f
                        ? $"pushing the WRONG WAY (dot {signed:0.00}) — the required direction is +outward on BOTH sides: from outside back AWAY from the wall, from inside drive TOWARD the room"
                    : $"hauling ({(gripFromOutside ? "walk" : "crouch")} speed {signed:0.00} m/s — " +
                      $"{Mathf.Max(0f, (strainToBreak - strain) / Mathf.Max(0.01f, signed)):0.0}s to go).";

                Debug.Log($"[Grate] gripping '{name}' from {(gripFromOutside ? "OUTSIDE (pull back)" : "INSIDE (push out)")}: " +
                          $"strain {strain:0.00}/{strainToBreak:0.00} ({t * 100f:0}%) " +
                          $"effort={axialEffort:0.0000} decay={decay:0.0000} dot={signed:0.00} " +
                          $"dist={dist:0.00} leash={Mathf.Max(0f, dist - tetherSlack):0.00}m past slack. {verdict}", this);
            }

            if (strain >= BreakThreshold)
            {
                // BREAK BEFORE ENDING THE GRIP, because Break() needs gripperCarry to hand the
                // grate over and EndGrip clears it. Safe in this order: EndGrip only restores
                // the rest pose while `!IsOpen`, which Break has just made false.
                // Fixed at grip time, not re-tested: the leash physically moves the player, so a
                // live check could flip the moment it dragged them through the mouth plane.
                Break(gripFromOutside);
                EndGrip();
            }
        }

        /// <summary>
        /// Free the grate. Public so a future damage path (or an NPC that wants through) can
        /// call it without going via the interaction system.
        /// </summary>
        public void Break() => Break(false);

        /// <param name="handToPlayer">Put it straight into the player's hands rather than
        /// letting it drop. Only ever true from the room side — see HandsFreeSideOf.</param>
        public void Break(bool handToPlayer)
        {
            if (IsOpen) return;
            IsOpen = true;

            Transform part = grate != null ? grate : transform;

            // Undo any strain offset first, so the body starts from where the grate actually
            // sits rather than from a pose that only existed while it was being hauled on.
            if (restCaptured) part.localPosition = restLocalPos;

            // DELIBERATELY NOT REPARENTED. The instinct is to detach the grate from its frame,
            // and it is both unnecessary and harmful here. Unnecessary because a Rigidbody moves
            // its transform in WORLD space and the mouth frame never moves or scales, so the
            // parent constrains nothing — a carried grate can be walked across the dungeon while
            // still nominally parented to the wall it came from.
            //
            // Harmful because the mouth instance is a direct child of the DungeonCrawlways root,
            // so "detach to my parent's parent" walks straight PAST that root and out of
            // ClearGenerated's reach — leaking one grate per regenerate, which is §5's
            // GeneratedRoots rule arriving from the opposite direction.

            // COLLIDER SWAP BEFORE THE RIGIDBODY, and the order is not cosmetic: PhysX rejects a
            // concave MeshCollider on a non-kinematic body, so adding the Rigidbody while the
            // mesh collider is still live logs an error and leaves the grate colliding with
            // nothing — it falls through the floor. Disabling the blocker also clears the
            // passage before the body wakes, so the grate cannot wedge in the opening it just
            // left.
            SwapToDynamicCollider(part);

            // A COVER IS NOT TELEPORTED ASIDE — it POPS. An earlier version placed it clear
            // before waking physics, which made "never falls down its own shaft" airtight but
            // also meant the piece jumped a metre sideways at the exact moment the player was
            // meant to see it burst free. The fight earns the pop, so the pop is what plays; the
            // rare bad bounce is caught afterwards by RescueFromShaft once it has settled.
            if (mode == GrateMode.FloorCover) openingY = part.position.y;

            var body = part.GetComponent<Rigidbody>();
            if (body == null) body = part.gameObject.AddComponent<Rigidbody>();
            body.mass = mass;
            body.isKinematic = false;
            body.useGravity = true;

            // Straight out into the open cell, plus a tumble. Velocity rather than a force so
            // the result does not depend on mass — a heavier grate should land harder, not
            // travel less far.
            //
            // A COVER GETS A FRACTION OF THAT, because it has already been PLACED where it
            // belongs. Its impulse only needs to settle it against the floor and let it rock;
            // a full shove would send a heavy disc skating across the cell and, worse, give it
            // a real chance of sliding back over the opening.
            Vector3 outward = mode == GrateMode.FloorCover
                ? (dragDir.sqrMagnitude > 0.0001f ? dragDir.normalized : SlideFallback(part))
                : (OutwardDirection.sqrMagnitude > 0.001f ? OutwardDirection.normalized : part.forward);
            float launch = mode == GrateMode.FloorCover ? outwardSpeed * 0.25f : outwardSpeed;
            body.linearVelocity = outward * launch;
            // THE POP IS THE PAYOFF. You fought a weight that kept dragging your head down, so
            // the cover jumping clear is the release that fight earned — sliding quietly aside
            // reads as nothing having happened.
            if (mode == GrateMode.FloorCover) body.linearVelocity += Vector3.up * coverPopSpeed;
            body.angularVelocity = Vector3.Cross(Vector3.up, outward) * tumble;
            body.sleepThreshold = 0f;
            CancelInvoke(nameof(AllowSleep));
            Invoke(nameof(AllowSleep), settleDelay);

            if (makeCarryable) MakeCarryable(part);

            // STRAIGHT INTO THE PLAYER'S HANDS, which is the point of the strain mechanic: you
            // hauled it out, so you are holding it, and where it lands is never a physics roll
            // you have to live with. Only from the room side — from inside the bore there is
            // nowhere to put it and PlayerCarry would jam it against the tube and drop it back
            // in the passage.
            // A COVER IS NEVER HANDED OVER, however it was opened. Its hole is right there, and
            // a carried prop that gets dropped or knocked loose lands wherever the carry rig
            // last had it — which for a cover means a fair chance of straight back down the
            // shaft. It stays carryable, so it can still be picked up deliberately once it is
            // lying safely on the floor; it just is not put in your hands over an open drain.
            if (handToPlayer && mode != GrateMode.FloorCover &&
                makeCarryable && loose != null && gripperCarry != null)
                gripperCarry.TryPickUp(loose);

            // A grate coming out of stone is loud, and this is the ONE place the crawlway system
            // touches the AI: opening a secret route announces you. Emitted through NoiseBus so
            // nothing here has to know NPCs exist (§10's mutually-ignorant emitters).
            NoiseBus.Emit(part.position, breakLoudness, part, Faction.Neutral);

            // ImpactAudio on the grate then handles the landing clang for free — it is speed
            // driven, so a grate that drops a foot ticks and one that is shoved clatters.
        }

        /// <summary>
        /// Turn the freed grate into an ordinary carryable prop.
        ///
        /// ADDED AT RUNTIME RATHER THAN AUTHORED ON THE PREFAB, and the reason is the intact
        /// state. `Carryable` requires a Rigidbody, so authoring it would put a Rigidbody on a
        /// grate that is still part of the wall — which either falls out of the masonry on the
        /// first frame, or has to be kinematic and then hands PhysX a concave MeshCollider it
        /// will reject the moment we wake it. Worse, `IInteractable` would be live, so the
        /// player could pick the grate up WITHOUT ever breaking it.
        ///
        /// The cost of adding it here is that its tuning cannot be authored on the Carryable
        /// itself, which is why the four fields are mirrored onto this component instead. That
        /// keeps all the grate's authoring in one inspector rather than split across a component
        /// that does not exist yet.
        /// </summary>
        void MakeCarryable(Transform part)
        {
            loose = part.GetComponent<Carryable>();
            if (loose != null) return;

            loose = part.gameObject.AddComponent<Carryable>();
            loose.displayName = carryDisplayName;
            loose.holdDistance = carryHoldDistance;
            loose.throwSpeed = carryThrowSpeed;
            loose.throwSpin = carryThrowSpin;
        }

        /// <summary>
        /// Retire the intact shape and bring up one a dynamic body may legally use.
        ///
        /// The two shapes answer different questions and cannot be the same collider. INTACT,
        /// the grate wants to follow the bars exactly, so a shot arrow passes between them —
        /// that means concave, which is only legal on static geometry. LOOSE, it needs a shape
        /// PhysX will simulate, which means convex or primitive. Once it is lying on the floor
        /// nobody is shooting through it, so a box loses nothing.
        /// </summary>
        void SwapToDynamicCollider(Transform part)
        {
            if (blockingCollider != null) blockingCollider.enabled = false;

            if (brokenCollider != null)
            {
                brokenCollider.enabled = true;
                return;
            }

            // Auto-fit from the RENDERER's local bounds. Safe in a build, unlike cooking a
            // convex hull: bounds are metadata and readable on any mesh, while vertex data is
            // what Read/Write Enabled gates.
            var box = part.gameObject.AddComponent<BoxCollider>();
            var renderer = part.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                // World bounds mapped back into the part's own space, so a rotated mouth still
                // fits its grate rather than getting an axis-aligned slab.
                Bounds b = renderer.bounds;
                box.center = part.InverseTransformPoint(b.center);
                Vector3 lossy = part.lossyScale;
                box.size = new Vector3(
                    b.size.x / Mathf.Max(0.0001f, Mathf.Abs(lossy.x)),
                    b.size.y / Mathf.Max(0.0001f, Mathf.Abs(lossy.y)),
                    b.size.z / Mathf.Max(0.0001f, Mathf.Abs(lossy.z)));
            }

            if (warnedAutoBox) return;
            warnedAutoBox = true;
            Debug.LogWarning(
                $"[Grate] '{name}' has no brokenCollider, so a BoxCollider was fitted from the renderer " +
                "bounds at runtime. That works, but author a disabled BoxCollider on the bars and assign " +
                "it instead: a bounds-fitted box includes the frame lugs, so the grate tends to land " +
                "balanced on a corner rather than lying flat.", this);
        }

        static bool warnedAutoBox;

        /// <summary>
        /// Where a floor cover goes when the player never actually dragged — a break from damage,
        /// from an NPC, or from a grip that reached the threshold on jitter alone.
        ///
        /// Casts the four compass directions and takes the first with room, rather than using an
        /// authored default: a manhole sits in a prison CELL, so at least two of its sides are
        /// wall, and a fixed direction would bury the cover in masonry half the time.
        /// </summary>
        Vector3 SlideFallback(Transform part)
        {
            Vector3 origin = part.position + Vector3.up * 0.2f;
            Vector3[] dirs = { part.forward, part.right, -part.forward, -part.right };
            foreach (var raw in dirs)
            {
                Vector3 d = new Vector3(raw.x, 0f, raw.z);
                if (d.sqrMagnitude < 0.0001f) continue;
                d.Normalize();
                if (!Physics.Raycast(origin, d, coverSlideDistance + 0.5f,
                                     ~0, QueryTriggerInteraction.Ignore))
                    return d;
            }
            return Vector3.forward;   // boxed in on all four sides; physics will settle it
        }

        /// <summary>
        /// A grip that ends because this component went away must still put the weapon back.
        /// The dungeon regenerating, the player dying, or the grate being destroyed mid-haul all
        /// take this object out without anyone calling EndGrip — and a stowed viewmodel has no
        /// other owner to restore it, so the player would be left permanently empty-handed with
        /// nothing pointing at why. Same defensive shape as PlayerBowAudio's OnDisable stopping
        /// its loop so a weapon swap mid-draw cannot leave a creak running forever.
        /// </summary>
        void OnDisable()
        {
            if (IsGripped) EndGrip("component disabled");
        }

        /// <summary>
        /// If a popped cover ended up down its own shaft, put it back on the floor.
        ///
        /// THE ONE INVARIANT A COVER HAS, and a pop cannot guarantee it the way a placement
        /// could: the opening is directly underneath, so a bad bounce genuinely can drop it in —
        /// where it blocks the passage and reads as a bug rather than as physics. Checked once,
        /// after it has settled, rather than constrained during flight; the pop is the payoff for
        /// the fight and should not be fenced in to make this cheap.
        /// </summary>
        void RescueFromShaft(Transform part, Rigidbody body)
        {
            if (mode != GrateMode.FloorCover || part == null || body == null) return;
            if (part.position.y > openingY - 0.6f) return;      // still at floor level: fine

            Vector3 aside = dragDir.sqrMagnitude > 0.0001f ? dragDir.normalized : SlideFallback(part);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            part.position = new Vector3(part.position.x, openingY, part.position.z)
                          + aside * coverSlideDistance + Vector3.up * 0.15f;
        }

        float openingY;

        void AllowSleep()
        {
            Transform part = grate != null ? grate : transform;
            var body = part != null ? part.GetComponent<Rigidbody>() : null;
            if (body == null) return;
            RescueFromShaft(part, body);
            body.sleepThreshold = 0.005f;
        }
    }
}
