using System.Data;

namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int totalFrames = 0;
        public static long playedEvents = 0;
        public static bool stopping;
        public static void StartPlayback()
        {
            stopping = false;
            var synthev = MIDI.synthEvents;
            SynthEvent* evptr = synthev.Pointer;
            SynthEvent* currev = evptr;
            SynthEvent* evend = evptr + synthev.Length;
            Tempo[] tevs = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            uint localwrite = Sound.write;
            uint localbuffermask = Sound.bufferMask;
            uint* buffer = Sound.ringbuffer;
            fixed (Tempo* t0 = tevs)
            {
                Tempo* currtev = t0;
                MIDIClock.Start();
                while (!stopping)
                {
                    uint localclock = (uint)MIDIClock.Update();
                    bool skipping = MIDIClock.skipping;
                    if (!skipping) 
                    {
                        while (currev->tick <= localclock)
                        {
                            buffer[localwrite] = (uint)(currev++->message & 0xFFFFFF);
                            localwrite = (localwrite + 1) & localbuffermask;
                        }
                    }
                    else 
                    {
                        SynthEvent* left = currev;
                        SynthEvent* right = evend;
                        while (right - left > 16)
                        {
                            SynthEvent* mid = left + ((right - left) >> 1);
                            if (mid->tick <= localclock)
                                left = mid + 1;
                            else
                                right = mid;
                        }
                        currev = left;
                    }
                    while (currtev->tick <= localclock)
                    {
                        MIDIClock.SubmitBPM(currtev->tick, currtev->tempo);
                        ++currtev;
                    }
                    Sound.write = localwrite;
                    playedEvents = currev - evptr;
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