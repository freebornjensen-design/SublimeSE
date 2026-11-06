using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SeHelper
{
    // Настоящий whitelist: собираем его тем же кодом, что и игра при запуске.
    // VRage.Scripting.MyScriptCompiler строит базовый список, MySpaceGameDefaultIlChecker из SpaceEngineers.Game
    // добавляет игровые пространства имён, а чёрный список повторяет MySandboxGame.InitIlChecker.
    // Всё через рефлексию: классы непубличные, и если обновление игры их поменяет, хелпер не упадёт,
    // а просто вернётся к приблизительным правилам из Whitelist.cs.
    public static class GameWhitelist
    {
        static readonly string[] RefDlls =
        {
            "Sandbox.Common", "Sandbox.Game", "SpaceEngineers.Game", "SpaceEngineers.ObjectBuilders", "VRage", "VRage.Game",
            "VRage.Library", "VRage.Math", "VRage.Input", "Sandbox.Graphics", "VRage.Render",
        };

        static readonly ManualResetEvent Ready = new ManualResetEvent(false);
        static object _whitelist, _ingame;
        static MethodInfo _isWhitelisted;
        static DiagnosticAnalyzer _analyzer;

        public static string Status = "собирается…";
        public static int Entries;

        static readonly Regex MissingAsm = new Regex(@"because (?<n>[\w.]+), Version=.*has not been added to the compiler");

        public static void Init(string bin64)
        {
            var dlls = RefDlls.ToList();
            try
            {
                // Игра добавляет в компилятор кучу своих сборок; какие именно нужны для регистрации, видно по ошибке
                // «X has not been added to the compiler» — добавляем такую сборку и пробуем ещё раз.
                for (int attempt = 0; ; attempt++)
                {
                    try { Build(bin64, dlls); break; }
                    catch (Exception ex)
                    {
                        var e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                        var m = MissingAsm.Match(e.Message);
                        if (!m.Success || attempt > 30 || dlls.Contains(m.Groups["n"].Value)
                            || !File.Exists(Path.Combine(bin64, m.Groups["n"].Value + ".dll"))) throw;
                        dlls.Add(m.Groups["n"].Value);
                    }
                }
                Status = "из игры, " + Entries + " записей";
            }
            catch (Exception ex)
            {
                _analyzer = null; _whitelist = null;
                var e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Status = "приблизительный (" + e.GetType().Name + ": " + e.Message + ")";
                Console.Error.WriteLine("Whitelist игры не собрался, использую приблизительный: " + e);
            }
            finally { Ready.Set(); }
            Console.WriteLine("Whitelist: " + Status);
        }

        static void Build(string bin64, List<string> dlls)
        {
            {   // один проход сборки: новый компилятор, ссылки, регистрация, чёрный список
                var scripting = Assembly.LoadFrom(Path.Combine(bin64, "VRage.Scripting.dll"));
                var seGame = Assembly.LoadFrom(Path.Combine(bin64, "SpaceEngineers.Game.dll"));
                var compilerType = scripting.GetType("VRage.Scripting.MyScriptCompiler", true);
                var compiler = Activator.CreateInstance(compilerType, true);

                var refs = dlls.Select(n => Path.Combine(bin64, n + ".dll")).Where(File.Exists).ToArray();
                Call(compiler, "AddReferencedAssemblies", new object[] { refs });

                var whitelist = compilerType.GetProperty("Whitelist").GetValue(compiler);
                var checker = seGame.GetType("SpaceEngineers.Game.MySpaceGameDefaultIlChecker", true);
                using (var batch = (IDisposable)Call(whitelist, "OpenBatch"))
                    foreach (var m in new[] { "AllowDefaultNamespaces", "AllowSandboxNamespaces", "AllowSpaceEngineersNamespaces" })
                        checker.GetMethod(m, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new[] { batch });

                // чёрный список из MySandboxGame.InitIlChecker
                using (var bl = (IDisposable)Call(whitelist, "OpenIngameBlacklistBatch"))
                {
                    var add = bl.GetType().GetInterfaces().Select(i => i.GetMethod("AddMembers")).First(m => m != null);
                    add.Invoke(bl, new object[] { typeof(Path), new[] { "GetTempFileName" } });
                    add.Invoke(bl, new object[] { typeof(CultureInfo), new[] { "DefaultThreadCurrentCulture", "DefaultThreadCurrentUICulture" } });
                    add.Invoke(bl, new object[] { typeof(Encoding), new[] { "RegisterProvider" } });
                    add.Invoke(bl, new object[] { typeof(Regex), new[] { "CacheSize" } });
                }

                _isWhitelisted = whitelist.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .First(m => m.Name == "IsWhitelisted" && m.ReturnType == typeof(bool) && m.GetParameters()[0].ParameterType == typeof(ISymbol));
                _ingame = Enum.Parse(_isWhitelisted.GetParameters()[1].ParameterType, "Ingame");
                _analyzer = (DiagnosticAnalyzer)compilerType
                    .GetField("m_inGameWhitelistDiagnosticAnalyzer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(compiler);
                _whitelist = whitelist;
                var dict = Call(whitelist, "GetWhitelist");
                var count = dict.GetType().GetProperty("Count");
                Entries = count != null ? (int)count.GetValue(dict) : 0;
            }
        }

        static object Call(object target, string method, object[] args = null)
        {
            var m = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(x => x.Name == method && x.GetParameters().Length == (args == null ? 0 : args.Length));
            return m.Invoke(target, args);
        }

        // Анализатор игры (null, если whitelist не собрался). Ждём сборку, но недолго: первый запуск — пара секунд.
        public static DiagnosticAnalyzer Analyzer
        {
            get { Ready.WaitOne(TimeSpan.FromSeconds(20)); return _analyzer; }
        }

        public static bool Available { get { return Ready.WaitOne(0) && _whitelist != null; } }

        // true/false — ответ игры, null — whitelist ещё не готов или символ не по зубам.
        public static bool? IsWhitelisted(ISymbol s)
        {
            if (!Available) return null;
            // методы-расширения в автокомплите приходят «приведёнными», а whitelist знает исходные определения
            var m = s as IMethodSymbol;
            if (m != null && m.ReducedFrom != null) s = m.ReducedFrom;
            s = s.OriginalDefinition;
            try { return (bool)_isWhitelisted.Invoke(_whitelist, new[] { s, _ingame }); }
            catch { return null; }
        }

        static readonly Regex ProhibitedRx = new Regex(@"^The type or member '(?<m>.*)' is prohibited$");

        // Сообщения анализатора игры по-английски: известную формулировку переводим, остальные оставляем как есть.
        public static string Translate(Diagnostic d)
        {
            var msg = d.GetMessage(CultureInfo.InvariantCulture);
            var m = ProhibitedRx.Match(msg);
            return m.Success ? "Запрещено whitelist'ом игры: " + m.Groups["m"].Value : "Запрещено в скриптах игры: " + msg;
        }
    }
}
