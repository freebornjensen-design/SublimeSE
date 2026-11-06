using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SeHelper
{
    // Точка входа. Здесь нельзя трогать типы игры и Roslyn напрямую:
    // сначала ставим AssemblyResolve на папку Bin64, потом уходим в Run().
    public static class EntryPoint
    {
        public static string Bin64;
        public static string ContentDir;
        public static string WebDir;
        public static int Port = 8765;

        public static int Main(string[] args)
        {
            Bin64 = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
            WebDir = Path.Combine(Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location), "..", "web");
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--bin64") Bin64 = args[i + 1];
                else if (args[i] == "--port") Port = int.Parse(args[i + 1]);
                else if (args[i] == "--web") WebDir = args[i + 1];
            }
            WebDir = Path.GetFullPath(WebDir);
            ContentDir = Path.GetFullPath(Path.Combine(Bin64, "..", "Content"));
            if (!File.Exists(Path.Combine(Bin64, "Sandbox.Common.dll")))
            {
                Console.Error.WriteLine("Не найдена игра в " + Bin64 + " (укажи --bin64)");
                return 2;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var p = Path.Combine(Bin64, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            Run();
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run()
        {
            var game = new GameApi(Bin64, ContentDir);
            var engine = new SimEngine(game);
            var api = new Api(game, engine);
            // прогрев Roslyn, чтобы первая подсказка в редакторе не ждала 4 секунды
            // и сборка настоящего whitelist игры (пара секунд, линтер её дождётся)
            new System.Threading.Thread(() =>
            {
                GameWhitelist.Init(game.Bin64);
                try { game.Diagnostics("void Main() { Echo(\"\"); }"); } catch { }
            }) { IsBackground = true }.Start();
            var server = new HttpServer(Port, WebDir, api.Handle);
            Console.WriteLine("SeHelper слушает http://127.0.0.1:" + Port + "/");
            server.Run();
        }
    }
}
