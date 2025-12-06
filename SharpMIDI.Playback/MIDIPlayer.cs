using Microsoft.VisualBasic.Devices;

namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int clock = 0, totalFrames = 0;
        public static long playedEvents = 0;
        public static bool stopping = false;
        public static void StartPlayback()
        {
            stopping = false;
            long* evs = MIDI.synthEvents.Pointer;
            long* evend = evs + MIDI.synthEvents.Length;
            long* p0 = evs;
            long[] tev = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            fixed (long* t0 = tev)
            {
                long* tevs = t0;
                long* tevend = t0 + tev.Length;
                
                long evval = (evs < evend ? *evs : long.MaxValue);
                long tevval = (tevs < tevend ? *tevs : long.MaxValue);
                
                int ePos = (int)(evval >> 32);
                int tPos = (int)(tevval >> 32);
                while (!stopping)
                {
                    int localclock = (int)MIDIClock.GetTick();
                    while (true)
                    {
                        if(ePos > localclock && tPos > localclock) 
                            break;
                        if (ePos <= tPos)
                        {
                            Sound.Submit((uint)evval);
                            ++evs;
                            evval = (evs < evend ? *evs : long.MaxValue);
                            ePos = (int)(evval >> 32);
                        }
                        else
                        {
                            MIDIClock.SubmitBPM(tPos, tevval);
                            ++tevs;
                            tevval = (tevs < tevend ? *tevs : long.MaxValue);
                            tPos = (int)(tevval >> 32);
                        }
                    }
                    // for the sake of less writes to ui stuff
                    if (localclock != clock)
                    {
                        playedEvents = evs - p0;
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