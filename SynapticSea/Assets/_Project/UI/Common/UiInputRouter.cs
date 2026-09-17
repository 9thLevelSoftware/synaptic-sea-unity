using System;
using System.Collections.Generic;
using SynapticSea.Runtime.Input;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Maps the generated <see cref="SynapticSeaInput"/> maps onto the <see cref="ModalStack"/> so exactly one owner
    /// handles each press (REQ-UIP-005). Call <see cref="Tick"/> once per frame.
    ///
    /// Escape contract (spec "Menus, inspection, and time"): Escape/Back closes the topmost inspection/dialog; Start or an
    /// on-screen Pause action enters pause; from normal play Escape enters pause. Escape is bound to both ui_pause and
    /// ui_cancel, so a press is resolved once: with a surface open it is Back, otherwise Pause.
    ///
    /// Navigation: while UI Toolkit focus is inside the top surface, directional/submit/cancel presses are left to UI
    /// Toolkit's navigation events (which the surfaces handle); the router only dispatches them when no element owns
    /// focus, so a press is never processed by both paths.
    /// Gameplay: while any surface is open the Player map is disabled and dev save/load and competing panel toggles are
    /// refused.
    /// </summary>
    public sealed class UiInputRouter
    {
        /// <summary>Panels-map toggle action → surface id it opens.</summary>
        public static readonly IReadOnlyDictionary<string, string> ToggleSurfaceIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "toggle_inventory", "inventory" },
            { "toggle_scanner", "scanner" },
            { "toggle_ship_mod", "ship_mod" },
            { "toggle_wounds", "wounds" },
            { "ui_open_map", "chart" },
        };

        readonly SynapticSeaInput _input;
        readonly ModalStack _stack;
        readonly Func<UiCommand, bool> _globalHandler;

        /// <summary>True when UI Toolkit focus is inside the top surface (defaults to a focus-controller check).</summary>
        public Func<bool> ToolkitOwnsFocus;

        /// <summary>A panel toggle the session should honour (already filtered by the modal stack).</summary>
        public event Action<string> PanelToggleRequested;
        /// <summary>save_run / quicksave_run / load_run, only while no surface is open.</summary>
        public event Action<string> DevShortcutRequested;

        /// <param name="globalHandler">Pause / OpenCodex handler (normally <c>MenuCoordinator.HandleUiInput</c>).</param>
        public UiInputRouter(SynapticSeaInput input, ModalStack stack, Func<UiCommand, bool> globalHandler)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _stack = stack ?? throw new ArgumentNullException(nameof(stack));
            _globalHandler = globalHandler;
            ToolkitOwnsFocus = DefaultToolkitOwnsFocus;
        }

        bool DefaultToolkitOwnsFocus() => _stack.Top is VisualElement top && UiFocus.FocusedWithin(top) != null;

        public void Enable()
        {
            _input.Panels.Enable();
            _input.Menu.Enable();
            ApplyGameplayGate();
        }

        public void Disable()
        {
            _input.Panels.Disable();
            _input.Menu.Disable();
        }

        /// <summary>Player map on only while no surface is open.</summary>
        public void ApplyGameplayGate()
        {
            if (_stack.BlocksGameplay) _input.Player.Disable();
            else _input.Player.Enable();
        }

        /// <summary>Polls this frame's presses in priority order; each press resolves once.</summary>
        public void Tick()
        {
            var consumed = new HashSet<InputControl>();

            var menu = _input.Menu;
            if (menu.ui_accept.WasReleasedThisFrame()) _stack.NotifyAcceptReleased();

            var panels = _input.Panels;
            if (panels.ui_pause.WasPressedThisFrame())
            {
                InputControl control = panels.ui_pause.activeControl;
                if (control != null) consumed.Add(control);
                bool escape = control is KeyControl key && key.keyCode == Key.Escape;
                if (escape && !_stack.IsEmpty)
                {
                    if (!ToolkitOwnsFocus()) _stack.Dispatch(UiCommand.Cancel);
                }
                else
                {
                    Global(UiCommand.Pause);
                }
            }
            if (panels.ui_open_codex.WasPressedThisFrame()) Global(UiCommand.OpenCodex);

            foreach (var pair in ToggleSurfaceIds)
            {
                InputAction action = panels.Get().FindAction(pair.Key);
                if (action != null && action.WasPressedThisFrame() && _stack.IsPanelToggleAllowed(pair.Value))
                    PanelToggleRequested?.Invoke(pair.Key);
            }
            if (_stack.IsEmpty)
            {
                if (panels.save_run.WasPressedThisFrame()) DevShortcutRequested?.Invoke("save_run");
                if (panels.quicksave_run.WasPressedThisFrame()) DevShortcutRequested?.Invoke("quicksave_run");
                if (panels.load_run.WasPressedThisFrame()) DevShortcutRequested?.Invoke("load_run");
            }

            if (!_stack.IsEmpty && !ToolkitOwnsFocus())
            {
                Nav(menu.ui_up, UiCommand.Up, consumed);
                Nav(menu.ui_down, UiCommand.Down, consumed);
                Nav(menu.ui_left, UiCommand.Left, consumed);
                Nav(menu.ui_right, UiCommand.Right, consumed);
                Nav(menu.ui_accept, UiCommand.Accept, consumed);
                Nav(menu.ui_cancel, UiCommand.Cancel, consumed);
            }
            ApplyGameplayGate();
        }

        void Nav(InputAction action, UiCommand command, HashSet<InputControl> consumed)
        {
            if (!action.WasPressedThisFrame()) return;
            InputControl control = action.activeControl;
            if (control != null && !consumed.Add(control)) return;
            _stack.Dispatch(command);
        }

        bool Global(UiCommand command) => _globalHandler != null ? _globalHandler(command) : _stack.Dispatch(command);
    }
}
