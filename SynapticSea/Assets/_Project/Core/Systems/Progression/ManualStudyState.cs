using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>The one in-progress manual study job held by the session. Books read persist through <see cref="PlayerProgressionState"/>; the job itself is not saved.</summary>
    public sealed class ManualStudyState
    {
        public const double RequiredSeconds = 30.0;
        public const string Running = "running", Paused = "paused", Completed = "completed";
        public string BookId { get; private set; } = "";
        public double ProgressSeconds { get; private set; }
        public string Status { get; private set; } = "";
        public string Reason { get; private set; } = "";
        public bool IsRunning => Status == Running;
        public bool IsEmpty => BookId.Length == 0;

        /// <summary>Starts a job, or resumes it when the same unfinished book was paused.</summary>
        public void Start(string book)
        {
            if (BookId != book || Status == Completed) ProgressSeconds = 0;
            BookId = book; Status = Running; Reason = "";
        }
        public void Pause(string reason) { if (Status == Running) { Status = Paused; Reason = reason; } }
        /// <summary>Adds progress and returns true once the full duration is reached.</summary>
        public bool Advance(double seconds)
        {
            ProgressSeconds = System.Math.Min(RequiredSeconds, ProgressSeconds + seconds);
            return ProgressSeconds >= RequiredSeconds;
        }
        public void Complete() { Status = Completed; Reason = ""; }
        public void Clear() { BookId = ""; ProgressSeconds = 0; Status = ""; Reason = ""; }

        /// <summary>Shape read by the UI and work-selection code: <c>{ job: { book_id, progress_seconds, status, resume_required, reason } }</c>.</summary>
        public GdDict ToDict() => new GdDict { { "job", IsEmpty ? new GdDict() : new GdDict {
            { "book_id", BookId }, { "progress_seconds", ProgressSeconds }, { "status", Status },
            { "resume_required", Status == Paused }, { "reason", Reason } } } };
    }
}
