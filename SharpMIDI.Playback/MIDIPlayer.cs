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
            // this could be done better but whatever
            var synthev = MIDI.synthEvents;
            long* evptr = synthev.Pointer;
            long* evend = evptr + synthev.Length;
            long* currev = evptr;
            long[] tevs = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            uint localwrite = Sound.write;
            uint localbuffermask = Sound.bufferMask;
            uint* buffer = Sound.ringbuffer;
            fixed (long* t0 = tevs)
            {
                long* currtev = t0;
                MIDIClock.Start();
                while (!stopping)
                {
                    uint localclock = (uint)MIDIClock.Update();
                    bool skipping = MIDIClock.skipping;
                    if (!skipping) 
                    {
                        while ((uint)(*currev >> 32) <= localclock)
                        {
                            long ev = *currev++;
                            buffer[localwrite] = (uint)ev;
                            localwrite = (localwrite + 1) & localbuffermask;
                        }
                    }
                    else 
                    {
                       // fucign binary search
                        long* left = currev;
                        long* right = evend;
                        while (right - left > 16)
                        {
                            long* mid = left + ((right - left) >> 1);
                            if ((uint)(*mid >> 32) <= localclock)
                                left = mid + 1;
                            else
                                right = mid;
                        }
                        currev = left;
                    }
                    while ((uint)(*currtev >> 32) <= localclock)
                    {
                        MIDIClock.SubmitBPM((uint)(*currtev >> 32), *currtev & 0xFFFFFF);
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