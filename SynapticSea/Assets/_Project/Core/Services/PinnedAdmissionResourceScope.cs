using System;
using System.Threading;

namespace SynapticSea.Core.Services
{
    internal sealed class UnsupportedWorkerPortException : InvalidOperationException
    {
        internal string Port { get; }
        internal UnsupportedWorkerPortException(string port) : base("unsupported_worker_port:" + port) { Port = port; }
    }
    /// <summary>Unwired diagnostic synchronous worker segment. Never span await or invoke application callbacks.</summary>
    internal sealed class PinnedAdmissionResourceScope : IDisposable
    {
        [ThreadStatic] static PinnedAdmissionResourceScope _current;
        readonly PinnedAdmissionResourceScope _previous;
        readonly int _thread;
        readonly ImmutableResourceAuthority _snapshot;
        bool _disposed;
        internal static ImmutableResourceAuthority ReaderOrNull => _current?._snapshot;
        internal static void RefusePort(string port)
        { if (_current != null) throw new UnsupportedWorkerPortException(port); }
        internal PinnedAdmissionResourceScope(ResourceAuthorityLease lease)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            _snapshot = lease.Snapshot; _previous = _current; _thread = Thread.CurrentThread.ManagedThreadId; _current = this;
        }
        public void Dispose()
        {
            if (_thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("worker_scope_wrong_thread");
            if (_disposed) return;
            if (!ReferenceEquals(_current, this)) throw new InvalidOperationException("worker_scope_wrong_order");
            _current = _previous; _disposed = true;
        }
    }
}
