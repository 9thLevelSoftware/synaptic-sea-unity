using System;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>Code-native, collider-free presentation of an authored discrete deck transfer.
    /// It owns no movement, floor, ladder climbing, portal or navigation policy.</summary>
    [DisallowMultipleComponent]
    public sealed class DeckTransferPresentation : MonoBehaviour
    {
        public string ConnectionId { get; internal set; }
        public string ConnectionType { get; internal set; }
        public long SourceDeck { get; internal set; }
        public long DestinationDeck { get; internal set; }
        public Vec3 SourceLocal { get; internal set; }
        public Vec3 DestinationLocal { get; internal set; }
        public Transform ShipRoot { get; internal set; }
        public string WorldLabelId { get; internal set; }
        Func<Vec3?> _viewer;
        MeshRenderer[] _renderers = Array.Empty<MeshRenderer>();
        public string Direction => DestinationDeck > SourceDeck ? "Up" : "Down";
        public string TransferType => ConnectionType == "ladder" ? "Ladder" : ConnectionType == "ramp" ? "Ramp" : "Deck";
        public string Label => TransferType + " transfer\n" + Direction + " to deck " + DestinationDeck;
        public Vector3 SourceWorld => ShipRoot != null ? ShipRoot.TransformPoint(Frame.ToUnity(SourceLocal)) : transform.position;
        public bool VisibleOnSourceDeck
        {
            get
            {
                if (_viewer == null) return true;
                Vec3? viewer = _viewer();
                if (!viewer.HasValue || ShipRoot == null) return false;
                Vector3 local = ShipRoot.InverseTransformPoint(Frame.ToUnity(viewer.Value));
                return Mathf.Abs(local.y - (float)SourceLocal.Y) < 1.6f;
            }
        }
        internal void BindViewer(Func<Vec3?> viewer)
        {
            _viewer = viewer;
            _renderers = GetComponentsInChildren<MeshRenderer>(true);
            RefreshVisibility();
        }
        void LateUpdate() => RefreshVisibility();
        void RefreshVisibility()
        {
            bool visible = VisibleOnSourceDeck;
            foreach (MeshRenderer renderer in _renderers) if (renderer != null) renderer.enabled = visible;
        }
    }
}
