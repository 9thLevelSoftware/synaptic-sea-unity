using System;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        public const string ActiveFirstAwaySalvageProfile = FirstAwayGenerationInputs.Profile;
        public bool EnableReviewedFirstAwayProfile
        {
            get => Deps.EnableReviewedFirstAwayProfile;
            set { Deps.EnableReviewedFirstAwayProfile = value; if (SaveLoadService != null) SaveLoadService.FirstAwaySalvageProfileEnabled = value; }
        }
        public bool ReviewedFirstAwayProfileEnabled => EnableReviewedFirstAwayProfile;
        ShipDocuments _acceptedFirstAwayDocuments;

        FirstAwayGenerationInputs FirstAwayInputs(long seed, long worldSeed, long size, long condition, string marker, string owner)
        {
            if (!ReviewedFirstAwayProfileEnabled || !CompleteGenerationEnabled)
                throw new InvalidOperationException("first_away_profile_not_admitted");
            var contract = new FirstRunContract();
            if (!contract.LoadContract()) throw new InvalidOperationException("first_away_contract_missing");
            var input = new FirstAwayGenerationInputs(seed, worldSeed, size, condition, marker, owner,
                contract.Contract.GetString("biome_id"), contract.Contract.GetString("difficulty_id"));
            if (!FirstAwayMatchesSourceMarker(input)) throw new InvalidOperationException("first_away_source_marker_mismatch");
            return input;
        }

        public static bool FirstAwayMatchesSourceMarker(FirstAwayGenerationInputs input)
        {
            if (input == null || input.OwnerId != "ship_" + input.MarkerId) return false;
            var source = new MarkerGenerator().MarkersForCell(input.WorldSeed,
                new Vec2i((int)input.SectorX, (int)input.SectorY)).SingleOrDefault(marker => marker.MarkerId == input.MarkerId);
            return source != null && source.SizeClass == input.Size && source.Condition == input.Condition;
        }

        GdDict FirstAwayContractContext()
        {
            var contract = new FirstRunContract();
            if (!contract.LoadContract()) throw new InvalidOperationException("first_away_contract_missing");
            return new GdDict { { "biome", contract.Contract.GetString("biome_id") }, { "difficulty", contract.Contract.GetString("difficulty_id") } };
        }

        ShipDocuments GenerateFirstAwayCandidate(ShipMarker marker, long seed, long size, long condition)
        {
            try
            {
                var expected = FirstAwayInputs(seed, SynapticSeaWorld.WorldSeed, size, condition, marker.MarkerId, "ship_" + marker.MarkerId);
                var source = new MarkerGenerator().MarkersForCell(expected.WorldSeed, new Vec2i((int)expected.SectorX, (int)expected.SectorY))[(int)expected.MarkerIndex];
                if (source.Position != marker.Position) return null;
                ShipDocuments docs = ShipGenerator.GenerateFirstAway(expected);
                if (docs != null) _acceptedFirstAwayDocuments = docs;
                return docs;
            }
            catch (Exception) { return null; }
        }

        bool ValidateFirstAwayDocuments(ShipDocuments docs, ShipBlueprint blueprint, string marker, string owner, long worldSeed, out ShipDocuments validated, string archivedKitText = null)
        {
            validated = docs;
            try
            {
                if (docs == null) return false;
                if (docs.Layout == null && !string.IsNullOrEmpty(docs.LayoutJson))
                    docs.Layout = GdJson.ParseString(docs.LayoutJson) as GdDict;
                string rawProfile = docs.Layout?.GetString("generation_profile") ?? "";
                if (blueprint != null && rawProfile != blueprint.GenerationProfile) return false;
                string profile = blueprint?.GenerationProfile ?? rawProfile;
                if (profile != FirstAwayGenerationInputs.Profile)
                    return docs != null && docs.Layout?.GetString("generation_profile") != FirstAwayGenerationInputs.Profile
                        && string.IsNullOrEmpty(blueprint?.FirstAwayDescriptorText);
                if (blueprint == null || string.IsNullOrEmpty(blueprint.FirstAwayDescriptorText) || owner != "ship_" + marker) return false;
                var expected = FirstAwayInputs(blueprint.SeedValue, worldSeed, blueprint.ShipSize, blueprint.ShipCondition, marker, owner);
                if (!(ShipGenerator ?? new ShipGenerator()).TryRestoreFirstAway(expected, PaidSnapshotCodec.Parse(blueprint.FirstAwayDescriptorText),
                    docs.LayoutJson, docs.GameplaySliceJson, out validated)) return false;
                string kitText = archivedKitText ?? SynapticSea.Core.Services.CoreServices.Resources?.ReadText(docs.KitPath);
                var pinned = FirstAwaySalvageProfile.LoadPinnedCatalogs();
                if (docs.KitPath != validated.KitPath || kitText == null || pinned == null || !pinned.TryGetValue(docs.KitPath, out FirstAwayCatalogIdentity identity)
                    || SaveGenerationArtifacts.Hash(kitText) != identity.Sha256) return false;
                var archivedKit = GdJson.ParseString(kitText) as GdDict;
                if (archivedKit == null || docs.Kit != null && !V.VariantEquals(archivedKit, docs.Kit)) return false;
                validated.Kit = archivedKit; validated.KitPath = docs.KitPath;
                return true;
            }
            catch (Exception) { validated = null; return false; }
        }

        bool ValidateFirstAwayBuild(ShipDocuments docs)
        {
            if (docs?.Layout?.GetString("generation_profile") != FirstAwayGenerationInputs.Profile) return docs != null;
            if (docs.FirstAwayInputs == null || docs.FirstAwayDescriptor == null || SynapticSeaWorld == null) return false;
            var input = docs.FirstAwayInputs;
            var bp = new ShipBlueprint(input.Size, input.Condition, input.CandidateSeed) {
                GenerationProfile = FirstAwayGenerationInputs.Profile,
                FirstAwayDescriptorText = PaidSnapshotCodec.Stringify(docs.FirstAwayDescriptor.Snapshot()) };
            return ValidateFirstAwayDocuments(docs, bp, input.MarkerId, input.OwnerId, SynapticSeaWorld.WorldSeed, out _);
        }

        IShipGenerator TravelGenerationSource(ShipMarker marker)
        {
            if (VisitedShips.TryGetValue(marker.MarkerId, out ShipInstance ship) && ship.Blueprint.GenerationProfile == FirstAwayGenerationInputs.Profile)
            {
                if (!_generationShipDocuments.TryGetValue(ship.ShipId, out GdDict raw)) return new ExactFirstAwayTravel(null, marker);
                var docs = new ShipDocuments { LayoutJson = raw.GetString("layout_text"), GameplaySliceJson = raw.GetString("slice_text"),
                    Kit = GdJson.ParseString(raw.GetString("kit_text")) as GdDict, KitPath = raw.GetString("kit_path") };
                if (!ValidateFirstAwayDocuments(docs, ship.Blueprint, marker.MarkerId, ship.ShipId,
                    SynapticSeaWorld.WorldSeed, out ShipDocuments validated, raw.GetString("kit_text"))) return new ExactFirstAwayTravel(null, marker);
                var input = validated.FirstAwayInputs;
                var source = new MarkerGenerator().MarkersForCell(input.WorldSeed, new Vec2i((int)input.SectorX, (int)input.SectorY))[(int)input.MarkerIndex];
                if (source.Position != marker.Position) return new ExactFirstAwayTravel(null, marker);
                return new ExactFirstAwayTravel(validated, marker, true);
            }
            if (_acceptedFirstAwayDocuments != null && _acceptedFirstAwayDocuments.FirstAwayInputs.MarkerId == marker.MarkerId)
            {
                var accepted = _acceptedFirstAwayDocuments; _acceptedFirstAwayDocuments = null;
                return new ExactFirstAwayTravel(accepted, marker);
            }
            return FirstRunGenerator();
        }

        sealed class ExactFirstAwayTravel : IShipGenerator
        {
            readonly ShipDocuments _docs; readonly long _seed, _size, _condition; readonly bool _retained;
            public ExactFirstAwayTravel(ShipDocuments docs, ShipMarker marker, bool retained = false)
            { _docs = docs; _seed = marker.SeedValue; _size = marker.SizeClass; _condition = marker.Condition; _retained = retained; }
            public object GenerateFromSeed(long seedValue, long size = 0, long condition = 1)
            {
                if (_docs?.FirstAwayInputs == null || seedValue != _seed || size != _size || condition != _condition) return null;
                var input = _docs.FirstAwayInputs;
                return (_retained || input.CandidateSeed == seedValue) && input.Size == size && input.Condition == condition ? _docs : null;
            }
        }

        bool TryFirstAwayCargoWorkPosition(IShipLoaderView active, out Vec3 position)
        {
            position = Vec3.Zero;
            if (CurrentShip?.Blueprint?.GenerationProfile != FirstAwayGenerationInputs.Profile) return false;
            GdDict layout = active.GetLayoutCopy(); string room = layout.GetString("first_away_reservation_owner");
            GdArray cell = layout.GetDictOrEmpty("first_away_reservations").GetArrayOrEmpty("work_approach");
            foreach (GdDict objective in active.GetObjectiveSpecsCopy().OfType<GdDict>())
                if (objective.GetString("room_id") == room && V.VariantEquals(objective.GetArrayOrEmpty("approach_cell"), cell)
                    && objective.Get("position") is Vec3 projected && projected != Vec3.Inf)
                { position = projected; return true; }
            return false;
        }
    }
}
