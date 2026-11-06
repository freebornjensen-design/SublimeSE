using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRageMath;

namespace SeHelper
{
    // Универсальный мок любого интерфейса игры на RealProxy.
    // Свойства без особой логики живут в словаре Props, интерфейсные значения
    // по умолчанию получают «пустой» мок, чтобы скрипт не падал на null.
    public class Mock : RealProxy, IRemotingTypeInfo
    {
        public static readonly object NotHandled = new object();

        public readonly Type Primary;
        readonly HashSet<Type> _ifaces = new HashSet<Type>();
        public Dictionary<string, object> Props;
        readonly Dictionary<string, object> _children = new Dictionary<string, object>();
        public Func<Mock, MethodInfo, object[], object> Handler;
        public Func<Type, bool> CastFilter;
        public object Tag;
        object _tp;

        public Mock(Type primary, Dictionary<string, object> props, params Type[] extra) : base(primary)
        {
            Primary = primary;
            Props = props ?? new Dictionary<string, object>();
            AddIface(primary);
            foreach (var t in extra) if (t != null) AddIface(t);
        }

        void AddIface(Type t)
        {
            _ifaces.Add(t);
            foreach (var i in t.GetInterfaces()) _ifaces.Add(i);
        }

        public object Proxy { get { return _tp ?? (_tp = GetTransparentProxy()); } }

        public static Mock Of(object proxy)
        {
            if (proxy == null || !RemotingServices.IsTransparentProxy(proxy)) return null;
            return RemotingServices.GetRealProxy(proxy) as Mock;
        }

        public string TypeName { get { return Primary.FullName; } set { } }

        public bool CanCastTo(Type t, object o)
        {
            if (t == typeof(object)) return true;
            foreach (var i in _ifaces) if (t.IsAssignableFrom(i)) return true;
            return CastFilter != null && CastFilter(t);
        }

        public bool Implements(Type t) { return CanCastTo(t, null); }

        public override IMessage Invoke(IMessage msg)
        {
            var call = (IMethodCallMessage)msg;
            var mi = (MethodInfo)call.MethodBase;
            var args = call.Args;
            try
            {
                object ret;
                if (mi.DeclaringType == typeof(object))
                    ret = ObjectMethod(mi, args);
                else
                {
                    ret = Handler != null ? Handler(this, mi, args) : NotHandled;
                    if (ret == NotHandled) ret = Default(mi, args);
                }
                return new ReturnMessage(Conv.To(ret, mi.ReturnType), args, args.Length, call.LogicalCallContext, call);
            }
            catch (TargetInvocationException ex) { return new ReturnMessage(ex.InnerException, call); }
            catch (Exception ex) { return new ReturnMessage(ex, call); }
        }

        object ObjectMethod(MethodInfo mi, object[] args)
        {
            switch (mi.Name)
            {
                case "Equals": return ReferenceEquals(args[0], Proxy);
                case "GetHashCode": return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
                case "ToString":
                    object n;
                    return Props.TryGetValue("CustomName", out n) && n != null ? n.ToString() : (Tag as BlockModel != null ? ((BlockModel)Tag).Name : Primary.Name);
            }
            return null;
        }

        object Default(MethodInfo mi, object[] args)
        {
            var n = mi.Name;
            if (n.StartsWith("get_") && args.Length == 0)
            {
                var k = n.Substring(4);
                object v;
                if (Props.TryGetValue(k, out v)) return v;
                return DefaultFor(mi.ReturnType, k);
            }
            if (n.StartsWith("set_") && args.Length == 1)
            {
                Props[n.Substring(4)] = Conv.Plain(args[0]);
                return null;
            }
            if (n.StartsWith("add_") || n.StartsWith("remove_")) return null;
            return DefaultFor(mi.ReturnType, "#" + n);
        }

        public object DefaultFor(Type t, string key)
        {
            if (t == typeof(void)) return null;
            if (t == typeof(string)) return "";
            if (t.IsInterface && !t.IsGenericType || t.IsInterface && t.Namespace != null && !t.Namespace.StartsWith("System"))
            {
                object c;
                if (!_children.TryGetValue(key, out c))
                    _children[key] = c = new Mock(t, null).Proxy;
                return c;
            }
            if (t.IsValueType) return Activator.CreateInstance(t);
            if (t == typeof(StringBuilder)) return new StringBuilder();
            return null;
        }
    }

    // Преобразования между «простыми» значениями (для JSON и GUI) и типами игры.
    public static class Conv
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static object To(object v, Type t)
        {
            if (t == typeof(void)) return null;
            if (v == null) return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
            if (t.IsInstanceOfType(v)) return v;
            var nt = Nullable.GetUnderlyingType(t);
            if (nt != null) return To(v, nt);
            try
            {
                if (t.IsEnum)
                {
                    if (v is string) return Enum.Parse(t, (string)v, true);
                    return Enum.ToObject(t, Convert.ToInt64(v, Inv));
                }
                if (t == typeof(MyFixedPoint)) return (MyFixedPoint)Convert.ToDouble(v, Inv);
                if (t == typeof(Color)) return ParseColor(v.ToString());
                if (t == typeof(TimeSpan)) return TimeSpan.FromSeconds(Convert.ToDouble(v, Inv));
                if (t == typeof(string)) return v.ToString();
                if (v is MyFixedPoint) return Convert.ChangeType((double)(MyFixedPoint)v, t, Inv);
                if (v is IConvertible && typeof(IConvertible).IsAssignableFrom(t)) return Convert.ChangeType(v, t, Inv);
            }
            catch { }
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }

        public static object Plain(object v)
        {
            if (v == null || v is string || v is bool) return v;
            if (v is Enum) return v.ToString();
            if (v is MyFixedPoint) return (double)(MyFixedPoint)v;
            if (v is Color) return ColorHex((Color)v);
            if (v is TimeSpan) return ((TimeSpan)v).TotalSeconds;
            if (v is IConvertible) { try { return Convert.ToDouble(v, Inv); } catch { } }
            return v;
        }

        public static Color ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return Color.White;
            s = s.Trim();
            if (s.StartsWith("#") && (s.Length == 7 || s.Length == 9))
            {
                int r = int.Parse(s.Substring(1, 2), NumberStyles.HexNumber), g = int.Parse(s.Substring(3, 2), NumberStyles.HexNumber), b = int.Parse(s.Substring(5, 2), NumberStyles.HexNumber);
                int a = s.Length == 9 ? int.Parse(s.Substring(7, 2), NumberStyles.HexNumber) : 255;
                return new Color(r, g, b, a);
            }
            var parts = s.Split(',');
            if (parts.Length >= 3) return new Color(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            return Color.White;
        }

        public static string ColorHex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
    }

    // ---------- логика конкретных интерфейсов ----------

    public class BlockLogic
    {
        public readonly BlockModel M;
        readonly SimEngine E;
        public Mock Mock;
        readonly List<Mock> _surfaces = new List<Mock>();
        readonly List<Mock> _invs = new List<Mock>();

        public BlockLogic(BlockModel m, SimEngine e)
        {
            M = m; E = e;
            var iface = e.Game.FindIngameInterface(m.Iface) ?? typeof(IMyTerminalBlock);
            var extra = new List<Type>();
            if (m.Surfaces.Count > 0) extra.Add(typeof(IMyTextSurfaceProvider));
            Mock = new Mock(iface, m.Props, extra.ToArray()) { Tag = m, Handler = Handle };
            foreach (var s in m.Surfaces)
            {
                var sm = new Mock(typeof(IMyTextSurface), new Dictionary<string, object>()) { Tag = s };
                var surf = s;
                sm.Handler = (mk, mi, a) => SurfaceLogic.Handle(surf, mi, a);
                _surfaces.Add(sm);
            }
            foreach (var inv in m.Inventories)
            {
                var im = new Mock(typeof(IMyInventory), new Dictionary<string, object>()) { Tag = inv };
                var model = inv;
                im.Handler = (mk, mi, a) => InventoryLogic.Handle(model, this, mi, a, E);
                _invs.Add(im);
            }
        }

        public object Proxy { get { return Mock.Proxy; } }
        public object Inventory(int i) { return i >= 0 && i < _invs.Count ? _invs[i].Proxy : null; }

        object Handle(Mock self, MethodInfo mi, object[] a)
        {
            var n = mi.Name;
            switch (n)
            {
                case "get_CustomName": case "get_DisplayNameText": case "get_DisplayName": case "get_Name": return M.Name;
                case "set_CustomName": M.Name = (string)a[0] ?? ""; E.MarkDirty(); return null;
                case "get_CustomData": return M.CustomData;
                case "set_CustomData": M.CustomData = (string)a[0] ?? ""; return null;
                case "get_Enabled": return M.Enabled;
                case "set_Enabled": M.Enabled = (bool)a[0]; return null;
                case "get_IsWorking": return M.Enabled;
                case "get_IsFunctional": return true;
                case "get_IsBeingHacked": return false;
                case "get_EntityId": return M.Id;
                case "get_CubeGrid": return E.GridMock(M.GridIndex);
                case "get_DefinitionDisplayNameText":
                    var def = E.Game.Defs.Block(M.TypeId, M.Subtype);
                    return def != null ? def.DisplayName : M.TypeId;
                case "get_DetailedInfo": return Behaviors.DetailedInfo(M, E.Game.Defs);
                case "get_BlockDefinition":
                    VRage.Game.MyDefinitionId id;
                    return VRage.Game.MyDefinitionId.TryParse("MyObjectBuilder_" + M.TypeId, M.Subtype, out id) ? (object)id : NotHandled();
                case "get_Position": return M.Pos != null ? new Vector3I(M.Pos[0], M.Pos[1], M.Pos[2]) : Vector3I.Zero;
                case "get_HasInventory": return M.Inventories.Count > 0;
                case "get_InventoryCount": return M.Inventories.Count;
                case "GetInventory": return Inventory(a.Length > 0 ? (int)a[0] : 0);
                case "IsSameConstructAs": return true;
                case "GetOwnerFactionTag": return "";
                case "GetUserRelationToOwner": return "Owner";
                case "HasLocalPlayerAccess": case "HasPlayerAccess": case "HasAction": return true;
                case "GetProperty": return E.Property((string)a[0]);
                case "GetActionWithName": return E.Action((string)a[0]);
                case "GetActions": case "SearchActionsOfName":
                {
                    var list = (IList)(n == "GetActions" ? a[0] : a[1]);
                    if (list != null)
                        foreach (var act in E.ActionsFor(M))
                            if (n == "GetActions" || act.IndexOf((string)a[0] ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                                list.Add(E.Action(act));
                    return null;
                }
                case "GetProperties": return null;
                case "get_SurfaceCount": return _surfaces.Count;
                case "GetSurface":
                {
                    int i = (int)a[0];
                    return i >= 0 && i < _surfaces.Count ? _surfaces[i].Proxy : null;
                }
            }
            // LCD-панель сама является поверхностью
            if (M.TypeId == "TextPanel" && M.Surfaces.Count > 0 && !n.StartsWith("get_Is"))
            {
                var r = SurfaceLogic.Handle(M.Surfaces[0], mi, a);
                if (r != Mock.NotHandled) return r;
            }
            return TypeSpecific(n, a);
        }

        static object NotHandled() { return Mock.NotHandled; }

        double D(string k, double def = 0)
        {
            object o;
            return M.Props.TryGetValue(k, out o) && o != null ? Convert.ToDouble(o, CultureInfo.InvariantCulture) : def;
        }

        object TypeSpecific(string n, object[] a)
        {
            var p = M.Props;
            switch (n)
            {
                // двери
                case "OpenDoor": if ((string)p.Get("Status") != "Open") p["Status"] = "Opening"; return null;
                case "CloseDoor": if ((string)p.Get("Status") != "Closed") p["Status"] = "Closing"; return null;
                case "ToggleDoor":
                {
                    var st = (string)p.Get("Status") ?? "Closed";
                    p["Status"] = st == "Open" || st == "Opening" ? "Closing" : "Opening";
                    return null;
                }
                // поршни
                case "Extend": p["Velocity"] = Math.Abs(D("Velocity", 0.5)); return null;
                case "Retract": p["Velocity"] = -Math.Abs(D("Velocity", 0.5)); return null;
                case "Reverse": p["Velocity"] = -D("Velocity"); return null;
                // роторы
                case "get_UpperLimitRad": return D("UpperLimitDeg", 361) * Math.PI / 180;
                case "set_UpperLimitRad": p["UpperLimitDeg"] = Convert.ToDouble(a[0]) * 180 / Math.PI; return null;
                case "get_LowerLimitRad": return D("LowerLimitDeg", -361) * Math.PI / 180;
                case "set_LowerLimitRad": p["LowerLimitDeg"] = Convert.ToDouble(a[0]) * 180 / Math.PI; return null;
                case "Attach": p["IsAttached"] = true; return null;
                case "Detach": p["IsAttached"] = false; return null;
                // двигатели
                case "set_ThrustOverridePercentage": p["ThrustOverride"] = Convert.ToDouble(a[0]) * D("MaxThrust", 1); return null;
                case "get_ThrustOverridePercentage": return D("MaxThrust", 1) > 0 ? D("ThrustOverride") / D("MaxThrust", 1) : 0;
                // коннекторы
                case "Connect":
                    if ((string)p.Get("Status") == "Connectable") { p["Status"] = "Connected"; p["IsConnected"] = true; }
                    return null;
                case "Disconnect":
                    if ((string)p.Get("Status") == "Connected") { p["Status"] = "Connectable"; p["IsConnected"] = false; }
                    return null;
                case "ToggleConnect":
                    if ((string)p.Get("Status") == "Connected") { p["Status"] = "Connectable"; p["IsConnected"] = false; }
                    else if ((string)p.Get("Status") == "Connectable") { p["Status"] = "Connected"; p["IsConnected"] = true; }
                    return null;
                // шасси
                case "Lock": p["IsLocked"] = true; p["LockMode"] = "Locked"; return null;
                case "Unlock": p["IsLocked"] = false; p["LockMode"] = "Unlocked"; return null;
                case "ToggleLock": p["IsLocked"] = !(p.Get("IsLocked") is bool && (bool)p.Get("IsLocked")); p["LockMode"] = (bool)p["IsLocked"] ? "Locked" : "Unlocked"; return null;
                // таймеры
                case "Trigger": E.Log("Таймер «" + M.Name + "» сработал (Trigger)"); return null;
                case "StartCountdown": p["IsCountingDown"] = true; p["_Remaining"] = D("TriggerDelay", 1); return null;
                case "StopCountdown": p["IsCountingDown"] = false; return null;
                // программируемый блок
                case "TryRun": return E.QueueRun(M, (string)a[0]);
                case "get_IsRunning": return M.Id == E.Grid.PbId && E.InsideRun;
                case "get_TerminalRunArgument": return E.LastArgument;
                // вентиляция
                case "GetOxygenLevel": return (float)D("_Oxygen", 1);
                case "CanPressurize": return true;
                // батареи
                case "get_IsCharging": return D("CurrentInput") > 0;
            }
            return Mock.NotHandled;
        }
    }

    static class DictExt
    {
        public static object Get(this Dictionary<string, object> d, string k)
        {
            object o; return d.TryGetValue(k, out o) ? o : null;
        }
    }

    public static class SurfaceLogic
    {
        // Примерные размеры глифа шрифта Debug при FontSize = 1
        const float CharW = 19.5f, LineH = 37f;

        public static object Handle(SurfaceModel s, MethodInfo mi, object[] a)
        {
            switch (mi.Name)
            {
                case "WriteText":
                {
                    var text = a[0] == null ? "" : a[0].ToString();
                    bool append = a.Length > 1 && a[1] is bool && (bool)a[1];
                    s.Text = append ? s.Text + text : text;
                    if (s.ContentType == "NONE") s.ContentType = "TEXT_AND_IMAGE";
                    return true;
                }
                case "GetText": return s.Text;
                case "ReadText":
                {
                    var sb = (StringBuilder)a[0];
                    bool append = a.Length > 1 && a[1] is bool && (bool)a[1];
                    if (!append) sb.Clear();
                    sb.Append(s.Text);
                    return null;
                }
                case "get_FontSize": return (float)s.FontSize;
                case "set_FontSize": s.FontSize = Math.Max(0.1, Math.Min(10, Convert.ToDouble(a[0]))); return null;
                case "get_FontColor": return Conv.ParseColor(s.FontColor);
                case "set_FontColor": s.FontColor = Conv.ColorHex((Color)a[0]); return null;
                case "get_BackgroundColor": return Conv.ParseColor(s.BackgroundColor);
                case "set_BackgroundColor": s.BackgroundColor = Conv.ColorHex((Color)a[0]); return null;
                case "get_Alignment": return s.Alignment;
                case "set_Alignment": s.Alignment = a[0].ToString(); return null;
                case "get_ContentType": return s.ContentType;
                case "set_ContentType": s.ContentType = a[0].ToString(); return null;
                case "get_Font": return s.Font;
                case "set_Font": s.Font = (string)a[0] ?? "Debug"; return null;
                case "get_TextPadding": return (float)s.TextPadding;
                case "set_TextPadding": s.TextPadding = Convert.ToDouble(a[0]); return null;
                case "get_SurfaceSize": case "get_TextureSize": return new Vector2((float)s.Width, (float)s.Height);
                case "get_Name": case "get_DisplayName": return s.Name;
                case "MeasureStringInPixels":
                {
                    var text = a[0] == null ? "" : a[0].ToString();
                    float scale = Convert.ToSingle(a[2]);
                    var lines = text.Split('\n');
                    return new Vector2(lines.Max(l => l.Length) * CharW * scale, lines.Length * LineH * scale);
                }
                case "GetFonts": ((List<string>)a[0]).AddRange(new[] { "Debug", "Monospace", "White", "Red", "Green", "Blue", "DarkBlue", "InfoMessageBoxText" }); return null;
                case "GetSprites": ((List<string>)a[0]).AddRange(SpriteNames != null ? SpriteNames() : new List<string>()); return null;
                case "GetScripts": return null;
                case "get_Script": return s.Script;
                case "set_Script": s.Script = (string)a[0] ?? ""; return null;
                case "get_ScriptBackgroundColor": return Conv.ParseColor(s.ScriptBackgroundColor);
                case "set_ScriptBackgroundColor": s.ScriptBackgroundColor = Conv.ColorHex((Color)a[0]); return null;
                case "get_ScriptForegroundColor": return Conv.ParseColor(s.ScriptForegroundColor);
                case "set_ScriptForegroundColor": s.ScriptForegroundColor = Conv.ColorHex((Color)a[0]); return null;
                case "DrawFrame":
                    // Кадр копит спрайты, а на Dispose (конец using) отдаёт их сюда — как игра на отрисовку.
                    return new MySpriteDrawFrame(frame =>
                    {
                        var list = new List<MySprite>();
                        frame.AddToList(list);
                        s.Sprites = list.Select(SpriteJson).ToList();
                        s.Frames++;
                    });
            }
            return Mock.NotHandled;
        }

        public static Func<List<string>> SpriteNames;

        static string Rgba(Color c)
        {
            return "rgba(" + c.R + "," + c.G + "," + c.B + "," + (c.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }

        static Dictionary<string, object> SpriteJson(MySprite sp)
        {
            var d = new Dictionary<string, object>
            {
                { "type", sp.Type.ToString() }, { "data", sp.Data }, { "font", sp.FontId },
                { "align", sp.Alignment.ToString() }, { "rot", (double)sp.RotationOrScale },
            };
            if (sp.Position.HasValue) d["pos"] = new[] { (double)sp.Position.Value.X, (double)sp.Position.Value.Y };
            if (sp.Size.HasValue) d["size"] = new[] { (double)sp.Size.Value.X, (double)sp.Size.Value.Y };
            if (sp.Color.HasValue) d["color"] = Rgba(sp.Color.Value);
            return d;
        }
    }

    public static class InventoryLogic
    {
        static MyItemType TypeOf(ItemModel it) { return new MyItemType(it.TypeId, it.Subtype); }
        static MyInventoryItem ItemOf(ItemModel it) { return new MyInventoryItem(TypeOf(it), it.ItemId, (MyFixedPoint)it.Amount); }
        static bool Same(ItemModel it, MyItemType t) { return it.TypeId == t.TypeId && it.Subtype == t.SubtypeId; }

        public static object Handle(InventoryModel inv, BlockLogic owner, MethodInfo mi, object[] a, SimEngine e)
        {
            var defs = e.Game.Defs;
            double cur = Behaviors.InventoryVolume(inv, defs);
            switch (mi.Name)
            {
                case "get_CurrentVolume": return (MyFixedPoint)cur;
                case "get_MaxVolume": return (MyFixedPoint)inv.MaxVolume;
                case "get_CurrentMass": return (MyFixedPoint)inv.Items.Sum(i => i.Amount * Behaviors.ItemMass(i, defs));
                case "get_ItemCount": return inv.Items.Count;
                case "get_IsFull": return cur >= inv.MaxVolume - 1e-6;
                case "get_VolumeFillFactor": return inv.MaxVolume > 0 ? (float)(cur / inv.MaxVolume) : 0f;
                case "get_Owner": return owner.Proxy;
                case "get_CanPutItems": return true;
                case "IsItemAt": return (int)a[0] >= 0 && (int)a[0] < inv.Items.Count;
                case "GetItemAt":
                {
                    int i = (int)a[0];
                    return i >= 0 && i < inv.Items.Count ? (MyInventoryItem?)ItemOf(inv.Items[i]) : null;
                }
                case "GetItemByID":
                {
                    var it = inv.Items.FirstOrDefault(x => x.ItemId == (uint)a[0]);
                    return it != null ? (MyInventoryItem?)ItemOf(it) : null;
                }
                case "FindItem":
                {
                    var t = (MyItemType)a[0];
                    var it = inv.Items.FirstOrDefault(x => Same(x, t));
                    return it != null ? (MyInventoryItem?)ItemOf(it) : null;
                }
                case "GetItemAmount":
                {
                    var t = (MyItemType)a[0];
                    return (MyFixedPoint)inv.Items.Where(x => Same(x, t)).Sum(x => x.Amount);
                }
                case "ContainItems":
                {
                    var t = (MyItemType)a[1];
                    return inv.Items.Where(x => Same(x, t)).Sum(x => x.Amount) >= (double)(MyFixedPoint)a[0];
                }
                case "CanItemsBeAdded":
                {
                    var t = (MyItemType)a[1];
                    var d = defs.Item(t.TypeId, t.SubtypeId);
                    return cur + (double)(MyFixedPoint)a[0] * (d != null ? d.Volume : 1) / 1000 <= inv.MaxVolume;
                }
                case "GetItems":
                {
                    var list = (List<MyInventoryItem>)a[0];
                    var filter = a.Length > 1 ? (Func<MyInventoryItem, bool>)a[1] : null;
                    foreach (var it in inv.Items)
                    {
                        var x = ItemOf(it);
                        if (filter == null || filter(x)) list.Add(x);
                    }
                    return null;
                }
                case "GetAcceptedItems": return null;
                case "IsConnectedTo": case "CanTransferItemTo": return true;
                case "TransferItemTo":
                case "TransferItemFrom":
                {
                    var other = Mock.Of(a[0]);
                    var otherInv = other != null ? other.Tag as InventoryModel : null;
                    if (otherInv == null) return false;
                    var src = mi.Name == "TransferItemTo" ? inv : otherInv;
                    var dst = mi.Name == "TransferItemTo" ? otherInv : inv;
                    ItemModel item = null;
                    double? amount = null;
                    if (a[1] is MyInventoryItem)
                    {
                        var ii = (MyInventoryItem)a[1];
                        item = src.Items.FirstOrDefault(x => x.ItemId == ii.ItemId);
                        if (a.Length > 2 && a[2] != null) amount = (double)(MyFixedPoint)a[2];
                    }
                    else
                    {
                        int idx = (int)a[1];
                        if (idx >= 0 && idx < src.Items.Count) item = src.Items[idx];
                        if (a.Length > 4 && a[4] != null) amount = (double)(MyFixedPoint)a[4];
                    }
                    if (item == null) return false;
                    double want = Math.Min(item.Amount, amount ?? item.Amount);
                    double unit = Behaviors.ItemVolume(item, defs);
                    double free = dst.MaxVolume - Behaviors.InventoryVolume(dst, defs);
                    if (unit > 0) want = Math.Min(want, Math.Floor(free / unit * 1e6) / 1e6);
                    if (want <= 0) return false;
                    item.Amount -= want;
                    if (item.Amount <= 1e-9) src.Items.Remove(item);
                    var target = dst.Items.FirstOrDefault(x => x.TypeId == item.TypeId && x.Subtype == item.Subtype);
                    if (target != null) target.Amount += want;
                    else dst.Items.Add(new ItemModel { TypeId = item.TypeId, Subtype = item.Subtype, Amount = want, ItemId = dst.NextId++ });
                    return true;
                }
            }
            return Mock.NotHandled;
        }
    }
}
