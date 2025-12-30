using System.Diagnostics;

namespace SharpMIDI
{
    public static class Timer
    {
        static readonly double tickToSeconds = 1.0 / Stopwatch.Frequency;

        public static double Seconds()
        {
            return Stopwatch.GetTimestamp() * tickToSeconds;
        }
    }
    
    static class MIDIClock
    {
        // MIDI state
        public static double tick;
        public static double bpm = 120;
        public static double ppq = 480;
        public static double tickscale;
        static double lastnow;
        public static bool skipevents = true;
        public static bool throttle = !skipevents;
        public static bool stalled = false;
        public static bool skipping = false;
        public static bool paused;

        public static void Start()
        {
            double now = Timer.Seconds();
            lastnow = now;
            tick = 0.0;
            tickscale = (bpm * ppq) / 60.0;
            paused = false;
        }

        public static void Reset() => Start();

        public static double Update()
        {
            if (paused || MIDIPlayer.stopping) return tick;
            double now = Timer.Seconds();
            double advancetime = now - lastnow;
            stalled = advancetime > 0.0166666;
            skipping = skipevents && stalled;
            if (throttle && stalled)
            {
                advancetime = 0.0166666;
            }
            lastnow = now;
            tick += advancetime * tickscale;
            return tick;
        }

        public static void SubmitBPM(double posTick, double microTempo)
        {
            Update();
            bpm = 60000000.0 / microTempo;
            tickscale = (bpm * ppq) / 60.0;

            if (tick < posTick)
                tick = posTick;
            //Console.WriteLine($"Tempo in pos {posTick} with value {microTempo} ({bpm})");
        }

        public static void Stop()
        {
            if (!paused)
            {
                Update();
                paused = true;
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
