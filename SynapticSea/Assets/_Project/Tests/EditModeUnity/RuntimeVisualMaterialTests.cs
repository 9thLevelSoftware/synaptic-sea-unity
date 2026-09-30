using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.EditorTools.Build;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SynapticSea.Tests.Unity
{
    public class RuntimeVisualMaterialTests
    {
        [Test]
        public void ResourcesLibraryRetainsBothUrpShaderDependencies()
        {
            var library = RuntimeVisualMaterialLibrary.Load();
            Assert.DoesNotThrow(library.Validate);
            Assert.DoesNotThrow(RuntimeVisualMaterialBuilder.ValidateForBuild);
            var dependencies = AssetDatabase.GetDependencies(RuntimeVisualMaterialBuilder.LibraryPath, true);
            var shaderNames = dependencies.Select(AssetDatabase.LoadAssetAtPath<Shader>).Where(s => s != null).Select(s => s.name);
            CollectionAssert.Contains(shaderNames, RuntimeVisualCatalog.LitShaderName);
            CollectionAssert.Contains(shaderNames, RuntimeVisualCatalog.UnlitShaderName);
        }

        [TestCase(false, false, false)] [TestCase(false, false, true)]
        [TestCase(false, true, false)] [TestCase(false, true, true)]
        [TestCase(true, false, false)] [TestCase(true, true, false)]
        public void RuntimeClonesRetainedFeatureVariantWithoutChangingTemplate(bool unlit, bool transparent, bool emissive)
        {
            var template = RuntimeVisualMaterialLibrary.Load().Select(unlit, transparent, emissive);
            var color = new Color(0.21f, 0.32f, 0.43f, 0.54f);
            var material = RuntimeVisualCatalog.Material(color, unlit, transparent, emissive ? 2 : 0, true);
            Assert.AreNotSame(template, material);
            Assert.AreSame(template.shader, material.shader);
            Assert.AreEqual(transparent, material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"));
            Assert.AreEqual(emissive, material.IsKeywordEnabled("_EMISSION"));
            Assert.AreEqual(transparent ? 0 : 1, material.GetFloat("_ZWrite"));
            Assert.AreEqual((float)CullMode.Off, material.GetFloat("_Cull"));
            Assert.AreEqual((float)CullMode.Back, template.GetFloat("_Cull"));
            Assert.AreEqual(Color.white, template.GetColor("_BaseColor"));
            var actualColor = material.GetColor("_BaseColor");
            Assert.AreEqual(color.r, actualColor.r, 1e-6f);
            Assert.AreEqual(color.g, actualColor.g, 1e-6f);
            Assert.AreEqual(color.b, actualColor.b, 1e-6f);
            Assert.AreEqual(color.a, actualColor.a, 1e-6f);
            Assert.AreSame(material, RuntimeVisualCatalog.Material(color, unlit, transparent, emissive ? 2 : 0, true));
        }

        [Test]
        public void IncompleteOrWrongKeywordTemplatesFailClosed()
        {
            var library = ScriptableObject.CreateInstance<RuntimeVisualMaterialLibrary>();
            try
            {
                Assert.Throws<InvalidOperationException>(library.Validate);
                var valid = RuntimeVisualMaterialLibrary.Load();
                library.litOpaque = valid.litOpaque;
                library.litTransparent = valid.litOpaque; // An opaque dependency cannot retain the alpha variant.
                library.litEmissive = valid.litEmissive;
                library.litTransparentEmissive = valid.litTransparentEmissive;
                library.unlitOpaque = valid.unlitOpaque;
                library.unlitTransparent = valid.unlitTransparent;
                Assert.Throws<InvalidOperationException>(library.Validate);
            }
            finally { UnityEngine.Object.DestroyImmediate(library); }
        }
    }
}
