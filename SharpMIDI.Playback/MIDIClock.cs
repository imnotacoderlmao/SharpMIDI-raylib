using System.Diagnostics;

namespace SharpMIDI
{
    public static class Timer
    {
        static readonly double tickToSeconds = 1.0d / Stopwatch.Frequency;

        public static double Seconds()
        {
            return Stopwatch.GetTimestamp() * tickToSeconds;
        }
    }
    
    static class MIDIClock
    {
        // MIDI state
        static double tick;
        public static double bpm = 120;
        public static double ppq = 480;
        public static double tickscale;
        static double lastnow;
        public const double stall_thresh = 1.0d/60.0d;
        public static bool skipevents = true;
        public static bool throttle = !skipevents;
        public static bool paused;

        public static void Start()
        {
            double now = Timer.Seconds();
            lastnow = now;
            bpm = 120;
            tick = 0.0;
            tickscale = (bpm * ppq) / 60.0;
            paused = false;
        }

        public static void Reset() => Start();

        public static void Skip(double SkipTick, bool skipTo = false)
        {
            double targetTick = tick + SkipTick;
            
            if (skipTo)
                targetTick = SkipTick;
            
            if (targetTick == 0) 
                targetTick = 0;
            
            tick = targetTick; 
        }

        public static double Update()
        {
            if (paused || MIDIPlayer.stopping) return tick;
            double now = Timer.Seconds();
            double advancetime = now - lastnow;
            bool stalled = advancetime > stall_thresh;
            MIDIPlayer.skipping = skipevents && stalled;
            if (throttle && stalled)
            {
                advancetime = Math.Min(stall_thresh, advancetime);
            }
            lastnow = now;
            tick += advancetime * tickscale;
            return tick;
        }

        public static void SubmitBPM(uint24 microTempo)
        {
            bpm = 60000000.0 / microTempo.Value;
            tickscale = (bpm * ppq) / 60.0;
            //Console.WriteLine($"Tempo with value {microTempo.Value} ({bpm})");
        }

        public static void Stop()
        {
            if (!paused)
            {
                Update();
                paused = true;
                Sound.AllNotesOFF();
            }
        }

        public static void Resume()
        {
            if (paused)
            {
                lastnow = Timer.Seconds();
                paused = false;
            }
        }
    }
}
