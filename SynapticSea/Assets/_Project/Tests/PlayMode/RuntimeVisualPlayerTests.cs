using System.Collections;
using NUnit.Framework;
using SynapticSea.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace SynapticSea.Tests.PlayMode
{
    /// <summary>Run with -testPlatform StandaloneWindows64 as well as PlayMode: Editor-only tests miss shader stripping.</summary>
    public class RuntimeVisualPlayerTests
    {
        [UnityTest]
        public IEnumerator RetainedRuntimeVariantsAreSupportedAndUnlitAlphaRendersInThePlayer()
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType, "This test requires GPU rendering.");
            var library = RuntimeVisualMaterialLibrary.Load();
            Assert.IsNotNull(library.playerOcclusionSilhouette);
            Assert.IsTrue(library.playerOcclusionSilhouette.shader.isSupported, "player-only silhouette shader must survive build stripping");
            foreach (bool unlit in new[] { false, true })
            foreach (bool transparent in new[] { false, true })
            foreach (bool emissive in new[] { false, true })
            {
                var template = library.Select(unlit, transparent, emissive);
                Assert.IsTrue(template.shader.isSupported, template.name);
                var material = RuntimeVisualCatalog.Material(new Color(0, 1, 0, 0.5f), unlit, transparent, emissive ? 2 : 0, true);
                Assert.AreSame(template.shader, material.shader);
                Assert.AreEqual(transparent, material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"));
                if (!unlit) Assert.AreEqual(emissive, material.IsKeywordEnabled("_EMISSION"));
            }

            var cameraObject = new GameObject("RuntimeMaterialRegressionCamera");
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var target = new RenderTexture(32, 32, 24);
            var pixels = new Texture2D(32, 32, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            try
            {
                // Keep the probe independent of scene cameras, geometry and lighting.
                quad.layer = 31;
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.cullingMask = 1 << 31;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.transform.position = new Vector3(0, 0, -2);
                camera.orthographic = true;
                camera.orthographicSize = 0.6f;
                camera.targetTexture = target;
                float opaqueGreen = 0;
                foreach (bool transparent in new[] { false, true })
                {
                    quad.GetComponent<Renderer>().sharedMaterial = RuntimeVisualCatalog.Material(new Color(0, 1, 0, 0.5f), true, transparent, doubleSided: true);
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
                    pixels.Apply();
                    var actual = pixels.GetPixel(16, 16);
                    Assert.Greater(actual.g, 0.2f, "URP Unlit should render green, rather than black or missing-shader pink.");
                    Assert.Less(actual.r, 0.1f);
                    Assert.Less(actual.b, 0.1f);
                    if (!transparent) opaqueGreen = actual.g;
                    else Assert.Less(actual.g, opaqueGreen - 0.05f, "The retained alpha variant must actually blend against black.");
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.Destroy(cameraObject);
                Object.Destroy(quad);
                Object.Destroy(target);
                Object.Destroy(pixels);
            }
            yield return null;
        }
    }
}
