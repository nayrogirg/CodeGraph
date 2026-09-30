using System.Diagnostics;

namespace CodeGraph.Core;

/// <summary>Accumulates wall-clock time per analysis phase (load, compile, walk...) so slow phases are visible.</summary>
internal sealed class PhaseTimer
{
    private readonly Dictionary<string, TimeSpan> _phases = new(StringComparer.Ordinal);

    public Scope Measure(string phase) => new(this, phase);

    public Dictionary<string, double> ToSeconds()
    {
        lock (_phases) return _phases.ToDictionary(p => p.Key, p => Math.Round(p.Value.TotalSeconds, 2));
    }

    public void Add(string phase, TimeSpan elapsed)
    {
        lock (_phases) _phases[phase] = _phases.GetValueOrDefault(phase) + elapsed;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly PhaseTimer _timer;
        private readonly string _phase;
        private readonly long _start;

        public Scope(PhaseTimer timer, string phase)
        {
            _timer = timer;
            _phase = phase;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose() => _timer.Add(_phase, Stopwatch.GetElapsedTime(_start));
    }
}
