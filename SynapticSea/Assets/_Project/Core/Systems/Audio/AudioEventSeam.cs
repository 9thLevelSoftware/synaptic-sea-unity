// Ported from scripts/audio/audio_event_seam.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Audio event id catalog (REQ-AU-001, ADR-0029): the single source of truth for which audio ids exist.
    /// SFX_* -> bus sfx, UI_* -> ui, META_* -> meta, VOICE_* -> voice, AMB_* -> ambient.
    /// GDScript StringName constants become strings; the ALL_* tables are shared read-only arrays (never mutate).
    /// </summary>
    public static class AudioEventSeam
    {
        public const string SFX_TOOL_PICKUP = "sfx.tool.pickup";
        public const string SFX_TOOL_USE = "sfx.tool.use";
        public const string SFX_SUIT_BREATH = "sfx.suit.breath";
        public const string SFX_DOOR_OPEN = "sfx.door.open";
        public const string SFX_DOOR_CLOSE = "sfx.door.close";
        public const string SFX_FIRE_CRACKLE = "sfx.fire.crackle";
        public const string SFX_ARC_ZAP = "sfx.arc.zap";
        public const string SFX_FOOTSTEP = "sfx.footstep";
        public const string SFX_DROP_ITEM = "sfx.drop.item";
        public const string SFX_DOCK_LAND = "sfx.dock.land";
        public const string SFX_HALLUCINATION_WHISPER = "sfx.hallucination.whisper";
        // PKG-D10: pillar / combat / work verbs (placeholder clips OK).
        public const string SFX_WORK_CUT = "sfx.work.cut";
        public const string SFX_WORK_WELD = "sfx.work.weld";
        public const string SFX_WORK_PATCH = "sfx.work.patch";
        public const string SFX_WORK_UNBOLT = "sfx.work.unbolt";
        public const string SFX_WORK_PRY = "sfx.work.pry";
        public const string SFX_WORK_SPLICE = "sfx.work.splice";
        public const string SFX_WORK_HARVEST = "sfx.work.harvest";
        public const string SFX_WORK_PLANT = "sfx.work.plant";
        public const string SFX_WORK_MOUNT = "sfx.work.mount";
        public const string SFX_COMBAT_HIT = "sfx.combat.hit";
        public const string SFX_COMBAT_THREAT_ALERT = "sfx.combat.threat_alert";
        public const string SFX_WOUND_BANDAGE = "sfx.wound.bandage";
        public const string SFX_WOUND_TREAT = "sfx.wound.treat";
        public const string SFX_CRAFT_COMPLETE = "sfx.craft.complete";
        public const string SFX_REPAIR_COMPLETE = "sfx.repair.complete";
        public const string SFX_SANITY_AMBIENT = "sfx.sanity.ambient";
        public const string SFX_SANITY_HUD = "sfx.sanity.hud_glitch";
        public const string SFX_SANITY_PHANTOM = "sfx.sanity.phantom";

        public const string UI_INVENTORY_OPEN = "ui.inventory.open";
        public const string UI_WORK_PROGRESS = "ui.work.progress";
        public const string UI_WOUNDS_OPEN = "ui.wounds.open";
        public const string UI_CHART_ROUTE = "ui.chart.route";
        public const string UI_SHIP_MOD_OPEN = "ui.ship_mod.open";
        public const string UI_SHIP_MOD_INSTALL = "ui.ship_mod.install";
        public const string UI_SHIP_MOD_UNINSTALL = "ui.ship_mod.uninstall";
        public const string UI_INVENTORY_CLOSE = "ui.inventory.close";
        public const string UI_PANEL_OPEN = "ui.panel.open";
        public const string UI_PANEL_CLOSE = "ui.panel.close";
        public const string UI_OBJECTIVE_ADVANCE = "ui.objective.advance";
        public const string UI_SAVE = "ui.save";
        public const string UI_LOAD = "ui.load";
        public const string UI_VITALS_LOW = "ui.vitals.low";

        public const string META_BEACON_DISTRESS = "meta.beacon.distress";
        public const string META_BIOMATTER_PULSE = "meta.biomatter.pulse";
        public const string META_HULL_GROAN = "meta.hull.groan";
        public const string META_REACTOR_HUM = "meta.reactor.hum";

        public const string VOICE_LOG_PLAY = "voice.log.play";

        public const string AMB_CARGO = "amb.cargo";
        public const string AMB_ENGINE = "amb.engine";
        public const string AMB_MED_BAY = "amb.med_bay";
        public const string AMB_CREW_QUARTERS = "amb.crew_quarters";
        public const string AMB_DOCKING = "amb.docking";

        /// <summary>All SFX-prefixed ids, useful for static catalog checks.</summary>
        public static readonly GdArray ALL_SFX_IDS = GdArray.Of(
            SFX_TOOL_PICKUP, SFX_TOOL_USE, SFX_SUIT_BREATH, SFX_DOOR_OPEN, SFX_DOOR_CLOSE,
            SFX_FIRE_CRACKLE, SFX_ARC_ZAP, SFX_FOOTSTEP, SFX_DROP_ITEM, SFX_DOCK_LAND,
            SFX_HALLUCINATION_WHISPER,
            SFX_WORK_CUT, SFX_WORK_WELD, SFX_WORK_PATCH, SFX_WORK_UNBOLT, SFX_WORK_PRY,
            SFX_WORK_SPLICE, SFX_WORK_HARVEST, SFX_WORK_PLANT, SFX_WORK_MOUNT,
            SFX_COMBAT_HIT, SFX_COMBAT_THREAT_ALERT,
            SFX_WOUND_BANDAGE, SFX_WOUND_TREAT,
            SFX_CRAFT_COMPLETE, SFX_REPAIR_COMPLETE,
            SFX_SANITY_AMBIENT, SFX_SANITY_HUD, SFX_SANITY_PHANTOM);

        public static readonly GdArray ALL_UI_IDS = GdArray.Of(
            UI_INVENTORY_OPEN, UI_INVENTORY_CLOSE, UI_PANEL_OPEN, UI_PANEL_CLOSE, UI_OBJECTIVE_ADVANCE,
            UI_SAVE, UI_LOAD, UI_VITALS_LOW,
            UI_WORK_PROGRESS, UI_WOUNDS_OPEN, UI_CHART_ROUTE,
            UI_SHIP_MOD_OPEN, UI_SHIP_MOD_INSTALL, UI_SHIP_MOD_UNINSTALL);

        /// <summary>Verb string (WorkAction definition.verb) -> event id for PKG-D10 coverage.</summary>
        public static readonly GdDict WORK_VERB_TO_SFX = new GdDict
        {
            { "cut", SFX_WORK_CUT },
            { "weld", SFX_WORK_WELD },
            { "patch", SFX_WORK_PATCH },
            { "unbolt", SFX_WORK_UNBOLT },
            { "pry", SFX_WORK_PRY },
            { "splice", SFX_WORK_SPLICE },
            { "harvest", SFX_WORK_HARVEST },
            { "plant", SFX_WORK_PLANT },
            { "mount", SFX_WORK_MOUNT },
            { "suppress", SFX_TOOL_USE },
            { "craft", SFX_CRAFT_COMPLETE },
        };

        public static string SfxForWorkVerb(string verb)
        {
            string key = verb.ToLowerInvariant();
            if (WORK_VERB_TO_SFX.Has(key)) return V.Str(WORK_VERB_TO_SFX[key]);
            return SFX_TOOL_USE;
        }

        public static readonly GdArray ALL_META_IDS = GdArray.Of(
            META_BEACON_DISTRESS, META_BIOMATTER_PULSE, META_HULL_GROAN, META_REACTOR_HUM);

        public static readonly GdArray ALL_VOICE_IDS = GdArray.Of(VOICE_LOG_PLAY);

        public static readonly GdArray ALL_AMBIENT_IDS = GdArray.Of(AMB_CARGO, AMB_ENGINE, AMB_MED_BAY, AMB_CREW_QUARTERS, AMB_DOCKING);

        // Bus ids; mirror AudioBusConfig bus ids so callers reference one source of truth.
        public const string BUS_MASTER = "master";
        public const string BUS_SFX = "sfx";
        public const string BUS_MUSIC = "music";
        public const string BUS_VOICE = "voice";
        public const string BUS_UI = "ui";
        public const string BUS_AMBIENT = "ambient";
        public const string BUS_META = "meta";

        public static readonly GdArray ALL_BUS_IDS = GdArray.Of(BUS_MASTER, BUS_SFX, BUS_MUSIC, BUS_VOICE, BUS_UI, BUS_AMBIENT, BUS_META);

        // Room role ids for ambient zone mapping (REQ-AU-003).
        public const string ROOM_ROLE_CARGO = "cargo";
        public const string ROOM_ROLE_ENGINE = "engine";
        public const string ROOM_ROLE_MED_BAY = "med_bay";
        public const string ROOM_ROLE_CREW_QUARTERS = "crew_quarters";
        public const string ROOM_ROLE_DOCKING = "docking";

        public static readonly GdArray ALL_ROOM_ROLES = GdArray.Of(
            ROOM_ROLE_CARGO, ROOM_ROLE_ENGINE, ROOM_ROLE_MED_BAY,
            ROOM_ROLE_CREW_QUARTERS, ROOM_ROLE_DOCKING);

        // Music state names (REQ-AU-004).
        public const string MUSIC_STATE_EXPLORATION = "EXPLORATION";
        public const string MUSIC_STATE_TENSION = "TENSION";
        public const string MUSIC_STATE_COMBAT = "COMBAT";
        public const string MUSIC_STATE_CRITICAL = "CRITICAL";

        public static readonly GdArray ALL_MUSIC_STATES = GdArray.Of(
            MUSIC_STATE_EXPLORATION, MUSIC_STATE_TENSION, MUSIC_STATE_COMBAT, MUSIC_STATE_CRITICAL);

        // Music layer ids: stable strings so the crossfade scheduler keeps state across transitions.
        public const string MUSIC_LAYER_BASE = "layer.base";
        public const string MUSIC_LAYER_TENSION_DRONE = "layer.tension_drone";
        public const string MUSIC_LAYER_COMBAT_PERCUSSION = "layer.combat_percussion";
        public const string MUSIC_LAYER_CRITICAL_PAD = "layer.critical_pad";

        public static readonly GdArray ALL_MUSIC_LAYERS = GdArray.Of(
            MUSIC_LAYER_BASE, MUSIC_LAYER_TENSION_DRONE,
            MUSIC_LAYER_COMBAT_PERCUSSION, MUSIC_LAYER_CRITICAL_PAD);

        // Meta-event types (REQ-AU-007). MetaEventState keeps a schedule of these.
        public const string META_EVENT_BEACON = "beacon_distress";
        public const string META_EVENT_PULSE = "biomatter_pulse";
        public const string META_EVENT_GROAN = "hull_groan";
    }
}
