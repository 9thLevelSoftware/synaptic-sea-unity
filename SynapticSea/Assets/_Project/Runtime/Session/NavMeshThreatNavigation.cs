// Unity port (no Godot source): the scene half of ThreatRuntime's motion, which Godot did by stepping along a
// hand-rolled A* path (port-status decision 59).
using System.Collections.Generic;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using UnityEngine;
using UnityEngine.AI;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Moves the threats with <see cref="NavMeshAgent"/>s on the active ship's <see cref="ShipNavMesh"/>. Core
    /// stays the owner of every threat's position: each tick it hands over a destination and stores the position
    /// the agent reached, so saves, the tick order and the headless tests are unchanged.
    ///
    /// An agent is created on the threat's placeholder node the first time it is asked to move, and warped onto
    /// the NavMesh. Where no agent can be placed (no node yet, or nothing walkable within
    /// <see cref="SampleRadius"/>), <see cref="TryAdvance"/> returns false and Core falls back to the nav graph.
    /// </summary>
    public sealed class NavMeshThreatNavigation : IThreatNavigation
    {
        /// <summary>How far from a threat's own position a point on the NavMesh is looked for.</summary>
        public const float SampleRadius = 3f;

        /// <summary>Beyond this the agent is warped instead of walked: a load, a teleport, or a ship that moved.</summary>
        public const float WarpDistance = 2f;

        /// <summary>Agent avoidance is off: Godot's threats never pushed each other aside.</summary>
        public const ObstacleAvoidanceType Avoidance = ObstacleAvoidanceType.NoObstacleAvoidance;

        readonly ThreatPlaceholderView _view;
        readonly Dictionary<string, NavMeshAgent> _agents = new Dictionary<string, NavMeshAgent>();
        readonly List<Vector3> _avoided = new List<Vector3>();
        bool _paused;

        /// <summary>The ship the threats are currently on; null when none is built.</summary>
        public ShipNavMesh Ship { get; private set; }

        public NavMeshThreatNavigation(ThreatPlaceholderView view) => _view = view;

        public bool HasNavMesh => Ship != null && Ship.HasNavMesh;

        /// <summary>The active ship changed (boot, travel, reload): its NavMesh carries the threats from now on.</summary>
        public void SetShip(ShipNavMesh ship)
        {
            if (Ship == ship) return;
            Ship = ship;
            // The old ship's agents are meaningless on the new mesh; they are re-made where the threats now stand.
            foreach (NavMeshAgent agent in _agents.Values)
                if (agent != null) Object.Destroy(agent);
            _agents.Clear();
        }

        /// <summary>
        /// Agents walk in the engine's own update, which does not stop when the simulation does, so a paused run
        /// (the modal stack, the results screen) stops them by hand.
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;
            foreach (NavMeshAgent agent in _agents.Values)
                if (agent != null && agent.isOnNavMesh) agent.isStopped = paused;
        }

        public bool TryAdvance(string instanceId, Vec3 current, Vec3 target, double speed, double delta, out Vec3 position)
        {
            position = current;
            if (!HasNavMesh) return false;
            NavMeshAgent agent = AgentFor(instanceId, current);
            if (agent == null) return false;
            Vector3 currentUnity = Frame.ToUnity(current);
            // An agent off the mesh keeps trying: the ship it needs may have been built after it spawned. One that
            // Core moved itself (a load, a teleport, or the nav graph fallback) is taken to where Core put it.
            bool away = (agent.transform.position - currentUnity).sqrMagnitude > WarpDistance * WarpDistance;
            if ((!agent.isOnNavMesh || away) && !Warp(agent, currentUnity)) return false;
            agent.speed = (float)speed;
            agent.isStopped = _paused;
            if (!_paused && NavMesh.SamplePosition(Frame.ToUnity(target), out NavMeshHit hit, SampleRadius, agent.areaMask))
                agent.SetDestination(hit.position);
            position = Frame.ToGodot(agent.transform.position);
            return true;
        }

        public void SetAvoidedCells(GdArray worldPositions, double cellSize)
        {
            if (Ship == null) return;
            _avoided.Clear();
            foreach (object position in worldPositions ?? new GdArray())
                if (position is Vec3 v) _avoided.Add(Frame.ToUnity(v));
            Ship.SetAvoidedCells(_avoided, (float)cellSize);
        }

        public void Hold(string instanceId)
        {
            if (!_agents.TryGetValue(instanceId ?? "", out NavMeshAgent agent) || agent == null || !agent.isOnNavMesh) return;
            agent.isStopped = true;
            agent.ResetPath();
        }

        public void Release(string instanceId)
        {
            if (!_agents.TryGetValue(instanceId ?? "", out NavMeshAgent agent)) return;
            _agents.Remove(instanceId);
            if (agent != null) Object.Destroy(agent);
        }

        /// <summary>The threat's agent, made on its placeholder node the first time it moves.</summary>
        NavMeshAgent AgentFor(string instanceId, Vec3 current)
        {
            if (_agents.TryGetValue(instanceId ?? "", out NavMeshAgent existing) && existing != null) return existing;
            if (instanceId == null || !_view.Nodes.TryGetValue(instanceId, out GameObject node) || node == null) return null;
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(ShipNavMesh.AgentTypeId);
            var agent = node.AddComponent<NavMeshAgent>();
            agent.agentTypeID = settings.agentTypeID;
            agent.radius = settings.agentRadius;
            agent.height = settings.agentHeight;
            agent.obstacleAvoidanceType = Avoidance;
            agent.autoBraking = false;
            agent.acceleration = 999f; // Godot's follower reached its speed instantly; the AI does the pacing.
            agent.angularSpeed = 999f;
            agent.stoppingDistance = 0f;
            agent.updateRotation = false; // the placeholder's facing is the view's business (lunges, death).
            agent.enabled = false; // it only starts steering once it is standing on the mesh.
            _agents[instanceId] = agent;
            Warp(agent, Frame.ToUnity(current));
            return agent;
        }

        /// <summary>Places the agent on the NavMesh at (or near) a world point; false when nothing walkable is in reach.</summary>
        static bool Warp(NavMeshAgent agent, Vector3 world)
        {
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
            if (!NavMesh.SamplePosition(world, out NavMeshHit hit, SampleRadius, filter)) return false;
            if (!agent.enabled)
            {
                agent.transform.position = hit.position;
                agent.enabled = true;
                return agent.isOnNavMesh;
            }
            return agent.Warp(hit.position);
        }
    }
}
