#pragma warning disable 8618
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace SharpMIDI
{
    public static unsafe class GLNoteRenderer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RenderNote
        {
            public int StartTick;
            public uint PackedData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct KeyHeader
        {
            public int ActiveAbsId;
            public int ActiveCount;
        }

        private const string LineVertSrc =
@"#version 430 core
struct RenderNote {
    int StartTick;
    uint PackedData;
};

layout(std430, binding = 0) readonly buffer NoteBuffer {
    RenderNote notes[];
};
uniform vec3 uMetrics;
uniform int uViewStart;
uniform int uViewEnd;
uniform int uRingStart;
uniform int uRingMask;
uniform int uCurrentTick;
uniform sampler2D uPalette;

flat out vec4 vColor;
flat out int vIsActive;

void main() {
    int physIdx = (gl_InstanceID + uRingStart) & uRingMask;
    RenderNote notedata = notes[physIdx];
    int aStartTick = notedata.StartTick;
    uint aPackedData = notedata.PackedData;
    int rawdur = int(aPackedData & 0xFFFFu);
    bool isNoteOff = bool(rawdur > 0? 1 : 0);
    float dur = isNoteOff? float(rawdur) : float(uViewEnd - aStartTick);
    uint isEnd = uint(gl_VertexID) & 1u;
    uint isTop = uint(gl_VertexID >> 1) & 1u;
    float startX = float(aStartTick - uViewStart) * uMetrics.x - 1.0;
    float endX = startX + dur * uMetrics.x;
    float x = bool(isEnd)? endX : startX;
    float y = uMetrics.y + float(((aPackedData >> 16) & 0xFFu) + isTop) * uMetrics.z;
    float z = dur / 65536.0;
    vColor = texelFetch(uPalette, ivec2(int(aPackedData >> 24), 0), 0);
    
    int endTick = aStartTick + (isNoteOff? int(rawdur) : (uViewEnd - aStartTick));
    vIsActive = (uCurrentTick >= aStartTick && uCurrentTick <= endTick) ? 1 : 0;

    gl_Position = vec4(x, y, z, 1.0);
}";

        private const string LineFragSrc =
@"#version 430 core
flat in vec4 vColor;
flat in int vIsActive;

uniform int uGlowEnabled;
out vec4 fragColor;

void main() {
    fragColor = (uGlowEnabled == 1 && vIsActive == 1)? vec4(min(vColor.rgb * 2 + vec3(0.1), vec3(1.0)), vColor.a) : vColor;
}";

        private static GL Gl;
        private const BufferStorageMask storageFlags = BufferStorageMask.MapWriteBit |
                                                       BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit;
        private const MapBufferAccessMask accessFlags = MapBufferAccessMask.WriteBit | 
                                                        MapBufferAccessMask.PersistentBit | MapBufferAccessMask.CoherentBit;
        private static uint _lineShader;
        private static int _uMetrics, _uViewStart, _uViewEnd;
        private static int _uRingStart, _uRingMask, _uPalette;
        private static int _uGlowEnabled, _uCurrentTick;
        private static uint _vao, _ssboBuffer, _paletteTex;

        private static RenderNote* _ring;
        // shadow cpu array, 2x memory usage BOOOOO (hey at least _ring is actually on vram now)
        private static RenderNote* _cpuRing;

        private static int _ringCap = 1 << 23;
        private static int _deferred_ringCap = -1;
        private static int _mask;
        
        private static int _head = 1;
        private static int _tail = 1;
        
        private readonly static byte[] paletteData = new byte[16 * 3];
        private static bool _paletteUploadPending = false;
        
        private static KeyHeader* _keyHeaders;
        private const int TOTAL_KEYS = 128 * 16 * 16;

        private static int _lookaheadTicks = 4000;
        private static float _pixelsPerTick;
        private static int _lastWindowTicks = -1;
        private static int _lastSweepEnd = -1;
        private static bool _isInitialized;

        public static int WindowTicks = 2000;
        public static int RingCap;
        public static int NotesDrawnLastFrame;
        public static bool UseForceCull = false;
        public static bool EnableGlow = true;

        public static void Initialize()
        {
            if (_isInitialized) return;
            Gl = GL.GetApi(NativeGLBindingsContext.GetProcAddress);

            _lineShader   = BuildShader(LineVertSrc, LineFragSrc);
            _uMetrics     = Gl.GetUniformLocation(_lineShader, "uMetrics");
            _uViewStart   = Gl.GetUniformLocation(_lineShader, "uViewStart");
            _uViewEnd     = Gl.GetUniformLocation(_lineShader, "uViewEnd");
            _uRingStart   = Gl.GetUniformLocation(_lineShader, "uRingStart");
            _uRingMask    = Gl.GetUniformLocation(_lineShader, "uRingMask");
            _uPalette     = Gl.GetUniformLocation(_lineShader, "uPalette");
            _uGlowEnabled = Gl.GetUniformLocation(_lineShader, "uGlowEnabled");
            _uCurrentTick = Gl.GetUniformLocation(_lineShader, "uCurrentTick");

            Gl.UseProgram(_lineShader);
            Gl.Uniform1(_uPalette, 0);
            Gl.UseProgram(0);

            _keyHeaders = (KeyHeader*)NativeMemory.Alloc(TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            
            _vao = Gl.GenVertexArray();
            _paletteTex = Gl.GenTexture();
                        
            AllocRing(_ringCap);
            _isInitialized = true;
        }
        
        private static void AllocRing(int cap)
        {
            _mask = cap - 1;
            
            if (_ssboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
                Gl.DeleteBuffer(_ssboBuffer);
            }
            
            _ssboBuffer = Gl.GenBuffer();
            Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssboBuffer);
            
            nuint totalBytes = (nuint)(cap * sizeof(RenderNote));
            Gl.BufferStorage(GLEnum.ShaderStorageBuffer, totalBytes, null, (uint)storageFlags);
            _ring = (RenderNote*)Gl.MapBufferRange(BufferTargetARB.ShaderStorageBuffer, 0, totalBytes, (uint)accessFlags);
            Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

            if (_cpuRing != null) NativeMemory.Free(_cpuRing);
            _cpuRing = (RenderNote*)NativeMemory.AllocZeroed(totalBytes);

            _ringCap = cap;
        }

        public static void InitializeForMIDI()
        {
            _isInitialized = false;
            NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            _head = 1;
            _tail = 1;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            _paletteUploadPending = true;
            _isInitialized = true;
        }

        public static void ResetForUnload()
        {
            _isInitialized = false;
            _head = 1;
            _tail = 1;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            if (_ringCap != 1 << 23) 
                _deferred_ringCap = 1 << 23;
        }

        public static void Dispose()
        {
            _isInitialized = false;
            if (_keyHeaders != null) { NativeMemory.Free(_keyHeaders); _keyHeaders = null; }
            if (_cpuRing != null) { NativeMemory.Free(_cpuRing); _cpuRing = null; }
            if (_ssboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
                Gl.DeleteBuffer(_ssboBuffer);
                _ssboBuffer = 0;
                _ring = null;
            }
            if (_paletteTex != 0) { Gl.DeleteTexture(_paletteTex); _paletteTex = 0; }
            if (_vao != 0) { Gl.DeleteVertexArray(_vao); _vao = 0; }
            if (_lineShader != 0) { Gl.DeleteProgram(_lineShader); _lineShader = 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (_paletteUploadPending)
            {
                for (int i = 0; i < 16; i++)
                {
                    uint c = (uint)Random.Shared.Next(0x808080, 0x1000000);
                    paletteData[i * 3 + 0] = (byte)((c >> 16) & 0xFF);
                    paletteData[i * 3 + 1] = (byte)((c >>  8) & 0xFF);
                    paletteData[i * 3 + 2] = (byte)( c        & 0xFF);
                }
                Gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
                fixed (byte* ptr = paletteData)
                    Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8, 16, 1, 0, PixelFormat.Rgb, PixelType.UnsignedByte, ptr);
                Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                Gl.BindTexture(TextureTarget.Texture2D, 0);
                _paletteUploadPending = false;
            }

            if (_deferred_ringCap > 0) 
            { 
                ResizeRing(_deferred_ringCap); 
                _deferred_ringCap = -1; 
            }

            if (!MIDILoader.midiLoaded) 
                return;

            int maxtick = MIDILoader.maxTick - 1;
            int half = WindowTicks >> 1;
            int viewStart = Math.Clamp(tick - half, 0, maxtick);
            int viewEnd = Math.Clamp(tick + half, 0, maxtick);

            if (WindowTicks != _lastWindowTicks)
            {
                _pixelsPerTick = 2.0f / WindowTicks;
                _lastWindowTicks = WindowTicks;
                _lookaheadTicks = Math.Min(WindowTicks / 2, 2000);
            }

            int sweepEnd = Math.Clamp(viewEnd + _lookaheadTicks, 0, maxtick);
            bool incremental = _lastSweepEnd >= 0 && sweepEnd >= _lastSweepEnd && sweepEnd - _lastSweepEnd < WindowTicks;

            if (!incremental)
            {
                _head = 1;
                _tail = 1;
                NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
                SweepRange(Math.Max(0, viewStart - WindowTicks), sweepEnd);
            }
            else
            {
                SweepRange(_lastSweepEnd + 1, sweepEnd);
            }

            _lastSweepEnd = sweepEnd;
            AdvanceTail(viewStart);
            // bulk write to ring instead of doing it every event
            int uploadCount = _head - _tail;
            if (uploadCount > 0)
            {
                int startIdx = _tail & _mask;
                if (startIdx + uploadCount <= _ringCap)
                    Unsafe.CopyBlock(_ring + startIdx, _cpuRing + startIdx, (uint)(uploadCount * sizeof(RenderNote)));
                else // wraparound case. should be rare enough but just for safety sakes
                {
                    int firstChunk = _ringCap - startIdx;
                    int secondChunk = uploadCount - firstChunk;
                    Unsafe.CopyBlock(_ring + startIdx, _cpuRing + startIdx, (uint)(firstChunk * sizeof(RenderNote)));
                    Unsafe.CopyBlock(_ring, _cpuRing, (uint)(secondChunk * sizeof(RenderNote)));
                }
            }

            Raylib_cs.Rlgl.DrawRenderBatchActive();

            NotesDrawnLastFrame = uploadCount;
            RingCap = _ringCap;

            if (uploadCount > 0)
            {
                float yBottom = -1.0f + 2.0f * pad / screenHeight;
                float yTop = 1.0f - 2.0f * pad / screenHeight;
                float yStep = (yTop - yBottom) / 128.0f;

                Gl.Viewport(0, 0, (uint)screenWidth, (uint)screenHeight);
                Gl.Enable(EnableCap.DepthTest);
                Gl.DepthFunc(DepthFunction.Less);
                Gl.UseProgram(_lineShader);

                Gl.Uniform3(_uMetrics, _pixelsPerTick, yBottom, yStep);
                Gl.Uniform1(_uViewStart, viewStart);
                Gl.Uniform1(_uViewEnd, viewEnd);
                Gl.Uniform1(_uRingStart, _tail & _mask);
                Gl.Uniform1(_uRingMask, _mask);
                Gl.Uniform1(_uGlowEnabled, EnableGlow ? 1 : 0);
                Gl.Uniform1(_uCurrentTick, tick);

                Gl.ActiveTexture(TextureUnit.Texture0);
                Gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
                
                Gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _ssboBuffer);

                Gl.BindVertexArray(_vao);
                Gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)uploadCount);
                Gl.BindVertexArray(0);

                Gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, 0);
                Gl.ActiveTexture(TextureUnit.Texture0);
                Gl.BindTexture(TextureTarget.Texture2D, 0);
                Gl.UseProgram(0);
                Gl.Disable(EnableCap.DepthTest);
            }
            int linepos = (int)Math.Min(tick * _pixelsPerTick * screenWidth / 2, screenWidth / 2);
            Raylib_cs.Raylib.DrawLine(linepos, 0, linepos, screenHeight, Raylib_cs.Color.Red);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SweepRange(int fromTick, int toTick)
        {
            // this is ghetto as hell mane
            RenderNote* ringLocal = _cpuRing;
            KeyHeader* keyheader = _keyHeaders;

            BigArray<TickGroup> groups = MIDIEvent.TickGroupArray;
            uint24* messages = SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track != null ? SynthEvent.track.Pointer : null;
            bool useTrack = tracks != null;

            int limit = Math.Min(toTick, (int)groups.Length - 2);
            TickGroup* tickgroups = groups.Pointer;
            int headLocal = _head;
            int maskLocal = _mask;
            int tailLocal = _tail;
            int cap = _ringCap;

            long currentOffset = tickgroups[fromTick].offset;
            int tick = fromTick;

            while (tick <= limit)
            {
                long nextOffset = tickgroups[tick + 1].offset;
                long totalEventsInTick = nextOffset - currentOffset;
                
                if (totalEventsInTick > 0)
                {
                    while (headLocal - tailLocal + totalEventsInTick >= cap)
                    {
                        _tail = tailLocal;
                        _head = headLocal;
                        ResizeRing(cap * 2);
                        cap = _ringCap;
                        maskLocal = _mask;
                        ringLocal = _cpuRing;
                    }
                }

                while (currentOffset < nextOffset)
                {
                    byte* synthev = (byte*)messages + (currentOffset * 3);
                    if ((synthev[0] & 0xE0) != 0x80)
                    {
                        currentOffset++;
                        continue;
                    }

                    byte channel = (byte)(synthev[0] & 0xFu);
                    byte key = synthev[1];
                    uint colorIdx = useTrack? (uint)(tracks[currentOffset] + channel) & 0xFu : channel;

                    // "key" is now used to merge notes instead of tracking oldest/newest for the linked list. which sadly means duration based layering kinda goes bye bye.
                    // its also why indexing became whatever concoction this is
                    KeyHeader* header = keyheader + ((channel << 11) | (key << 4) | colorIdx);

                    if ((synthev[0] & 0x10) == 0) // NoteOff
                    {
                        if (header->ActiveCount > 0)
                        {
                            header->ActiveCount--;
                            if (header->ActiveCount == 0)
                            {
                                int absId = header->ActiveAbsId;
                                if (absId >= headLocal - cap)
                                {
                                    int physIdx = absId & maskLocal;
                                    ringLocal[physIdx].PackedData |= (ushort)(tick - ringLocal[physIdx].StartTick);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (header->ActiveCount == 0)
                        {
                            int physIdx = headLocal & maskLocal;

                            header->ActiveAbsId = headLocal;
                            header->ActiveCount = 1;

                            ringLocal[physIdx] = new RenderNote
                            {
                                StartTick = tick,
                                PackedData = (colorIdx << 24) | ((uint)key << 16)
                            };
                            headLocal++;
                        }
                        else
                            header->ActiveCount++;
                    }
                    currentOffset++;
                }
                currentOffset = nextOffset;
                tick++;
            }
            _tail = tailLocal;
            _head = headLocal;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceTail(int viewStart)
        {
            int safeTail = _head - _ringCap;
            if (_tail < safeTail) 
                _tail = safeTail;

            int forcecullthresh = Math.Min(_lookaheadTicks * 2, ushort.MaxValue);
            bool forceCull = UseForceCull && NotesDrawnLastFrame > 262144;
            int forceCullBefore = viewStart - forcecullthresh;
            
            RenderNote* ring = _cpuRing;
            int maskLocal = _mask;
            int headLocal = _head;
            int tailLocal = _tail;

            while (tailLocal < headLocal)
            {
                int physIdx = tailLocal & maskLocal;
                RenderNote note = ring[physIdx];
                bool isopen = (note.PackedData & 0xFFFFu) == 0;
                int startTick = note.StartTick;
                int endTick = (int)(startTick + (note.PackedData & 0xFFFFu));
                
                if ((!isopen && endTick < viewStart) || (forceCull && startTick < forceCullBefore))
                    tailLocal++;
                else
                    break;
            }
            _tail = tailLocal;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResizeRing(int newCap)
        {
            if (newCap < 0) 
                return;
            int newMask = newCap - 1;
            
            nuint totalBytes = (nuint)newCap * (nuint)sizeof(RenderNote);
            uint newBuffer = Gl.GenBuffer();
            Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, newBuffer);
            Gl.BufferStorage(GLEnum.ShaderStorageBuffer, totalBytes, null, (uint)storageFlags);
            RenderNote* newRing = (RenderNote*)Gl.MapBufferRange(BufferTargetARB.ShaderStorageBuffer, 0, totalBytes, (uint)accessFlags);
            RenderNote* newCpuRing = (RenderNote*)NativeMemory.AllocZeroed(totalBytes);
            
            if (_cpuRing != null && _head > _tail)
            {
                int remaining = _head - _tail;
                int absId = _tail;
                
                while (remaining > 0)
                {
                    int oldIdx = absId & _mask;
                    int newIdx = absId & newMask;
                    int chunk = Math.Min(remaining, Math.Min(_ringCap - oldIdx, newCap - newIdx));

                    Unsafe.CopyBlock(newCpuRing + newIdx, _cpuRing + oldIdx, (uint)(chunk * sizeof(RenderNote)));
                    Unsafe.CopyBlock(newRing + newIdx, newCpuRing + newIdx, (uint)(chunk * sizeof(RenderNote)));
                    
                    absId += chunk;
                    remaining -= chunk;
                }
            }

            if (_ssboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer);
                Gl.DeleteBuffer(_ssboBuffer);
            }
            
            if (_cpuRing != null) 
                NativeMemory.Free(_cpuRing);

            _cpuRing = newCpuRing;
            _ssboBuffer = newBuffer;
            _ring = newRing;
            _mask = newMask;
            _ringCap = newCap;

            Gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        }

        private static uint BuildShader(string vert, string frag)
        {
            uint vertex = CompileStage(ShaderType.VertexShader, vert);
            uint fragment = CompileStage(ShaderType.FragmentShader, frag);
            uint program = Gl.CreateProgram();
            Gl.AttachShader(program, vertex);
            Gl.AttachShader(program, fragment);
            Gl.LinkProgram(program);
            Gl.GetProgram(program, GLEnum.LinkStatus, out int ok);
            if (ok == 0) 
                throw new Exception("Shader link:\n" + Gl.GetProgramInfoLog(program));
            Gl.DeleteShader(vertex);
            Gl.DeleteShader(fragment);
            return program;
        }

        private static uint CompileStage(ShaderType type, string src)
        {
            uint shader = Gl.CreateShader(type);
            Gl.ShaderSource(shader, src);
            Gl.CompileShader(shader);
            Gl.GetShader(shader, GLEnum.CompileStatus, out int ok);
            if (ok == 0) 
                throw new Exception($"{type}:\n" + Gl.GetShaderInfoLog(shader));
            return shader;
        }

        public class NativeGLBindingsContext
        {
            private static readonly nint s_glLibrary = LoadGL();
            private static nint LoadGL()
            {
                if (OperatingSystem.IsWindows()) 
                    return NativeLibrary.Load("opengl32.dll");
                if (OperatingSystem.IsLinux())
                    return NativeLibrary.TryLoad("libGL.so.1", out nint h) ? h : NativeLibrary.Load("libGL.so");
                if (OperatingSystem.IsMacOS())
                    return NativeLibrary.Load("/System/Library/Frameworks/OpenGL.framework/OpenGL");
                throw new PlatformNotSupportedException();
            }
            [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", ExactSpelling = true)]
            private static extern nint WglGetProcAddress(string name);
            public static nint GetProcAddress(string procName)
            {
                if (OperatingSystem.IsWindows())
                {
                    nint addr = WglGetProcAddress(procName);
                    if (addr is 0 or 1 or 2 or 3 or -1)
                        NativeLibrary.TryGetExport(s_glLibrary, procName, out addr);
                    return addr;
                }
                NativeLibrary.TryGetExport(s_glLibrary, procName, out nint unixAddr);
                return unixAddr;
            }
        }
    }
}