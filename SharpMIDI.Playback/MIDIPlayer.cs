using System.ComponentModel;
using System.Text;
using SharpMIDI.Renderer;

namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static long totalFrames = 0;
        public static long playedEvents, playedevents2, eventspersec = 0;
        public static float MIDIFps = 0f;
        public static bool stopping = true;
        public static bool stalled = false;
        public static bool skipping = false;
        public static void StartPlayback()
        {
            if (!Sound.issynthinitiated)
            { 
                Console.WriteLine("NO synth initiated. trying OmniMIDI as a fallback");
                Sound.InitSynth("KDMAPI"); // fallback since i kept forgetting to do both and it crashes the whole program lmao
            }
            if (!MIDILoader.midiLoaded) 
            {
                Console.WriteLine("no midi loaded!!!");
                return;
            }
            playedEvents = 0;
            playedevents2 = 0;
            stopping = false;
            var synthev = MIDI.synthEvents;
            uint24* msgptr = MIDI.synthEvents.Pointer;
            uint24* msgcur = msgptr;
            TickGroup[] tickGroupArr = MIDI.tickGroupArr;
            Tempo[] tevs = MIDI.tempoEvents;
            //SysEx[] sysExes = MIDI.SysExarr;
            uint maxTick = (uint)MIDILoader.maxTick;
            uint clock = 0;
            uint24* buffer = Sound.ringbuffer;
            ushort writeptr = 0;
            //uint sysexidx = 0;
            fixed(TickGroup* tg0 = tickGroupArr)
            {
                fixed (Tempo* t0 = tevs)
                {
                    Tempo* currtev = t0;
                    TickGroup* currtg = tg0;
                    Sound.StartAudioThread();
                    Task.Run(PlaybackStats);
                    MIDIClock.Start();
                    while (!stopping)
                    {
                        clock = (uint)MIDIClock.Update();
                        if (!skipping)
                        {
                            while (currtg->tick <= clock)
                            {
                                uint24* groupEnd = msgcur + currtg->count;
                                while (msgcur < groupEnd)
                                    buffer[writeptr++] = *msgcur++;
                                playedEvents += currtg->count;
                                currtg++;
                            }
                        }
                        else
                        {
                            while (currtg->tick <= clock)
                            {
                                msgcur += currtg->count;
                                playedEvents += currtg->count;
                                currtg++;
                            }
                        }
                        while (currtev->tick <= clock)
                        {
                            MIDIClock.SubmitBPM(currtev->tick, currtev->tempo);
                            currtev++;
                        }
                        /*while (sysExes[sysexidx].tick <= clock)
                        {
                            SubmitSysEx(sysExes[sysexidx].message);
                            sysexidx++;
                            Console.WriteLine($"found sysex event at tick {clock}, sysex idx now at {sysexidx}");
                        }*/
                        totalFrames++;
                        if (clock > maxTick) stopping = true;
                    }
                }
            }
            MIDIClock.Reset();
            Sound.KillAudioThread();
            Console.WriteLine("Playback finished...");
        }

        /*public static void SubmitSysEx(byte[] message)
        {
            MIDIHDR data = new MIDIHDR
            {
                lpdata = message[0].ToString(),
                dwBufferLength = (uint)message.Length,
                dwBytesRecorded = (uint)message.Length
            };
            uint success = KDMAPI.PrepareLongData(&data, (uint)sizeof(MIDIHDR));
            if (success == 0) 
            {
                uint sendsuccess = KDMAPI.SendDirectLongData(&data, (uint)sizeof(MIDIHDR));
                if (sendsuccess == 0)
                    KDMAPI.UnprepareLongData(&data, (uint)sizeof(MIDIHDR));
            }
        }*/

        public static void PlaybackStats()
        {
            while (!stopping)
            {
                MIDIFps = totalFrames * 30;
                eventspersec = (playedEvents - playedevents2) * 30;
                if (stalled) Console.Write($"\rTick: {(int)MIDIClock.tick} / {MIDILoader.maxTick} | Played Events: {playedEvents} / {MIDILoader.eventCount} ({eventspersec}/s) | MIDI Thread: STALLED | Skip events?: {MIDIClock.skipevents}");
                else Console.Write($"\rTick: {(int)MIDIClock.tick} / {MIDILoader.maxTick} | Played Events: {playedEvents} / {MIDILoader.eventCount} ({eventspersec}/s) | MIDI Thread: @{MIDIFps} fps | Skip events?: {MIDIClock.skipevents}");
                playedevents2 = playedEvents;
                totalFrames = 0;
                Thread.Sleep(1000/30);
            }
        }
    }
}