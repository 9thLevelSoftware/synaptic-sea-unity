using System;
using System.Collections.Generic;

namespace SynapticSea.UI
{
    /// <summary>The UI commands every surface understands (mapped from the Menu/Panels input maps and UI Toolkit navigation).</summary>
    public enum UiCommand
    {
        Up,
        Down,
        Left,
        Right,
        Accept,
        Cancel,
        Pause,
        OpenCodex,
    }

    /// <summary>Time/input contract of a surface (ui_presentation_program.md "Menus, inspection, and time").</summary>
    public enum SurfaceTime
    {
        /// <summary>Inspection: simulation live, all gameplay commands blocked, LIVE badge.</summary>
        Live,
        /// <summary>Pause/settings/records/save dialogs: simulation suspended, menu input only, PAUSED badge.</summary>
        Paused,
        /// <summary>Death/extraction results: existing terminal run state, result actions only.</summary>
        Terminal,
    }

    /// <summary>A surface on the <see cref="ModalStack"/>. Only the top-most consumer receives commands.</summary>
    public interface IInputConsumer
    {
        /// <summary>Stable id ("inventory", "pause_menu", "save_load", ...). Registered once per surface.</summary>
        string SurfaceId { get; }

        SurfaceTime Time { get; }

        /// <summary>Handles one command; true when consumed.</summary>
        bool Consume(UiCommand command);

        /// <summary>The stable token of the element that owns focus in this surface ("" when none).</summary>
        string CaptureFocusToken();

        /// <summary>Restores focus to the element carrying <paramref name="token"/> (or the surface's initial focus).</summary>
        void RestoreFocus(string token);

        /// <summary>True while another surface is above this one: it must not take input or focus.</summary>
        void SetCovered(bool covered);
    }

    /// <summary>
    /// The single UI input owner (REQ-UIP-005). Surfaces push on open and pop on close; the top-most consumes every
    /// command, so opening pause above inspection can never make two surfaces process one input. Covered surfaces are
    /// disabled. Popping restores the uncovered surface's focus from the token captured when it was covered, and
    /// swallows a still-held Accept so it cannot activate the restored view.
    /// </summary>
    public sealed class ModalStack
    {
        readonly List<IInputConsumer> _stack = new List<IInputConsumer>();
        readonly Dictionary<IInputConsumer, string> _coveredTokens = new Dictionary<IInputConsumer, string>();
        bool _suppressAccept;

        /// <summary>Raised after any push/pop (session: re-evaluate gameplay input gating and simulation pause).</summary>
        public event Action Changed;

        public int Count => _stack.Count;
        public bool IsEmpty => _stack.Count == 0;
        public IInputConsumer Top => _stack.Count == 0 ? null : _stack[_stack.Count - 1];
        public IReadOnlyList<IInputConsumer> Surfaces => _stack;

        /// <summary>Every gameplay action (move, attack, reload, crouch, hotbar/use, field craft, held work/interact,
        /// dev save/load, competing panel toggles) is denied while any surface is open.</summary>
        public bool BlocksGameplay => _stack.Count > 0;

        /// <summary>True when any open surface suspends simulation (pause stack or terminal results).</summary>
        public bool SimulationPaused
        {
            get
            {
                foreach (IInputConsumer c in _stack)
                {
                    if (c.Time != SurfaceTime.Live) return true;
                }
                return false;
            }
        }

        public bool Contains(IInputConsumer consumer) => _stack.Contains(consumer);

        public bool IsTop(IInputConsumer consumer) => consumer != null && Top == consumer;

        /// <summary>Opens <paramref name="consumer"/> above everything; idempotent.</summary>
        public void Push(IInputConsumer consumer)
        {
            if (consumer == null || _stack.Contains(consumer)) return;
            IInputConsumer below = Top;
            if (below != null)
            {
                _coveredTokens[below] = below.CaptureFocusToken();
                below.SetCovered(true);
            }
            _stack.Add(consumer);
            consumer.SetCovered(false);
            consumer.RestoreFocus("");
            Changed?.Invoke();
        }

        /// <summary>Closes <paramref name="consumer"/>. When it was on top, the surface below is uncovered and its focus
        /// restored; a held Accept is swallowed until released. Returns false when it was not open.</summary>
        public bool Pop(IInputConsumer consumer)
        {
            int index = _stack.IndexOf(consumer);
            if (index < 0) return false;
            bool wasTop = index == _stack.Count - 1;
            _stack.RemoveAt(index);
            _coveredTokens.Remove(consumer);
            if (wasTop)
            {
                IInputConsumer uncovered = Top;
                if (uncovered != null)
                {
                    uncovered.SetCovered(false);
                    _coveredTokens.TryGetValue(uncovered, out string token);
                    _coveredTokens.Remove(uncovered);
                    uncovered.RestoreFocus(token ?? "");
                }
                _suppressAccept = true;
            }
            Changed?.Invoke();
            return true;
        }

        /// <summary>Closes everything above <paramref name="consumer"/> (exclusive).</summary>
        public void PopAbove(IInputConsumer consumer)
        {
            int index = _stack.IndexOf(consumer);
            if (index < 0) return;
            while (_stack.Count - 1 > index) Pop(Top);
        }

        /// <summary>Routes a command to the top-most surface only. A held Accept after a pop is swallowed.</summary>
        public bool Dispatch(UiCommand command)
        {
            if (_stack.Count == 0) return false;
            if (command == UiCommand.Accept && _suppressAccept) return true;
            if (command != UiCommand.Accept) _suppressAccept = false;
            return Top.Consume(command);
        }

        /// <summary>The input router calls this when the confirm control is released.</summary>
        public void NotifyAcceptReleased() => _suppressAccept = false;

        public bool AcceptSuppressed => _suppressAccept;

        /// <summary>Gameplay input gate: every gameplay action is denied by default while a surface is open.</summary>
        public bool IsGameplayActionAllowed(string actionId) => _stack.Count == 0;

        /// <summary>A panel toggle may open its own panel from play, or close it when it is on top; any other surface
        /// on top blocks it ("competing panel toggles").</summary>
        public bool IsPanelToggleAllowed(string surfaceId) => _stack.Count == 0 || Top.SurfaceId == surfaceId;
    }
}
