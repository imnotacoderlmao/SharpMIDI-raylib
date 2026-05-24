using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Core.Native;
using OpenTK;

namespace SharpMIDI
{
    public static unsafe class GLNoteRenderer
    {
        // PoolSlot encodes (key<<2)|ringSlot for back-patching on compaction
        [StructLayout(LayoutKind.Sequential)]
        private struct Note
        {
            public int  StartTick;
            public int  EndTick;
            public uint ColorPitch; //pitch stored in alpha's place lmao
            public int  PoolSlot;
        }

        // Fixed 4-slot ring per (channel,key) pair.
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyHeader
        {
            public int Head;
            public int Count;
            public fixed int NoteIdx[4];
        }

        // Depth goes through gl_Position.z so early-z can reject long-note fragments
        // before the fragment shader runs. No gl_FragDepth means early-z stays active.
        // short note = depth near 0 (wins GL_LEQUAL = drawn on top)
        // long note = depth near 1 (loses to shorter notes at same pixel)
        // open note = depth = 1.0 (behind all closed notes, GL_LEQUAL lets it pass the initial clear (1.0 <= 1.0 = true))
        private const string LineVertSrc =
            "#version 330 core\n"                                                                           +
            "layout(location = 0) in ivec2 aTicks;\n"                                                       +
            "layout(location = 1) in vec4  aColorPitchNorm;\n"                                              +
            "uniform int uViewStart;\n"                                                                     +
            "uniform int uViewEnd;\n"                                                                       +
            "uniform float uPpt;\n"                                                                         +
            "flat out vec4 vColor;\n"                                                                       +
            "void main()\n"                                                                                 +
            "{\n"                                                                                           +
            "    const float kScale  = 255.0 / 64.0;\n"                                                     +
            "    const float kBias   = 0.5   / 64.0 - 1.0;\n"                                               +
            "    const float kMaxDur = 65535.0;\n"                                                          +
            "    int endTick = aTicks.y < 0 ? uViewEnd : aTicks.y;\n"                                       +
            "    int tick = gl_VertexID == 0 ? aTicks.x : endTick;\n"                                       +
            "    float x = float(tick - uViewStart) * uPpt - 1.0;\n"                                        +
            "    float y = aColorPitchNorm.a * kScale + kBias;\n"                                           +
            "    float dur = aTicks.y < 0 ? kMaxDur : min(float(aTicks.y - aTicks.x), kMaxDur);\n"          +
            "    float ndcZ = (dur / kMaxDur) * 2.0 - 1.0;\n"                                               +
            "    vColor = vec4(aColorPitchNorm.rgb, 1.0);\n"                                                +
            "    gl_Position = vec4(x, y, ndcZ, 1.0);\n"                                                    +
            "}\n";

        private const string LineFragSrc =
            "#version 330 core\n"                   +
            "flat in  vec4 vColor;\n"               +
            "out vec4 fragColor;\n"                 +
            "void main()\n"                         + 
            "{\n"                                   + 
            "   fragColor = vColor;\n"              +
            "}\n";

        private const string BlitVertSrc =
            "#version 330 core\n"                                               +
            "uniform float uYBottom;\n"                                         +
            "uniform float uYTop;\n"                                            +
            "out vec2 vUV;\n"                                                   +
            "void main()\n"                                                     +
            "{\n"                                                               +
            "    float x = (gl_VertexID & 1) == 0 ? -1.0 : 1.0;\n"              +
            "    float t = (gl_VertexID >> 1) == 0 ?  0.0 : 1.0;\n"             +
            "    gl_Position = vec4(x, mix(uYBottom, uYTop, t), 0.0, 1.0);\n"   +
            "    vUV = vec2((x + 1.0) * 0.5, t);\n"                             +
            "}\n";

        private const string BlitFragSrc =
            "#version 330 core\n"                               +
            "uniform sampler2D uTex;\n"                         +
            "in vec2 vUV;\n"                                    +
            "out vec4 fragColor;\n"                             +
            "void main()\n"                                     + 
            "{\n"                                               + 
            "   fragColor = texture(uTex, vUV);\n"              + 
            "}\n";

        private static int _lineShader, _blitShader;
        private static int _uViewStart, _uViewEnd, _uPpt;
        private static int _uYBottom, _uYTop, _uTex;
        private static int _vao, _vbo, _blitVao;
        private static int _vboCap;
        private static int _fbo, _fboTex, _fboDepth, _fboWidth;

        private static Note* notePool;
        private static int activeCount;
        public  static int activeCap;
        private static KeyHeader* KeyHeaders;

        private const int INITIAL_CAP = ushort.MaxValue + 1;
        private const int TOTAL_KEYS = 128 * 16; // 128 keys, 16 channels
        private const int COLOR_SIZE = byte.MaxValue + 1;

        private static readonly uint[] colorTable = new uint[COLOR_SIZE];

        private static float PixelsPerTick; 
        private static int lastWindowTicks = -1;
        private static int lastNoteClosedTick = int.MaxValue;
        private static bool isInitialized; 
        private static int lastViewEnd = -1;

        public static int WindowTicks = 2000;
        public static int NotesDrawnLastFrame;

        public static void Initialize()
        {
            if (isInitialized) return;

            GL.LoadBindings(new NativeGLBindingsContext());

            _lineShader = BuildShader(LineVertSrc, LineFragSrc);
            _uViewStart = GL.GetUniformLocation(_lineShader, "uViewStart");
            _uViewEnd = GL.GetUniformLocation(_lineShader, "uViewEnd");
            _uPpt = GL.GetUniformLocation(_lineShader, "uPpt");
 
            _blitShader = BuildShader(BlitVertSrc, BlitFragSrc);
            _uYBottom = GL.GetUniformLocation(_blitShader, "uYBottom");
            _uYTop = GL.GetUniformLocation(_blitShader, "uYTop");
            _uTex = GL.GetUniformLocation(_blitShader, "uTex");

            GL.UseProgram(_blitShader);
            GL.Uniform1(_uTex, 0);
            GL.UseProgram(0);
            GL.ActiveTexture(TextureUnit.Texture0);

            _blitVao = GL.GenVertexArray();

            _vbo = GL.GenBuffer();
            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribIPointer(0, 2, VertexAttribIntegerType.Int, 16, 0);
            GL.VertexAttribDivisor(0, 1);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 16, 8);
            GL.VertexAttribDivisor(1, 1);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

            KeyHeaders = (KeyHeader*)NativeMemory.AlignedAlloc(TOTAL_KEYS * (nuint)sizeof(KeyHeader), 64);
            activeCap = INITIAL_CAP;
            notePool = (Note*)NativeMemory.AlignedAlloc((nuint)(activeCap * sizeof(Note)), 64);

            isInitialized = true;
        }

        public static void InitializeForMIDI()
        {
            isInitialized = false;
            NativeMemory.Clear(KeyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            activeCount = 0;
            lastNoteClosedTick = int.MaxValue;
            lastViewEnd = -1;
            lastWindowTicks = -1;

            for (int i = 0; i < COLOR_SIZE; i++)
            {
                uint c = (uint)Random.Shared.Next(0x808080, 0x1000000);
                uint r = (c >> 16) & 0xFF;
                uint g = (c >>  8) & 0xFF;
                uint b =  c & 0xFF;
                colorTable[i] = r | (g << 8) | (b << 16);
            }

            isInitialized = true;
        }

        public static void ResetForUnload()
        {
            isInitialized = false;
            activeCount = 0;
            lastNoteClosedTick = int.MaxValue;
            lastViewEnd = -1;
            lastWindowTicks = -1;

            if (activeCap > INITIAL_CAP)
            {
                activeCap= INITIAL_CAP;
                notePool= (Note*)NativeMemory.AlignedRealloc(notePool, (nuint)(activeCap * sizeof(Note)), 64);
            }
        }

        public static void Dispose()
        {
            isInitialized = false;
            if (KeyHeaders != null) { NativeMemory.AlignedFree(KeyHeaders); KeyHeaders = null; }
            if (notePool != null) { NativeMemory.AlignedFree(notePool); notePool = null; }

            DestroyFbo();

            if (_vbo != 0) { GL.DeleteBuffer(_vbo); _vbo = 0; }
            if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
            if (_blitVao != 0) { GL.DeleteVertexArray(_blitVao); _blitVao = 0; }
            if (_lineShader != 0) { GL.DeleteProgram(_lineShader); _lineShader = 0; }
            if (_blitShader != 0) { GL.DeleteProgram(_blitShader); _blitShader = 0; }
            _vboCap = 0;
        }

        private static void DestroyFbo()
        {
            if (_fboTex != 0) { GL.DeleteTexture(_fboTex); _fboTex = 0; }
            if (_fboDepth != 0) { GL.DeleteRenderbuffer(_fboDepth); _fboDepth = 0; }
            if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); _fbo = 0; }
            _fboWidth = 0;
        }

        private static void EnsureFbo(int width)
        {
            if (_fboWidth == width) return;
            DestroyFbo();

            _fboTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _fboTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, 128, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,     (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,     (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            _fboDepth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent32f, width, 128);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fboTex, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _fboWidth = width;
        }

        private static void EnsureVbo(int cap)
        {
            if (_vboCap >= cap) return;
            _vboCap = Math.Max(cap, _vboCap == 0 ? INITIAL_CAP : _vboCap * 2);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, (nint)(_vboCap * sizeof(Note)), IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (!MIDILoader.midiLoaded) return;

            int maxtick = MIDILoader.maxTick - 1;
            int half = WindowTicks >> 1;
            int viewStart = Math.Clamp(tick - half, 0, maxtick);
            int viewEnd = Math.Clamp(tick + half, 0, maxtick);

            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track.Pointer;

            bool incremental = lastViewEnd >= 0 && WindowTicks == lastWindowTicks
                            && viewEnd >= lastViewEnd && viewEnd - lastViewEnd < WindowTicks;

            if (!incremental)
            {
                activeCount = 0;
                lastNoteClosedTick = int.MaxValue;
                NativeMemory.Clear(KeyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
                SweepRange(Math.Max(0, viewStart - WindowTicks), viewEnd, groups, messages, tracks);
            }
            else
            {
                SweepRange(lastViewEnd + 1, viewEnd, groups, messages, tracks);
            }

            if (lastNoteClosedTick < viewStart)
                CullDeadNotes(viewStart);

            lastViewEnd = viewEnd;

            if (WindowTicks != lastWindowTicks)
            {
                PixelsPerTick = 2.0f / WindowTicks;
                lastWindowTicks = WindowTicks;
            }

            Raylib_cs.Rlgl.DrawRenderBatchActive();

            int active = activeCount;

            if (active > 0)
            {
                EnsureVbo(active);
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                Note* dst = (Note*)GL.MapBufferRange(BufferTarget.ArrayBuffer, IntPtr.Zero,
                (nint)(active * sizeof(Note)), MapBufferAccessMask.MapWriteBit | MapBufferAccessMask.MapInvalidateBufferBit);
                if (dst != null)
                {
                    NativeMemory.Copy(notePool, dst, (nuint)(active * sizeof(Note)));
                    GL.UnmapBuffer(BufferTarget.ArrayBuffer);
                }
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }

            EnsureFbo(screenWidth);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Viewport(0, 0, screenWidth, 128);
            GL.ClearColor(0f, 0f, 0f, 0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            // LEqual so open notes (depth=1.0) pass the initial clear (1.0 <= 1.0).
            // Shorter closed notes still overwrite them since their depth < 1.0.
            GL.DepthFunc(DepthFunction.Lequal);

            if (active > 0)
            {
                GL.UseProgram(_lineShader);
                GL.Uniform1(_uViewStart, viewStart);
                GL.Uniform1(_uViewEnd, viewEnd);
                GL.Uniform1(_uPpt, PixelsPerTick);
                GL.BindVertexArray(_vao);
                GL.DrawArraysInstanced(PrimitiveType.Lines, 0, 2, active);
                GL.BindVertexArray(0);
                GL.UseProgram(0);
            }

            GL.Disable(EnableCap.DepthTest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            float yBottom = -1.0f + 2.0f * pad / screenHeight;
            float yTop = 1.0f - 2.0f * pad / screenHeight;

            GL.Viewport(0, 0, screenWidth, screenHeight);
            GL.UseProgram(_blitShader);
            GL.Uniform1(_uYBottom, yBottom);
            GL.Uniform1(_uYTop, yTop);
            GL.BindTexture(TextureTarget.Texture2D, _fboTex);
            GL.BindVertexArray(_blitVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);

            NotesDrawnLastFrame = active;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SweepRange(int fromTick, int toTick, TickGroup[] groups, byte* messages, ushort* tracks)
        {
            bool useTrack = tracks != null;
            KeyHeader* _keyheaders = KeyHeaders;
            Note* pool = notePool;
            int minEnd = lastNoteClosedTick;
            int limit = Math.Min(toTick, groups.Length - 2);

            fixed (TickGroup* _group = groups)
            fixed (uint* palette = colorTable)
            {
                for (int tick = fromTick; tick <= limit; tick++)
                {
                    long offset = _group[tick].offset;
                    long next = _group[tick + 1].offset;
                    if (offset == next) continue;

                    byte* ev = messages + offset * 3;
                    byte* evEnd = messages + next * 3;

                    for (long idx = offset; ev < evEnd; ev += 3, idx++)
                    {
                        byte status = ev[0];
                        uint diff = (uint)(status & 0xF0) - 0x80u;
                        if (diff > 0x10u) 
                            continue;

                        byte channel = (byte)(status & 0xF);
                        byte note = ev[1];
                        KeyHeader* header = _keyheaders + (channel << 7 | note);

                        if (diff == 0) // NoteOff
                        {
                            if (header->Count > 0)
                            {
                                int noteIdx  = header->NoteIdx[header->Head];
                                header->Head = (header->Head + 1) & 3;
                                header->Count--;
                                int endTick  = pool[noteIdx].EndTick = tick;
                                if (endTick < minEnd) 
                                    minEnd = endTick;
                            }
                        }
                        else
                        {
                            if (header->Count >= 4) 
                                continue;

                            if (activeCount == activeCap)
                            {
                                activeCap *= 2;
                                notePool = (Note*)NativeMemory.AlignedRealloc(notePool, (nuint)(activeCap * sizeof(Note)), 64);
                                pool = notePool;
                            }

                            int ringTail = (header->Head + header->Count) & 3;
                            int noteidx = activeCount++;
                            byte colorIdx = useTrack ? (byte)(tracks[idx] + channel) : channel;

                            pool[noteidx].StartTick = tick;
                            pool[noteidx].EndTick = -1;
                            pool[noteidx].ColorPitch = palette[colorIdx] | ((uint)note << 24);
                            pool[noteidx].PoolSlot = (channel << 7 | note) << 2 | ringTail;

                            header->NoteIdx[ringTail] = noteidx;
                            header->Count++;
                        }
                    }
                }
            }

            lastNoteClosedTick = minEnd;
        }

        // Swap-remove method (O(evicted) since depth buffer handles draw order)
        // uint cast makes open notes (EndTick=-1 which overflows to 4 billion) survive without a separate branch
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void CullDeadNotes(int viewStart)
        {
            uint  _viewstartLocal = (uint)viewStart;
            Note* _pool = notePool;
            int lo = 0;
            int hi = activeCount - 1;
            int newMin = int.MaxValue;

            // Pre-shrink hi past any dead tail elements, drop with zero copies
            while (hi >= 0 && (uint)_pool[hi].EndTick < _viewstartLocal) 
                hi--;

            while (lo <= hi)
            {
                if ((uint)_pool[lo].EndTick < _viewstartLocal)
                {
                    // "lo" is dead, pull the live note at "hi" into it
                    _pool[lo] = _pool[hi];
                    // Back-patch open notes so NoteOff can still find them
                    if (_pool[lo].EndTick < 0)
                    {
                        int packed = _pool[lo].PoolSlot;
                        KeyHeaders[packed >> 2].NoteIdx[packed & 3] = lo;
                    }
                    hi--;
                    
                    while (hi >= lo && (uint)_pool[hi].EndTick < _viewstartLocal) 
                        hi--;
                }
                else
                {
                    int endtick = _pool[lo].EndTick;
                    if (endtick >= 0 && endtick < newMin) 
                        newMin = endtick;
                    lo++;
                }
            }

            activeCount = hi + 1;
            lastNoteClosedTick = newMin;
        }

        private sealed class NativeGLBindingsContext : IBindingsContext
        {
            private static readonly nint s_gl = LoadGL();
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
            public nint GetProcAddress(string procName)
            {
                nint addr;
                if (OperatingSystem.IsWindows())
                {
                    addr = WglGetProcAddress(procName);
                    if (addr is 0 or 1 or 2 or 3 or -1) NativeLibrary.TryGetExport(s_gl, procName, out addr);
                }
                else 
                    NativeLibrary.TryGetExport(s_gl, procName, out addr);
                return addr;
            }
        }

        private static int BuildShader(string vert, string frag)
        {
            int vertex   = CompileStage(ShaderType.VertexShader, vert);
            int fragment = CompileStage(ShaderType.FragmentShader, frag);
            int program  = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("Shader link:\n" + GL.GetProgramInfoLog(program));
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return program;
        }

        private static int CompileStage(ShaderType type, string src)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, src);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception($"{type}:\n" + GL.GetShaderInfoLog(shader));
            return shader;
        }
    }
}