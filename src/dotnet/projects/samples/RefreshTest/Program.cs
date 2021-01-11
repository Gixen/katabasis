using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Katabasis;

namespace RefreshTest
{
    public class Program
    {
        static void Main(string[] args)
        {
            var libvulkan = NativeLibrary.Load("/Users/lstranks/Programming/Katabasis/src/dotnet/projects/samples/RefreshTest/bin/Debug/net5.0/libMoltenVK.dylib");
            
            NativeLibrary.SetDllImportResolver(typeof(refresh).Assembly, Resolver);
            
            using TestGame game = new TestGame();
            var init = game.Initialize(1280, 720);
            if (init)
            {
                game.Run();
            }
            else
            {
                System.Console.WriteLine("uh oh!");
            }
        }

        private static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "SDL2")
            {
                var sdl2 = NativeLibrary.Load("/Users/lstranks/Programming/sdl2-bin/install/lib/libSDL2-2.0.0.dylib");
                return sdl2;
            }

            if (libraryName == "Refresh")
            {
                var refresh = NativeLibrary.Load("/Users/lstranks/Programming/Refresh/build/libRefresh.dylib");
                return refresh;
            }

            var up = new Exception("uhoh");
            throw up;
        }
    }
}
