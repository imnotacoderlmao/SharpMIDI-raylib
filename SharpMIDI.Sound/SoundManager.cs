
using System.Runtime.InteropServices;

namespace SharpMIDI
{
    static unsafe class Sound
    {
        public static uint24* ringbuffer;
        public const int bufferSize = ushort.MaxValue;
        static bool running = false;
        public static bool issynthinitiated = false;
        static Thread? audthread; 
        private static int engine = 0;
        static delegate* unmanaged[SuppressGCTransition]<uint,void> sendTo;
        public static void InitSynth(string synth)
        {
            Close();
            AllocateEvBuffer();
            switch (synth)
            {
                case "KDMAPI":
                    try 
                    { 
                        if(!KDMAPI.IsKDMAPIAvailable()) return;
                        KDMAPI.InitializeFunctionPointer();
                        KDMAPI.InitializeKDMAPIStream();
                        engine = 1;
                        sendTo = KDMAPI._sendDirectData;
                        issynthinitiated = true; 
                        return;
                    } catch (DllNotFoundException) 
                    { 
                        Console.WriteLine("KDMAPI is not available.");
                        return;
                    }
                case "XSynth":
                    try 
                    {
                        if(!XSynth.IsKDMAPIAvailable()) return;
                        XSynth.InitializeFunctionPointer();
                        int loaded = XSynth.InitializeKDMAPIStream();
                        engine = 3;
                        sendTo = XSynth._sendDirectData;
                        issynthinitiated = true; 
                        return;
                    } catch (DllNotFoundException) 
                    { 
                        Console.WriteLine("XSynth is not available."); 
                        return; 
                    }
                default:
                    Console.WriteLine($"{synth} is not a valid option!");
                    return;
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
            for (int channel = 0; channel < 16; ++channel)
                sendTo((uint)(0xB0 | channel) | (0x7B << 8)); 
        }

        static void Close()
        {
            issynthinitiated = false; 
            switch(engine){
                case 1:
                    KDMAPI.TerminateKDMAPIStream();
                    return;
                case 2:
                    XSynth.TerminateKDMAPIStream();
                    return;
            }
        }
    }
}
