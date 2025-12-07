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
        // timing state
        static double startRaw;
        static double lastRaw;
        static double elapsed;

        // MIDI state
        public static double tick;
        public static double bpm = 120;
        public static double ppq = 480;
        public static double rawticklen;

        public static bool throttle = true;
        public static bool paused;

        public static void Start()
        {
            double now = Timer.Seconds();
            startRaw = now;
            lastRaw = now;
            elapsed = 0.0;
            tick = 0.0;

            rawticklen = 60.0 / (bpm * ppq);
            paused = false;
        }

        public static void Reset() => Start();

        public static double GetTick()
        {
            Update();
            return tick;
        }

        static void Update()
        {
            if (paused) return;

            double now = Timer.Seconds();
            double rawDelta = now - lastRaw;
            double ticklength = rawticklen / (double)Renderer.WindowManager.speed;
            // apply throttle to delta only
            double delta = throttle
                ? Math.Min(rawDelta, 0.0166666)
                : rawDelta;

            elapsed += delta;
            lastRaw = now;

            // advance MIDI time
            tick += delta / ticklength;
        }

        public static void SubmitBPM(double posTick, double microTempo)
        {
            Update();
            bpm = 60000000.0 / microTempo;
            rawticklen = 60.0 / (bpm * ppq);

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
                lastRaw = Timer.Seconds();
                paused = false;
            }
        }
    }
}
