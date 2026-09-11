"""Generate SynapticSea.inputactions from the Godot action list.

Action names equal the Godot InputMap ids (scripts/procgen/playable_generated_ship.gd
ensure_default_input_actions + the coordinator's hard-coded hotbar keys) so data/ui/input_glyphs.json
and ported code keep resolving them. Movement stays four analog buttons (not a Vector2) because the
Godot player reads Input.get_action_strength per direction; stick directions give the same analog strength.
IDs are uuid5-derived so regeneration is stable.

    python tools/python/gen_input_actions.py
"""
import json
import os
import uuid

NS = uuid.UUID("4f6b1d0e-5c2a-4f5e-9b1e-7a2d3c4b5a69")
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "SynapticSea", "Assets", "Content", "Input", "SynapticSea.inputactions")

KB = "KeyboardMouse"
GP = "Gamepad"


def gid(*parts):
    return str(uuid.uuid5(NS, "/".join(parts)))


# (action, type, keyboard paths, gamepad paths)
MAPS = {
    "Player": [
        ("move_forward", "Value", ["<Keyboard>/w", "<Keyboard>/upArrow"], ["<Gamepad>/leftStick/up"]),
        ("move_back", "Value", ["<Keyboard>/s", "<Keyboard>/downArrow"], ["<Gamepad>/leftStick/down"]),
        ("move_left", "Value", ["<Keyboard>/a", "<Keyboard>/leftArrow"], ["<Gamepad>/leftStick/left"]),
        ("move_right", "Value", ["<Keyboard>/d", "<Keyboard>/rightArrow"], ["<Gamepad>/leftStick/right"]),
        ("interact", "Button", ["<Keyboard>/e", "<Keyboard>/enter", "<Keyboard>/space", "<Keyboard>/numpadEnter"], ["<Gamepad>/buttonSouth"]),
        ("attack_primary", "Button", ["<Keyboard>/f"], ["<Gamepad>/rightTrigger"]),
        ("reload_weapon", "Button", ["<Keyboard>/r"], ["<Gamepad>/buttonWest"]),
        ("crouch", "Button", ["<Keyboard>/ctrl"], ["<Gamepad>/buttonEast"]),
        ("field_craft", "Button", ["<Keyboard>/c"], ["<Gamepad>/dpad/down"]),
        ("hotbar_1", "Button", ["<Keyboard>/1"], ["<Gamepad>/dpad/left"]),
        ("hotbar_2", "Button", ["<Keyboard>/2"], ["<Gamepad>/dpad/up"]),
        ("hotbar_3", "Button", ["<Keyboard>/3"], ["<Gamepad>/dpad/right"]),
    ],
    "Panels": [
        ("toggle_inventory", "Button", ["<Keyboard>/i"], ["<Gamepad>/buttonNorth"]),
        ("toggle_scanner", "Button", ["<Keyboard>/tab"], ["<Gamepad>/leftShoulder"]),
        ("toggle_ship_mod", "Button", ["<Keyboard>/u"], []),
        ("toggle_wounds", "Button", ["<Keyboard>/o"], []),
        ("ui_open_map", "Button", ["<Keyboard>/m"], ["<Gamepad>/rightShoulder"]),
        ("ui_open_codex", "Button", ["<Keyboard>/f1"], ["<Gamepad>/select"]),
        ("ui_pause", "Button", ["<Keyboard>/escape"], ["<Gamepad>/start"]),
        ("save_run", "Button", ["<Keyboard>/f5"], []),
        ("quicksave_run", "Button", ["<Keyboard>/f6"], []),
        ("load_run", "Button", ["<Keyboard>/f9"], []),
    ],
    "Menu": [
        ("ui_up", "Button", ["<Keyboard>/upArrow"], ["<Gamepad>/dpad/up", "<Gamepad>/leftStick/up"]),
        ("ui_down", "Button", ["<Keyboard>/downArrow"], ["<Gamepad>/dpad/down", "<Gamepad>/leftStick/down"]),
        ("ui_left", "Button", ["<Keyboard>/leftArrow"], ["<Gamepad>/dpad/left", "<Gamepad>/leftStick/left"]),
        ("ui_right", "Button", ["<Keyboard>/rightArrow"], ["<Gamepad>/dpad/right", "<Gamepad>/leftStick/right"]),
        ("ui_accept", "Button", ["<Keyboard>/enter", "<Keyboard>/space", "<Keyboard>/numpadEnter"], ["<Gamepad>/buttonSouth"]),
        ("ui_cancel", "Button", ["<Keyboard>/escape"], ["<Gamepad>/buttonEast"]),
    ],
}

# Standard map consumed by InputSystemUIInputModule (UI Toolkit / EventSystem).
UI_MAP = [
    ("Navigate", "PassThrough", "Vector2"),
    ("Submit", "Button", "Button"),
    ("Cancel", "Button", "Button"),
    ("Point", "PassThrough", "Vector2"),
    ("Click", "PassThrough", "Button"),
    ("ScrollWheel", "PassThrough", "Vector2"),
    ("MiddleClick", "PassThrough", "Button"),
    ("RightClick", "PassThrough", "Button"),
]
UI_BINDINGS = [
    ("Navigate", "<Gamepad>/leftStick", GP),
    ("Navigate", "<Gamepad>/dpad", GP),
    ("Navigate", "<Keyboard>/arrowKeys", KB),
    ("Submit", "<Keyboard>/enter", KB),
    ("Submit", "<Keyboard>/space", KB),
    ("Submit", "<Gamepad>/buttonSouth", GP),
    ("Cancel", "<Keyboard>/escape", KB),
    ("Cancel", "<Gamepad>/buttonEast", GP),
    ("Point", "<Mouse>/position", KB),
    ("Click", "<Mouse>/leftButton", KB),
    ("ScrollWheel", "<Mouse>/scroll", KB),
    ("MiddleClick", "<Mouse>/middleButton", KB),
    ("RightClick", "<Mouse>/rightButton", KB),
]


def action_entry(map_name, name, kind, control):
    return {
        "name": name,
        "type": kind,
        "id": gid(map_name, name),
        "expectedControlType": control,
        "processors": "",
        "interactions": "",
        "initialStateCheck": kind != "Button",
    }


def binding(map_name, action, path, group, index):
    return {
        "name": "",
        "id": gid(map_name, action, path, str(index)),
        "path": path,
        "interactions": "",
        "processors": "",
        "groups": group,
        "action": action,
        "isComposite": False,
        "isPartOfComposite": False,
    }


def build():
    maps = []
    for map_name, actions in MAPS.items():
        acts, binds = [], []
        for name, kind, keys, pads in actions:
            acts.append(action_entry(map_name, name, kind, "Axis" if kind == "Value" else "Button"))
            for i, p in enumerate(keys):
                binds.append(binding(map_name, name, p, KB, i))
            for i, p in enumerate(pads):
                binds.append(binding(map_name, name, p, GP, 100 + i))
        maps.append({"name": map_name, "id": gid(map_name), "actions": acts, "bindings": binds})

    ui_acts = [action_entry("UI", n, k, c) for n, k, c in UI_MAP]
    ui_binds = [binding("UI", a, p, g, i) for i, (a, p, g) in enumerate(UI_BINDINGS)]
    maps.append({"name": "UI", "id": gid("UI"), "actions": ui_acts, "bindings": ui_binds})

    return {
        "version": 1,
        "name": "SynapticSea",
        "maps": maps,
        "controlSchemes": [
            {"name": KB, "bindingGroup": KB, "devices": [
                {"devicePath": "<Keyboard>", "isOptional": False, "isOR": False},
                {"devicePath": "<Mouse>", "isOptional": True, "isOR": False}]},
            {"name": GP, "bindingGroup": GP, "devices": [
                {"devicePath": "<Gamepad>", "isOptional": False, "isOR": False}]},
        ],
    }


if __name__ == "__main__":
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(build(), fh, indent=4)
        fh.write("\n")
    total = sum(len(m["actions"]) for m in build()["maps"])
    print(f"INPUT ACTIONS PASS maps={len(build()['maps'])} actions={total} path={os.path.normpath(OUT)}")
