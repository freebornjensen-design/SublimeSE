using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;

namespace SeHelper
{
    public class SimEngine
    {
        public readonly GameApi Game;
        public readonly BlockFactory Factory;
        public readonly object Sync = new object();

        public GridModel Grid = new GridModel();
        public string GridFile;           // где сохранена сетка (рядом со скриптом)
        string _gridSnapshot;             // сохранённое начальное состояние для «Сброса»

        // скрипт
        public string ScriptPath = "", ScriptCode = "";
        public List<Diag> CompileDiags = new List<Diag>();
        Type _programType;
        object _program;
        string _storage = "";
        public bool Crashed;
        public string Error = "";
        public int ErrorLine = -1;
        public string Echo = "";
        StringBuilder _echo = new StringBuilder();
        public bool InsideRun;
        public string LastArgument = "";
        public double LastRunMs;
        public long Runs;

        // время
        public bool Running;
        public long Tick;
        public double Speed = 1;
        public UpdateFrequency Frequency = UpdateFrequency.None;
        long _lastRunTick;
        readonly Queue<KeyValuePair<long, string>> _pendingRuns = new Queue<KeyValuePair<long, string>>();

        // моки
        readonly Dictionary<long, BlockLogic> _blocks = new Dictionary<long, BlockLogic>();
        readonly Dictionary<int, object> _grids = new Dictionary<int, object>();
        readonly Dictionary<string, object> _groupMocks = new Dictionary<string, object>();
        readonly Dictionary<string, object> _props = new Dictionary<string, object>();
        readonly Dictionary<string, object> _actions = new Dictionary<string, object>();
        object _gts, _runtime, _igc;

        public readonly List<string> LogLines = new List<string>();
        public long Version;

        public SimEngine(GameApi game)
        {
            Game = game;
            Factory = new BlockFactory(game);
            SurfaceLogic.SpriteNames = () => game.Tex.Names();
            new Thread(() => Game.Defs.EnsureLoaded()) { IsBackground = true }.Start();
            NewDefaultGrid();
            new Thread(Loop) { IsBackground = true, Name = "sim" }.Start();
        }

        public void MarkDirty() { Version++; }

        public void Log(string s)
        {
            LogLines.Add("[" + Tick + "] " + s);
            if (LogLines.Count > 300) LogLines.RemoveRange(0, LogLines.Count - 300);
            Version++;
        }

        // ---------- сетка ----------

        public void NewDefaultGrid()
        {
            var g = new GridModel { Name = "Тестовая сетка" };
            g.GridNames.Add("Тестовая сетка");
            var pb = Factory.Create("MyProgrammableBlock", "LargeProgrammableBlock", "Программируемый блок", true);
            g.Blocks.Add(pb);
            g.Blocks.Add(Factory.Create("BatteryBlock", "LargeBlockBatteryBlock", "Батарея 1", true));
            g.Blocks.Add(Factory.Create("TextPanel", "LargeLCDPanel", "LCD Панель", true));
            g.Blocks.Add(Factory.Create("CargoContainer", "LargeBlockLargeContainer", "Контейнер", false));
            g.PbId = pb.Id;
            SetGrid(g, true);
        }

        public void SetGrid(GridModel g, bool snapshot)
        {
            Grid = g;
            if (Grid.GridNames.Count == 0) Grid.GridNames.Add(Grid.Name);
            if (Grid.Blocks.All(b => b.Id != Grid.PbId))
            {
                var pb = Grid.Blocks.FirstOrDefault(b => b.Iface == "IMyProgrammableBlock");
                if (pb == null)
                {
                    pb = Factory.Create("MyProgrammableBlock", "LargeProgrammableBlock", "Программируемый блок", true);
                    Grid.Blocks.Insert(0, pb);
                }
                Grid.PbId = pb.Id;
            }
            if (snapshot) _gridSnapshot = Js.Serialize(Grid.ToJson());
            RebuildMocks();
            Version++;
        }

        void RebuildMocks()
        {
            _blocks.Clear(); _grids.Clear(); _groupMocks.Clear();
            foreach (var b in Grid.Blocks) _blocks[b.Id] = new BlockLogic(b, this);
            _gts = MakeGts();
            _runtime = MakeRuntime();
            _igc = new Mock(typeof(IMyIntergridCommunicationSystem), null).Proxy;
        }

        public BlockLogic BlockById(long id) { BlockLogic b; return _blocks.TryGetValue(id, out b) ? b : null; }

        public object GridMock(int index)
        {
            object g;
            if (!_grids.TryGetValue(index, out g))
            {
                var name = index < Grid.GridNames.Count ? Grid.GridNames[index] : Grid.Name;
                var m = new Mock(typeof(IMyCubeGrid), new Dictionary<string, object>
                {
                    { "CustomName", name }, { "DisplayName", name }, { "Name", name }, { "EntityId", 1L + index },
                    { "GridSizeEnum", "Large" }, { "GridSize", 2.5 }, { "IsStatic", false },
                });
                m.Handler = (mk, mi, a) => mi.Name == "IsSameConstructAs" ? (object)true : Mock.NotHandled;
                _grids[index] = g = m.Proxy;
            }
            return g;
        }

        public void SaveGrid(string file = null)
        {
            file = file ?? GridFile;
            _gridSnapshot = Js.Serialize(Grid.ToJson());
            if (!string.IsNullOrEmpty(file)) { Grid.Save(file); GridFile = file; }
        }

        public static string GridFileFor(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            return Path.Combine(Path.GetDirectoryName(scriptPath) ?? "", Path.GetFileNameWithoutExtension(scriptPath) + ".se-grid.json");
        }

        // ---------- GridTerminalSystem ----------

        IEnumerable<BlockLogic> Ordered() { return Grid.Blocks.Select(b => _blocks[b.Id]); }

        static bool Pass(Delegate collect, object proxy)
        {
            return collect == null || (bool)collect.DynamicInvoke(proxy);
        }

        object MakeGts()
        {
            var m = new Mock(typeof(IMyGridTerminalSystem), null);
            m.Handler = (mk, mi, a) =>
            {
                switch (mi.Name)
                {
                    case "GetBlocks":
                    {
                        var list = (IList)a[0];
                        var collect = a.Length > 1 ? a[1] as Delegate : null;
                        foreach (var b in Ordered()) if (Pass(collect, b.Proxy)) list.Add(b.Proxy);
                        return null;
                    }
                    case "GetBlocksOfType":
                    {
                        var t = mi.GetGenericArguments()[0];
                        var list = (IList)a[0];
                        var collect = a.Length > 1 ? a[1] as Delegate : null;
                        if (list != null)
                            foreach (var b in Ordered())
                                if (b.Mock.Implements(t) && Pass(collect, b.Proxy)) list.Add(b.Proxy);
                        return null;
                    }
                    case "SearchBlocksOfName":
                    {
                        var name = (string)a[0] ?? "";
                        var list = (IList)a[1];
                        var collect = a.Length > 2 ? a[2] as Delegate : null;
                        foreach (var b in Ordered())
                            if (b.M.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && Pass(collect, b.Proxy)) list.Add(b.Proxy);
                        return null;
                    }
                    case "GetBlockWithName":
                    {
                        var b = Ordered().FirstOrDefault(x => x.M.Name == (string)a[0]);
                        return b != null ? b.Proxy : null;
                    }
                    case "GetBlockWithId":
                    {
                        var b = BlockById((long)a[0]);
                        return b != null ? b.Proxy : null;
                    }
                    case "GetBlockGroupWithName":
                        return Grid.Groups.ContainsKey((string)a[0] ?? "") ? GroupMock((string)a[0]) : null;
                    case "GetBlockGroups":
                    {
                        var list = (IList)a[0];
                        var collect = a.Length > 1 ? a[1] as Delegate : null;
                        foreach (var g in Grid.Groups.Keys)
                        {
                            var gm = GroupMock(g);
                            if (Pass(collect, gm)) list.Add(gm);
                        }
                        return null;
                    }
                    case "CanAccess": return true;
                }
                return Mock.NotHandled;
            };
            return m.Proxy;
        }

        object GroupMock(string name)
        {
            object g;
            if (_groupMocks.TryGetValue(name, out g)) return g;
            var m = new Mock(typeof(IMyBlockGroup), new Dictionary<string, object> { { "Name", name } });
            m.Handler = (mk, mi, a) =>
            {
                List<long> ids;
                if (!Grid.Groups.TryGetValue(name, out ids)) ids = new List<long>();
                var members = ids.Select(BlockById).Where(b => b != null);
                switch (mi.Name)
                {
                    case "GetBlocks":
                    {
                        var list = (IList)a[0];
                        var collect = a.Length > 1 ? a[1] as Delegate : null;
                        foreach (var b in members) if (Pass(collect, b.Proxy)) list.Add(b.Proxy);
                        return null;
                    }
                    case "GetBlocksOfType":
                    {
                        var t = mi.GetGenericArguments()[0];
                        var list = (IList)a[0];
                        var collect = a.Length > 1 ? a[1] as Delegate : null;
                        foreach (var b in members) if (b.Mock.Implements(t) && Pass(collect, b.Proxy)) list.Add(b.Proxy);
                        return null;
                    }
                }
                return Mock.NotHandled;
            };
            _groupMocks[name] = g = m.Proxy;
            return g;
        }

        object MakeRuntime()
        {
            var m = new Mock(typeof(IMyGridProgramRuntimeInfo), new Dictionary<string, object>
            {
                { "MaxInstructionCount", 50000 }, { "CurrentInstructionCount", 0 }, { "MaxCallChainDepth", 10 }, { "CurrentCallChainDepth", 0 },
            });
            m.Handler = (mk, mi, a) =>
            {
                switch (mi.Name)
                {
                    case "get_UpdateFrequency": return Frequency;
                    case "set_UpdateFrequency": Frequency = (UpdateFrequency)a[0]; return null;
                    case "get_TimeSinceLastRun": return TimeSpan.FromSeconds((Tick - _lastRunTick) / 60.0);
                    case "get_LastRunTimeMs": return LastRunMs;
                }
                return Mock.NotHandled;
            };
            return m.Proxy;
        }

        // ---------- свойства и действия терминала ----------

        static readonly Dictionary<string, string> PropAlias = new Dictionary<string, string>
        {
            { "OnOff", "Enabled" }, { "Override", "ThrustOverride" }, { "FontSize", "#FontSize" }, { "FontColor", "#FontColor" },
            { "BackgroundColor", "#BackgroundColor" }, { "Power", "GyroPower" },
        };

        public object Property(string id)
        {
            object p;
            if (_props.TryGetValue(id ?? "", out p)) return p;
            var m = new Mock(typeof(ITerminalProperty), new Dictionary<string, object> { { "Id", id }, { "TypeName", "Single" } });
            m.CastFilter = t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ITerminalProperty<>);
            m.Handler = (mk, mi, a) =>
            {
                switch (mi.Name)
                {
                    case "GetValue": case "GetDefaultValue": return GetProp(a[0], id);
                    case "SetValue": SetProp(a[0], id, a[1]); return null;
                    case "GetMinimum": case "GetMininum": return Conv.To(-1e9, mi.ReturnType);
                    case "GetMaximum": return Conv.To(1e9, mi.ReturnType);
                }
                return Mock.NotHandled;
            };
            _props[id ?? ""] = p = m.Proxy;
            return p;
        }

        string PropKey(BlockModel b, string id)
        {
            if (id == "Override" && b.TypeId == "Gyro") return "GyroOverride";
            if ((id == "UpperLimit" || id == "LowerLimit") && b.TypeId.Contains("Stator")) return id + "Deg";
            if (id == "UpperLimit") return "MaxLimit";
            if (id == "LowerLimit") return "MinLimit";
            string k;
            return PropAlias.TryGetValue(id, out k) ? k : id;
        }

        object GetProp(object block, string id)
        {
            var mk = Mock.Of(block);
            var b = mk != null ? mk.Tag as BlockModel : null;
            if (b == null) return null;
            var key = PropKey(b, id);
            if (key == "Enabled") return b.Enabled;
            if (key.StartsWith("#")) return b.Surfaces.Count > 0 ? SurfaceLogic.Handle(b.Surfaces[0], typeof(IMyTextSurface).GetMethod("get_" + key.Substring(1)), new object[0]) : null;
            return b.Props.Get(key);
        }

        void SetProp(object block, string id, object value)
        {
            var mk = Mock.Of(block);
            var b = mk != null ? mk.Tag as BlockModel : null;
            if (b == null) return;
            var key = PropKey(b, id);
            if (key == "Enabled") { b.Enabled = Convert.ToBoolean(value); return; }
            if (key.StartsWith("#")) { if (b.Surfaces.Count > 0) SurfaceLogic.Handle(b.Surfaces[0], typeof(IMyTextSurface).GetMethod("set_" + key.Substring(1)), new[] { value }); return; }
            b.Props[key] = Conv.Plain(value);
        }

        public object Action(string name)
        {
            object p;
            if (_actions.TryGetValue(name ?? "", out p)) return p;
            var m = new Mock(typeof(ITerminalAction), new Dictionary<string, object> { { "Id", name }, { "Icon", "" } });
            m.Handler = (mk, mi, a) =>
            {
                switch (mi.Name)
                {
                    case "get_Name": return new StringBuilder(name);
                    case "Apply": ApplyAction(a[0], name); return null;
                    case "IsEnabled": return true;
                    case "WriteValue": return null;
                }
                return Mock.NotHandled;
            };
            _actions[name ?? ""] = p = m.Proxy;
            return p;
        }

        public IEnumerable<string> ActionsFor(BlockModel b)
        {
            var list = new List<string> { "OnOff", "OnOff_On", "OnOff_Off" };
            switch (b.TypeId)
            {
                case "Door": case "AirtightHangarDoor": case "AirtightSlideDoor": case "AdvancedDoor": list.AddRange(new[] { "Open", "Open_On", "Open_Off" }); break;
                case "PistonBase": case "ExtendedPistonBase": list.AddRange(new[] { "Extend", "Retract", "Reverse", "IncreaseVelocity", "DecreaseVelocity", "ResetVelocity" }); break;
                case "MotorStator": case "MotorAdvancedStator": list.AddRange(new[] { "Reverse", "IncreaseVelocity", "DecreaseVelocity", "ResetVelocity", "Attach", "Detach" }); break;
                case "BatteryBlock": list.AddRange(new[] { "Recharge", "Discharge", "Auto" }); break;
                case "TimerBlock": list.AddRange(new[] { "TriggerNow", "Start", "Stop" }); break;
                case "MyProgrammableBlock": list.AddRange(new[] { "Run", "RunWithDefaultArgument" }); break;
                case "ShipConnector": list.AddRange(new[] { "SwitchLock", "Connect", "Disconnect" }); break;
                case "LandingGear": list.AddRange(new[] { "Lock", "Unlock", "SwitchLock" }); break;
                case "OxygenTank": list.AddRange(new[] { "Stockpile", "Stockpile_On", "Stockpile_Off", "Refill" }); break;
            }
            return list;
        }

        void ApplyAction(object block, string name)
        {
            var mk = Mock.Of(block);
            var b = mk != null ? mk.Tag as BlockModel : null;
            if (b == null) return;
            var p = b.Props;
            var logic = BlockById(b.Id);
            switch (name)
            {
                case "OnOff": b.Enabled = !b.Enabled; return;
                case "OnOff_On": b.Enabled = true; return;
                case "OnOff_Off": b.Enabled = false; return;
                case "Open": Invoke(logic, "ToggleDoor"); return;
                case "Open_On": Invoke(logic, "OpenDoor"); return;
                case "Open_Off": Invoke(logic, "CloseDoor"); return;
                case "Extend": case "Retract": case "Reverse": case "Attach": case "Detach": Invoke(logic, name); return;
                case "IncreaseVelocity": p["Velocity"] = D(p, "Velocity") + 0.1; return;
                case "DecreaseVelocity": p["Velocity"] = D(p, "Velocity") - 0.1; return;
                case "ResetVelocity": p["Velocity"] = 0.0; return;
                case "Recharge": p["ChargeMode"] = (string)p.Get("ChargeMode") == "Recharge" ? "Auto" : "Recharge"; return;
                case "Discharge": p["ChargeMode"] = (string)p.Get("ChargeMode") == "Discharge" ? "Auto" : "Discharge"; return;
                case "Auto": p["ChargeMode"] = "Auto"; return;
                case "TriggerNow": Log("Таймер «" + b.Name + "» сработал"); return;
                case "Start": p["IsCountingDown"] = true; p["_Remaining"] = D(p, "TriggerDelay", 1); return;
                case "Stop": p["IsCountingDown"] = false; return;
                case "Run": case "RunWithDefaultArgument": QueueRun(b, ""); return;
                case "Connect": Invoke(logic, "Connect"); return;
                case "Disconnect": Invoke(logic, "Disconnect"); return;
                case "SwitchLock": Invoke(logic, b.TypeId == "LandingGear" ? "ToggleLock" : "ToggleConnect"); return;
                case "Lock": case "Unlock": Invoke(logic, name); return;
                case "Stockpile": p["Stockpile"] = !(p.Get("Stockpile") is bool && (bool)p.Get("Stockpile")); return;
                case "Stockpile_On": p["Stockpile"] = true; return;
                case "Stockpile_Off": p["Stockpile"] = false; return;
            }
            Log("Действие «" + name + "» на «" + b.Name + "» не симулируется");
        }

        static void Invoke(BlockLogic logic, string method)
        {
            if (logic == null) return;
            // вызываем через интерфейс, чтобы сработала та же логика, что и у скрипта
            foreach (var t in new[] { typeof(IMyDoor), typeof(IMyPistonBase), typeof(IMyMotorStator), typeof(IMyShipConnector), typeof(IMyLandingGear), typeof(IMyMechanicalConnectionBlock) })
            {
                var mi = t.GetMethod(method, Type.EmptyTypes);
                if (mi != null && logic.Mock.Implements(t)) { mi.Invoke(logic.Proxy, null); return; }
            }
        }

        static double D(Dictionary<string, object> p, string k, double def = 0)
        {
            object o; return p.TryGetValue(k, out o) && o != null ? Convert.ToDouble(o, CultureInfo.InvariantCulture) : def;
        }

        public bool QueueRun(BlockModel pb, string arg)
        {
            if (pb.Id != Grid.PbId) { Log("Запуск другого PB «" + pb.Name + "» с аргументом «" + arg + "» (не симулируется)"); return true; }
            if (InsideRun) return false;   // как в игре: PB не может запустить сам себя
            _pendingRuns.Enqueue(new KeyValuePair<long, string>(pb.Id, arg ?? ""));
            return true;
        }

        // ---------- скрипт ----------

        public List<Diag> LoadScript(string path, string code)
        {
            SaveProgramState();
            ScriptPath = path ?? ""; ScriptCode = code ?? "";
            var gf = GridFileFor(ScriptPath);
            if (gf != null && gf != GridFile)
            {
                GridFile = gf;
                if (File.Exists(gf))
                {
                    try { SetGrid(GridModel.Load(gf), true); Log("Загружена сетка " + Path.GetFileName(gf)); }
                    catch (Exception ex) { Log("Не удалось прочитать " + gf + ": " + ex.Message); }
                }
            }
            CompileDiags = Game.Diagnostics(ScriptCode);
            _programType = null; _program = null;
            Crashed = false; Error = ""; ErrorLine = -1;
            if (CompileDiags.Any(d => d.severity == "error"))
            {
                Error = "Ошибки компиляции: " + CompileDiags.Count(d => d.severity == "error");
                var first = CompileDiags.First(d => d.severity == "error");
                ErrorLine = first.line;
                Version++;
                return CompileDiags;
            }
            try
            {
                var ps = Game.Prepare(ScriptCode, ScriptPath);
                var comp = Game.Compile(ps, debug: true);
                using (var pe = new MemoryStream())
                using (var pdb = new MemoryStream())
                {
                    var res = comp.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                    if (!res.Success)
                    {
                        Error = string.Join("\n", res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                        Version++;
                        return CompileDiags;
                    }
                    var asm = Assembly.Load(pe.ToArray(), pdb.ToArray());
                    _programType = asm.GetTypes().FirstOrDefault(t => typeof(MyGridProgram).IsAssignableFrom(t) && !t.IsAbstract);
                }
                if (_programType == null) { Error = "Не найден класс Program : MyGridProgram"; Version++; return CompileDiags; }
                Instantiate();
                Log("Скрипт скомпилирован" + (Path.GetFileName(ScriptPath) != "" ? ": " + Path.GetFileName(ScriptPath) : ""));
            }
            catch (Exception ex)
            {
                Error = "Ошибка загрузки: " + ex.Message;
            }
            Version++;
            return CompileDiags;
        }

        void SaveProgramState()
        {
            if (_program == null || Crashed) return;
            var gp = (Sandbox.ModAPI.IMyGridProgram)_program;
            if (!gp.HasSaveMethod) return;
            RunGuarded("Save()", () => gp.Save());
            if (_program != null) _storage = gp.Storage ?? "";
        }

        void Instantiate()
        {
            Frequency = UpdateFrequency.None;
            _echo.Clear();
            var obj = FormatterServices.GetUninitializedObject(_programType);
            var gp = (Sandbox.ModAPI.IMyGridProgram)obj;
            var pb = BlockById(Grid.PbId);
            gp.GridTerminalSystem = (IMyGridTerminalSystem)_gts;
            gp.Me = pb != null ? (IMyProgrammableBlock)pb.Proxy : null;
            gp.Runtime = (IMyGridProgramRuntimeInfo)_runtime;
            gp.Storage = _storage;
            gp.Echo = s => { _echo.Append(s).Append('\n'); };
            gp.IGC_ContextGetter = () => (IMyIntergridCommunicationSystem)_igc;
            _program = obj;
            var ctor = _programType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor != null) RunGuarded("конструктор Program()", () => ctor.Invoke(obj, null));
            Echo = _echo.ToString();
        }

        public void Recompile()
        {
            if (!string.IsNullOrEmpty(ScriptCode)) LoadScript(ScriptPath, ScriptCode);
        }

        public void ResetAll()
        {
            Running = false;
            Tick = 0; _lastRunTick = 0;
            _storage = "";
            _program = null;
            _pendingRuns.Clear();
            if (_gridSnapshot != null) SetGrid(GridModel.FromJson(Js.Parse(_gridSnapshot)), false);
            if (_programType != null) { Crashed = false; Error = ""; ErrorLine = -1; Instantiate(); }
            Log("Сброс симуляции");
        }

        void RunMain(string arg, UpdateType type)
        {
            if (_program == null || Crashed) return;
            var gp = (Sandbox.ModAPI.IMyGridProgram)_program;
            if (!gp.HasMainMethod) { Error = "В скрипте нет метода Main"; Crashed = true; return; }
            _echo.Clear();
            LastArgument = arg ?? "";
            var sw = Stopwatch.StartNew();
            RunGuarded("Main", () => gp.Main(arg ?? "", type));
            sw.Stop();
            LastRunMs = sw.Elapsed.TotalMilliseconds;
            _lastRunTick = Tick;
            Runs++;
            Echo = _echo.ToString();
            Version++;
        }

        // Запуск кода скрипта в отдельном потоке со сторожем от бесконечных циклов.
        void RunGuarded(string what, System.Action action)
        {
            Exception error = null;
            InsideRun = true;
            var th = new Thread(() =>
            {
                // игра выполняет скрипты в инвариантной культуре: 0.5, а не 0,5
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                try { action(); }
                catch (ThreadAbortException) { Thread.ResetAbort(); }
                catch (Exception ex) { error = ex; }
            }, 16 * 1024 * 1024) { IsBackground = true, Name = "script" };
            th.Start();
            bool done = th.Join(3000);
            InsideRun = false;
            if (!done)
            {
                try { th.Abort(); } catch { }
                Crash(what + ": скрипт выполняется дольше 3 с — похоже на бесконечный цикл. В игре он упал бы с «Script Too Complex».", -1);
                return;
            }
            if (error != null)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                var st = error.StackTrace ?? "";
                var line = -1;
                var mt = Regex.Match(st, @"Program:(?:line|строка|Zeile|ligne)\s+(\d+)");
                if (mt.Success) line = int.Parse(mt.Groups[1].Value) - 1;
                var frames = st.Split('\n').Where(l => l.Contains("Program:") || l.Contains(" Program.")).Select(l => l.Trim());
                Crash("Исключение в " + what + ": " + error.GetType().Name + ": " + error.Message + "\n" + string.Join("\n", frames), line);
            }
        }

        void Crash(string msg, int line)
        {
            // мир продолжает жить (блоки двигаются), остановлен только скрипт — как в игре
            Crashed = true;
            Error = msg;
            ErrorLine = line;
            Echo = _echo.ToString();
            Log("Скрипт остановлен: " + msg.Split('\n')[0]);
        }

        // ---------- время ----------

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            double acc = 0;
            double last = sw.Elapsed.TotalSeconds;
            while (true)
            {
                Thread.Sleep(5);
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - last;
                last = now;
                if (!Running) { acc = 0; DrainRuns(); continue; }
                acc += dt * Speed;
                int guard = 0;
                while (acc >= 1 / 60.0 && guard++ < 120)
                {
                    acc -= 1 / 60.0;
                    lock (Sync) StepOne();
                    if (!Running) break;
                }
                if (guard >= 120) acc = 0;   // не успеваем — не копим долг
            }
        }

        void DrainRuns()
        {
            if (_pendingRuns.Count == 0) return;
            lock (Sync)
                while (_pendingRuns.Count > 0)
                {
                    var r = _pendingRuns.Dequeue();
                    RunMain(r.Value, UpdateType.Script);
                }
        }

        public void StepOne()
        {
            Tick++;
            foreach (var b in Grid.Blocks)
            {
                Behaviors.Tick(b, 1 / 60.0);
                if (b.Props.ContainsKey("_Fired")) { b.Props.Remove("_Fired"); Log("Таймер «" + b.Name + "» сработал"); }
            }
            UpdateType ut = UpdateType.None;
            if ((Frequency & UpdateFrequency.Update1) != 0) ut |= UpdateType.Update1;
            if ((Frequency & UpdateFrequency.Update10) != 0 && Tick % 10 == 0) ut |= UpdateType.Update10;
            if ((Frequency & UpdateFrequency.Update100) != 0 && Tick % 100 == 0) ut |= UpdateType.Update100;
            if ((Frequency & UpdateFrequency.Once) != 0) { ut |= UpdateType.Once; Frequency &= ~UpdateFrequency.Once; }
            while (_pendingRuns.Count > 0)
            {
                var r = _pendingRuns.Dequeue();
                RunMain(r.Value, UpdateType.Script);
            }
            if (ut != UpdateType.None) RunMain("", ut);
            Version++;
        }

        public void RunTerminal(string arg)
        {
            if (Crashed || _program == null) return;
            RunMain(arg, UpdateType.Terminal);
        }
    }
}
