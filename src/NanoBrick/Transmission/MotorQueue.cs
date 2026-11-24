using NanoBrick.Protocols;

namespace NanoBrick.Transmission
{
    /// <summary>
    /// Job queue of one receiver output (one motor). A job is a command sent for a number of active
    /// frames, followed by a number of neutral frames: one wheel notch, a stop, a pause…
    /// Fixed-size ring buffer: no allocation once running.
    /// </summary>
    internal sealed class MotorQueue
    {
        private const int Capacity = 32;

        private readonly Jm039Command[] _commands = new Jm039Command[Capacity];
        private readonly int[] _activeFrames = new int[Capacity];
        private readonly int[] _neutralFrames = new int[Capacity];
        private int _head, _tail;

        // Job in progress
        private Jm039Command _command;
        private int _activeLeft, _neutralLeft;

        /// <summary>True while a job is running or waiting.</summary>
        public bool IsBusy
        {
            get { return _activeLeft > 0 || _neutralLeft > 0 || _head != _tail; }
        }

        /// <summary>Adds a job. Returns false when the queue is full.</summary>
        public bool TryEnqueue(Jm039Command command, int activeFrames, int neutralFrames)
        {
            int next = (_tail + 1) % Capacity;
            if (next == _head) return false;

            _commands[_tail] = command;
            _activeFrames[_tail] = activeFrames;
            _neutralFrames[_tail] = neutralFrames;
            _tail = next;
            return true;
        }

        /// <summary>Drops every job, including the one in progress.</summary>
        public void Clear()
        {
            _head = _tail = 0;
            _activeLeft = _neutralLeft = 0;
        }

        /// <summary>Command of this output for the current frame (<see cref="Jm039Command.None"/> = neutral).</summary>
        public Jm039Command NextCommand()
        {
            if (_activeLeft == 0 && _neutralLeft == 0 && _head != _tail)
            {
                _command = _commands[_head];
                _activeLeft = _activeFrames[_head];
                _neutralLeft = _neutralFrames[_head];
                _head = (_head + 1) % Capacity;
            }

            if (_activeLeft > 0)
            {
                _activeLeft--;
                return _command;
            }

            if (_neutralLeft > 0) _neutralLeft--;
            return Jm039Command.None;
        }
    }
}
