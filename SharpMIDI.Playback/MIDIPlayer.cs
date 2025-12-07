namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int clock = 0, totalFrames = 0;
        public static long playedEvents = 0;
        public static bool stopping;
        public static void StartPlayback()
        {
            stopping = false;
            // this could be done better but whatever
            var synthev = MIDI.synthEvents;
            long* evend = synthev.Pointer + synthev.Length;
            long* evs = synthev.Pointer;
            long* ev = evs;
            
            long[] tev = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            fixed (long* t0 = tev)
            {
                long* tevs = t0;
                long* tevend = t0 + tev.Length;
                
                int ePos = (int)(*ev >> 32);
                int tPos = (int)(*ev >> 32);
                while (!stopping)
                {
                    int localclock = (int)MIDIClock.GetTick();
                    while (true)
                    {
                        if(ePos > localclock && tPos > localclock) 
                            break;
                        if (ePos <= tPos)
                        {
                            Sound.Submit((uint)*ev);
                            ++ev;
                            if (ev < evend) 
                            {
                                ePos = (int)(*ev >> 32);
                            } 
                            else 
                            {
                                ePos = int.MaxValue;
                            }
                        }
                        else
                        {
                            // playack kills itself when a tempo event happens cause it was also reading the upper 32 bits lmao
                            MIDIClock.SubmitBPM(tPos, *tevs & 0xFFFFFF);
                            ++tevs;
                            if (tevs < tevend) 
                            {
                                tPos = (int)(*tevs >> 32);
                            } 
                            else 
                            {
                                tPos = int.MaxValue;
                            }
                        }
                    }
                    // for the sake of less writes to ui stuff
                    if (localclock != clock)
                    {
                        playedEvents = ev - evs;
                        clock = localclock;
                    }
                    ++totalFrames;
                    if (localclock > maxTick) stopping = true;
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