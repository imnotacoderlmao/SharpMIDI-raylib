using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

namespace SharpMIDI
{
    static class MIDIPlayer
    {
        public static int ppq = 0, totalFrames = 0, clock = 0;
        public static bool stopping = false , paused = false;

        public static void StartPlayback()
        {
            stopping = false;
            var ev = MIDI.synthEvents;
            var tev = MIDI.tempos;
            int localclock = 0, tempoProgress = 0, eventProgress = 0, evcount = ev.Length, tevcount = tev.Count, maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            while (!stopping)
            {
                clock = localclock = (int)MIDIClock.GetTick();
                while (eventProgress < evcount)
                {
                    int pos = ev[eventProgress].pos;
                    if(pos > localclock) break;
                    uint val = (uint)ev[eventProgress].val;
                    Sound.Submit(val);
                    eventProgress++;
                }
                while (tempoProgress < tevcount)
                {
                    int pos = tev[tempoProgress].pos;
                    if(pos > localclock) break;
                    int tempo = tev[tempoProgress].tempo;
                    MIDIClock.SubmitBPM(pos, tempo);
                    tempoProgress++;
                }
                totalFrames++;
                Sound.playedEvents = eventProgress;
                if (localclock > maxTick) stopping = true;
            }
            MIDIClock.Reset();
            Console.WriteLine("Playback finished...");
            Starter.form.button4.Enabled = true;
            Starter.form.button5.Enabled = false;
            Starter.form.button6.Enabled = false;
        }
    }
}