using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpMIDI
{
    static unsafe class Sound
    {
        const int BufferBits = 16;
        const int BufferSize = 1 << BufferBits;
        const int BufferMask = BufferSize - 1;
        static uint* bufStart;
        static uint* bufEnd;
        static volatile uint* headPtr;
        static volatile uint* tailPtr; 
        static bool running = false;
        static Thread? audthread;
        
        private static int engine = 0;
        public static long playedEvents = 0;
        static string lastWinMMDevice = "";
        private static IntPtr? handle;
        public static delegate* unmanaged[SuppressGCTransition]<uint,uint> sendTo;
        public static bool Init(int synth, string winMMdev)
        {
            Close();
            AllocateEvBuffer();
            StartAudioThread();
            switch (synth)
            {
                case 1:
                    bool KDMAPIAvailable = false;
                    try { KDMAPIAvailable = KDMAPI.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (KDMAPIAvailable)
                    {
                        KDMAPI.InitializeFunctionPointer();
                        int loaded = KDMAPI.InitializeKDMAPIStream();
                        if (loaded == 1)
                        {
                            engine = 1;
                            sendTo = KDMAPI._sendDirectData;
                            return true;
                        }
                        else { return false; }
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
                        lastWinMMDevice = winMMdev;
                        return true;
                    }
                case 3:
                    bool XSynthAvailable = false;
                    try { XSynthAvailable = XSynth.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (XSynthAvailable)
                    {
                        XSynth.InitializeFunctionPointer();
                        int loaded = XSynth.InitializeKDMAPIStream();
                        if (loaded == 1)
                        {
                            engine = 3;
                            sendTo = XSynth._sendDirectData;
                            return true;
                        }
                        else { MessageBox.Show("KDMAPI is not available."); return false; }
                    }
                    else { return false; }
                default:
                    return false;
            }
        }

        static void AllocateEvBuffer()
        {    
            uint* mem = (uint*)NativeMemory.Alloc(sizeof(uint) * BufferSize);

            bufStart = mem;
            bufEnd   = mem + BufferSize;
    
            headPtr = bufStart;
            tailPtr = bufStart;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Submit(uint ev)
        {
            uint* h = headPtr;
            *h = ev;
            h++;
            
            // pointer wrap
            if (h == bufEnd) h = bufStart;

            headPtr = h;
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
            SpinWait spinWait = new SpinWait();
            uint* t = tailPtr;
            uint* h = headPtr;
            uint* start = bufStart;
            uint* end = bufEnd;
            while (running)
            {
                var sendFn = sendTo;
                if (t == h)
                {
                    spinWait.SpinOnce();
                    h = headPtr;
                    continue;
                }
                // prefetching stuff
                _ = *(t + 1);
                _ = *(t + 4);
                _ = *(t + 8);
                
                uint ev = *t;
                sendFn(ev);
                t++;
                if (t == end) t = start;

                tailPtr = t;
            }
        }
        
        public static void Close()
        {
            running = false;
            audthread?.Join(100);
            
            if (bufStart != null)
            {
                NativeMemory.Free(bufStart);
                bufStart = null;
                bufEnd = null;
                headPtr = null;
                tailPtr = null;
            }
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
