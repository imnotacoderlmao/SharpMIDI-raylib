using System.Diagnostics;

namespace SharpMIDI
{
    static class MIDIClock
    {
        public static double time = 0f;
        public static double bpm = 120d;
        public static double ppq = 0;
        public static double ticklen;
        public readonly static Stopwatch test = new Stopwatch();
        public static double elapsed = 0, last = 0;
        public static bool throttle = true;
        public static double timeLost = 0;
        public static void Start()
        {
            test.Start();
            ticklen = (1 / (double)ppq) * (60 / bpm);
        }

        public static void Reset()
        {
            time = 0f;
            last = 0;
            timeLost = 0f;
            test.Reset();
        }

        public static double GetElapsed()
        {
            elapsed = (double)test.ElapsedTicks * 0.0000001;
            if (!throttle) return elapsed;
            if (elapsed - last > 0.0166666d) timeLost += elapsed - last - 0.0166666d;
            last = elapsed;
            return elapsed-timeLost;
        }

        public static double GetTick() => time + (GetElapsed() / ticklen);

        public static void SubmitBPM(double pos, double tempo)
        {
            double remainder = (time - pos);
            time = pos + (GetElapsed() / ticklen);
            bpm = 60000000 / tempo;
            timeLost = 0d;
            //Console.WriteLine("New BPM: " + bpm + " Tick: " + pos); this mf was slowing down the playback thread when tempo changes occur :(
            ticklen = 60.0 / (bpm * ppq);
            time += remainder;
            test.Restart();
        }

        public static void Stop()
        {
            test.Stop();
        }

        public static void Resume()
        {
            test.Start();
        }
    }
}
