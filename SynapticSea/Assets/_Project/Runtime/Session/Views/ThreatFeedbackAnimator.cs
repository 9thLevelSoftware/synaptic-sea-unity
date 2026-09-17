using System.Collections.Generic;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>
    /// Short procedural feedback on threat placeholders (Godot drew none): an attack lunge toward the target and back, a
    /// hit "punch" when the player's weapon lands, and a death effect (the node shrinks away, then is destroyed, with an
    /// optional VFX burst). One component on the threat root drives every animation from <c>Update</c>; the steps are
    /// public so EditMode tests can advance time without a player loop.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ThreatFeedbackAnimator : MonoBehaviour
    {
        public const float LungeSeconds = 0.28f;
        public const float LungeDistance = 0.6f;
        public const float PunchSeconds = 0.18f;
        public const float DeathSeconds = 0.4f;
        public const float BurstSeconds = 0.6f;

        sealed class Anim
        {
            public Transform Target;
            public Vector3 Direction;
            public Vector3 BaseScale;
            public float Elapsed;
            public float Duration;
            public int Kind; // 0 lunge (child offset), 1 punch, 2 death, 3 timed destroy
            public Vector3 BaseLocal;
        }

        readonly List<Anim> _anims = new List<Anim>();

        public int ActiveCount => _anims.Count;

        /// <summary>Moves the placeholder's mesh child toward <paramref name="worldTarget"/> and back.</summary>
        public void Lunge(GameObject node, Vector3 worldTarget)
        {
            Transform mesh = MeshChild(node);
            if (mesh == null) return;
            Vector3 dir = worldTarget - node.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) dir = node.transform.forward;
            Remove(mesh, 0);
            _anims.Add(new Anim { Target = mesh, Direction = dir.normalized, BaseLocal = mesh.localPosition, Duration = LungeSeconds, Kind = 0 });
        }

        /// <summary>A brief scale punch on the placeholder (a weapon hit).</summary>
        public void Punch(GameObject node)
        {
            Transform mesh = MeshChild(node);
            if (mesh == null) return;
            Remove(mesh, 1);
            _anims.Add(new Anim { Target = mesh, BaseScale = mesh.localScale, Duration = PunchSeconds, Kind = 1 });
        }

        /// <summary>Shrinks <paramref name="node"/> (already detached from the view) to nothing, then destroys it.</summary>
        public void Die(GameObject node)
        {
            if (node == null) return;
            foreach (Collider c in node.GetComponentsInChildren<Collider>()) c.enabled = false;
            _anims.Add(new Anim { Target = node.transform, BaseScale = node.transform.localScale, Duration = DeathSeconds, Kind = 2 });
        }

        /// <summary>Destroys <paramref name="node"/> after <paramref name="seconds"/> (a one-shot VFX burst).</summary>
        public void DestroyAfter(GameObject node, float seconds)
        {
            if (node == null) return;
            _anims.Add(new Anim { Target = node.transform, Duration = Mathf.Max(0.01f, seconds), Kind = 3 });
        }

        void Update() => Step(Time.deltaTime);

        public void Step(float delta)
        {
            for (int i = _anims.Count - 1; i >= 0; i--)
            {
                Anim a = _anims[i];
                if (a.Target == null)
                {
                    _anims.RemoveAt(i);
                    continue;
                }
                a.Elapsed += delta;
                float t = Mathf.Clamp01(a.Elapsed / a.Duration);
                bool done = t >= 1f;
                switch (a.Kind)
                {
                    case 0:
                        a.Target.localPosition = a.BaseLocal + a.Target.parent.InverseTransformDirection(a.Direction) * (Mathf.Sin(t * Mathf.PI) * LungeDistance);
                        if (done) a.Target.localPosition = a.BaseLocal;
                        break;
                    case 1:
                        a.Target.localScale = a.BaseScale * (1f + 0.25f * Mathf.Sin(t * Mathf.PI));
                        if (done) a.Target.localScale = a.BaseScale;
                        break;
                    case 2:
                        a.Target.localScale = a.BaseScale * (1f - t);
                        if (done) DestroyNode(a.Target.gameObject);
                        break;
                    case 3:
                        if (done) DestroyNode(a.Target.gameObject);
                        break;
                }
                if (done) _anims.RemoveAt(i);
            }
        }

        void Remove(Transform target, int kind)
        {
            for (int i = _anims.Count - 1; i >= 0; i--)
            {
                if (_anims[i].Target != target || _anims[i].Kind != kind) continue;
                if (kind == 0) target.localPosition = _anims[i].BaseLocal;
                if (kind == 1) target.localScale = _anims[i].BaseScale;
                _anims.RemoveAt(i);
            }
        }

        static Transform MeshChild(GameObject node)
        {
            if (node == null) return null;
            Transform mesh = node.transform.Find("Mesh");
            return mesh != null ? mesh : node.transform.childCount > 0 ? node.transform.GetChild(0) : null;
        }

        static void DestroyNode(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        void OnDestroy()
        {
            _anims.Clear();
        }
    }
}
