using System;
using System.IO;
using System.Linq;
using SynapticSea.App;
using SynapticSea.UI;
using SynapticSea.UI.Presenters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.EditorTools.Scenes
{
    /// <summary>
    /// Captures the real Boot → Title flow in play mode: enters play mode on Boot.unity, waits for the title screen,
    /// points the menu PanelSettings at a RenderTexture for a few frames and writes artifacts/screenshots/&lt;stem&gt;.png.
    /// Needs a GPU, so run it in batch mode WITHOUT -nographics (and without -quit; it exits itself):
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Scenes.TitleScreenshot.Capture
    ///     [-width 1920] [-height 1080] [-textScale 1|1.5|2] [-titleMenu main|settings|records] [-stem title_main]
    /// </summary>
    [InitializeOnLoad]
    public static class TitleScreenshot
    {
        const string Key = "SynapticSea.TitleScreenshot.";

        static RenderTexture _texture;
        static PanelSettings _panelSettings;
        static int _frames;
        static double _startedAt;

        static TitleScreenshot()
        {
            if (SessionState.GetString(Key + "path", "").Length != 0) EditorApplication.update += Tick;
        }

        [MenuItem("Synaptic Sea/Scenes/Capture Title Screenshot")]
        public static void CaptureMenu() => Begin(1920, 1080, "", "main", "title_main");

        public static void Capture()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            int width = int.TryParse(Arg("-width"), out int w) ? w : 1920;
            int height = int.TryParse(Arg("-height"), out int h) ? h : 1080;
            Begin(width, height, Arg("-textScale") ?? "", Arg("-titleMenu") ?? "main", Arg("-stem") ?? "title_main");
        }

        static void Begin(int width, int height, string textScale, string menu, string stem)
        {
            if (textScale.Length != 0) Environment.SetEnvironmentVariable(AccessibilitySettings.ENV_VAR_NAME, textScale);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "artifacts", "screenshots"));
            Directory.CreateDirectory(dir);
            SessionState.SetString(Key + "path", Path.Combine(dir, stem + ".png"));
            SessionState.SetInt(Key + "width", width);
            SessionState.SetInt(Key + "height", height);
            SessionState.SetString(Key + "menu", menu);
            EditorSceneManager.OpenScene(FrontEndSceneBuilder.BootScenePath, OpenSceneMode.Single);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            if (_startedAt == 0) _startedAt = EditorApplication.timeSinceStartup;
            if (EditorApplication.timeSinceStartup - _startedAt > 60)
            {
                Finish("[TitleScreenshot] FAIL timed out waiting for the title screen", 1);
                return;
            }
            TitleScreen title = Object.FindAnyObjectByType<TitleScreen>();
            if (title == null || !title.IsBuilt) return;

            if (_texture == null)
            {
                string menu = SessionState.GetString(Key + "menu", "main");
                if (menu == "settings" || menu == "records")
                {
                    var c = title.Coordinator;
                    c.MenuState.SetFocusIndex(c.MenuPanel.Rows.ToList().FindIndex(r => r.Id == menu));
                    c.HandleUiInput(UiCommand.Accept);
                }
                _panelSettings = title.GetComponent<UIDocument>().panelSettings;
                _texture = new RenderTexture(SessionState.GetInt(Key + "width", 1920), SessionState.GetInt(Key + "height", 1080), 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                _panelSettings.targetTexture = _texture;
                _frames = 0;
                return;
            }
            if (++_frames < 20) return;

            var previous = RenderTexture.active;
            RenderTexture.active = _texture;
            var image = new Texture2D(_texture.width, _texture.height, TextureFormat.RGBA32, false, false);
            image.ReadPixels(new Rect(0, 0, _texture.width, _texture.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            string path = SessionState.GetString(Key + "path", "");
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            Finish($"[TitleScreenshot] PASS {path} menu={title.Coordinator.GetCurrentMenu()} text_scale={AppServices.Instance?.Accessibility.GetTextScale()}", 0);
        }

        static void Finish(string message, int code)
        {
            EditorApplication.update -= Tick;
            if (_panelSettings != null) _panelSettings.targetTexture = null;
            SessionState.EraseString(Key + "path");
            Debug.Log(message);
            if (Application.isBatchMode) EditorApplication.Exit(code);
            else EditorApplication.ExitPlaymode();
        }
    }
}
