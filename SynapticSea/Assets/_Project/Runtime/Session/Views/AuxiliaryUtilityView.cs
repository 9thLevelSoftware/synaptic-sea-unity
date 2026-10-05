using System;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>Owner-bound hardware presentation; reads committed Core effects and never changes gameplay.</summary>
    public sealed class AuxiliaryUtilityView : MonoBehaviour
    {
        public string ServiceId { get; private set; }
        public Light Lamp { get; private set; }
        public TextMesh StatusText { get; private set; }
        public bool HardwareReady { get; private set; }
        public bool RackReleased { get; private set; }
        Func<GdDict> _effects;
        Material _hardwareMaterial;
        Renderer _hardware;

        public static GameObject CreateFixture(string serviceId, Transform parent)
        {
            var root = new GameObject("AuxiliaryHardware"); root.transform.SetParent(parent, false);
            var view = root.AddComponent<AuxiliaryUtilityView>(); view.ServiceId = serviceId;
            var hardware = GameObject.CreatePrimitive(PrimitiveType.Cube); hardware.name = "ServiceHousing";
            hardware.transform.SetParent(root.transform, false); hardware.transform.localScale = new Vector3(.32f, .22f, .18f);
            var collider = hardware.GetComponent<Collider>(); collider.enabled = false; UnityEngine.Object.Destroy(collider);
            view._hardware = hardware.GetComponent<Renderer>();
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            if (shader != null) { view._hardwareMaterial = new Material(shader); view._hardware.sharedMaterial = view._hardwareMaterial; }
            var text = new GameObject("ServiceStatus"); text.transform.SetParent(root.transform, false); text.transform.localPosition = new Vector3(0, .28f, 0);
            view.StatusText = text.AddComponent<TextMesh>(); view.StatusText.anchor = TextAnchor.MiddleCenter;
            view.StatusText.characterSize = .07f; view.StatusText.fontSize = 32; view.StatusText.color = Color.white;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) { view.StatusText.font = font; text.GetComponent<Renderer>().sharedMaterial = font.material; }
            if (serviceId == "medbay_task_light_01" || serviceId == "airlock_dock_beacon_01")
            {
                var lamp = new GameObject("ServiceLamp"); lamp.transform.SetParent(root.transform, false); lamp.transform.localPosition = new Vector3(0, .18f, 0);
                view.Lamp = lamp.AddComponent<Light>(); view.Lamp.type = LightType.Point; view.Lamp.range = 4; view.Lamp.intensity = 1;
                view.Lamp.color = serviceId == "medbay_task_light_01" ? new Color(.85f, .95f, 1) : new Color(.25f, .9f, 1);
                view.Lamp.enabled = false;
            }
            view.Refresh(); return root;
        }
        public void BindEffects(Func<GdDict> effects) { _effects = effects; Refresh(); }
        void Update() => Refresh();
        public void Refresh()
        {
            GdDict effects = _effects?.Invoke(); HardwareReady = effects?.GetBool("hardware_ready") ?? false;
            RackReleased = effects?.GetBool("released") ?? false;
            bool powered = effects?.GetBool("powered") ?? false;
            if (Lamp != null) Lamp.enabled = HardwareReady && powered;
            if (_hardwareMaterial != null) _hardwareMaterial.color = !HardwareReady ? new Color(.75f, .32f, .12f) : powered ? new Color(.2f, .75f, .5f) : new Color(.35f, .4f, .45f);
            string title = ServiceId == "maintenance_fabricator_feed_01" ? "Fabricator feed" : ServiceId == "maintenance_cargo_relay_01" ? "Cargo relay" :
                ServiceId == "medbay_task_light_01" ? "Medbay task light" : ServiceId == "airlock_dock_beacon_01" ? "Dock beacon" : "Cold spare harness";
            if (StatusText != null) StatusText.text = title + "\n" + (effects?.GetString("status") ?? "Needs repair");
        }
        void OnDestroy() { if (_hardwareMaterial != null) UnityEngine.Object.Destroy(_hardwareMaterial); }
    }
}
