using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SeHelper
{
    public class ItemModel
    {
        public string TypeId;   // MyObjectBuilder_Ore
        public string Subtype;  // Iron
        public double Amount;
        public uint ItemId;
    }

    public class InventoryModel
    {
        public double MaxVolume;   // м³
        public List<ItemModel> Items = new List<ItemModel>();
        public uint NextId = 1;
    }

    public class SurfaceModel
    {
        public string Name = "Экран";
        public string Text = "";
        public double FontSize = 1;
        public string FontColor = "#FFFFFF";
        public string BackgroundColor = "#000000";
        public string Alignment = "LEFT";
        public string ContentType = "NONE";
        public string Font = "Debug";
        public double TextPadding = 2;
        public double Width = 512, Height = 512;
        // режим SCRIPT: спрайты последнего кадра DrawFrame (живут только в симуляции, в файл не пишутся)
        public string Script = "";
        public string ScriptBackgroundColor = "#005897";
        public string ScriptForegroundColor = "#B3EDFF";
        public List<Dictionary<string, object>> Sprites = new List<Dictionary<string, object>>();
        public long Frames;
    }

    public class BlockModel
    {
        public long Id;
        public string Name = "";
        public string TypeId = "TerminalBlock";
        public string Subtype = "";
        public string Iface = "IMyTerminalBlock";
        public bool Enabled = true;
        public string CustomData = "";
        public int GridIndex;
        public int[] Pos;
        public Dictionary<string, object> Props = new Dictionary<string, object>();
        public List<InventoryModel> Inventories = new List<InventoryModel>();
        public List<SurfaceModel> Surfaces = new List<SurfaceModel>();
    }

    public class GridModel
    {
        public string Name = "Новая сетка";
        public string Source = "";
        public List<string> GridNames = new List<string>();
        public List<BlockModel> Blocks = new List<BlockModel>();
        public Dictionary<string, List<long>> Groups = new Dictionary<string, List<long>>();
        public long PbId;

        // ---------- JSON ----------

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "name", Name }, { "source", Source }, { "gridNames", GridNames }, { "pbId", PbId.ToString() },
                { "groups", Groups.ToDictionary(g => g.Key, g => (object)g.Value.Select(i => i.ToString()).ToList()) },
                { "blocks", Blocks.Select(BlockJson).ToList() },
            };
        }

        static Dictionary<string, object> BlockJson(BlockModel b)
        {
            return new Dictionary<string, object>
            {
                { "id", b.Id.ToString() }, { "name", b.Name }, { "typeId", b.TypeId }, { "subtype", b.Subtype }, { "iface", b.Iface },
                { "enabled", b.Enabled }, { "customData", b.CustomData }, { "grid", b.GridIndex }, { "pos", b.Pos },
                { "props", b.Props.ToDictionary(p => p.Key, p => Plain(p.Value)) },
                { "inventories", b.Inventories.Select(inv => new Dictionary<string, object>
                    {
                        { "maxVolume", inv.MaxVolume },
                        { "items", inv.Items.Select(it => new Dictionary<string, object> { { "typeId", it.TypeId }, { "subtype", it.Subtype }, { "amount", it.Amount } }).ToList() },
                    }).ToList() },
                { "surfaces", b.Surfaces.Select(s => new Dictionary<string, object>
                    {
                        { "name", s.Name }, { "text", s.Text }, { "fontSize", s.FontSize }, { "fontColor", s.FontColor },
                        { "backgroundColor", s.BackgroundColor }, { "alignment", s.Alignment }, { "contentType", s.ContentType },
                        { "font", s.Font }, { "padding", s.TextPadding }, { "width", s.Width }, { "height", s.Height },
                    }).ToList() },
            };
        }

        public static object Plain(object v)
        {
            if (v == null || v is string || v is bool) return v;
            if (v is Enum) return v.ToString();
            if (v is IConvertible) { try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { } }
            return v.ToString();
        }

        public static GridModel FromJson(Dictionary<string, object> j)
        {
            var g = new GridModel
            {
                Name = S(j, "name", "Сетка"), Source = S(j, "source", ""),
                PbId = L(j, "pbId"),
            };
            object o;
            if (j.TryGetValue("gridNames", out o) && o is System.Collections.ArrayList)
                g.GridNames = ((System.Collections.ArrayList)o).Cast<object>().Select(x => x + "").ToList();
            if (j.TryGetValue("groups", out o) && o is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)o)
                    g.Groups[kv.Key] = ((System.Collections.ArrayList)kv.Value).Cast<object>().Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToList();
            if (j.TryGetValue("blocks", out o) && o is System.Collections.ArrayList)
                foreach (Dictionary<string, object> bj in (System.Collections.ArrayList)o)
                {
                    var b = new BlockModel
                    {
                        Id = L(bj, "id"), Name = S(bj, "name", ""), TypeId = S(bj, "typeId", "TerminalBlock"), Subtype = S(bj, "subtype", ""),
                        Iface = S(bj, "iface", "IMyTerminalBlock"), Enabled = !bj.ContainsKey("enabled") || Convert.ToBoolean(bj["enabled"]),
                        CustomData = S(bj, "customData", ""), GridIndex = bj.ContainsKey("grid") ? Convert.ToInt32(bj["grid"]) : 0,
                    };
                    if (bj.TryGetValue("props", out o) && o is Dictionary<string, object>)
                        foreach (var kv in (Dictionary<string, object>)o) b.Props[kv.Key] = kv.Value is decimal ? Convert.ToDouble(kv.Value) : kv.Value is int ? Convert.ToDouble(kv.Value) : kv.Value;
                    if (bj.TryGetValue("inventories", out o) && o is System.Collections.ArrayList)
                        foreach (Dictionary<string, object> ij in (System.Collections.ArrayList)o)
                        {
                            var inv = new InventoryModel { MaxVolume = D(ij, "maxVolume") };
                            foreach (Dictionary<string, object> it in (System.Collections.ArrayList)ij["items"])
                                inv.Items.Add(new ItemModel { TypeId = S(it, "typeId", ""), Subtype = S(it, "subtype", ""), Amount = D(it, "amount"), ItemId = inv.NextId++ });
                            b.Inventories.Add(inv);
                        }
                    if (bj.TryGetValue("surfaces", out o) && o is System.Collections.ArrayList)
                        foreach (Dictionary<string, object> sj in (System.Collections.ArrayList)o)
                            b.Surfaces.Add(new SurfaceModel
                            {
                                Name = S(sj, "name", "Экран"), Text = S(sj, "text", ""), FontSize = D(sj, "fontSize", 1), FontColor = S(sj, "fontColor", "#FFFFFF"),
                                BackgroundColor = S(sj, "backgroundColor", "#000000"), Alignment = S(sj, "alignment", "LEFT"), ContentType = S(sj, "contentType", "NONE"),
                                Font = S(sj, "font", "Debug"), TextPadding = D(sj, "padding", 2), Width = D(sj, "width", 512), Height = D(sj, "height", 512),
                            });
                    g.Blocks.Add(b);
                }
            return g;
        }

        public static string S(Dictionary<string, object> j, string k, string def)
        {
            object o; return j.TryGetValue(k, out o) && o != null ? o.ToString() : def;
        }
        public static double D(Dictionary<string, object> j, string k, double def = 0)
        {
            object o; return j.TryGetValue(k, out o) && o != null ? Convert.ToDouble(o, CultureInfo.InvariantCulture) : def;
        }
        public static long L(Dictionary<string, object> j, string k)
        {
            object o; long v;
            return j.TryGetValue(k, out o) && o != null && long.TryParse(o.ToString(), out v) ? v : 0;
        }

        public void Save(string file)
        {
            File.WriteAllText(file, Js.Serialize(ToJson()));
        }

        public static GridModel Load(string file)
        {
            return FromJson(Js.Parse(File.ReadAllText(file)));
        }
    }

    // ---------- импорт блупринта ----------

    public static class BlueprintImporter
    {
        static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string BlueprintsDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "Blueprints", "local"); }
        }

        public static List<string> List()
        {
            if (!Directory.Exists(BlueprintsDir)) return new List<string>();
            return Directory.GetDirectories(BlueprintsDir)
                .Where(d => File.Exists(Path.Combine(d, "bp.sbc")))
                .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "bp.sbc")))
                .Select(Path.GetFileName).ToList();
        }

        public static GridModel Import(string name, BlockFactory factory)
        {
            var file = Path.Combine(BlueprintsDir, name, "bp.sbc");
            var doc = XDocument.Load(file);
            var grid = new GridModel { Name = name, Source = "blueprint:" + name };
            int gi = 0;
            foreach (var cg in doc.Descendants("CubeGrid"))
            {
                grid.GridNames.Add((string)cg.Element("DisplayName") ?? ("Сетка " + gi));
                var byPos = new Dictionary<string, BlockModel>();
                var blocks = cg.Element("CubeBlocks");
                if (blocks != null)
                    foreach (var x in blocks.Elements())
                    {
                        var xtype = ((string)x.Attribute(Xsi + "type") ?? "MyObjectBuilder_CubeBlock").Replace("MyObjectBuilder_", "");
                        var sub = (string)x.Element("SubtypeName") ?? "";
                        var customName = (string)x.Element("CustomName");
                        var b = factory.Create(xtype, sub, customName, x.Element("Enabled") != null);
                        if (b == null) continue;
                        var min = x.Element("Min");
                        if (min != null) b.Pos = new[] { (int)min.Attribute("x"), (int)min.Attribute("y"), (int)min.Attribute("z") };
                        var eid = (string)x.Element("EntityId");
                        long id;
                        if (eid != null && long.TryParse(eid, out id)) b.Id = id;
                        b.GridIndex = gi;
                        if (x.Element("Enabled") != null) b.Enabled = (bool)x.Element("Enabled");
                        b.CustomData = (string)x.Element("CustomData") ?? "";
                        ReadState(x, b, factory);
                        grid.Blocks.Add(b);
                        if (b.Pos != null) byPos[string.Join(",", b.Pos)] = b;
                    }
                var groups = cg.Element("BlockGroups");
                if (groups != null)
                    foreach (var g in groups.Elements("MyObjectBuilder_BlockGroup"))
                    {
                        var gname = (string)g.Element("Name");
                        if (gname == null) continue;
                        List<long> ids;
                        if (!grid.Groups.TryGetValue(gname, out ids)) grid.Groups[gname] = ids = new List<long>();
                        var bl = g.Element("Blocks");
                        if (bl == null) continue;
                        foreach (var v in bl.Elements("Vector3I"))
                        {
                            BlockModel b;
                            var key = (string)v.Element("X") + "," + (string)v.Element("Y") + "," + (string)v.Element("Z");
                            if (byPos.TryGetValue(key, out b) && !ids.Contains(b.Id)) ids.Add(b.Id);
                        }
                    }
                gi++;
            }
            var pb = grid.Blocks.FirstOrDefault(b => b.Iface == "IMyProgrammableBlock");
            if (pb == null)
            {
                pb = factory.Create("MyProgrammableBlock", "LargeProgrammableBlock", "Программируемый блок (симулятор)", true);
                grid.Blocks.Insert(0, pb);
            }
            grid.PbId = pb.Id;
            return grid;
        }

        static double Num(XElement x, string name, double def)
        {
            var e = x.Element(name);
            double v;
            return e != null && double.TryParse(e.Value, NumberStyles.Float, Inv, out v) ? v : def;
        }

        static void ReadState(XElement x, BlockModel b, BlockFactory factory)
        {
            switch (b.TypeId)
            {
                case "BatteryBlock":
                    b.Props["CurrentStoredPower"] = Num(x, "CurrentStoredPower", Convert.ToDouble(b.Props["CurrentStoredPower"]));
                    var cm = (string)x.Element("ChargeMode");
                    if (cm == "1") b.Props["ChargeMode"] = "Recharge";
                    else if (cm == "2") b.Props["ChargeMode"] = "Discharge";
                    else if (cm != null && cm != "0") b.Props["ChargeMode"] = cm;
                    break;
                case "OxygenTank":
                    b.Props["FilledRatio"] = Num(x, "FilledRatio", 0);
                    break;
                case "ExtendedPistonBase":
                case "PistonBase":
                    b.Props["CurrentPosition"] = Num(x, "CurrentPosition", 0);
                    b.Props["Velocity"] = Num(x, "Velocity", Convert.ToDouble(b.Props["Velocity"]));
                    if (x.Element("MaxLimit") != null) b.Props["MaxLimit"] = Num(x, "MaxLimit", 10);
                    if (x.Element("MinLimit") != null) b.Props["MinLimit"] = Num(x, "MinLimit", 0);
                    break;
                case "Door":
                case "AirtightHangarDoor":
                case "AirtightSlideDoor":
                    var st = x.Element("State");
                    if (st != null && (bool)st) { b.Props["OpenRatio"] = 1.0; b.Props["Status"] = "Open"; }
                    break;
            }

            // инвентари
            var invs = x.Descendants().Where(e => (string)e.Attribute(Xsi + "type") == "MyObjectBuilder_Inventory").ToList();
            for (int i = 0; i < invs.Count && i < b.Inventories.Count; i++)
            {
                var inv = b.Inventories[i];
                var vol = Num(invs[i], "Volume", 0);
                if (vol > 0 && vol < 1e9) inv.MaxVolume = vol;
                var items = invs[i].Element("Items");
                if (items == null) continue;
                foreach (var it in items.Elements())
                {
                    var pc = it.Element("PhysicalContent");
                    if (pc == null) continue;
                    inv.Items.Add(new ItemModel
                    {
                        TypeId = (string)pc.Attribute(Xsi + "type") ?? "MyObjectBuilder_Component",
                        Subtype = (string)pc.Element("SubtypeName") ?? "",
                        Amount = Num(it, "Amount", 1),
                        ItemId = inv.NextId++,
                    });
                }
            }

            // текст на LCD (старый и новый формат)
            var txt = x.Element("PublicDescription") ?? x.Element("Text");
            if (txt != null && b.Surfaces.Count > 0) b.Surfaces[0].Text = txt.Value;
            var fs = x.Element("FontSize");
            if (fs != null && b.Surfaces.Count > 0) b.Surfaces[0].FontSize = Num(x, "FontSize", 1);
        }
    }
}
