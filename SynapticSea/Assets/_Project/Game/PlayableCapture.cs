using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SynapticSea.Game
{
    /// <summary>
    /// Captures the running Playable scene (iso camera plus the HUD and menu UIDocuments) to a PNG without a swap chain:
    /// the camera renders into a RenderTexture, both UI panels are redirected to RenderTextures through runtime copies of
    /// their PanelSettings, and the layers are composited on the CPU (UI Toolkit writes premultiplied colour).
    /// </summary>
    public static class PlayableCapture
    {
        public static IEnumerator Capture(PlayableBootstrap bootstrap, string path, int width, int height, Action<bool> done)
        {
            if (bootstrap == null || !bootstrap.IsBooted || bootstrap.Host.SceneState.CameraRig == null)
            {
                done?.Invoke(false);
                yield break;
            }
            Camera cam = bootstrap.Host.SceneState.CameraRig.Camera;
            var hudRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var menuRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            PanelSettings hudOriginal = bootstrap.HudDocument.panelSettings;
            PanelSettings menuOriginal = bootstrap.MenuDocument.panelSettings;
            PanelSettings hudCopy = Object.Instantiate(hudOriginal);
            PanelSettings menuCopy = Object.Instantiate(menuOriginal);
            hudCopy.targetTexture = hudRt;
            menuCopy.targetTexture = menuRt;
            hudCopy.clearColor = true;
            menuCopy.clearColor = true;
            hudCopy.colorClearValue = Color.clear;
            menuCopy.colorClearValue = Color.clear;
            bootstrap.HudDocument.panelSettings = hudCopy;
            bootstrap.MenuDocument.panelSettings = menuCopy;
            // Layout and repaint happen in the player loop: give the panels a few frames at the new size.
            // (Not WaitForEndOfFrame: it never resumes in batch mode.)
            for (int i = 0; i < 4; i++) yield return null;

            bool ok = false;
            try
            {
                Texture2D scene = RenderCamera(cam, width, height);
                Texture2D hud = Read(hudRt);
                Texture2D menu = Read(menuRt);
                Color32[] px = scene.GetPixels32();
                Over(px, hud.GetPixels32());
                Over(px, menu.GetPixels32());
                scene.SetPixels32(px);
                scene.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, scene.EncodeToPNG());
                Object.Destroy(scene);
                Object.Destroy(hud);
                Object.Destroy(menu);
                ok = true;
            }
            catch (Exception e)
            {
                Debug.LogError("PlayableCapture: " + e);
            }
            finally
            {
                bootstrap.HudDocument.panelSettings = hudOriginal;
                bootstrap.MenuDocument.panelSettings = menuOriginal;
                Object.Destroy(hudCopy);
                Object.Destroy(menuCopy);
                hudRt.Release();
                menuRt.Release();
                Object.Destroy(hudRt);
                Object.Destroy(menuRt);
            }
            done?.Invoke(ok);
        }

        static Texture2D RenderCamera(Camera cam, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            RenderTexture previous = RenderTexture.active;
            RenderTexture previousTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                cam.Render();
                return Read(rt);
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previous;
                rt.Release();
                Object.Destroy(rt);
            }
        }

        static Texture2D Read(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            return tex;
        }

        static void Over(Color32[] dst, Color32[] src)
        {
            for (int i = 0; i < dst.Length && i < src.Length; i++)
            {
                int a = src[i].a;
                if (a == 0) continue;
                int inv = 255 - a;
                dst[i].r = (byte)Mathf.Min(255, src[i].r + dst[i].r * inv / 255);
                dst[i].g = (byte)Mathf.Min(255, src[i].g + dst[i].g * inv / 255);
                dst[i].b = (byte)Mathf.Min(255, src[i].b + dst[i].b * inv / 255);
            }
        }
    }
}
