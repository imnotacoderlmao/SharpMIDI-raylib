using System.Runtime.InteropServices;
namespace SharpMIDI
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct MIDIHDR
    {
        public byte* lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public MIDIHDR* lpNext;
        public IntPtr reserved;
        public uint dwOffset;

        public IntPtr dwReserved0;
        public IntPtr dwReserved1;
        public IntPtr dwReserved2;
        public IntPtr dwReserved3;
        public IntPtr dwReserved4;
        public IntPtr dwReserved5;
        public IntPtr dwReserved6;
        public IntPtr dwReserved7;
    }
    
    static unsafe class Sound
    {
        public static uint24* ringbuffer;
        public const int bufferSize = ushort.MaxValue;
        static bool running = false;
        public static bool issynthinitiated = false;
        //static string lastWinMMDevice = "";
        static Thread? audthread; 
        //private static IntPtr? handle;
        private static int engine = 0;
        public static delegate* unmanaged[SuppressGCTransition]<uint, void> sendTo;
        //public static delegate* unmanaged[SuppressGCTransition]<IntPtr, uint, uint> sendToWinMM
        //public static void InitSynth(string synth, string WinMMDevice)
        public static void InitSynth(string synth)
        {
            Close();
            AllocateEvBuffer();
            switch (synth)
            {
                case "KDMAPI":
                    try 
                    { 
                        KDMAPI.Load();
                        KDMAPI._initializeKDMAPIStream();
                        engine = 1;
                        sendTo = KDMAPI._sendDirectData;
                        issynthinitiated = true; 
                        return;
                    } catch (DllNotFoundException) 
                    { 
                        Console.WriteLine($"{synth} is not available.");
                        MIDILoader.loadstatus = $"{synth} is not available."; 
                        return;
                    }
                /*case "WinMM":
                    (bool, string, string, IntPtr?, MidiOutCaps?) result = WinMM.Setup(WinMMDevice);
                    if (!result.Item1)
                    {
                        Console.WriteLine(result.Item3);
                        return;
                    }
                    else
                    {
                        engine = 2;
                        sendToWinMM = WinMM._midiOutShortMsg;
                        handle = result.Item4;
                        lastWinMMDevice = WinMMDevice;
                        return;
                    }*/
            }
        }

        static void AllocateEvBuffer()
        {    
            ringbuffer = (uint24*)NativeMemory.AllocZeroed((nuint)(bufferSize * sizeof(uint24)));
        }
        
        public static void StartAudioThread()
        {
            if (running) return;
            running = true;
            audthread = new Thread(AudioThread)
            {
                IsBackground = true
            };
            audthread.Start();
        }

        static void AudioThread()
        {
            uint24* buffer = ringbuffer;
            ushort readidx = 0;
            var sendfn = sendTo;
            
            while (running)
            {
                uint val = (uint)buffer[readidx].Value;
                if (val != 0)
                {
                    sendfn(val);
                    buffer[readidx] = 0;
                }
                readidx++;
            }
        }
        
        public static void KillAudioThread()
        {
            running = false;
            audthread?.Join(100);
        }

        public static void AllNotesOFF()
        {
            if (issynthinitiated)
            {
                for (int channel = 0; channel < 16; ++channel)
                    sendTo((uint)(0xB0 | channel) | (0x7B << 8));
            } 
        }


        static void Close()
        {
            issynthinitiated = false; 
            switch(engine){
                case 1:
                    KDMAPI._terminateKDMAPIStream();
                    return;
                /*case 2:
                    WinMM._midiOutClose();
                    return;*/
            }
        }
    }
}
