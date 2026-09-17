// Ported from scripts/ui/save_load_menu.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Systems;

namespace SynapticSea.UI.Presenters
{
    /// <summary>
    /// The slice of <see cref="SaveLoadService"/> the save/load menu calls. Godot bound any duck-typed object; the
    /// interface keeps that seam (stubs in tests, the real service via <see cref="SaveLoadMenu.Bind(SaveLoadService)"/>).
    /// </summary>
    public interface ISaveSlotService
    {
        List<SaveSlotState> ListSlots();
        RunSnapshot LoadFromSlot(string slotId);
        bool SaveToSlot(string slotId, RunSnapshot snapshot, string slotKind, bool isQuicksave, string displayName);
        bool DeleteSlot(string slotId);
    }

    /// <summary>
    /// Save/Load menu UI seam (ADR-0031, REQ-SL-011). Pure: reads the service's slot list and dispatches the player's
    /// choices. Headlessly testable because it never touches a view.
    /// </summary>
    public sealed class SaveLoadMenu
    {
        sealed class ServiceAdapter : ISaveSlotService
        {
            readonly SaveLoadService _service;
            public ServiceAdapter(SaveLoadService service) => _service = service;
            public List<SaveSlotState> ListSlots() => _service.ListSlots();
            public RunSnapshot LoadFromSlot(string slotId) => _service.LoadFromSlot(slotId);
            public bool SaveToSlot(string slotId, RunSnapshot snapshot, string slotKind, bool isQuicksave, string displayName) =>
                _service.SaveToSlot(slotId, snapshot, slotKind, isQuicksave, displayName);
            public bool DeleteSlot(string slotId) => _service.DeleteSlot(slotId);
        }

        ISaveSlotService _service;

        public void Bind(ISaveSlotService service) => _service = service;

        public void Bind(SaveLoadService service) => _service = service == null ? null : new ServiceAdapter(service);

        public bool IsBound => _service != null;

        /// <summary>Slot rows sorted by saved_at desc (the service's order); empty when unbound.</summary>
        public List<SaveSlotState> Refresh() => _service == null ? new List<SaveSlotState>() : _service.ListSlots();

        public RunSnapshot SelectSlotForLoad(string slotId) => _service?.LoadFromSlot(slotId);

        public bool ConfirmSaveToSlot(string slotId, RunSnapshot snapshot, string slotKind, string displayName) =>
            _service != null && _service.SaveToSlot(slotId, snapshot, slotKind, false, displayName);

        public bool ConfirmQuicksave(RunSnapshot snapshot) =>
            _service != null && _service.SaveToSlot("quicksave", snapshot, "quick", true, "Quicksave");

        public bool ConfirmDelete(string slotId) => _service != null && _service.DeleteSlot(slotId);

        /// <summary>The row for the active autosave, or null.</summary>
        public SaveSlotState ActiveAutosaveRow()
        {
            foreach (SaveSlotState row in Refresh())
            {
                if (row != null && row.SlotId == "autosave_active") return row;
            }
            return null;
        }
    }
}
