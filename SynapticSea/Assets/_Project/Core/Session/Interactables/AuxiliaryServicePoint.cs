using System;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    public sealed class AuxiliaryServicePoint : SessionInteractable
    {
        public override string Kind => "auxiliary_service";
        public string ServiceId = "", ServiceKind = "", OwnerId = "";
        public Func<string, GdDict> Interact;
        public Func<string, bool> Actionable;
        public bool TryInteract(Vec3 position)
        {
            if (!IsValid || !IsInsideTree || !IsPlayerInDirectRangeStrict(position)) return false;
            if (Actionable?.Invoke(ServiceId) == false) return false;
            var result = Interact?.Invoke(ServiceId); NotifyChanged(); return result?.GetBool("committed") == true;
        }
    }
}
