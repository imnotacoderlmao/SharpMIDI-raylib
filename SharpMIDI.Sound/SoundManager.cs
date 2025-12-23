
using System.Runtime.InteropServices;

namespace SharpMIDI
{
    static unsafe class Sound
    {
        static uint* ring;      // buffer
        static uint write;
        //static ushort read;
        const int bufferSize = 2097152;
        const int bufferMask = 2097151;
        static bool running = false;
        static Thread? audthread;
        
        private static int engine = 0;
        private static IntPtr? handle;
        static delegate* unmanaged[SuppressGCTransition]<uint,void> sendTo;
        public static bool Init(int synth, string winMMdev)
        {
            Close();
            AllocateEvBuffer();
            // idk how to start the audio thread in a better way without having sendfn being declared inside the while loop
            switch (synth)
            {
                case 1:
                    bool KDMAPIAvailable = false;
                    try { KDMAPIAvailable = KDMAPI.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (KDMAPIAvailable)
                    {
                        KDMAPI.InitializeFunctionPointer();
                        int loaded = KDMAPI.InitializeKDMAPIStream();
                        if (loaded != 1) return false;
                        engine = 1;
                        sendTo = KDMAPI._sendDirectData;
                        StartAudioThread();
                        return true;
                    }
                    else { MessageBox.Show("KDMAPI is not available."); return false; }
                case 2:
                    (bool, string, string, IntPtr?, MidiOutCaps?) result = WinMM.Setup(winMMdev);
                    if (!result.Item1)
                    {
                        MessageBox.Show(result.Item3);
                        return false;
                    }
                    else
                    {
                        engine = 2;
                        sendTo = WinMM._midiOutShortMsg;
                        handle = result.Item4;
                        StartAudioThread();
                        return true;
                    }
                case 3:
                    bool XSynthAvailable = false;
                    try { XSynthAvailable = XSynth.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (XSynthAvailable)
                    {
                        XSynth.InitializeFunctionPointer();
                        int loaded = XSynth.InitializeKDMAPIStream();
                        if (loaded != 1) return false;
                        engine = 3;
                        sendTo = XSynth._sendDirectData;
                        StartAudioThread();
                        return true;
                    }
                    else { MessageBox.Show("XSynth is not available."); return false; }
                default:
                    return false;
            }
        }

        static void AllocateEvBuffer()
        {    
            ring = (uint*)NativeMemory.AlignedAlloc(bufferSize * sizeof(uint), 64);
            write = 0;
        }
        
        public static void Submit(uint ev)
        {
            ring[write & bufferMask] = ev;
            write++;
        }

        static void StartAudioThread()
        {
            if (running) return;
            running = true;
            audthread = new Thread(AudioThread)
            {
                IsBackground = true,
            };
            audthread.Start();
        }

        static void AudioThread()
        {
            uint* buffer = ring;
            uint readidx = 0;
            var sendfn = sendTo;
            
            while (running)
            {
                uint writeidx = write;
                
                while (readidx <= write)
                {
                    sendfn(buffer[readidx & bufferMask]);
                    readidx++;
                }
                
                if (readidx == writeidx) 
                {
                    Thread.Sleep(0);
                    continue;
                }
            }
        }
        
        static void Close()
        {
            running = false;
            audthread?.Join(100);
            switch(engine){
                case 1:
                    KDMAPI.TerminateKDMAPIStream();
                    return;
                case 2:
                    if(handle!=null){
                        WinMM.midiOutClose((IntPtr)handle);
                    }
                    return;
                case 3:
                    XSynth.TerminateKDMAPIStream();
                    return;
            }
        }
    }
}
