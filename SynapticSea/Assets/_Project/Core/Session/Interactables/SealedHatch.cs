// Ported from scripts/interaction/sealed_hatch.gd @ 96ecb2b0
using System;
using System.Diagnostics;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>
    /// A locked passage on a generated ship. Blocks traversal (a StaticBody3D collider) until the player bypasses it
    /// with the matching utility flag: a mechanical hatch needs "lockpick", an electronic one needs "hack_chip".
    /// Domain 5. <see cref="SessionInteractable.CandidatePlayerInRange"/> is Godot's <c>_player_in_range</c> and
    /// <see cref="SessionInteractable.InteractionRadius"/> is <c>_radius</c>.
    /// </summary>
    public sealed class SealedHatch : SessionInteractable
    {
        public override string Kind => "sealed_hatch";

        public const string MECHANICAL = "mechanical";
        public const string ELECTRONIC = "electronic";

        /// <summary>signal hatch_bypassed(hatch_id, lock_kind)</summary>
        public event Action<string, string> HatchBypassed;
        /// <summary>signal hatch_resealed(hatch_id, lock_kind) — restores the bulkhead blocker (Fire B2 closed link).</summary>
        public event Action<string, string> HatchResealed;

        public string HatchId = "";
        public string LockKind = MECHANICAL;
        public bool Bypassed = false;
        /// <summary>Fire B2: optional bulkhead endpoints this hatch seals (empty = no fire link).</summary>
        public string CompartmentA = "";
        public string CompartmentB = "";

        /// <summary>
        /// RUNTIME: the HatchBlocker StaticBody3D's CollisionShape3D.disabled (box radius x radius*2 x 0.4); the
        /// detection sphere stays enabled.
        /// </summary>
        public bool BlockerDisabled => Bypassed;

        /// <summary>Same as <see cref="BlockerDisabled"/> (the only collider this node toggles).</summary>
        public bool CollisionDisabled => Bypassed;

        public void Configure(string hatchId, string lockKind, Vec3 worldPosition, double radius = 1.8, string compartmentA = "", string compartmentB = "")
        {
            Debug.Assert(radius >= 0.0, "radius must be non-negative");
            HatchId = hatchId;
            LockKind = (lockKind == MECHANICAL || lockKind == ELECTRONIC) ? lockKind : MECHANICAL;
            CompartmentA = compartmentA;
            CompartmentB = compartmentB;
            InteractionRadius = radius;
            LocalPosition = worldPosition;
            // (Godot configure does not set the node name; NodeName is left to the caller.)
            // RUNTIME: detection sphere (radius, once); HatchBlocker StaticBody3D box collider (once);
            // GameplayPropFactory.build("hatch_wheel") visual; blocker disabled = BlockerDisabled.
            ApplyBlockedState();
        }

        public string RequiredFlag() => LockKind == MECHANICAL ? "lockpick" : "hack_chip";

        public void SetBypassed(bool value)
        {
            Bypassed = value;
            ApplyBlockedState();
        }

        /// <summary>
        /// Attempts to bypass using the player's active utility flags. On success the blocker is disabled and
        /// HatchBypassed is raised once.
        /// </summary>
        public GdDict TryBypass(Vec3 playerPosition, GdDict activeFlags)
        {
            if (Bypassed)
                return new GdDict { { "ok", false }, { "reason", "already_open" }, { "hatch_id", HatchId } };
            if (!IsPlayerInRange(playerPosition))
                return new GdDict { { "ok", false }, { "reason", "out_of_range" }, { "hatch_id", HatchId } };
            string flag = RequiredFlag();
            if (activeFlags == null || !activeFlags.Has(flag))
                return new GdDict { { "ok", false }, { "reason", "locked" }, { "hatch_id", HatchId }, { "needs", flag }, { "lock_kind", LockKind } };
            SetBypassed(true);
            HatchBypassed?.Invoke(HatchId, LockKind);
            return new GdDict { { "ok", true }, { "hatch_id", HatchId }, { "lock_kind", LockKind }, { "consumed_flag", flag } };
        }

        /// <summary>Re-close a previously bypassed hatch (no utility flag cost).</summary>
        public GdDict TryReseal(Vec3 playerPosition)
        {
            if (!Bypassed)
                return new GdDict { { "ok", false }, { "reason", "already_sealed" }, { "hatch_id", HatchId } };
            if (!IsPlayerInRange(playerPosition))
                return new GdDict { { "ok", false }, { "reason", "out_of_range" }, { "hatch_id", HatchId } };
            SetBypassed(false);
            HatchResealed?.Invoke(HatchId, LockKind);
            return new GdDict { { "ok", true }, { "hatch_id", HatchId }, { "lock_kind", LockKind } };
        }

        bool IsPlayerInRange(Vec3 playerPosition)
        {
            if (CandidatePlayerInRange)
                return true;
            return GlobalPosition.DistanceTo(playerPosition) <= InteractionRadius;
        }

        void ApplyBlockedState()
        {
            // RUNTIME: for each CollisionShape3D under HatchBlocker: disabled = bypassed.
            NotifyChanged();
        }
    }
}
