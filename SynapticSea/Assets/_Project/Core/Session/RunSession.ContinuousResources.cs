using System;
using System.Collections.Generic;
using System.Text;
using SynapticSea.Core.Services;
namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // Explicit selected stock admission/survival dependencies. No recursive asset scan.
        // Undeclared accesses refuse and must be audited before expanding this list.
        static readonly string[] ContinuousResourcePaths={
            "res://data/combat/ammo_definitions.json",
            "res://data/combat/status_effect_definitions.json",
            "res://data/combat/threat_archetypes.json",
            "res://data/combat/weapon_definitions.json",
            "res://data/components/component_catalog.json",
            "res://data/diagnostics/earned-services-home-v1/gameplay_slice.json",
            "res://data/items/equipment_definitions.json",
            "res://data/items/item_definitions.json",
            "res://data/items/junk_items.json",
            "res://data/items/medicine_definitions.json",
            "res://data/items/stimulant_definitions.json",
            "res://data/items/trade_item_definitions.json",
            "res://data/items/unique_items.json",
            "res://data/items/utility_item_definitions.json",
            "res://data/materials/material_definitions.json",
            "res://data/player/classes.json",
            "res://data/player/skill_books.json",
            "res://data/player/skill_tree.json",
            "res://data/player/skills.json",
            "res://data/player/training_actions.json",
            "res://data/recipes/recipe_definitions.json",
            "res://data/ship_systems/systems.json",
            "res://data/tools/tool_definitions.json",
            "res://data/ui/rarity_palette.json",
            "res://data/work_actions/work_action_catalog.json",
        };
        // Application-owned preload seam: called BEFORE ordinary SelectGeneration/Create on Continue.
        // Fixed trusted source paths, never saved resource capsules or archive-reported bytes.
        public static bool TryPublishContinuousDiagnosticResources(out string reason)
            =>TryPublishContinuousTrustedResources(out _,out reason);
        bool TryPublishContinuousBootstrapResources(out ResourceAuthorityLease lease,out string reason)
        {
            lease=null;reason="continuous_resource_bootstrap_closed";
            if(!ContinuousDiagnosticBootstrapOpen)return false;
            return TryPublishContinuousTrustedResources(out lease,out reason);
        }
        static bool TryPublishContinuousTrustedResources(out ResourceAuthorityLease lease,out string reason)
        {
            lease=null;reason="continuous_resource_bootstrap_closed";
            if(ResourceAuthorityPublication.TryAcquire(out lease,out reason))return true;
            var reader=CoreServices.Resources;
            if(reader==null){reason="continuous_resource_reader_missing";return false;}
            var texts=new Dictionary<string,string>(StringComparer.Ordinal);long bytes=0;
            var paths=new SortedSet<string>(ContinuousResourcePaths,StringComparer.Ordinal);
            // Layout is an ordinary captured scene artifact, not a proof-admission resource.
            // Full layout bytes remain in the generation document set and ordinary artifact reader.
            foreach(var path in new[]{"res://data/diagnostics/earned-services-home-v1/gameplay_slice.json",
                "res://data/diagnostics/earned-services-home-v1/blueprint.json",
                "res://data/kits/ship_structural_v0.json"})
                if(!string.IsNullOrEmpty(path))paths.Add(path);
            try
            {
                foreach(string path in paths)
                {
                    string text=reader.ReadText(path);bytes=checked(bytes+(text==null?0:Encoding.UTF8.GetByteCount(text)));
                    if(bytes>4L*1024*1024){reason="continuous_resource_closure_capacity";return false;}
                    texts.Add(path,text);
                }
                if(!ReferenceEquals(reader,CoreServices.Resources)){reason="continuous_resource_reader_changed";return false;}
                // Trusted application publisher controls this immutable replacement. No resource bytes from saves.
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(texts));
                return ResourceAuthorityPublication.TryAcquire(out lease,out reason);
            }
            catch(Exception failure){reason="continuous_resource_bootstrap_failed:"+failure.Message;return false;}
        }
    }
}
