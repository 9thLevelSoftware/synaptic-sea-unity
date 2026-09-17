using System;
using UnityEngine.InputSystem;

namespace SynapticSea.UI
{
    /// <summary>
    /// The device family the player used last (B4: glyph scheme "auto" follows the last-used device, not the connected
    /// gamepad count Godot read). Listens to <see cref="InputSystem.onActionChange"/> performed actions; a gamepad or
    /// joystick switches to gamepad glyphs, a keyboard or pointer switches back.
    /// </summary>
    public sealed class LastInputDevice : IDisposable
    {
        bool _listening;

        public bool IsGamepad { get; private set; }

        /// <summary>Raised with the new <see cref="IsGamepad"/> when the family changes.</summary>
        public event Action<bool> Changed;

        public LastInputDevice(bool listen = true)
        {
            if (!listen) return;
            InputSystem.onActionChange += OnActionChange;
            _listening = true;
        }

        /// <summary>The "connected joypads" stand-in <c>ControllerGlyphState.ResolveScheme</c> reads.</summary>
        public int JoypadCountForGlyphs() => IsGamepad ? 1 : 0;

        void OnActionChange(object action, InputActionChange change)
        {
            if (change != InputActionChange.ActionPerformed || !(action is InputAction a) || a.activeControl == null) return;
            Report(a.activeControl.device);
        }

        /// <summary>Records a device use (public for tests and for callers that see raw events).</summary>
        public void Report(InputDevice device)
        {
            if (device == null) return;
            bool gamepad;
            if (device is Gamepad || device is Joystick) gamepad = true;
            else if (device is Keyboard || device is Pointer) gamepad = false;
            else return;
            if (gamepad == IsGamepad) return;
            IsGamepad = gamepad;
            Changed?.Invoke(gamepad);
        }

        public void Dispose()
        {
            if (!_listening) return;
            InputSystem.onActionChange -= OnActionChange;
            _listening = false;
        }
    }
}
