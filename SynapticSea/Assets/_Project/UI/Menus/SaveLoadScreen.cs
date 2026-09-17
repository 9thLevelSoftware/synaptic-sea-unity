// Ported from scripts/ui/menu_coordinator.gd (save_load meta screen view) + scripts/ui/save_load_menu.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.UI.Presenters;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Save / Load slot screen (records screen, PAUSED) over <see cref="SaveSlotScreenModel"/>. Each row labels the slot
    /// scope and provenance (MANUAL / AUTO / QUICK / WORLD) and the ADR-0046 metadata — location, class, objective,
    /// play time, seed — so the list is scannable without opening a payload. Frozen (permadeath) rows show
    /// "DEAD — epitaph" and accept no verb; Delete needs a second confirm. Keyboard/gamepad: Up/Down rows, Left/Right
    /// cycle the verb, Submit confirms (arm, then act). Mouse: click a verb button to act directly.
    /// </summary>
    public sealed class SaveLoadScreen : SurfacePanel
    {
        readonly SaveSlotScreenModel _model;
        readonly SelectableList _list;
        readonly VisualElement _verbs;
        readonly Label _hint;

        /// <summary>Every confirm result ({screen, action, ok, detail[, snapshot]}).</summary>
        public event Action<GdDict> Confirmed;
        public event Action BackRequested;

        public SaveLoadScreen(SaveSlotScreenModel model) : base("SAVE / LOAD", SurfaceTime.Paused)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            AddToClassList("ss-saveload");
            _list = new SelectableList("slot", "(no save slots)");
            Body.Add(_list);
            _verbs = UiFactory.Box(UiClasses.Toolbar, "ss-saveload__verbs");
            Body.Add(_verbs);
            _hint = UiFactory.Text("", UiClasses.LabelSecondary);
            Body.Add(_hint);
            _list.SelectionRequested += i => _model.SelectRow(i);
            _list.ActivateRequested += i =>
            {
                _model.SelectRow(i);
                Confirm();
            };
            _list.MoveOut = dir =>
            {
                _model.CycleVerb(dir == NavigationMoveEvent.Direction.Left ? -1 : 1);
                return true;
            };
            _model.Changed += Render;
        }

        public override string SurfaceId => "save_load";

        public SaveSlotScreenModel Model => _model;
        public SelectableList List => _list;
        public IEnumerable<Button> VerbButtons => _verbs.Query<Button>().ToList();
        public string HintText => _hint.text;

        /// <summary>Opened from the records list: cursor and verb state reset on every open.</summary>
        public void OnOpened()
        {
            _model.Reset();
            Render();
        }

        public void OnClosed() => _model.ClearPending();

        public GdDict Confirm()
        {
            GdDict result = _model.Confirm();
            ShowResult(result);
            Confirmed?.Invoke(result);
            return result;
        }

        public GdDict ConfirmVerb(string verb)
        {
            GdDict result = _model.ConfirmVerb(verb);
            ShowResult(result);
            Confirmed?.Invoke(result);
            return result;
        }

        static string KindLabel(SaveSlotState row)
        {
            if (row.IsWorld()) return "WORLD";
            if (row.IsAuto()) return "AUTO";
            if (row.IsQuick()) return "QUICK";
            return "MANUAL";
        }

        /// <summary>The ADR-0046 metadata line for a row with a payload.</summary>
        public static string MetadataLine(SaveSlotState row)
        {
            string loc = row.CurrentLocation.Length != 0 ? row.CurrentLocation : "?";
            string cls = row.PlayerClass.Length != 0 ? row.PlayerClass : "?";
            return "Location " + loc + " · Class " + cls + " · Objective " + GdString.FormatInt(row.ObjectiveSequence)
                + " · Played " + SaveSlotScreenModel.FormatPlayTime(row.PlayTimeSeconds) + " · Seed " + GdString.FormatInt(row.SynapticSeaSeed);
        }

        void ShowResult(GdDict result)
        {
            string action = V.Str(result.Get("action", ""));
            bool ok = result.GetBool("ok", false);
            string detail = V.Str(result.Get("detail", ""));
            switch (action)
            {
                case "arm":
                    StatusText.Set("Choose an action for " + detail + ", then confirm", Severity.Info);
                    break;
                case "delete_armed":
                    StatusText.Set("Confirm again to delete " + detail, Severity.Caution);
                    break;
                case "delete":
                    StatusText.Set(ok ? "Deleted " + detail : "Could not delete " + detail, ok ? Severity.Success : Severity.Caution);
                    break;
                case "save":
                    if (detail == "demo_blocked") StatusText.Set("Saving is not available past the demo limit", Severity.Caution);
                    else StatusText.Set(ok ? "Saved to " + detail : "Save failed — " + detail + " was not written", ok ? Severity.Success : Severity.Danger);
                    break;
                case "load":
                    StatusText.Set(ok ? "Loading " + detail : "Could not load " + detail, ok ? Severity.Success : Severity.Caution);
                    break;
                case "load_world":
                    StatusText.Set("Loading the world save", Severity.Success);
                    break;
                default:
                    StatusText.Set(detail.Length == 0 ? "No slot selected" : "No action available for " + detail, Severity.Caution);
                    break;
            }
        }

        void Render()
        {
            List<SaveSlotState> rows = _model.Rows();
            var items = new List<SelectableList.Item>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                SaveSlotState row = rows[i];
                var item = new SelectableList.Item { Id = row.SlotId, Chip = KindLabel(row) };
                if (row.Frozen)
                {
                    item.Text = row.SlotId + " — DEAD";
                    item.Detail = "⚠ " + _model.Epitaph(row.SlotId);
                    item.Severity = Severity.Danger;
                }
                else if (row.IsManual() && row.DisplayName.Length == 0 && !SaveSlotScreenModel.RowHasPayload(row))
                {
                    item.Text = row.SlotId + " — empty";
                    item.Detail = "Save here";
                    item.Muted = true;
                }
                else
                {
                    item.Text = row.SlotId + " · " + (row.DisplayName.Length != 0 ? row.DisplayName : row.SlotId);
                    item.Detail = MetadataLine(row) + (row.Corrupt ? " · ▲ corrupt" : "");
                    if (row.Corrupt) item.Severity = Severity.Caution;
                }
                items.Add(item);
            }
            _list.SetItems(items, _model.RowIndex);
            RenderVerbs(rows);
        }

        void RenderVerbs(List<SaveSlotState> rows)
        {
            _verbs.Clear();
            if (rows.Count == 0 || _model.RowIndex >= rows.Count)
            {
                _hint.text = "";
                return;
            }
            SaveSlotState row = rows[_model.RowIndex];
            List<string> verbs = row.Frozen ? new List<string>() : SaveSlotScreenModel.ValidVerbsForRow(row);
            foreach (string verb in verbs)
            {
                string v = verb;
                bool armed = _model.PendingVerb == v;
                string label = v == SaveSlotScreenModel.VerbDelete && _model.PendingDeleteSlotId == row.SlotId ? "Confirm Delete" : v;
                Button b = UiFactory.Button(label, () => ConfirmVerb(v), "verb:" + v);
                b.EnableInClassList("ss-button--armed", armed);
                if (v == SaveSlotScreenModel.VerbDelete) b.AddToClassList("ss-button--danger");
                _verbs.Add(b);
            }
            if (row.Frozen) _hint.text = "This run ended. The slot is frozen.";
            else if (verbs.Count == 0) _hint.text = row.IsAuto() || row.IsQuick() ? "Automatic saves are read-only here." : "";
            else _hint.text = _model.PendingVerb.Length != 0 ? "Armed: " + _model.PendingVerb + " — confirm to proceed" : "◀ ▶ choose an action · confirm to arm";
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                    _model.MoveSelection(-1);
                    return true;
                case UiCommand.Down:
                    _model.MoveSelection(1);
                    return true;
                case UiCommand.Left:
                    _model.CycleVerb(-1);
                    return true;
                case UiCommand.Right:
                    _model.CycleVerb(1);
                    return true;
                case UiCommand.Accept:
                    Confirm();
                    return true;
            }
            return false;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }
}
