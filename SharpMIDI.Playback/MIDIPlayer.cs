namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int ppq = 0, totalFrames = 0, clock = 0;
        public static bool stopping = false , paused = false;
        public static void StartPlayback()
        {
            stopping = false;
            long[] ev = MIDI.synthEvents;
            long[] tev = MIDI.tempos.ToArray();
            int localclock = 0, evcount = ev.Length, tevcount = tev.Length, maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            fixed (long* p0 = ev)
            {
                fixed (long* t0 = tev)
                {
                    long* evs = p0;
                    long* evend = p0 + evcount;
                    long* tevs = t0;
                    long* tevend = t0 + tevcount;
                    while (!stopping)
                    {
                        clock = localclock = (int)MIDIClock.GetTick();
                        do
                        {
                            long e = *evs;
                            if ((int)(e >> 32) > localclock) break;
                            Sound.Submit((uint)e);
                            ++evs;
                        } while (evs < evend);
                        if (tevs < tevend)
                        {
                            long t = *tevs;
                            if ((int)(t >> 32) < localclock)
                            {
                                uint tempo = (uint)t;
                                MIDIClock.SubmitBPM((int)(t >> 32), tempo);
                                ++tevs;
                            }
                        }
                        Sound.playedEvents = evs - p0;
                        ++totalFrames;
                        if (localclock > maxTick) stopping = true;
                    }
                }
            }
            MIDIClock.Reset();
            Console.WriteLine("Playback finished...");
            Starter.form.button4.Enabled = true;
            Starter.form.button5.Enabled = false;
            Starter.form.button6.Enabled = false;
        }
    }
}