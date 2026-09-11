using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>A structural socket (port of the Godot <c>Anchor_SOCK_*</c> markers, positioned from the placement contract).</summary>
    public sealed class SocketMarker : MonoBehaviour
    {
        public string socketId;
        public string kind;
        public string[] compatibleKinds;

        /// <summary>Socket position as authored in the contract (Godot frame, module-local metres).</summary>
        public Vector3 godotLocalPosition;
    }
}
