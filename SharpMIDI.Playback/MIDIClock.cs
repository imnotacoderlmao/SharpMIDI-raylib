using System.Diagnostics;

namespace SharpMIDI
{
    static class Timer
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

        public static bool throttle = true;
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
            if (paused) return tick;
            double now = Timer.Seconds();
            double delta = now - lastnow;

            if (throttle && delta > 0.0166666)
                delta = 0.0166666;
            lastnow = now;
            
            tick += delta * tickscale;
            return tick;
        }

        public static void SubmitBPM(double posTick, double microTempo)
        {
            Update();
            bpm = 60000000.0 / microTempo;
            tickscale = (bpm * ppq) / 60.0;

            // ensure tick never jumps backwards
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
