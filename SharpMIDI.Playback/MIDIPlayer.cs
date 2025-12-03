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
            long[] tev = MIDI.tempoEvents;
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
                    long evval = (evs < evend ? *evs : long.MaxValue);
                    long tevval = (tevs < tevend ? *tevs : long.MaxValue);
                    while (!stopping)
                    {
                        clock = localclock = (int)MIDIClock.GetTick();
                        while (true)
                        {
                            int ePos = (int)(evval >> 32);
                            int tPos = (int)(tevval >> 32);

                            if (ePos > localclock & tPos > localclock)
                                break;

                            if (ePos <= tPos)
                            {
                                Sound.Submit((uint)evval);
                                ++evs;
                                evval = (evs < evend ? *evs : long.MaxValue);
                            }
                            else
                            {
                                MIDIClock.SubmitBPM(tPos, (uint)tevval);
                                ++tevs;
                                tevval = (tevs < tevend ? *tevs : long.MaxValue);
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