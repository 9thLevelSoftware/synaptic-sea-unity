using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests.Unity
{
    /// <summary>Points CatalogRegistry at StreamingAssets and user storage at memory for each UI test.</summary>
    public abstract class UiTestBase
    {
        IResourceReader _prevResources;
        IStorage _prevStorage;
        protected MemoryStorage Storage;

        [SetUp]
        public void UiBaseSetUp()
        {
            _prevResources = CoreServices.Resources;
            _prevStorage = CoreServices.UserStorage;
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            Storage = new MemoryStorage();
            CoreServices.UserStorage = Storage;
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void UiBaseTearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Resources = _prevResources;
            CoreServices.UserStorage = _prevStorage;
        }

        /// <summary>Every Label text under <paramref name="root"/>, joined by newlines (what a player can read).</summary>
        protected static string VisibleText(VisualElement root) =>
            string.Join("\n", root.Query<Label>().ToList().Where(IsDisplayed).Select(l => l.text));

        static bool IsDisplayed(VisualElement el)
        {
            for (VisualElement e = el; e != null; e = e.parent)
            {
                if (e.style.display.value == DisplayStyle.None) return false;
            }
            return true;
        }
    }

    /// <summary>Records UI SFX and serves bus volumes / voice logs without an AudioSource.</summary>
    public sealed class FakeUiAudio : IUiAudio
    {
        public readonly List<string> Sfx = new List<string>();
        public readonly Dictionary<string, double> Volumes = new Dictionary<string, double>();
        public readonly Dictionary<string, bool> Mutes = new Dictionary<string, bool>();
        public AudioLog AudioLog { get; } = new AudioLog();
        public string CurrentVoiceLogId { get; private set; } = "";

        public bool PlaySfx(string eventId)
        {
            Sfx.Add(eventId);
            return true;
        }

        public double GetBusVolume(string busId) => Volumes.TryGetValue(busId, out double v) ? v : -6.0;
        public bool SetBusVolume(string busId, double volumeDb)
        {
            Volumes[busId] = volumeDb;
            return true;
        }

        public bool IsBusMuted(string busId) => Mutes.TryGetValue(busId, out bool m) && m;
        public bool SetBusMuted(string busId, bool muted)
        {
            Mutes[busId] = muted;
            return true;
        }

        public bool PlayVoiceLog(string entryId)
        {
            if (!AudioLog.HasEntry(entryId)) return false;
            CurrentVoiceLogId = entryId;
            return true;
        }

        public void StopVoiceLog() => CurrentVoiceLogId = "";
    }

    /// <summary>
    /// A real runtime UI Toolkit panel (UIDocument over a RenderTexture-backed copy of the project PanelSettings, so the
    /// SynapticSea.tss theme and its USS apply) for layout, focus and navigation-event tests in EditMode.
    /// </summary>
    public sealed class UiHarness : IDisposable
    {
        public const string HudPanelSettings = "Assets/Content/UI/PanelSettings_HUD.asset";
        public const string MenuPanelSettings = "Assets/Content/UI/PanelSettings_Menu.asset";

        readonly GameObject _go;
        readonly PanelSettings _settings;
        readonly RenderTexture _texture;
        public readonly UIDocument Document;

        public UiHarness(int width = 1280, int height = 720, string panelSettingsPath = MenuPanelSettings)
        {
            var asset = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
            Assert.IsNotNull(asset, panelSettingsPath);
            _settings = Object.Instantiate(asset);
            _texture = new RenderTexture(width, height, 0);
            _settings.targetTexture = _texture;
            _go = new GameObject("ui-harness");
            Document = _go.AddComponent<UIDocument>();
            Document.panelSettings = _settings;
        }

        public VisualElement Root => Document.rootVisualElement;

        public void Mount(VisualElement element) => Root.Add(element);

        /// <summary>Resolves styles and layout now (internal panel API; the same passes a frame runs).</summary>
        public void Layout()
        {
            IPanel panel = Root.panel;
            Assert.IsNotNull(panel, "harness root is not attached to a panel");
            Invoke(panel, "ApplyStyles");
            Invoke(panel, "ValidateLayout");
        }

        static void Invoke(object target, string method)
        {
            MethodInfo m = null;
            for (Type t = target.GetType(); t != null && m == null; t = t.BaseType)
                m = t.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            Assert.IsNotNull(m, "panel method " + method);
            m.Invoke(target, null);
        }

        public VisualElement Focused => Root.panel?.focusController?.focusedElement as VisualElement;

        public static void Navigate(VisualElement target, NavigationMoveEvent.Direction direction)
        {
            using (var e = NavigationMoveEvent.GetPooled(direction))
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        public static void Submit(VisualElement target)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        public static void Cancel(VisualElement target)
        {
            using (var e = NavigationCancelEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        public void Dispose()
        {
            Object.DestroyImmediate(_go);
            Object.DestroyImmediate(_settings);
            _texture.Release();
            Object.DestroyImmediate(_texture);
        }
    }
}
