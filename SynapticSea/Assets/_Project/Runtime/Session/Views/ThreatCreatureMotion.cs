using CritterCrafter;
using SynapticSea.Core.Systems;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>Observe game-owned movement; animation never changes published AI speeds or agent position.</summary>
    public sealed class ThreatCreatureMotion : MonoBehaviour
    {
        ThreatAIState _threat;
        CreatureMotion _motion;
        Vector3 _previous;
        string _state;
        bool _initialized;
        public void Bind(ThreatAIState threat, CreatureMotion motion)
        { _threat = threat; _motion = motion; _previous = transform.position; }
        void LateUpdate()
        {
            if (_motion == null || _threat == null) return;
            if (!_initialized) { _previous = transform.position; _initialized = true; }
            Vector3 velocity = Time.deltaTime > 0f ? (transform.position - _previous) / Time.deltaTime : Vector3.zero;
            _previous = transform.position;
            _motion.SetVelocity(velocity);
            if (_state == _threat.State) return;
            _state = _threat.State;
            _motion.SetState(_state == ThreatAIState.STATE_DEAD ? CreatureState.Dead :
                _state == ThreatAIState.STATE_STUN ? CreatureState.Stunned :
                _state == ThreatAIState.STATE_TELEGRAPH ? CreatureState.Telegraph :
                _state == ThreatAIState.STATE_ATTACK ? CreatureState.Attacking :
                velocity.sqrMagnitude > .0001f ? CreatureState.Moving : CreatureState.Idle);
        }
        public void PlayAttack() => _motion?.PlayAttack();
        public void PlayHit() => _motion?.PlayHit();
        public void Die() => _motion?.SetState(CreatureState.Dead);
    }
}
