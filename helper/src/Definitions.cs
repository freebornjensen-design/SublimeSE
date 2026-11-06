using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SeHelper
{
    public class BlockDef
    {
        public string TypeId, SubtypeId, DisplayName, CubeSize;
        public double MaxStoredPower;      // МВт·ч (батареи)
        public double MaxPowerOutput;      // МВт
        public double InventoryVolume;     // м³
        public double GasCapacity;         // л
        public double ForceMagnitude;      // Н (двигатели)
        public double PistonMax;           // м
        public int Screens;                // число экранов (IMyTextSurfaceProvider)
    }

    public class ItemDef
    {
        public string TypeId, SubtypeId, DisplayName, Icon;
        public double Mass, Volume;       // кг, л
    }

    // Читает определения из Content\Data, чтобы проценты и ёмкости были как в игре.
    public class Definitions
    {
        public readonly Dictionary<string, BlockDef> Blocks = new Dictionary<string, BlockDef>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ItemDef> Items = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> InventoryComponents = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _texts = new Dictionary<string, string>();
        readonly string _data;
        bool _loaded;
        readonly object _lock = new object();

        static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public Definitions(string contentDir) { _data = Path.Combine(contentDir, "Data"); }

        public void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                try { Load(); } catch (Exception ex) { Console.Error.WriteLine("Определения: " + ex.Message); }
            }
        }

        public BlockDef Block(string typeId, string subtype)
        {
            EnsureLoaded();
            BlockDef d;
            return Blocks.TryGetValue(typeId + "/" + (subtype ?? ""), out d) ? d : null;
        }

        public ItemDef Item(string typeId, string subtype)
        {
            EnsureLoaded();
            ItemDef d;
            return Items.TryGetValue(Short(typeId) + "/" + subtype, out d) ? d : null;
        }

        public string Text(string key)
        {
            EnsureLoaded();
            string v;
            return key != null && _texts.TryGetValue(key, out v) ? v : key;
        }

        static string Short(string typeId)
        {
            return typeId != null && typeId.StartsWith("MyObjectBuilder_") ? typeId.Substring(16) : typeId;
        }

        void Load()
        {
            LoadTexts(Path.Combine(_data, "Localization", "MyTexts.resx"));
            LoadTexts(Path.Combine(_data, "Localization", "MyTexts.ru.resx"));

            var cube = Path.Combine(_data, "CubeBlocks");
            if (Directory.Exists(cube))
                foreach (var f in Directory.GetFiles(cube, "*.sbc"))
                    LoadBlocks(f);
            foreach (var f in Directory.GetFiles(_data, "*.sbc"))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("CubeBlocks", StringComparison.OrdinalIgnoreCase)) LoadBlocks(f);
                else if (name.StartsWith("PhysicalItems") || name.StartsWith("Components") || name.StartsWith("AmmoMagazines") || name.StartsWith("EntityComponents"))
                    LoadItems(f);
            }
        }

        void LoadTexts(string file)
        {
            if (!File.Exists(file)) return;
            foreach (var d in XDocument.Load(file).Root.Elements("data"))
            {
                var v = d.Element("value");
                if (v != null) _texts[(string)d.Attribute("name")] = v.Value;
            }
        }

        static double Num(XElement e, string name)
        {
            var x = e.Element(name);
            double v;
            return x != null && double.TryParse(x.Value.Trim(), NumberStyles.Float, Inv, out v) ? v : 0;
        }

        void LoadBlocks(string file)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); } catch { return; }
            foreach (var def in doc.Descendants("Definition"))
            {
                var id = def.Element("Id");
                if (id == null) continue;
                var typeId = (string)id.Element("TypeId");
                var sub = (string)id.Element("SubtypeId") ?? "";
                if (typeId == null) continue;
                var bd = new BlockDef
                {
                    TypeId = typeId, SubtypeId = sub,
                    DisplayName = Text((string)def.Element("DisplayName")),
                    CubeSize = (string)def.Element("CubeSize") ?? "Large",
                    MaxStoredPower = Num(def, "MaxStoredPower"),
                    MaxPowerOutput = Num(def, "MaxPowerOutput"),
                    InventoryVolume = Num(def, "InventoryMaxVolume"),
                    GasCapacity = Num(def, "Capacity"),
                    ForceMagnitude = Num(def, "ForceMagnitude"),
                    PistonMax = Num(def, "Maximum"),
                };
                var size = def.Element("InventorySize");
                if (bd.InventoryVolume == 0 && size != null)
                    bd.InventoryVolume = Num(size, "X") * Num(size, "Y") * Num(size, "Z");
                var screens = def.Element("ScreenAreas");
                if (screens != null) bd.Screens = screens.Elements("ScreenArea").Count();
                Blocks[typeId + "/" + sub] = bd;
            }
        }

        void LoadItems(string file)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); } catch { return; }
            // предметы лежат в <PhysicalItem>, <Component>, <AmmoMagazine>… — берём любой элемент с <Id>
            foreach (var def in doc.Descendants().Where(e => e.Element("Id") != null))
            {
                var id = def.Element("Id");
                var typeId = (string)id.Element("TypeId") ?? (string)id.Attribute("Type");
                var sub = (string)id.Element("SubtypeId") ?? (string)id.Attribute("Subtype") ?? "";
                if (typeId == null) continue;
                var xtype = (string)def.Attribute(Xsi + "type") ?? "";
                if (xtype.Contains("InventoryComponentDefinition"))
                {
                    var v = Num(def, "Volume");
                    var size = def.Element("Size");
                    if (v == 0 && size != null) v = Num(size, "X") * Num(size, "Y") * Num(size, "Z");
                    InventoryComponents[sub] = v;
                    continue;
                }
                if (def.Element("Volume") == null || def.Element("Mass") == null) continue;
                Items[typeId + "/" + sub] = new ItemDef
                {
                    TypeId = "MyObjectBuilder_" + typeId, SubtypeId = sub,
                    DisplayName = Text((string)def.Element("DisplayName")),
                    Mass = Num(def, "Mass"), Volume = Num(def, "Volume"),
                    Icon = (string)def.Elements("Icon").FirstOrDefault(),
                };
            }
        }
    }
}
