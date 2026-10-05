using System;
namespace SynapticSea.Core.Variant
{
    // Closed data only. Registry issuance/admission guards remain authority; this table cannot mint it.
    internal sealed class ProjectionLifetimeState
    {
        internal readonly ProjectionNodeId Id;
        internal readonly ProjectionNodeKind Kind;
        internal readonly ulong LastAdmitted, LastIssued;
        readonly ProjectionNodeIssuer _issuer;
        readonly WeakReference<ProjectionNodeVersion> _exactIssued;
        internal ProjectionLifetimeState(ProjectionNodeId id, ProjectionNodeKind kind, ulong admitted,
            ProjectionNodeIssuer issuer, ProjectionNodeVersion issued)
        {
            if (id.Registry == 0 || id.Value == 0 || issuer == null || (kind != ProjectionNodeKind.Dictionary && kind != ProjectionNodeKind.Array) ||
                issued != null && (!issued.Id.Equals(id) || issued.Kind != kind || !issued.HasIssuer(issuer)) ||
                admitted > (issued?.Version ?? 0)) throw new ArgumentException("invalid_projection_lifetime_state");
            Id = id; Kind = kind; LastAdmitted = admitted; LastIssued = issued?.Version ?? 0;
            _issuer = issuer; _exactIssued = issued == null ? null : new WeakReference<ProjectionNodeVersion>(issued);
        }
        internal bool SameIssuer(ProjectionLifetimeState other) => other != null && ReferenceEquals(_issuer, other._issuer);
        internal bool HasIssuer(ProjectionNodeIssuer exact) => ReferenceEquals(_issuer, exact);
        internal bool IsExactIssued(ProjectionNodeVersion exact) => exact != null && exact.Id.Equals(Id) &&
            exact.Kind == Kind && exact.Version == LastIssued && exact.HasIssuer(_issuer) &&
            _exactIssued != null && _exactIssued.TryGetTarget(out var current) && ReferenceEquals(current, exact);
    }
    // Same fixed 13 x 32 path as ProjectionVersionTable. Updates occur during preparation, not install.
    internal sealed class ProjectionLifetimeLedger
    {
        internal const int Height = 13, Fanout = 32, MaximumEntries = 4096;
        readonly Page _root;
        internal ulong Registry { get; }
        internal int Count => _root?.Count ?? 0;
        internal long Units => _root?.Units ?? 0;
        internal ProjectionLifetimeLedger(ulong registry)
        { if (registry == 0) throw new ArgumentException("invalid_registry"); Registry = registry; }
        ProjectionLifetimeLedger(ulong registry, Page root) { Registry = registry; _root = root; }
        internal ProjectionLifetimeState Get(ProjectionNodeId id)
        {
            if (id.Registry != Registry || id.Value == 0) return null;
            var page = _root;
            for (int level = Height - 1; level > 0; level--)
            { if (page == null) return null; page = page.Children[Index(id.Value, level)]; }
            return page?.Values[Index(id.Value, 0)];
        }
        internal ProjectionLifetimeLedger Set(ProjectionNodeId id, ProjectionLifetimeState value)
        {
            if (id.Registry != Registry || id.Value == 0 || value != null && !value.Id.Equals(id))
                throw new ArgumentException("projection_lifetime_binding");
            var before = Get(id);
            if (value != null && before == null && Count == MaximumEntries)
                throw new ArgumentException("projection_lifetime_capacity");
            if (before != null && value != null &&
                (!value.SameIssuer(before) || value.Kind != before.Kind || value.LastAdmitted < before.LastAdmitted || value.LastIssued < before.LastIssued))
                throw new ArgumentException("projection_lifetime_rewind");
            return new ProjectionLifetimeLedger(Registry, Update(_root, id.Value, Height - 1, value));
        }
        static int Index(ulong id, int level) => (int)((id >> (level * 5)) & 31UL);
        static Page Update(Page before, ulong id, int level, ProjectionLifetimeState value)
        {
            if (level == 0)
            {
                var values = before == null ? new ProjectionLifetimeState[Fanout] : (ProjectionLifetimeState[])before.Values.Clone();
                values[Index(id, 0)] = value; var page = new Page(values); return page.Count == 0 ? null : page;
            }
            var children = before == null ? new Page[Fanout] : (Page[])before.Children.Clone();
            int at = Index(id, level); children[at] = Update(children[at], id, level - 1, value);
            var result = new Page(children); return result.Count == 0 ? null : result;
        }
        sealed class Page
        {
            internal readonly Page[] Children;
            internal readonly ProjectionLifetimeState[] Values;
            internal readonly int Count;
            internal readonly long Units;
            internal Page(Page[] children)
            { Children = children; int count = 0; long units = 1; foreach (var child in children) { count += child?.Count ?? 0; units += child?.Units ?? 0; } Count = count; Units = units; }
            internal Page(ProjectionLifetimeState[] values)
            { Values = values; int count = 0; foreach (var value in values) if (value != null) count++; Count = count; Units = 1 + count; }
        }
    }
}
