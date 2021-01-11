using System;
using System.IO;
using System.Runtime.InteropServices;
using SDL2;

namespace RefreshTest
{
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public float x, y, z;
        public float u, v;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RaymarchUniforms
    {
        public float time, padding;
        public float resolutionX, resolutionY;
    }

    public unsafe class TestGame : IDisposable
    {
        bool quit = false;

        double t = 0;
        double dt = 0.01;

        ulong currentTime = SDL.SDL_GetPerformanceCounter();
        double accumulator = 0;

        IntPtr RefreshDevice;
        IntPtr WindowHandle;

        refresh.Refresh_Rect renderArea;
        refresh.Refresh_Rect flip;

        /* shaders */
        IntPtr passthroughVertexShaderModule;
        IntPtr raymarchFragmentShaderModule;

        IntPtr woodTexture;
        IntPtr noiseTexture;

        IntPtr vertexBuffer;
        UInt64[] offsets;

        RaymarchUniforms raymarchUniforms;

        IntPtr mainRenderPass;
        IntPtr mainColorTargetTexture;
        refresh.Refresh_TextureSlice mainColorTargetTextureSlice;

        IntPtr mainColorTarget;
        IntPtr mainDepthStencilTarget;

        IntPtr mainFrameBuffer;
        IntPtr raymarchPipeline;

        IntPtr sampler;

        IntPtr[] sampleTextures = new IntPtr[2];
        IntPtr[] sampleSamplers = new IntPtr[2];

        refresh.Refresh_Color clearColor;
        refresh.Refresh_DepthStencilValue depthStencilClearValue;

        /* Functions */

        public uint[] ReadBytecode(FileInfo fileInfo)
        {
            byte[] data;
            int size;
            using (FileStream stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read))
            {
                size = (int) stream.Length;
                data = new byte[size];
                stream.Read(data, 0, size);
            }

            uint[] uintData = new uint[size / 4];
            using (var memoryStream = new MemoryStream(data))
            {
                using (var reader = new BinaryReader(memoryStream))
                {
                    for (int i = 0; i < size / 4; i++)
                    {
                        uintData[i] = reader.ReadUInt32();
                    }
                }
            }

            return uintData;
        }

        public bool Initialize(uint windowWidth, uint windowHeight)
        {
            if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_TIMER | SDL.SDL_INIT_GAMECONTROLLER) < 0)
            {
                System.Console.WriteLine("Failed to initialize SDL!");
                return false;
            }

            WindowHandle = SDL.SDL_CreateWindow(
                "RefreshCSTest",
                SDL.SDL_WINDOWPOS_UNDEFINED,
                SDL.SDL_WINDOWPOS_UNDEFINED,
                (int)windowWidth,
                (int)windowHeight,
                SDL.SDL_WindowFlags.SDL_WINDOW_VULKAN | SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN
            );

            refresh.Refresh_PresentationParameters presentationParameters;
            presentationParameters.deviceWindowHandle = (void*)WindowHandle;
            presentationParameters.presentMode = refresh.Refresh_PresentMode.REFRESH_PRESENTMODE_MAILBOX;

            RefreshDevice = refresh.Refresh_CreateDevice(ref presentationParameters, 1);

            renderArea.x = 0;
            renderArea.y = 0;
            renderArea.w = (int)windowWidth;
            renderArea.h = (int)windowHeight;

            flip.x = 0;
            flip.y = (int)windowHeight;
            flip.w = (int)windowWidth;
            flip.h = -(int)windowHeight;

            clearColor.r = 100;
            clearColor.g = 149;
            clearColor.b = 237;
            clearColor.a = 255;

            depthStencilClearValue.depth = 1;
            depthStencilClearValue.stencil = 0;

            /* load shaders */

            var passthroughVertBytecodeFile = new FileInfo("assets/passthrough_vert.spv");
            var raymarchFragBytecodeFile = new FileInfo("assets/hexagon_grid.spv");

            unsafe
            {
                fixed (uint* ptr = ReadBytecode(passthroughVertBytecodeFile))
                {
                    refresh.Refresh_ShaderModuleCreateInfo passthroughVertexShaderModuleCreateInfo;
                    passthroughVertexShaderModuleCreateInfo.codeSize = (ulong)passthroughVertBytecodeFile.Length;
                    passthroughVertexShaderModuleCreateInfo.byteCode = ptr;

                    passthroughVertexShaderModule = refresh.Refresh_CreateShaderModule(RefreshDevice, ref passthroughVertexShaderModuleCreateInfo);
                }

                fixed (uint* ptr = ReadBytecode(raymarchFragBytecodeFile))
                {
                    refresh.Refresh_ShaderModuleCreateInfo raymarchFragmentShaderModuleCreateInfo;
                    raymarchFragmentShaderModuleCreateInfo.codeSize = (ulong)raymarchFragBytecodeFile.Length;
                    raymarchFragmentShaderModuleCreateInfo.byteCode = ptr;

                    raymarchFragmentShaderModule = refresh.Refresh_CreateShaderModule(RefreshDevice, ref raymarchFragmentShaderModuleCreateInfo);
                }
            }

            /* load textures */

            void* pixels = refresh_image.Refresh_Image_Load((byte*)Marshal.StringToHGlobalAnsi("woodgrain.png"), out var textureWidth, out var textureHeight, out var numChannels);
            woodTexture = refresh.Refresh_CreateTexture2D(
                RefreshDevice,
                refresh.Refresh_ColorFormat.REFRESH_COLORFORMAT_R8G8B8A8,
                (uint)textureWidth,
                (uint)textureHeight,
                1,
                refresh.Refresh_TextureUsageFlags.REFRESH_TEXTUREUSAGE_SAMPLER_BIT
            );

            refresh.Refresh_TextureSlice setTextureDataSlice;
            setTextureDataSlice.texture = woodTexture;
            setTextureDataSlice.rectangle.x = 0;
            setTextureDataSlice.rectangle.y = 0;
            setTextureDataSlice.rectangle.w = textureWidth;
            setTextureDataSlice.rectangle.h = textureHeight;
            setTextureDataSlice.depth = 0;
            setTextureDataSlice.layer = 0;
            setTextureDataSlice.level = 0;

            refresh.Refresh_SetTextureData(
                RefreshDevice,
                ref setTextureDataSlice,
                (void*)pixels,
                (uint)textureWidth * (uint)textureHeight * 4
            );

            refresh_image.Refresh_Image_Free((byte*)pixels);

            pixels = refresh_image.Refresh_Image_Load((byte*)Marshal.StringToHGlobalAnsi("noise.png"), out textureWidth, out textureHeight, out numChannels);
            noiseTexture = refresh.Refresh_CreateTexture2D(
                RefreshDevice,
                refresh.Refresh_ColorFormat.REFRESH_COLORFORMAT_R8G8B8A8,
                (uint)textureWidth,
                (uint)textureHeight,
                1,
                refresh.Refresh_TextureUsageFlags.REFRESH_TEXTUREUSAGE_SAMPLER_BIT
            );

            setTextureDataSlice.texture = noiseTexture;
            setTextureDataSlice.rectangle.w = textureWidth;
            setTextureDataSlice.rectangle.h = textureHeight;

            refresh.Refresh_SetTextureData(
                RefreshDevice,
                ref setTextureDataSlice,
                pixels,
                (uint)textureWidth * (uint)textureHeight * 4
            );

            refresh_image.Refresh_Image_Free((byte*)pixels);

            /* vertex data */

            var vertices = new Vertex[3];
            vertices[0].x = -1;
            vertices[0].y = -1;
            vertices[0].z = 0;
            vertices[0].u = 0;
            vertices[0].v = 1;

            vertices[1].x = 3;
            vertices[1].y = -1;
            vertices[1].z = 0;
            vertices[1].u = 1;
            vertices[1].v = 1;

            vertices[2].x = -1;
            vertices[2].y = 3;
            vertices[2].z = 0;
            vertices[2].u = 0;
            vertices[2].v = 0;

            vertexBuffer = refresh.Refresh_CreateBuffer(
                RefreshDevice,
                refresh.Refresh_BufferUsageFlags.REFRESH_BUFFERUSAGE_VERTEX_BIT,
                4 * 5 * 3
            );

            GCHandle handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);

            refresh.Refresh_SetBufferData(
                RefreshDevice,
                vertexBuffer,
                0,
                (void*)handle.AddrOfPinnedObject(),
                4 * 5 * 3
            );

            handle.Free();

            offsets = new UInt64[1];
            offsets[0] = 0;

            /* uniforms */

            raymarchUniforms.time = 0;
            raymarchUniforms.padding = 0;
            raymarchUniforms.resolutionX = (float)windowWidth;
            raymarchUniforms.resolutionY = (float)windowHeight;

            /* render pass */

            refresh.Refresh_ColorTargetDescription mainColorTargetDescriptions;
            mainColorTargetDescriptions.format = refresh.Refresh_ColorFormat.REFRESH_COLORFORMAT_R8G8B8A8;
            mainColorTargetDescriptions.loadOp = refresh.Refresh_LoadOp.REFRESH_LOADOP_CLEAR;
            mainColorTargetDescriptions.storeOp = refresh.Refresh_StoreOp.REFRESH_STOREOP_STORE;
            mainColorTargetDescriptions.multisampleCount = refresh.Refresh_SampleCount.REFRESH_SAMPLECOUNT_1;

            refresh.Refresh_DepthStencilTargetDescription mainDepthStencilTargetDescription;
            mainDepthStencilTargetDescription.depthFormat = refresh.Refresh_DepthFormat.REFRESH_DEPTHFORMAT_D32_SFLOAT_S8_UINT;
            mainDepthStencilTargetDescription.loadOp = refresh.Refresh_LoadOp.REFRESH_LOADOP_CLEAR;
            mainDepthStencilTargetDescription.storeOp = refresh.Refresh_StoreOp.REFRESH_STOREOP_DONT_CARE;
            mainDepthStencilTargetDescription.stencilLoadOp = refresh.Refresh_LoadOp.REFRESH_LOADOP_DONT_CARE;
            mainDepthStencilTargetDescription.stencilStoreOp = refresh.Refresh_StoreOp.REFRESH_STOREOP_DONT_CARE;

            GCHandle colorTargetDescriptionHandle = GCHandle.Alloc(mainColorTargetDescriptions, GCHandleType.Pinned);
            GCHandle depthStencilTargetDescriptionHandle = GCHandle.Alloc(mainDepthStencilTargetDescription, GCHandleType.Pinned);

            refresh.Refresh_RenderPassCreateInfo mainRenderPassCreateInfo;
            mainRenderPassCreateInfo.colorTargetCount = 1;
            mainRenderPassCreateInfo.colorTargetDescriptions = (refresh.Refresh_ColorTargetDescription*)colorTargetDescriptionHandle.AddrOfPinnedObject();
            mainRenderPassCreateInfo.depthTargetDescription = (refresh.Refresh_DepthStencilTargetDescription*)depthStencilTargetDescriptionHandle.AddrOfPinnedObject();

            mainRenderPass = refresh.Refresh_CreateRenderPass(RefreshDevice, ref mainRenderPassCreateInfo);

            colorTargetDescriptionHandle.Free();
            depthStencilTargetDescriptionHandle.Free();

            mainColorTargetTexture = refresh.Refresh_CreateTexture2D(
                RefreshDevice,
                refresh.Refresh_ColorFormat.REFRESH_COLORFORMAT_R8G8B8A8,
                windowWidth,
                windowHeight,
                1,
                refresh.Refresh_TextureUsageFlags.REFRESH_TEXTUREUSAGE_COLOR_TARGET_BIT
            );

            mainColorTargetTextureSlice.texture = mainColorTargetTexture;
            mainColorTargetTextureSlice.rectangle.x = 0;
            mainColorTargetTextureSlice.rectangle.y = 0;
            mainColorTargetTextureSlice.rectangle.w = (int)windowWidth;
            mainColorTargetTextureSlice.rectangle.h = (int)windowHeight;
            mainColorTargetTextureSlice.depth = 0;
            mainColorTargetTextureSlice.layer = 0;
            mainColorTargetTextureSlice.level = 0;

            mainColorTarget = refresh.Refresh_CreateColorTarget(
                RefreshDevice,
                refresh.Refresh_SampleCount.REFRESH_SAMPLECOUNT_1,
                ref mainColorTargetTextureSlice
            );

            mainDepthStencilTarget = refresh.Refresh_CreateDepthStencilTarget(
                RefreshDevice,
                windowWidth,
                windowHeight,
                refresh.Refresh_DepthFormat.REFRESH_DEPTHFORMAT_D32_SFLOAT_S8_UINT
            );

            GCHandle colorTargetHandle = GCHandle.Alloc(mainColorTarget, GCHandleType.Pinned);

            refresh.Refresh_FramebufferCreateInfo framebufferCreateInfo;
            framebufferCreateInfo.width = windowWidth;
            framebufferCreateInfo.height = windowHeight;
            framebufferCreateInfo.colorTargetCount = 1;
            framebufferCreateInfo.pColorTargets = colorTargetHandle.AddrOfPinnedObject();
            framebufferCreateInfo.pDepthStencilTarget = mainDepthStencilTarget;
            framebufferCreateInfo.renderPass = mainRenderPass;

            mainFrameBuffer = refresh.Refresh_CreateFramebuffer(RefreshDevice, ref framebufferCreateInfo);

            colorTargetHandle.Free();

            System.Console.WriteLine("created framebuffer");

            /* pipeline */

            refresh.Refresh_ColorTargetBlendState[] colorTargetBlendStates = new refresh.Refresh_ColorTargetBlendState[1];
            colorTargetBlendStates[0].blendEnable = 0;
            colorTargetBlendStates[0].alphaBlendOp = 0;
            colorTargetBlendStates[0].colorBlendOp = 0;
            colorTargetBlendStates[0].colorWriteMask = 
                refresh.Refresh_ColorComponentFlags.REFRESH_COLORCOMPONENT_R_BIT |
                refresh.Refresh_ColorComponentFlags.REFRESH_COLORCOMPONENT_G_BIT |
                refresh.Refresh_ColorComponentFlags.REFRESH_COLORCOMPONENT_B_BIT |
                refresh.Refresh_ColorComponentFlags.REFRESH_COLORCOMPONENT_A_BIT;
            
            colorTargetBlendStates[0].dstAlphaBlendFactor = 0;
            colorTargetBlendStates[0].dstColorBlendFactor = 0;
            colorTargetBlendStates[0].srcAlphaBlendFactor = 0;
            colorTargetBlendStates[0].srcColorBlendFactor = 0;

            var colorTargetBlendStateHandle = GCHandle.Alloc(colorTargetBlendStates, GCHandleType.Pinned);

            refresh.Refresh_ColorBlendState colorBlendState;

            unsafe
            {
                colorBlendState.logicOpEnable = 0;
                colorBlendState.logicOp = refresh.Refresh_LogicOp.REFRESH_LOGICOP_NO_OP;
                colorBlendState.blendConstants[0] = 0;
                colorBlendState.blendConstants[1] = 0;
                colorBlendState.blendConstants[2] = 0;
                colorBlendState.blendConstants[3] = 0;
                colorBlendState.blendStateCount = 1;
                colorBlendState.blendStates = (refresh.Refresh_ColorTargetBlendState*)colorTargetBlendStateHandle.AddrOfPinnedObject();
            }

            refresh.Refresh_DepthStencilState depthStencilState;
            depthStencilState.depthTestEnable = 0;
            depthStencilState.backStencilState.compareMask = 0;
            depthStencilState.backStencilState.compareOp = refresh.Refresh_CompareOp.REFRESH_COMPAREOP_NEVER;
            depthStencilState.backStencilState.depthFailOp = refresh.Refresh_StencilOp.REFRESH_STENCILOP_ZERO;
            depthStencilState.backStencilState.failOp = refresh.Refresh_StencilOp.REFRESH_STENCILOP_ZERO;
            depthStencilState.backStencilState.passOp = refresh.Refresh_StencilOp.REFRESH_STENCILOP_ZERO;
            depthStencilState.backStencilState.reference = 0;
            depthStencilState.backStencilState.writeMask = 0;
            depthStencilState.compareOp = refresh.Refresh_CompareOp.REFRESH_COMPAREOP_NEVER;
            depthStencilState.depthBoundsTestEnable = 0;
            depthStencilState.depthWriteEnable = 0;
            depthStencilState.frontStencilState.compareMask = 0;
            depthStencilState.frontStencilState.compareOp = refresh.Refresh_CompareOp.REFRESH_COMPAREOP_NEVER;
            depthStencilState.frontStencilState.depthFailOp = refresh.Refresh_StencilOp.REFRESH_STENCILOP_ZERO;
            depthStencilState.frontStencilState.failOp = refresh.Refresh_StencilOp.REFRESH_STENCILOP_ZERO;
            depthStencilState.frontStencilState.passOp = refresh.Refresh_StencilOp.REFRESH_STENCILOP_ZERO;
            depthStencilState.frontStencilState.reference = 0;
            depthStencilState.frontStencilState.writeMask = 0;
            depthStencilState.maxDepthBounds = 1.0f;
            depthStencilState.minDepthBounds = 0.0f;
            depthStencilState.stencilTestEnable = 0;

            refresh.Refresh_ShaderStageState vertexShaderState;
            vertexShaderState.shaderModule = passthroughVertexShaderModule;
            vertexShaderState.entryPointName = (byte*)Marshal.StringToHGlobalAnsi("main");
            vertexShaderState.uniformBufferSize = 0;

            refresh.Refresh_ShaderStageState fragmentShaderStage;
            fragmentShaderStage.shaderModule = raymarchFragmentShaderModule;
            fragmentShaderStage.entryPointName = (byte*)Marshal.StringToHGlobalAnsi("main");
            fragmentShaderStage.uniformBufferSize = 4;

            refresh.Refresh_MultisampleState multisampleState;
            multisampleState.multisampleCount = refresh.Refresh_SampleCount.REFRESH_SAMPLECOUNT_1;
            multisampleState.sampleMask = uint.MaxValue;

            refresh.Refresh_GraphicsPipelineLayoutCreateInfo pipelineLayoutCreateInfo;
            pipelineLayoutCreateInfo.vertexSamplerBindingCount = 0;
            pipelineLayoutCreateInfo.fragmentSamplerBindingCount = 2;

            refresh.Refresh_RasterizerState rasterizerState;
            rasterizerState.cullMode = refresh.Refresh_CullMode.REFRESH_CULLMODE_BACK;
            rasterizerState.depthBiasClamp = 0;
            rasterizerState.depthBiasConstantFactor = 0;
            rasterizerState.depthBiasEnable = 0;
            rasterizerState.depthBiasSlopeFactor = 0;
            rasterizerState.depthClampEnable = 0;
            rasterizerState.fillMode = refresh.Refresh_FillMode.REFRESH_FILLMODE_FILL;
            rasterizerState.frontFace = refresh.Refresh_FrontFace.REFRESH_FRONTFACE_CLOCKWISE;
            rasterizerState.lineWidth = 1.0f;

            refresh.Refresh_TopologyState topologyState;
            topologyState.topology = refresh.Refresh_PrimitiveType.REFRESH_PRIMITIVETYPE_TRIANGLELIST;

            refresh.Refresh_VertexBinding[] vertexBindings = new refresh.Refresh_VertexBinding[1];
            vertexBindings[0].binding = 0;
            vertexBindings[0].inputRate = refresh.Refresh_VertexInputRate.REFRESH_VERTEXINPUTRATE_VERTEX;
            vertexBindings[0].stride = 4 * 5;

            refresh.Refresh_VertexAttribute[] vertexAttributes = new refresh.Refresh_VertexAttribute[2];
            vertexAttributes[0].binding = 0;
            vertexAttributes[0].location = 0;
            vertexAttributes[0].format = refresh.Refresh_VertexElementFormat.REFRESH_VERTEXELEMENTFORMAT_VECTOR3;
            vertexAttributes[0].offset = 0;

            vertexAttributes[1].binding = 0;
            vertexAttributes[1].location = 1;
            vertexAttributes[1].format = refresh.Refresh_VertexElementFormat.REFRESH_VERTEXELEMENTFORMAT_VECTOR2;
            vertexAttributes[1].offset = 4 * 3;

            GCHandle vertexBindingsHandle = GCHandle.Alloc(vertexBindings, GCHandleType.Pinned);
            GCHandle vertexAttributesHandle = GCHandle.Alloc(vertexAttributes, GCHandleType.Pinned);

            refresh.Refresh_VertexInputState vertexInputState;
            vertexInputState.vertexBindings = (refresh.Refresh_VertexBinding*)vertexBindingsHandle.AddrOfPinnedObject();
            vertexInputState.vertexBindingCount = 1;
            vertexInputState.vertexAttributes = (refresh.Refresh_VertexAttribute*)vertexAttributesHandle.AddrOfPinnedObject();
            vertexInputState.vertexAttributeCount = 2;

            refresh.Refresh_Viewport viewport;
            viewport.x = 0;
            viewport.y = 0;
            viewport.w = (float)windowWidth;
            viewport.h = (float)windowHeight;
            viewport.minDepth = 0;
            viewport.maxDepth = 1;

            GCHandle viewportHandle = GCHandle.Alloc(viewport, GCHandleType.Pinned);
            GCHandle scissorHandle = GCHandle.Alloc(renderArea, GCHandleType.Pinned);

            refresh.Refresh_ViewportState viewportState;
            viewportState.viewports = (refresh.Refresh_Viewport*)viewportHandle.AddrOfPinnedObject();
            viewportState.viewportCount = 1;
            viewportState.scissors = (refresh.Refresh_Rect*)scissorHandle.AddrOfPinnedObject();
            viewportState.scissorCount = 1;

            unsafe
            {
                System.Console.WriteLine(sizeof(refresh.Refresh_GraphicsPipelineLayoutCreateInfo));
            }

            refresh.Refresh_GraphicsPipelineCreateInfo graphicsPipelineCreateInfo;
            graphicsPipelineCreateInfo.colorBlendState = colorBlendState;
            graphicsPipelineCreateInfo.depthStencilState = depthStencilState;
            graphicsPipelineCreateInfo.vertexShaderState = vertexShaderState;
            graphicsPipelineCreateInfo.fragmentShaderState = fragmentShaderStage;
            graphicsPipelineCreateInfo.multisampleState = multisampleState;
            graphicsPipelineCreateInfo.pipelineLayoutCreateInfo = pipelineLayoutCreateInfo;
            graphicsPipelineCreateInfo.rasterizerState = rasterizerState;
            graphicsPipelineCreateInfo.topologyState = topologyState;
            graphicsPipelineCreateInfo.vertexInputState = vertexInputState;
            graphicsPipelineCreateInfo.viewportState = viewportState;
            graphicsPipelineCreateInfo.renderPass = mainRenderPass;

            System.Console.WriteLine("creating graphics pipeline");

            raymarchPipeline = refresh.Refresh_CreateGraphicsPipeline(RefreshDevice, ref graphicsPipelineCreateInfo);

            System.Console.WriteLine("created graphics pipeline");

            colorTargetBlendStateHandle.Free();
            vertexBindingsHandle.Free();
            vertexAttributesHandle.Free();
            viewportHandle.Free();
            scissorHandle.Free();

            refresh.Refresh_SamplerStateCreateInfo samplerStateCreateInfo;
            samplerStateCreateInfo.addressModeU = refresh.Refresh_SamplerAddressMode.REFRESH_SAMPLERADDRESSMODE_REPEAT;
            samplerStateCreateInfo.addressModeV = refresh.Refresh_SamplerAddressMode.REFRESH_SAMPLERADDRESSMODE_REPEAT;
            samplerStateCreateInfo.addressModeW = refresh.Refresh_SamplerAddressMode.REFRESH_SAMPLERADDRESSMODE_REPEAT;
            samplerStateCreateInfo.anisotropyEnable = 0;
            samplerStateCreateInfo.borderColor = refresh.Refresh_BorderColor.REFRESH_BORDERCOLOR_INT_OPAQUE_BLACK;
            samplerStateCreateInfo.compareEnable = 0;
            samplerStateCreateInfo.compareOp = refresh.Refresh_CompareOp.REFRESH_COMPAREOP_NEVER;
            samplerStateCreateInfo.magFilter = refresh.Refresh_Filter.REFRESH_FILTER_LINEAR;
            samplerStateCreateInfo.maxAnisotropy = 0;
            samplerStateCreateInfo.maxLod = 1;
            samplerStateCreateInfo.minFilter = refresh.Refresh_Filter.REFRESH_FILTER_LINEAR;
            samplerStateCreateInfo.minLod = 1;
            samplerStateCreateInfo.mipLodBias = 1;
            samplerStateCreateInfo.mipmapMode = refresh.Refresh_SamplerMipmapMode.REFRESH_SAMPLERMIPMAPMODE_LINEAR;

            sampler = refresh.Refresh_CreateSampler(RefreshDevice, ref samplerStateCreateInfo);

            sampleTextures[0] = woodTexture;
            sampleTextures[1] = noiseTexture;

            sampleSamplers[0] = sampler;
            sampleSamplers[1] = sampler;


            return true;
        }

        public void Run()
        {
            while (!quit)
            {
                SDL.SDL_Event _Event;

                while (SDL.SDL_PollEvent(out _Event) == 1)
                {
                    switch (_Event.type)
                    {
                        case SDL.SDL_EventType.SDL_QUIT:
                            quit = true;
                            break;
                    }
                }

                var newTime = SDL.SDL_GetPerformanceCounter();
                double frameTime = (newTime - currentTime) / (double)SDL.SDL_GetPerformanceFrequency();

                if (frameTime > 0.25)
                {
                    frameTime = 0.25;
                }

                currentTime = newTime;

                accumulator += frameTime;

                bool updateThisLoop = (accumulator >= dt);

                while (accumulator >= dt && !quit)
                {
                    Update(dt);

                    t += dt;
                    accumulator -= dt;
                }

                if (updateThisLoop && !quit)
                {
                    Draw();
                }
            }
        }

        public void Update(double dt)
        {
            raymarchUniforms.time = (float)t;
        }

        public void Draw()
        {
            IntPtr commandBuffer = refresh.Refresh_AcquireCommandBuffer(RefreshDevice, 0);

            unsafe
            {
                fixed (refresh.Refresh_Color* ptr = &clearColor)
                {
                    refresh.Refresh_BeginRenderPass(
                        RefreshDevice,
                        commandBuffer,
                        mainRenderPass,
                        mainFrameBuffer,
                        renderArea,
                        ptr,
                        1,
                        ref depthStencilClearValue
                    );
                }
            }

            refresh.Refresh_BindGraphicsPipeline(
                RefreshDevice,
                commandBuffer,
                raymarchPipeline
            );

            uint fragmentParamOffset;

            unsafe
            {
                fixed (RaymarchUniforms* ptr = &raymarchUniforms)
                {
                    fragmentParamOffset = refresh.Refresh_PushFragmentShaderParams(
                        RefreshDevice,
                        commandBuffer,
                        ptr,
                        1
                    );
                }
            }

            GCHandle vertexBufferHandle = GCHandle.Alloc(vertexBuffer, GCHandleType.Pinned);
            GCHandle offsetHandle = GCHandle.Alloc(offsets, GCHandleType.Pinned);

            refresh.Refresh_BindVertexBuffers(
                RefreshDevice,
                commandBuffer,
                0,
                1,
                vertexBufferHandle.AddrOfPinnedObject(),
                offsetHandle.AddrOfPinnedObject()
            );

            vertexBufferHandle.Free();
            offsetHandle.Free();

            GCHandle sampleTextureHandle = GCHandle.Alloc(sampleTextures, GCHandleType.Pinned);
            GCHandle sampleSamplerHandle = GCHandle.Alloc(sampleSamplers, GCHandleType.Pinned);

            refresh.Refresh_BindFragmentSamplers(
                RefreshDevice,
                commandBuffer,
                sampleTextureHandle.AddrOfPinnedObject(),
                sampleSamplerHandle.AddrOfPinnedObject()
            );

            sampleTextureHandle.Free();
            sampleSamplerHandle.Free();

            refresh.Refresh_DrawPrimitives(
                RefreshDevice,
                commandBuffer,
                0,
                1,
                0,
                fragmentParamOffset
            );

            refresh.Refresh_EndRenderPass(
                RefreshDevice,
                commandBuffer
            );

            refresh.Refresh_QueuePresent(
                RefreshDevice,
                commandBuffer,
                ref mainColorTargetTextureSlice,
                ref flip,
                refresh.Refresh_Filter.REFRESH_FILTER_NEAREST
            );

            GCHandle commandBufferHandle = GCHandle.Alloc(commandBuffer, GCHandleType.Pinned);

            refresh.Refresh_Submit(
                RefreshDevice,
                1,
                commandBufferHandle.AddrOfPinnedObject()
            );

            commandBufferHandle.Free();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
