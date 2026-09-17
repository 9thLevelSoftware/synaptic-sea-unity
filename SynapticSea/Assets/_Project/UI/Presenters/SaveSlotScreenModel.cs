// Ported from scripts/ui/menu_coordinator.gd @ 96ecb2b0 (the Domain 8 / ADR-0043 / ADR-0046 save_load meta-screen logic)
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.UI.Presenters
{
    /// <summary>
    /// The save/load slot screen's cursor + verb state machine, lifted out of MenuCoordinator so it is pure and
    /// testable headless. Row list = <see cref="SaveLoadMenu.Refresh"/> rows (saved_at desc) followed by one
    /// synthesized empty row per missing manual slot, with permadeath <c>frozen</c> overlaid from the death records.
    /// Verb model (spec 3.2): empty manual [Save]; filled manual [Load, Save, Delete]; world [Load]; auto/quick
    /// display-only; frozen rows accept no verb. Delete needs a second confirm on the same slot.
    /// </summary>
    public sealed class SaveSlotScreenModel
    {
        public const string VerbLoad = "Load";
        public const string VerbSave = "Save";
        public const string VerbDelete = "Delete";

        readonly SaveLoadMenu _menu;
        readonly PermadeathResolver _deaths;

        /// <summary>Builds the RunSnapshot for a Save verb (Godot's <c>_snapshot_builder</c> Callable).</summary>
        public Func<RunSnapshot> SnapshotBuilder;
        /// <summary>The playable's demo play-time refusal predicate (Godot's <c>_demo_save_refused_cb</c>).</summary>
        public Func<bool> DemoSaveRefused;

        /// <summary>The RunSnapshot of the last successful "load" confirm. Godot put it in the result dict under
        /// "snapshot"; GdDict holds only Variant leaves, so it travels here (and on MenuCoordinator.SlotSnapshotLoaded).</summary>
        public RunSnapshot LastLoadedSnapshot { get; private set; }

        public int RowIndex { get; private set; }
        public string PendingVerb { get; private set; } = "";
        public string PendingDeleteSlotId { get; private set; } = "";

        /// <summary>Raised whenever rows, cursor or verb state change (the view re-renders).</summary>
        public event Action Changed;

        public SaveSlotScreenModel(SaveLoadMenu menu, PermadeathResolver deathRecords = null)
        {
            _menu = menu ?? throw new ArgumentNullException(nameof(menu));
            _deaths = deathRecords ?? new PermadeathResolver();
        }

        public SaveLoadMenu Menu => _menu;
        public PermadeathResolver DeathRecords => _deaths;

        /// <summary>Resets cursor and verb state (every (re)open of the screen).</summary>
        public void Reset()
        {
            RowIndex = 0;
            PendingVerb = "";
            PendingDeleteSlotId = "";
            Changed?.Invoke();
        }

        /// <summary>Clears verb state on close (cursor is reset on the next open).</summary>
        public void ClearPending()
        {
            PendingVerb = "";
            PendingDeleteSlotId = "";
        }

        public List<SaveSlotState> Rows()
        {
            var rows = new List<SaveSlotState>(_menu.Refresh());
            var presentManual = new HashSet<string>(StringComparer.Ordinal);
            foreach (SaveSlotState row in rows)
            {
                if (row != null && row.IsManual()) presentManual.Add(row.SlotId);
            }
            foreach (object slotId in SaveSlotState.ManualSlotIds)
            {
                string id = V.Str(slotId);
                if (!presentManual.Contains(id)) rows.Add(SynthesizeEmptyManualRow(id));
            }
            // Review fix (Finding 2): death is derived at read time from the resolver, never persisted to the index.
            foreach (SaveSlotState row in rows)
            {
                if (row != null) row.Frozen = _deaths.HasDiedIn(row.SlotId);
            }
            return rows;
        }

        static SaveSlotState SynthesizeEmptyManualRow(string slotId) =>
            new SaveSlotState
            {
                SlotId = slotId,
                SlotKind = SaveSlotState.SlotKindManual,
                DisplayName = "",
                Frozen = false,
                Corrupt = false,
            };

        /// <summary>True for a real on-disk row; synthesized placeholders never carry a saved_at/schema stamp.</summary>
        public static bool RowHasPayload(SaveSlotState row) => row.SavedAtEpoch != 0 || row.SchemaVersion.Length != 0;

        public static List<string> ValidVerbsForRow(SaveSlotState row)
        {
            if (row.IsWorld()) return new List<string> { VerbLoad };
            if (row.IsAuto() || row.IsQuick()) return new List<string>();
            if (row.IsManual())
            {
                if (!RowHasPayload(row)) return new List<string> { VerbSave };
                return new List<string> { VerbLoad, VerbSave, VerbDelete };
            }
            return new List<string>();
        }

        /// <summary>Accumulated play clock: <c>1h02m</c> or <c>3m07s</c>.</summary>
        public static string FormatPlayTime(double seconds)
        {
            long total = Math.Max(0, GdMath.FloorI(seconds));
            long h = total / 3600;
            long m = (total % 3600) / 60;
            long s = total % 60;
            if (h > 0) return GdString.FormatInt(h) + "h" + GdString.FormatIntPadded(m, 2) + "m";
            return GdString.FormatInt(m) + "m" + GdString.FormatIntPadded(s, 2) + "s";
        }

        /// <summary>The Godot row line (ADR-0046 metadata: location, class, objective, play time, seed).</summary>
        public string RowLine(SaveSlotState row, int index)
        {
            if (row == null) return "?";
            string slotId = row.SlotId;
            string displayName = row.DisplayName;
            if (row.Frozen) return slotId + " | DEAD -- " + Epitaph(slotId);
            if (row.IsManual() && displayName.Length == 0 && !RowHasPayload(row))
            {
                string emptyVerbText = "";
                if (index == RowIndex && PendingVerb.Length != 0) emptyVerbText = " | verb=" + PendingVerb;
                return slotId + " -- empty" + emptyVerbText;
            }
            string verbText = "";
            if (index == RowIndex && PendingVerb.Length != 0)
            {
                verbText = " | verb=" + PendingVerb;
                if (PendingDeleteSlotId == slotId && PendingVerb == VerbDelete) verbText = " | verb=Delete (confirm again to delete)";
            }
            string shownName = displayName.Length != 0 ? displayName : slotId;
            string loc = row.CurrentLocation.Length != 0 ? row.CurrentLocation : "?";
            string cls = row.PlayerClass.Length != 0 ? row.PlayerClass : "?";
            return slotId + " | " + shownName + " | " + loc + " | " + cls + " | obj=" + GdString.FormatInt(row.ObjectiveSequence)
                + " | " + FormatPlayTime(row.PlayTimeSeconds) + " | seed=" + GdString.FormatInt(row.SynapticSeaSeed) + verbText;
        }

        public string Epitaph(string slotId) => V.Str(_deaths.LoadEpitaph(slotId).Get("epitaph", "unknown"));

        /// <summary>Display lines: "SAVE / LOAD", then one per row with a "> " cursor prefix.</summary>
        public List<string> Lines()
        {
            var lines = new List<string> { "SAVE / LOAD" };
            List<SaveSlotState> rows = Rows();
            if (rows.Count == 0)
            {
                lines.Add("(no save slots)");
                return lines;
            }
            ClampIndex(rows.Count);
            for (int i = 0; i < rows.Count; i++) lines.Add((i == RowIndex ? "> " : "  ") + RowLine(rows[i], i));
            return lines;
        }

        void ClampIndex(int count)
        {
            if (RowIndex >= count) RowIndex = count - 1;
            if (RowIndex < 0) RowIndex = 0;
        }

        public void MoveSelection(int direction)
        {
            List<SaveSlotState> rows = Rows();
            if (rows.Count == 0) return;
            RowIndex = (int)GdMath.Clampi(RowIndex + direction, 0, rows.Count - 1);
            PendingVerb = "";
            PendingDeleteSlotId = "";
            Changed?.Invoke();
        }

        /// <summary>Selects a row directly (mouse click / list focus), clearing any armed verb when it moves.</summary>
        public void SelectRow(int index)
        {
            List<SaveSlotState> rows = Rows();
            if (rows.Count == 0) return;
            int clamped = (int)GdMath.Clampi(index, 0, rows.Count - 1);
            if (clamped == RowIndex) return;
            RowIndex = clamped;
            PendingVerb = "";
            PendingDeleteSlotId = "";
            Changed?.Invoke();
        }

        public void CycleVerb(int direction)
        {
            List<SaveSlotState> rows = Rows();
            if (rows.Count == 0 || RowIndex >= rows.Count) return;
            SaveSlotState row = rows[RowIndex];
            if (row.Frozen) return;
            List<string> verbs = ValidVerbsForRow(row);
            if (verbs.Count == 0) return;
            int current = verbs.IndexOf(PendingVerb);
            if (current < 0)
                current = direction < 0 ? verbs.Count - 1 : 0;
            else
                current = (int)GdMath.Wrapi(current + direction, 0, verbs.Count);
            PendingVerb = verbs[current];
            PendingDeleteSlotId = "";
            Changed?.Invoke();
        }

        /// <summary>Arms <paramref name="verb"/> on the current row then confirms (pointer path: one click acts,
        /// except Delete, which still needs its second confirm). Returns the same dict shape as <see cref="Confirm"/>.</summary>
        public GdDict ConfirmVerb(string verb)
        {
            List<SaveSlotState> rows = Rows();
            if (rows.Count == 0 || RowIndex >= rows.Count) return Result("none", false, "");
            SaveSlotState row = rows[RowIndex];
            if (!row.Frozen && ValidVerbsForRow(row).Contains(verb) && PendingVerb != verb)
            {
                PendingVerb = verb;
                if (verb != VerbDelete) PendingDeleteSlotId = "";
            }
            return Confirm();
        }

        /// <summary>
        /// Slot-screen confirm. Returns {screen:"save_load", action, ok, detail}. Actions: none, arm, delete_armed,
        /// delete, save, load, load_world. A Load's RunSnapshot is left in <see cref="LastLoadedSnapshot"/> for the
        /// session to apply.
        /// </summary>
        public GdDict Confirm()
        {
            List<SaveSlotState> rows = Rows();
            if (rows.Count == 0 || RowIndex >= rows.Count) return Result("none", false, "");
            SaveSlotState row = rows[RowIndex];
            string slotId = row.SlotId;
            if (row.Frozen) return Result("none", false, slotId);
            List<string> verbs = ValidVerbsForRow(row);
            if (verbs.Count == 0) return Result("none", false, slotId);
            if (PendingVerb.Length == 0)
            {
                PendingVerb = verbs[0];
                Changed?.Invoke();
                return Result("arm", true, slotId);
            }
            string verb = PendingVerb;
            if (verb == VerbDelete)
            {
                if (PendingDeleteSlotId != slotId)
                {
                    PendingDeleteSlotId = slotId;
                    Changed?.Invoke();
                    return Result("delete_armed", true, slotId);
                }
                bool deleted = _menu.ConfirmDelete(slotId);
                PendingDeleteSlotId = "";
                PendingVerb = "";
                if (deleted) ReanchorRowIndex(slotId);
                Changed?.Invoke();
                return Result("delete", deleted, slotId);
            }
            if (verb == VerbSave)
            {
                if (DemoSaveRefused != null && DemoSaveRefused())
                {
                    PendingVerb = "";
                    Changed?.Invoke();
                    return Result("save", false, "demo_blocked");
                }
                string displayName = row.DisplayName.Length != 0 ? row.DisplayName : slotId;
                bool ok = false;
                if (SnapshotBuilder != null)
                {
                    RunSnapshot snap = SnapshotBuilder();
                    if (snap != null) ok = _menu.ConfirmSaveToSlot(slotId, snap, "manual", displayName);
                }
                PendingVerb = "";
                if (ok) ReanchorRowIndex(slotId);
                Changed?.Invoke();
                return Result("save", ok, slotId);
            }
            if (verb == VerbLoad)
            {
                if (row.IsWorld())
                {
                    PendingVerb = "";
                    Changed?.Invoke();
                    return Result("load_world", true, slotId);
                }
                RunSnapshot snapshot = _menu.SelectSlotForLoad(slotId);
                LastLoadedSnapshot = snapshot;
                PendingVerb = "";
                Changed?.Invoke();
                return Result("load", snapshot != null, slotId);
            }
            return Result("none", false, slotId);
        }

        /// <summary>Cursor-drift fix: re-find the acted-on slot in the refreshed rows (a save jumps to the front; a
        /// delete leaves a synthesized empty row at the tail).</summary>
        void ReanchorRowIndex(string slotId)
        {
            List<SaveSlotState> refreshed = Rows();
            for (int i = 0; i < refreshed.Count; i++)
            {
                if (refreshed[i] != null && refreshed[i].SlotId == slotId)
                {
                    RowIndex = i;
                    return;
                }
            }
            RowIndex = (int)GdMath.Clampi(RowIndex, 0, Math.Max(0, refreshed.Count - 1));
        }

        static GdDict Result(string action, bool ok, string detail) =>
            new GdDict { { "screen", "save_load" }, { "action", action }, { "ok", ok }, { "detail", detail } };
    }
}
