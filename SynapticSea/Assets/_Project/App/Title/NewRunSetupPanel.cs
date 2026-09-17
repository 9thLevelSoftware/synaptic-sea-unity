// Unity-port addition (plan C1): the title's New Run setup. Godot's title started a run with the fixed default start
// (title_main.gd _instantiate_gameplay). Milestone A New Run boots golden coherent_ship_001 for the slice defaults
// (seed 17 / breach_field / standard); a non-slice choice fails closed. The setup still asks for biome, difficulty and seed.
using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine.UIElements;

namespace SynapticSea.App
{
    /// <summary>
    /// PAUSED title submenu built like <see cref="MenuPanel"/> (44 px focusable rows, selector rows with ◀ value ▶): Biome
    /// and Difficulty cycle with Left/Right, Seed steps with Left/Right and opens an inline numeric field on Accept (keyboard
    /// entry; Enter commits, Back cancels), Randomize seed rolls a new seed, Start fills a <see cref="RunLaunchRequest"/>.
    /// Back closes the submenu; the modal stack restores focus to the New Run row below. Input arrives either as UI Toolkit
    /// navigation events on the focused row (gamepad / keyboard through the EventSystem) or as <see cref="Consume"/> from
    /// the title's router when nothing owns focus.
    /// </summary>
    public sealed class NewRunSetupPanel : SurfacePanel
    {
        public const string Id = "new_run_setup";
        public const string BiomesDir = "res://data/procgen/biomes";
        public const string DifficultyDir = "res://data/procgen/difficulty";
        public const string RowBiome = "biome";
        public const string RowDifficulty = "difficulty";
        public const string RowSeed = "seed";
        public const string RowRandomize = "randomize";
        public const string RowStart = "start";

        /// <summary>Godot difficulty order (<c>title_main.gd</c> cycled standard → hardened → deep_dive).</summary>
        public static readonly IReadOnlyList<string> DifficultyOrder = new[] { DifficultyProfile.STANDARD_ID, "hardened", "deep_dive" };

        static readonly string[] RowIds = { RowBiome, RowDifficulty, RowSeed, RowRandomize, RowStart };

        /// <summary>Random seed source (tests pin it).</summary>
        public static Func<long> RandomSeed = () => UnityEngine.Random.Range(1, int.MaxValue);

        public event Action<RunLaunchRequest> StartRequested;
        public event Action BackRequested;

        readonly List<string> _biomes;
        readonly List<string> _difficulties;
        readonly List<VisualElement> _rows = new List<VisualElement>();
        readonly TextField _seedField;
        int _biomeIndex;
        int _difficultyIndex;
        int _focusIndex;
        long _seed;

        public NewRunSetupPanel(IEnumerable<string> biomeIds, IEnumerable<string> difficultyIds, string biomeId, string difficultyId, long seed)
            : base("NEW RUN", SurfaceTime.Paused)
        {
            AddToClassList("ss-menu");
            AddToClassList("ss-new-run-setup");
            _biomes = new List<string>(biomeIds ?? Array.Empty<string>());
            _difficulties = new List<string>(difficultyIds ?? Array.Empty<string>());
            if (_difficulties.Count == 0) _difficulties.Add(DifficultyProfile.STANDARD_ID);
            _biomeIndex = Math.Max(0, _biomes.IndexOf(biomeId ?? ""));
            _difficultyIndex = Math.Max(0, _difficulties.IndexOf(difficultyId ?? ""));
            _seed = Math.Max(0, seed);

            var rowsBox = UiFactory.Box("ss-menu__rows");
            Body.Add(rowsBox);
            foreach (string id in RowIds)
            {
                VisualElement row = BuildRow(id);
                _rows.Add(row);
                rowsBox.Add(row);
            }
            _seedField = new TextField { name = "new-run-seed-field", maxLength = 18, isDelayed = false };
            _seedField.AddToClassList("ss-new-run-setup__seed-field");
            _seedField.RegisterCallback<KeyDownEvent>(OnSeedFieldKey, TrickleDown.TrickleDown);
            _seedField.RegisterCallback<FocusOutEvent>(_ => CommitSeedEdit());
            UiFactory.SetShown(_seedField, false);
            RowElement(RowSeed).Add(_seedField);
            Refresh();
            SetViewVisible(true);
        }

        public override string SurfaceId => Id;

        public IReadOnlyList<string> BiomeIds => _biomes;
        public IReadOnlyList<string> DifficultyIds => _difficulties;
        public string BiomeId => _biomes.Count == 0 ? "" : _biomes[_biomeIndex];
        public string DifficultyId => _difficulties[_difficultyIndex];
        public long Seed => _seed;
        public int FocusIndex => _focusIndex;
        public string FocusedRowId => RowIds[_focusIndex];
        public bool IsEditingSeed { get; private set; }
        public TextField SeedField => _seedField;

        public static string TokenFor(string rowId) => "new_run:" + rowId;

        public VisualElement RowElement(string rowId) => _rows[Array.IndexOf(RowIds, rowId)];

        /// <summary>The value text a row shows ("" for command rows).</summary>
        public string RowValue(string rowId) => RowElement(rowId).Q<Label>(className: "ss-menu-row__value").text;

        /// <summary>Biome ids from <c>data/procgen/biomes/*.json</c> (sorted).</summary>
        public static List<string> LoadBiomeIds()
        {
            var ids = new List<string>();
            foreach (string file in ListData(BiomesDir))
            {
                if (ResPath.GetExtension(file) == "json") ids.Add(ResPath.GetBasename(file));
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        /// <summary>Difficulty ids from <c>data/procgen/difficulty/*.json</c>: Godot's order first, then any others sorted.</summary>
        public static List<string> LoadDifficultyIds()
        {
            var present = new List<string>();
            foreach (string file in ListData(DifficultyDir))
            {
                if (ResPath.GetExtension(file) == "json") present.Add(ResPath.GetBasename(file));
            }
            present.Sort(StringComparer.Ordinal);
            var ids = new List<string>();
            foreach (string id in DifficultyOrder)
            {
                if (present.Contains(id)) ids.Add(id);
            }
            foreach (string id in present)
            {
                if (!ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) ids.AddRange(DifficultyOrder);
            return ids;
        }

        static IReadOnlyList<string> ListData(string dir) =>
            CoreServices.Resources is FileSystemResourceReader reader ? reader.ListFiles(dir) : (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>The request this setup describes (class and settings are filled by the title).</summary>
        public RunLaunchRequest BuildRequest() => RunLaunchRequest.NewRun(_seed, BiomeId, DifficultyId);

        // ------------------------------------------------------------------ commands

        public override bool Consume(UiCommand command)
        {
            if (IsEditingSeed)
            {
                if (command == UiCommand.Cancel) CancelSeedEdit();
                else if (command == UiCommand.Accept) CommitSeedEdit();
                return true;
            }
            return base.Consume(command);
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                    MoveFocus(-1);
                    return true;
                case UiCommand.Down:
                    MoveFocus(1);
                    return true;
                case UiCommand.Left:
                    Cycle(-1);
                    return true;
                case UiCommand.Right:
                    Cycle(1);
                    return true;
                case UiCommand.Accept:
                    Activate();
                    return true;
            }
            return false;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _rows[_focusIndex];

        public override void RestoreFocus(string token)
        {
            int index = Array.IndexOf(RowIds, (token ?? "").StartsWith("new_run:", StringComparison.Ordinal) ? token.Substring(8) : "");
            if (index >= 0) _focusIndex = index;
            Refresh();
            base.RestoreFocus(TokenFor(RowIds[_focusIndex]));
        }

        void MoveFocus(int delta)
        {
            _focusIndex = (_focusIndex + delta + _rows.Count) % _rows.Count;
            Refresh();
            UiFocus.Focus(_rows[_focusIndex]);
        }

        /// <summary>Left/Right on the focused row: biome / difficulty wrap around, seed steps by one.</summary>
        public void Cycle(int direction)
        {
            switch (FocusedRowId)
            {
                case RowBiome:
                    if (_biomes.Count > 0) _biomeIndex = (_biomeIndex + direction + _biomes.Count) % _biomes.Count;
                    break;
                case RowDifficulty:
                    _difficultyIndex = (_difficultyIndex + direction + _difficulties.Count) % _difficulties.Count;
                    break;
                case RowSeed:
                    _seed = Math.Max(0, _seed + direction);
                    break;
                default:
                    return;
            }
            Refresh();
        }

        /// <summary>Accept on the focused row.</summary>
        public void Activate()
        {
            switch (FocusedRowId)
            {
                case RowBiome:
                case RowDifficulty:
                    Cycle(1);
                    break;
                case RowSeed:
                    BeginSeedEdit();
                    break;
                case RowRandomize:
                    SetSeed(RandomSeed());
                    break;
                case RowStart:
                    StartRequested?.Invoke(BuildRequest());
                    break;
            }
        }

        public void SetSeed(long seed)
        {
            _seed = Math.Max(0, seed);
            Refresh();
        }

        public void FocusRow(string rowId)
        {
            int index = Array.IndexOf(RowIds, rowId);
            if (index < 0) return;
            _focusIndex = index;
            Refresh();
            UiFocus.Focus(_rows[_focusIndex]);
        }

        // ------------------------------------------------------------------ seed entry

        public void BeginSeedEdit()
        {
            _focusIndex = Array.IndexOf(RowIds, RowSeed);
            IsEditingSeed = true;
            _seedField.SetValueWithoutNotify(_seed.ToString(CultureInfo.InvariantCulture));
            UiFactory.SetShown(_seedField, true);
            Refresh();
            _seedField.Focus();
        }

        /// <summary>Commits the typed seed: digits only (non-digits are dropped); an empty entry keeps the old seed.</summary>
        public void CommitSeedEdit()
        {
            if (!IsEditingSeed) return;
            string digits = "";
            foreach (char c in _seedField.value ?? "")
            {
                if (c >= '0' && c <= '9') digits += c;
            }
            if (digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)) _seed = parsed;
            else if (digits.Length > 0) StatusText.Set("Seed is too large", Severity.Caution);
            EndSeedEdit();
        }

        public void CancelSeedEdit()
        {
            if (!IsEditingSeed) return;
            EndSeedEdit();
        }

        void EndSeedEdit()
        {
            IsEditingSeed = false;
            UiFactory.SetShown(_seedField, false);
            Refresh();
            UiFocus.Focus(RowElement(RowSeed));
        }

        void OnSeedFieldKey(KeyDownEvent e)
        {
            if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter)
            {
                UiFocus.Consume(e, this);
                CommitSeedEdit();
            }
            else if (e.keyCode == UnityEngine.KeyCode.Escape)
            {
                UiFocus.Consume(e, this);
                CancelSeedEdit();
            }
        }

        // ------------------------------------------------------------------ view

        VisualElement BuildRow(string id)
        {
            var row = new VisualElement();
            row.AddToClassList(UiClasses.Row);
            row.AddToClassList(UiClasses.Focusable);
            row.AddToClassList("ss-menu-row");
            row.focusable = true;
            row.tabIndex = 0;
            UiFocus.Tag(row, TokenFor(id));
            var prev = UiFactory.Text("◀", "ss-menu-row__cycle");
            var label = UiFactory.Text(LabelFor(id), "ss-menu-row__label");
            var value = UiFactory.Text("", "ss-menu-row__value", UiClasses.LabelMono);
            var next = UiFactory.Text("▶", "ss-menu-row__cycle");
            row.Add(label);
            row.Add(prev);
            row.Add(value);
            row.Add(next);
            prev.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                FocusRow(id);
                Cycle(-1);
            });
            next.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                FocusRow(id);
                Cycle(1);
            });
            row.RegisterCallback<ClickEvent>(e =>
            {
                if (IsEditingSeed) return;
                FocusRow(id);
                Activate();
            });
            row.RegisterCallback<NavigationSubmitEvent>(e =>
            {
                if (IsEditingSeed) return;
                UiFocus.Consume(e, this);
                FocusRow(id);
                Activate();
            });
            row.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (IsEditingSeed) return;
                UiFocus.Consume(e, this);
                switch (e.direction)
                {
                    case NavigationMoveEvent.Direction.Up: MoveFocus(-1); break;
                    case NavigationMoveEvent.Direction.Down: MoveFocus(1); break;
                    case NavigationMoveEvent.Direction.Left: Cycle(-1); break;
                    case NavigationMoveEvent.Direction.Right: Cycle(1); break;
                }
                UiFocus.Focus(_rows[_focusIndex]);
            });
            row.RegisterCallback<FocusInEvent>(e =>
            {
                int index = _rows.IndexOf(row);
                if (index >= 0 && index != _focusIndex)
                {
                    _focusIndex = index;
                    Refresh();
                }
            });
            return row;
        }

        static string LabelFor(string id)
        {
            switch (id)
            {
                case RowBiome: return "Biome";
                case RowDifficulty: return "Difficulty";
                case RowSeed: return "Seed";
                case RowRandomize: return "Randomize seed";
                default: return "Start run";
            }
        }

        void Refresh()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                string id = RowIds[i];
                VisualElement row = _rows[i];
                bool cyclable = id == RowBiome || id == RowDifficulty || (id == RowSeed && !IsEditingSeed);
                string value = id == RowBiome ? BiomeId : id == RowDifficulty ? DifficultyId : id == RowSeed ? _seed.ToString(CultureInfo.InvariantCulture) : "";
                var valueLabel = row.Q<Label>(className: "ss-menu-row__value");
                valueLabel.text = value;
                UiFactory.SetShown(valueLabel, value.Length != 0 && !(id == RowSeed && IsEditingSeed));
                foreach (Label cycle in row.Query<Label>(className: "ss-menu-row__cycle").ToList()) UiFactory.SetShown(cycle, cyclable);
                row.EnableInClassList(UiClasses.RowSelected, i == _focusIndex);
            }
            RememberFocus(TokenFor(RowIds[_focusIndex]));
        }
    }
}
