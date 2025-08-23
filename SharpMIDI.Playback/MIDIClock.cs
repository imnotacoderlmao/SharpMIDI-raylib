using System.Diagnostics;

namespace SharpMIDI
{
    class MIDIClock
    {
        public static double time = 0f;
        static double bpm = 120d;
        public static double ppq = 0;
        public static double ticklen;
        static Stopwatch test = new Stopwatch();
        static double last = 0;
        public static double elapsed = 0;
        public static bool throttle = true;
        static double timeLost = 0;
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
            elapsed = (double)test.ElapsedTicks / TimeSpan.TicksPerSecond;
            if (throttle)
            {
                if (elapsed-last > 0.0166666d)
                {
                    timeLost += (elapsed - last) - 0.0166666d;
                    last = elapsed;
                    return elapsed-timeLost;
                }
            }
            //last = temp;
            //return temp-timeLost;
            return elapsed;
        }

        public static void SubmitBPM(double pos, double tempo)
        {
            double remainder = (time - pos);
            time = pos + (GetElapsed() / ticklen);
            bpm = 60000000 / tempo;
            timeLost = 0d;
            //Console.WriteLine("New BPM: " + bpm + " Tick: " + pos); this mf was slowing down the playback thread when tempo changes occur :(
            ticklen = (1 / (double)ppq) * (60 / bpm);
            time += remainder;
            test.Restart();
        }

        public static double GetTick() => time + (GetElapsed() / ticklen);

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
