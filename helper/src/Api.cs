using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VRage;
using VRageMath;

namespace SeHelper
{
    public class Api
    {
        readonly GameApi _game;
        readonly SimEngine _e;
        readonly Dictionary<string, object> _schemaCache = new Dictionary<string, object>();

        public Api(GameApi game, SimEngine engine) { _game = game; _e = engine; }

        static string Str(Dictionary<string, object> j, string k, string def = "") { return GridModel.S(j, k, def); }

        public HttpResult Handle(string method, string path, string query, string body)
        {
            var j = method == "POST" ? Js.Parse(body) : new Dictionary<string, object>();
            switch (path)
            {
                case "/api/ping":
                    return HttpResult.Json(new Dictionary<string, object> { { "ok", true }, { "version", _e.Version }, { "pid", System.Diagnostics.Process.GetCurrentProcess().Id }, { "whitelist", GameWhitelist.Status } });

                // ---- редактор ----
                case "/api/diagnostics":
                    return HttpResult.Json(new Dictionary<string, object> { { "diagnostics", _game.Diagnostics(Str(j, "code")) } });
                case "/api/complete":
                    return HttpResult.Json(new Dictionary<string, object> { { "items", _game.Complete(Str(j, "code"), (int)GridModel.D(j, "pos")) } });
                case "/api/hover":
                    return HttpResult.Json(_game.Hover(Str(j, "code"), (int)GridModel.D(j, "pos")) ?? new Dictionary<string, object>());
                case "/api/load":
                    lock (_e.Sync)
                    {
                        var d = _e.LoadScript(Str(j, "path"), Str(j, "code"));
                        return HttpResult.Json(new Dictionary<string, object> { { "diagnostics", d }, { "error", _e.Error } });
                    }

                // ---- симулятор ----
                case "/api/state":
                    lock (_e.Sync) return HttpResult.Json(State());
                case "/api/control":
                    lock (_e.Sync) Control(j);
                    return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                case "/api/block":
                    lock (_e.Sync) UpdateBlock(j);
                    return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                case "/api/block/add":
                    lock (_e.Sync)
                    {
                        var b = _e.Factory.Create(Str(j, "typeId"), Str(j, "subtype"), Str(j, "name", null), true);
                        if (b == null) return HttpResult.Error(400, "Неизвестный тип блока");
                        while (_e.Grid.Blocks.Any(x => x.Id == b.Id)) b.Id++;
                        if (string.IsNullOrEmpty(b.Name)) b.Name = b.TypeId;
                        _e.Grid.Blocks.Add(b);
                        _e.SetGrid(_e.Grid, false);
                        return HttpResult.Json(new Dictionary<string, object> { { "id", b.Id.ToString() } });
                    }
                case "/api/block/remove":
                    lock (_e.Sync)
                    {
                        var id = GridModel.L(j, "id");
                        _e.Grid.Blocks.RemoveAll(b => b.Id == id);
                        foreach (var g in _e.Grid.Groups.Values) g.Remove(id);
                        _e.SetGrid(_e.Grid, false);
                        return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                    }
                case "/api/pb":
                    lock (_e.Sync)
                    {
                        _e.Grid.PbId = GridModel.L(j, "id");
                        _e.SetGrid(_e.Grid, false);
                        _e.Recompile();
                        return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                    }
                case "/api/group":
                    lock (_e.Sync)
                    {
                        var name = Str(j, "name");
                        if (j.ContainsKey("delete")) _e.Grid.Groups.Remove(name);
                        else
                        {
                            var ids = ((ArrayList)j["ids"]).Cast<object>().Select(x => long.Parse(x.ToString())).ToList();
                            _e.Grid.Groups[name] = ids;
                        }
                        _e.SetGrid(_e.Grid, false);
                        return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                    }
                case "/api/blueprints":
                    return HttpResult.Json(new Dictionary<string, object> { { "blueprints", BlueprintImporter.List() } });
                case "/api/grid/import":
                {
                    var g = BlueprintImporter.Import(Str(j, "name"), _e.Factory);
                    lock (_e.Sync)
                    {
                        _e.SetGrid(g, true);
                        _e.Log("Импортирован блупринт «" + g.Name + "»: " + g.Blocks.Count + " терминальных блоков, групп: " + g.Groups.Count);
                        if (_e.GridFile != null) _e.SaveGrid();
                        _e.Recompile();
                    }
                    return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                }
                case "/api/grid/new":
                    lock (_e.Sync) { _e.NewDefaultGrid(); _e.Recompile(); }
                    return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
                case "/api/grid/save":
                    lock (_e.Sync)
                    {
                        _e.SaveGrid(j.ContainsKey("path") ? Str(j, "path") : null);
                        _e.Log(_e.GridFile != null ? "Сетка сохранена: " + _e.GridFile : "Начальное состояние запомнено (скрипт не открыт, файл не записан)");
                        return HttpResult.Json(new Dictionary<string, object> { { "ok", true }, { "file", _e.GridFile } });
                    }
                case "/api/catalog":
                    return HttpResult.Json(Catalog());
                case "/api/texture":
                {
                    var name = Uri.UnescapeDataString((query ?? "").StartsWith("name=") ? query.Substring(5).Replace('+', ' ') : "");
                    var png = _game.Tex.Png(name);
                    if (png == null) return HttpResult.Error(404, "Нет текстуры " + name);
                    return new HttpResult { ContentType = "image/png", Body = png, Cacheable = true };
                }
            }
            return HttpResult.Error(404, "Нет такого метода: " + path);
        }

        void Control(Dictionary<string, object> j)
        {
            switch (Str(j, "action"))
            {
                case "start": _e.Running = true; break;
                case "pause": _e.Running = false; break;
                case "step":
                    int n = Math.Max(1, (int)GridModel.D(j, "ticks", 1));
                    for (int i = 0; i < n; i++) _e.StepOne();
                    break;
                case "run": _e.RunTerminal(Str(j, "argument")); break;
                case "reset": _e.ResetAll(); break;
                case "recompile": _e.Recompile(); break;
                case "speed": _e.Speed = Math.Max(0.05, Math.Min(20, GridModel.D(j, "speed", 1))); break;
                case "clearlog": _e.LogLines.Clear(); break;
            }
            _e.MarkDirty();
        }

        void UpdateBlock(Dictionary<string, object> j)
        {
            var b = _e.Grid.Blocks.FirstOrDefault(x => x.Id == GridModel.L(j, "id"));
            if (b == null) return;
            bool rebuild = false;
            if (j.ContainsKey("name")) b.Name = Str(j, "name");
            if (j.ContainsKey("enabled")) b.Enabled = Convert.ToBoolean(j["enabled"]);
            if (j.ContainsKey("customData")) b.CustomData = Str(j, "customData");
            object o;
            if (j.TryGetValue("props", out o) && o is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)o)
                {
                    if (kv.Value == null) { b.Props.Remove(kv.Key); continue; }
                    b.Props[kv.Key] = kv.Value is int || kv.Value is decimal || kv.Value is long ? Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture) : kv.Value;
                }
            if (j.TryGetValue("surface", out o) && o is Dictionary<string, object>)
            {
                var sj = (Dictionary<string, object>)o;
                int i = (int)GridModel.D(sj, "index");
                if (i >= 0 && i < b.Surfaces.Count)
                {
                    var s = b.Surfaces[i];
                    if (sj.ContainsKey("text")) s.Text = Str(sj, "text");
                    if (sj.ContainsKey("fontSize")) s.FontSize = GridModel.D(sj, "fontSize", 1);
                    if (sj.ContainsKey("fontColor")) s.FontColor = Str(sj, "fontColor");
                    if (sj.ContainsKey("backgroundColor")) s.BackgroundColor = Str(sj, "backgroundColor");
                    if (sj.ContainsKey("alignment")) s.Alignment = Str(sj, "alignment");
                }
            }
            if (j.TryGetValue("inventory", out o) && o is Dictionary<string, object>)
            {
                var ij = (Dictionary<string, object>)o;
                int i = (int)GridModel.D(ij, "index");
                if (i >= 0 && i < b.Inventories.Count)
                {
                    var inv = b.Inventories[i];
                    if (ij.ContainsKey("maxVolume")) inv.MaxVolume = GridModel.D(ij, "maxVolume");
                    if (ij.ContainsKey("items"))
                    {
                        inv.Items.Clear();
                        foreach (Dictionary<string, object> it in (ArrayList)ij["items"])
                        {
                            var amount = GridModel.D(it, "amount");
                            if (amount <= 0) continue;
                            inv.Items.Add(new ItemModel { TypeId = Str(it, "typeId"), Subtype = Str(it, "subtype"), Amount = amount, ItemId = inv.NextId++ });
                        }
                    }
                }
            }
            if (j.ContainsKey("groups") && j["groups"] is ArrayList)
            {
                var want = ((ArrayList)j["groups"]).Cast<object>().Select(x => x.ToString()).ToList();
                foreach (var g in _e.Grid.Groups) if (!want.Contains(g.Key)) g.Value.Remove(b.Id);
                foreach (var name in want)
                {
                    List<long> ids;
                    if (!_e.Grid.Groups.TryGetValue(name, out ids)) _e.Grid.Groups[name] = ids = new List<long>();
                    if (!ids.Contains(b.Id)) ids.Add(b.Id);
                }
                rebuild = true;
            }
            if (rebuild) _e.SetGrid(_e.Grid, false);
            _e.MarkDirty();
        }

        // ---------- состояние для GUI ----------

        Dictionary<string, object> State()
        {
            var defs = _game.Defs;
            var groupsOf = new Dictionary<long, List<string>>();
            foreach (var g in _e.Grid.Groups)
                foreach (var id in g.Value)
                {
                    List<string> l;
                    if (!groupsOf.TryGetValue(id, out l)) groupsOf[id] = l = new List<string>();
                    l.Add(g.Key);
                }
            var blocks = new List<object>();
            foreach (var b in _e.Grid.Blocks)
            {
                var def = defs.Block(b.TypeId, b.Subtype);
                List<string> gl;
                groupsOf.TryGetValue(b.Id, out gl);
                blocks.Add(new Dictionary<string, object>
                {
                    { "id", b.Id.ToString() }, { "name", b.Name }, { "typeId", b.TypeId }, { "subtype", b.Subtype }, { "iface", b.Iface },
                    { "display", def != null ? def.DisplayName : b.TypeId }, { "enabled", b.Enabled }, { "customData", b.CustomData },
                    { "grid", b.GridIndex }, { "groups", gl ?? new List<string>() }, { "isPb", b.Id == _e.Grid.PbId },
                    { "bars", Behaviors.Bars(b, defs) },
                    { "props", b.Props.ToDictionary(p => p.Key, p => GridModel.Plain(p.Value)) },
                    { "inventories", b.Inventories.Select(inv => new Dictionary<string, object>
                        {
                            { "maxVolume", inv.MaxVolume }, { "curVolume", Behaviors.InventoryVolume(inv, defs) },
                            { "items", inv.Items.Select(it =>
                                {
                                    var idf = defs.Item(it.TypeId, it.Subtype);
                                    return new Dictionary<string, object>
                                    {
                                        { "typeId", it.TypeId }, { "subtype", it.Subtype }, { "amount", it.Amount },
                                        { "name", idf != null ? idf.DisplayName : it.Subtype },
                                    };
                                }).ToList() },
                        }).ToList() },
                    { "surfaces", b.Surfaces.Select((s, i) => new Dictionary<string, object>
                        {
                            { "index", i }, { "name", s.Name }, { "text", s.Text }, { "fontSize", s.FontSize }, { "fontColor", s.FontColor },
                            { "backgroundColor", s.BackgroundColor }, { "alignment", s.Alignment }, { "contentType", s.ContentType },
                            { "font", s.Font }, { "padding", s.TextPadding }, { "width", s.Width }, { "height", s.Height },
                            { "script", s.Script }, { "scriptBg", s.ScriptBackgroundColor }, { "scriptFg", s.ScriptForegroundColor },
                            { "sprites", s.Sprites }, { "frames", s.Frames },
                        }).ToList() },
                });
            }
            var schemas = new Dictionary<string, object>();
            foreach (var iface in _e.Grid.Blocks.Select(b => b.Iface).Distinct()) schemas[iface] = Schema(iface);
            return new Dictionary<string, object>
            {
                { "version", _e.Version },
                { "running", _e.Running }, { "tick", _e.Tick }, { "speed", _e.Speed },
                { "frequency", _e.Frequency.ToString() }, { "lastRunMs", _e.LastRunMs }, { "runs", _e.Runs },
                { "echo", _e.Echo }, { "error", _e.Error }, { "errorLine", _e.ErrorLine }, { "crashed", _e.Crashed },
                { "script", new Dictionary<string, object>
                    {
                        { "path", _e.ScriptPath }, { "name", Path.GetFileName(_e.ScriptPath ?? "") },
                        { "loaded", !string.IsNullOrEmpty(_e.ScriptCode) },
                        { "errors", _e.CompileDiags.Count(d => d.severity == "error") },
                        { "warnings", _e.CompileDiags.Count(d => d.severity == "warning") },
                        { "diagnostics", _e.CompileDiags.Take(30).ToList() },
                    } },
                { "grid", new Dictionary<string, object>
                    {
                        { "name", _e.Grid.Name }, { "source", _e.Grid.Source }, { "file", _e.GridFile ?? "" },
                        { "gridNames", _e.Grid.GridNames }, { "pbId", _e.Grid.PbId.ToString() },
                        { "groups", _e.Grid.Groups.ToDictionary(g => g.Key, g => (object)g.Value.Select(i => i.ToString()).ToList()) },
                    } },
                { "blocks", blocks },
                { "schemas", schemas },
                { "log", _e.LogLines.Skip(Math.Max(0, _e.LogLines.Count - 100)).ToList() },
            };
        }

        static readonly HashSet<string> SkipProps = new HashSet<string>
        {
            "CustomName", "CustomData", "Enabled", "DetailedInfo", "CustomInfo", "DisplayNameText", "DisplayName", "Name", "EntityId",
            "DefinitionDisplayNameText", "IsBeingHacked", "IsFunctional", "IsWorking", "OwnerId", "NumberInGrid", "Mass", "DisassembleRatio",
            "ShowInTerminal", "ShowInToolbarConfig", "ShowOnHUD", "ShowInInventory", "BlockDefinition", "CubeGrid", "Position", "Min", "Max",
            "Orientation", "WorldMatrix", "WorldAABB", "WorldAABBHr", "WorldVolume", "WorldVolumeHr", "Closed", "HasInventory", "InventoryCount",
            "CheckConnectionAllowed", "SurfaceCount", "FontSize", "FontColor", "BackgroundColor", "BackgroundAlpha", "Alignment", "ContentType",
            "Font", "TextPadding", "Script", "ScriptBackgroundColor", "ScriptForegroundColor", "PreserveAspectRatio", "ChangeInterval",
            "SurfaceSize", "TextureSize", "CurrentlyShownImage", "ShowText", "DisplayNameText",
        };

        static readonly Dictionary<string, string> SimLabels = new Dictionary<string, string>
        {
            { "_Load", "Нагрузка сети, МВт" }, { "_Supply", "Внешнее питание, МВт" }, { "_Flow", "Приток газа, л/с" },
            { "_Oxygen", "Уровень O₂ (0..1)" }, { "_Remaining", "Осталось до срабатывания, с" },
        };

        object Schema(string ifaceName)
        {
            object cached;
            if (_schemaCache.TryGetValue(ifaceName, out cached)) return cached;
            var list = new List<object>();
            var t = _game.FindIngameInterface(ifaceName);
            if (t != null)
            {
                var seen = new HashSet<string>();
                foreach (var it in new[] { t }.Concat(t.GetInterfaces()))
                    foreach (var p in it.GetProperties())
                    {
                        if (SkipProps.Contains(p.Name) || !seen.Add(p.Name)) continue;
                        var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                        string kind = null;
                        object options = null;
                        if (pt == typeof(bool)) kind = "bool";
                        else if (pt.IsEnum) { kind = "enum"; options = Enum.GetNames(pt); }
                        else if (pt == typeof(float) || pt == typeof(double) || pt == typeof(int) || pt == typeof(long) || pt == typeof(MyFixedPoint)) kind = "number";
                        else if (pt == typeof(string)) kind = "string";
                        else if (pt == typeof(Color)) kind = "color";
                        if (kind == null) continue;
                        var d = new Dictionary<string, object> { { "name", p.Name }, { "kind", kind }, { "scriptCanSet", p.CanWrite } };
                        if (options != null) d["options"] = options;
                        list.Add(d);
                    }
            }
            foreach (var kv in SimLabels)
                list.Add(new Dictionary<string, object> { { "name", kv.Key }, { "kind", "number" }, { "label", kv.Value }, { "sim", true } });
            _schemaCache[ifaceName] = list;
            return list;
        }

        Dictionary<string, object> Catalog()
        {
            var defs = _game.Defs;
            defs.EnsureLoaded();
            return new Dictionary<string, object>
            {
                { "blocks", BlockFactory.Catalog.Select(c => new Dictionary<string, object> { { "typeId", c[0] }, { "subtype", c[1] }, { "label", c[2] } }).ToList() },
                { "items", defs.Items.Values
                    .Where(i => new[] { "MyObjectBuilder_Ore", "MyObjectBuilder_Ingot", "MyObjectBuilder_Component", "MyObjectBuilder_AmmoMagazine", "MyObjectBuilder_PhysicalGunObject", "MyObjectBuilder_GasContainerObject", "MyObjectBuilder_OxygenContainerObject", "MyObjectBuilder_ConsumableItem" }.Contains(i.TypeId))
                    .OrderBy(i => i.TypeId).ThenBy(i => i.DisplayName)
                    .Select(i => new Dictionary<string, object> { { "typeId", i.TypeId }, { "subtype", i.SubtypeId }, { "name", i.DisplayName }, { "volume", i.Volume } }).ToList() },
            };
        }
    }
}
