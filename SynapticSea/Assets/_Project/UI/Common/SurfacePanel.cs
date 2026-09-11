using System;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Base for every inspection, pause-stack, and terminal surface. Provides the shared frame (header with title and
    /// LIVE / PAUSED state badge, body, action bar with an always-reachable Close/Back), the <see cref="IInputConsumer"/>
    /// contract, token-based focus capture/restore, and covered-state handling. Subclasses keep their Godot logic and
    /// render into <see cref="Body"/>.
    /// </summary>
    public abstract class SurfacePanel : VisualElement, IInputConsumer
    {
        public const string BadgeLiveText = "● LIVE";
        public const string BadgePausedText = "■ PAUSED";

        protected readonly Label TitleLabel;
        protected readonly Label BadgeLabel;
        protected readonly VisualElement Header;
        protected readonly VisualElement Body;
        protected readonly VisualElement ActionBar;
        protected readonly Button CloseButton;
        protected readonly StatusLine StatusText;

        SurfaceTime _time;
        string _lastFocusToken = "";
        readonly Label _pauseHint;
        readonly Label _backGlyph;
        Func<string, string> _glyphs;

        /// <summary>
        /// Glyph source (input action id → chip text, e.g. "[B]"). The Back action shows its glyph chip; LIVE surfaces
        /// also show the pause path ("[Start] Pause") — spec: immediate close and pause paths from inspection.
        /// </summary>
        public void SetGlyphResolver(Func<string, string> glyphs)
        {
            _glyphs = glyphs;
            RefreshGlyphs();
        }

        public string BackGlyphText => _backGlyph.text;
        public string PauseHintText => _pauseHint.text;

        void RefreshGlyphs()
        {
            string back = _glyphs?.Invoke("ui_cancel") ?? "";
            _backGlyph.text = back;
            UiFactory.SetShown(_backGlyph, back.Length != 0);
            string pause = _time == SurfaceTime.Live ? _glyphs?.Invoke("ui_pause") ?? "" : "";
            _pauseHint.text = pause.Length != 0 ? pause + " Pause" : "";
            UiFactory.SetShown(_pauseHint, _pauseHint.text.Length != 0);
        }

        protected SurfacePanel(string title, SurfaceTime time)
        {
            AddToClassList(UiClasses.Surface);
            AddToClassList(UiClasses.Panel);
            focusable = false;

            Header = UiFactory.Box(UiClasses.SurfaceHeader);
            TitleLabel = UiFactory.Text(title, UiClasses.SurfaceTitle, UiClasses.LabelHeading);
            BadgeLabel = UiFactory.Text("", UiClasses.Badge);
            Header.Add(TitleLabel);
            Header.Add(BadgeLabel);
            Add(Header);

            Body = UiFactory.Box(UiClasses.SurfaceBody);
            Add(Body);

            StatusText = new StatusLine();
            Add(StatusText);

            ActionBar = UiFactory.Box(UiClasses.SurfaceActions);
            _pauseHint = UiFactory.Text("", "ss-surface__hint", UiClasses.LabelSecondary);
            _backGlyph = GlyphChips.Chip("");
            CloseButton = UiFactory.Button("", () => RequestClose(), "action:close");
            ActionBar.Add(_pauseHint);
            ActionBar.Add(_backGlyph);
            ActionBar.Add(CloseButton);
            Add(ActionBar);

            SetTime(time);
            RegisterCallback<NavigationCancelEvent>(e =>
            {
                UiFocus.Consume(e, this);
                Consume(UiCommand.Cancel);
            });
            UiFactory.SetShown(this, false);
        }

        public abstract string SurfaceId { get; }

        public SurfaceTime Time => _time;

        public string Title => TitleLabel.text;
        public string BadgeText => BadgeLabel.text;
        public string StatusDisplay => StatusText.text;
        public Button CloseAction => CloseButton;

        /// <summary>Sets the time contract and its visible badge (LIVE for inspection, PAUSED for the pause stack).</summary>
        protected void SetTime(SurfaceTime time)
        {
            _time = time;
            EnableInClassList(UiClasses.SurfaceLive, time == SurfaceTime.Live);
            EnableInClassList(UiClasses.SurfacePaused, time == SurfaceTime.Paused);
            EnableInClassList(UiClasses.SurfaceTerminal, time == SurfaceTime.Terminal);
            BadgeLabel.text = time == SurfaceTime.Live ? BadgeLiveText : time == SurfaceTime.Paused ? BadgePausedText : "";
            BadgeLabel.EnableInClassList(UiClasses.BadgeLive, time == SurfaceTime.Live);
            BadgeLabel.EnableInClassList(UiClasses.BadgePaused, time == SurfaceTime.Paused);
            UiFactory.SetShown(BadgeLabel, BadgeLabel.text.Length != 0);
            CloseButton.text = time == SurfaceTime.Live ? "Close" : "Back";
            if (_backGlyph != null) RefreshGlyphs();
        }

        protected void SetTitle(string title) => TitleLabel.text = title ?? "";

        /// <summary>Shows the surface view (logical open state stays with the subclass, as in Godot).</summary>
        protected void SetViewVisible(bool shown) => UiFactory.SetShown(this, shown);

        public bool IsViewVisible => UiFactory.IsShown(this);

        public virtual bool Consume(UiCommand command)
        {
            if (command == UiCommand.Cancel)
            {
                RequestClose();
                return true;
            }
            return OnCommand(command);
        }

        /// <summary>Directional/accept handling for the logical (non-focus) input path. Default: not consumed.</summary>
        protected virtual bool OnCommand(UiCommand command) => false;

        /// <summary>Close / Back (Godot <c>close()</c> or <c>dismiss()</c>).</summary>
        protected abstract void RequestClose();

        public virtual string CaptureFocusToken()
        {
            VisualElement focused = UiFocus.FocusedWithin(this);
            if (focused != null)
            {
                string token = UiFocus.TokenOf(focused);
                if (token.Length != 0) _lastFocusToken = token;
            }
            return _lastFocusToken;
        }

        public virtual void RestoreFocus(string token)
        {
            VisualElement target = UiFocus.FindByToken(this, token);
            if (target == null || !target.enabledInHierarchy) target = InitialFocusElement();
            if (target == null) return;
            _lastFocusToken = UiFocus.TokenOf(target);
            UiFocus.Focus(target);
        }

        public virtual void SetCovered(bool covered)
        {
            if (covered) CaptureFocusToken();
            SetEnabled(!covered);
            EnableInClassList(UiClasses.SurfaceCovered, covered);
        }

        /// <summary>Explicit initial focus. Default: the first enabled focusable in the body, else Close/Back.</summary>
        protected virtual VisualElement InitialFocusElement()
        {
            VisualElement first = Body.Query<VisualElement>().Where(e => e.focusable && e.enabledInHierarchy && UiFactory.IsShown(e) && UiFocus.TokenOf(e).Length != 0).First();
            return first ?? CloseButton;
        }

        /// <summary>The last focus token this surface recorded (works headless).</summary>
        public string LastFocusToken => _lastFocusToken;

        protected void RememberFocus(string token)
        {
            if (!string.IsNullOrEmpty(token)) _lastFocusToken = token;
        }

        protected static SelectableList.Item Item(string id, string text, string detail = "", Severity severity = Severity.None, string chip = "")
            => new SelectableList.Item { Id = id, Text = text, Detail = detail, Severity = severity, Chip = chip };
    }
}
