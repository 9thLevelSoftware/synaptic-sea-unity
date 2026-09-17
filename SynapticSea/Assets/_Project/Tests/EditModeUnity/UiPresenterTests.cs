using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;

namespace SynapticSea.Tests.Unity
{
    public class UiPresenterTests : UiTestBase
    {
        [Test]
        public void AccessibilitySettingsClampsAndReflowsUpNeverDown()
        {
            var a11y = new AccessibilitySettings(_ => "1.2");
            Assert.AreEqual(1.2, a11y.GetTextScale(), 1e-9, "env var is read");
            Assert.AreEqual(150, a11y.ReflowStep(), "1.2x rounds up to the 1.5x step");
            Assert.AreEqual("scale-150", a11y.ReflowClass());
            a11y.SetTextScale(5.0);
            Assert.AreEqual(2.0, a11y.GetTextScale());
            Assert.AreEqual("scale-200", a11y.ReflowClass());
            a11y.SetTextScale(-1);
            Assert.AreEqual(2.0, a11y.GetTextScale(), "non-positive values are ignored");
            a11y.SetTextScale(1.0);
            Assert.AreEqual("", a11y.ReflowClass());
            Assert.AreEqual(36, a11y.ScaledHudFontSize(18) * 2);
            Assert.AreEqual(0.0005, new AccessibilitySettings(_ => "2").ScaledWorldPixelSize(0.0001), 1e-12);
            Assert.AreEqual(1.0, new AccessibilitySettings(_ => "garbage").GetTextScale());
        }

        [Test]
        public void SettingsStateWritesThroughTheSink()
        {
            var settings = new SettingsState();
            settings.SetTextScale(2.0);
            settings.SetColorblindMode("tritanopia");
            var a11y = new AccessibilitySettings(_ => null);
            Assert.IsTrue(settings.ApplyToAccessibility(a11y));
            Assert.AreEqual(2.0, a11y.GetTextScale());
            Assert.AreEqual("tritanopia", a11y.GetColorblindMode());
        }

        static SaveLoadService NewService(MemoryStorage storage) => new SaveLoadService(storage, new ManualClock());

        static RunSnapshot Snapshot(string location, string cls, long objective, double playTime, long seed) =>
            new RunSnapshot
            {
                CurrentLocation = location,
                PlayerProgressionSummary = new GdDict { { "class_id", cls } },
                CurrentObjectiveSequence = objective,
                PlayTimeSeconds = playTime,
                WorldSeed = seed,
            };

        [Test]
        public void SlotRowsShowAdr0046MetadataAndSynthesizeEmptyManualSlots()
        {
            SaveLoadService service = NewService(Storage);
            Assert.IsTrue(service.SaveToSlot("slot_02", Snapshot("engine_room", "engineer", 3, 3725, 42), "manual", false, "Before the breach"));
            var menu = new SaveLoadMenu();
            menu.Bind(service);
            var model = new SaveSlotScreenModel(menu, new PermadeathResolver(Storage));
            List<SaveSlotState> rows = model.Rows();
            Assert.AreEqual("slot_02", rows[0].SlotId, "real rows first");
            Assert.AreEqual(1 + 5, rows.Count, "plus one synthesized row per missing manual slot");
            string line = model.RowLine(rows[0], 0);
            Assert.AreEqual("slot_02 | Before the breach | engine_room | engineer | obj=3 | 1h02m | seed=42", line);
            StringAssert.Contains("slot_01 -- empty", model.RowLine(rows[1], 1));
            Assert.AreEqual("3m07s", SaveSlotScreenModel.FormatPlayTime(187.9));
            CollectionAssert.AreEqual(new[] { "Load", "Save", "Delete" }, SaveSlotScreenModel.ValidVerbsForRow(rows[0]));
            CollectionAssert.AreEqual(new[] { "Save" }, SaveSlotScreenModel.ValidVerbsForRow(rows[1]));
        }

        [Test]
        public void SlotVerbsArmThenActAndDeleteNeedsASecondConfirm()
        {
            SaveLoadService service = NewService(Storage);
            service.SaveToSlot("slot_01", Snapshot("hub", "medic", 1, 60, 777), "manual", false, "Start");
            var menu = new SaveLoadMenu();
            menu.Bind(service);
            var model = new SaveSlotScreenModel(menu, new PermadeathResolver(Storage)) { SnapshotBuilder = () => Snapshot("cargo", "medic", 2, 90, 777) };
            Assert.AreEqual("arm", model.Confirm().GetString("action"));
            Assert.AreEqual("Load", model.PendingVerb);
            model.CycleVerb(1);
            model.CycleVerb(1);
            Assert.AreEqual("Delete", model.PendingVerb);
            Assert.AreEqual("delete_armed", model.Confirm().GetString("action"));
            StringAssert.Contains("confirm again to delete", model.RowLine(model.Rows()[0], 0));
            GdDict deleted = model.Confirm();
            Assert.AreEqual("delete", deleted.GetString("action"));
            Assert.IsTrue(deleted.GetBool("ok"));
            Assert.IsFalse(service.HasSlot("slot_01"));
            Assert.AreEqual("slot_01", model.Rows()[model.RowIndex].SlotId, "cursor re-anchors on the acted-on slot");

            model.Confirm(); // arm Save on the now-empty slot
            GdDict saved = model.Confirm();
            Assert.AreEqual("save", saved.GetString("action"));
            Assert.IsTrue(saved.GetBool("ok"));
            Assert.AreEqual("cargo", model.Rows()[model.RowIndex].CurrentLocation);
        }

        [Test]
        public void FrozenRowsShowTheEpitaphAndRefuseVerbs()
        {
            SaveLoadService service = NewService(Storage);
            service.SaveToSlot("slot_03", Snapshot("med_bay", "medic", 4, 400, 9), "manual", false, "Last stand");
            new PermadeathResolver(Storage).RecordDeath("slot_03", "suffocation", "Ran out of air.", 400, 4);
            var menu = new SaveLoadMenu();
            menu.Bind(service);
            var model = new SaveSlotScreenModel(menu, new PermadeathResolver(Storage));
            Assert.IsTrue(model.Rows()[0].Frozen);
            Assert.AreEqual("slot_03 | DEAD -- Ran out of air.", model.RowLine(model.Rows()[0], 0));
            Assert.AreEqual("none", model.Confirm().GetString("action"));
        }

        [Test]
        public void SaveLoadScreenRowsListLocationPlayTimeSeedClassAndObjective()
        {
            SaveLoadService service = NewService(Storage);
            service.SaveToSlot("slot_04", Snapshot("bridge", "mechanic", 5, 7200, 1234), "manual", false, "Deep");
            var menu = new SaveLoadMenu();
            menu.Bind(service);
            var screen = new SaveLoadScreen(new SaveSlotScreenModel(menu, new PermadeathResolver(Storage)));
            screen.OnOpened();
            screen.style.display = UnityEngine.UIElements.DisplayStyle.Flex;
            Assert.AreEqual(SurfacePanel.BadgePausedText, screen.BadgeText);
            SelectableList.Item row = screen.List.Items[0];
            Assert.AreEqual("MANUAL", row.Chip);
            StringAssert.Contains("slot_04 · Deep", row.Text);
            Assert.AreEqual("Location bridge · Class mechanic · Objective 5 · Played 2h00m · Seed 1234", row.Detail);
            StringAssert.Contains("Played 2h00m", VisibleText(screen));
            var verbs = new List<string>();
            foreach (var b in screen.VerbButtons) verbs.Add(b.text);
            CollectionAssert.AreEqual(new[] { "Load", "Save", "Delete" }, verbs);
            GdDict load = screen.ConfirmVerb("Load");
            Assert.AreEqual("load", load.GetString("action"));
            Assert.IsTrue(load.GetBool("ok"));
            Assert.IsNotNull(screen.Model.LastLoadedSnapshot);
            Assert.AreEqual("bridge", screen.Model.LastLoadedSnapshot.CurrentLocation);
        }
    }
}
