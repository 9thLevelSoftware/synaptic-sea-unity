using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    public class InputActionTests
    {
        /// <summary>Glyph data uses the UI-facing names; the Godot InputMap (and this asset) use these ids.</summary>
        static readonly Dictionary<string, string> GlyphAliases = new Dictionary<string, string>
        {
            { "move_up", "move_forward" },
            { "move_down", "move_back" },
        };

        [Test]
        public void EveryGlyphActionExists()
        {
            var input = new SynapticSeaInput();
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "data", "ui", "input_glyphs.json");
                var glyphs = GdJson.ParseDict(File.ReadAllText(path));
                var missing = new List<string>();
                foreach (object entry in glyphs.GetArrayOrEmpty("actions"))
                {
                    string action = ((GdDict)entry).GetString("action");
                    if (GlyphAliases.TryGetValue(action, out string alias)) action = alias;
                    if (input.asset.FindAction(action) == null) missing.Add(action);
                }
                Assert.IsEmpty(missing, "glyph actions missing from SynapticSea.inputactions: " + string.Join(", ", missing));
            }
            finally
            {
                Object.DestroyImmediate(input.asset);
            }
        }

        [Test]
        public void PlayerActionsHaveKeyboardAndGamepadBindings()
        {
            var input = new SynapticSeaInput();
            try
            {
                foreach (InputAction action in input.Player.Get().actions)
                {
                    var groups = action.bindings.Select(b => b.groups ?? "").ToList();
                    Assert.IsTrue(groups.Any(g => g.Contains("KeyboardMouse")), $"{action.name} has no keyboard binding");
                    Assert.IsTrue(groups.Any(g => g.Contains("Gamepad")), $"{action.name} has no gamepad binding");
                }
            }
            finally
            {
                Object.DestroyImmediate(input.asset);
            }
        }

        [Test]
        public void GodotInputMapActionsAllExist()
        {
            string[] godotActions =
            {
                "move_forward", "move_back", "move_left", "move_right", "interact", "attack_primary", "reload_weapon",
                "crouch", "field_craft", "save_run", "quicksave_run", "load_run", "toggle_scanner", "toggle_inventory",
                "toggle_ship_mod", "toggle_wounds", "ui_up", "ui_down", "ui_left", "ui_right", "ui_accept", "ui_cancel",
                "ui_pause", "ui_open_codex", "ui_open_map", "hotbar_1", "hotbar_2", "hotbar_3",
            };
            var input = new SynapticSeaInput();
            try
            {
                var missing = godotActions.Where(a => input.asset.FindAction(a) == null).ToList();
                Assert.IsEmpty(missing, string.Join(", ", missing));
            }
            finally
            {
                Object.DestroyImmediate(input.asset);
            }
        }
    }
}
