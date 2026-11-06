using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SeHelper
{
    // Создание блоков с правдоподобными значениями по умолчанию, поведение по тикам и процентные шкалы.
    public class BlockFactory
    {
        readonly GameApi _game;
        long _nextId = 1000;

        public BlockFactory(GameApi game) { _game = game; }

        static readonly Dictionary<string, string> IfaceByType = new Dictionary<string, string>
        {
            { "BatteryBlock", "IMyBatteryBlock" }, { "TextPanel", "IMyTextPanel" }, { "LCDPanelsBlock", "IMyFunctionalBlock" },
            { "MyProgrammableBlock", "IMyProgrammableBlock" }, { "CargoContainer", "IMyCargoContainer" },
            { "Door", "IMyDoor" }, { "AirtightHangarDoor", "IMyAirtightHangarDoor" }, { "AirtightSlideDoor", "IMyAirtightSlideDoor" },
            { "AdvancedDoor", "IMyAdvancedDoor" }, { "PistonBase", "IMyPistonBase" }, { "ExtendedPistonBase", "IMyExtendedPistonBase" },
            { "MotorStator", "IMyMotorStator" }, { "MotorAdvancedStator", "IMyMotorAdvancedStator" }, { "MotorSuspension", "IMyMotorSuspension" },
            { "Thrust", "IMyThrust" }, { "Gyro", "IMyGyro" }, { "Reactor", "IMyReactor" }, { "SolarPanel", "IMySolarPanel" },
            { "WindTurbine", "IMyPowerProducer" }, { "HydrogenEngine", "IMyPowerProducer" }, { "OxygenTank", "IMyGasTank" },
            { "OxygenGenerator", "IMyGasGenerator" }, { "Refinery", "IMyRefinery" }, { "Assembler", "IMyAssembler" },
            { "SurvivalKit", "IMyAssembler" }, { "ShipConnector", "IMyShipConnector" }, { "Collector", "IMyCollector" },
            { "Cockpit", "IMyCockpit" }, { "RemoteControl", "IMyRemoteControl" }, { "CryoChamber", "IMyCryoChamber" },
            { "TimerBlock", "IMyTimerBlock" }, { "SensorBlock", "IMySensorBlock" }, { "InteriorLight", "IMyInteriorLight" },
            { "ReflectorLight", "IMyReflectorLight" }, { "LandingGear", "IMyLandingGear" }, { "MergeBlock", "IMyShipMergeBlock" },
            { "ShipWelder", "IMyShipWelder" }, { "ShipGrinder", "IMyShipGrinder" }, { "Drill", "IMyShipDrill" },
            { "Projector", "IMyProjector" }, { "CameraBlock", "IMyCameraBlock" }, { "RadioAntenna", "IMyRadioAntenna" },
            { "LaserAntenna", "IMyLaserAntenna" }, { "Beacon", "IMyBeacon" }, { "OreDetector", "IMyOreDetector" }, { "AirVent", "IMyAirVent" },
            { "MedicalRoom", "IMyMedicalRoom" }, { "SmallGatlingGun", "IMySmallGatlingGun" }, { "SmallMissileLauncher", "IMySmallMissileLauncher" },
            { "SmallMissileLauncherReload", "IMySmallMissileLauncherReload" }, { "LargeGatlingTurret", "IMyLargeGatlingTurret" },
            { "LargeMissileTurret", "IMyLargeMissileTurret" }, { "InteriorTurret", "IMyLargeInteriorTurret" }, { "Warhead", "IMyWarhead" },
            { "Decoy", "IMyDecoy" }, { "GravityGenerator", "IMyGravityGenerator" }, { "GravityGeneratorSphere", "IMyGravityGeneratorSphere" },
            { "VirtualMass", "IMyVirtualMass" }, { "ButtonPanel", "IMyButtonPanel" }, { "SoundBlock", "IMySoundBlock" },
            { "ConveyorSorter", "IMyConveyorSorter" }, { "JumpDrive", "IMyJumpDrive" }, { "Parachute", "IMyParachute" },
            { "OxygenFarm", "IMyOxygenFarm" }, { "UpgradeModule", "IMyUpgradeModule" }, { "Searchlight", "IMySearchlight" },
            { "HeatVent", "IMyHeatVent" }, { "EmotionControllerBlock", "IMyEmotionControllerBlock" }, { "EventControllerBlock", "IMyEventControllerBlock" },
            { "TurretControlBlock", "IMyTurretControlBlock" }, { "OffensiveCombatBlock", "IMyOffensiveCombatBlock" },
        };

        // Каталог для кнопки «+ блок» в GUI: тип, сабтайп по умолчанию, подпись.
        public static readonly string[][] Catalog =
        {
            new[] { "BatteryBlock", "LargeBlockBatteryBlock", "Батарея" },
            new[] { "TextPanel", "LargeLCDPanel", "LCD-панель" },
            new[] { "TextPanel", "LargeLCDPanelWide", "Широкая LCD-панель" },
            new[] { "CargoContainer", "LargeBlockLargeContainer", "Большой контейнер" },
            new[] { "CargoContainer", "LargeBlockSmallContainer", "Малый контейнер" },
            new[] { "OxygenTank", "LargeHydrogenTank", "Водородный бак" },
            new[] { "OxygenTank", "", "Кислородный бак" },
            new[] { "Door", "", "Дверь" },
            new[] { "AirtightHangarDoor", "", "Ангарная дверь" },
            new[] { "ExtendedPistonBase", "LargePistonBase", "Поршень" },
            new[] { "MotorStator", "LargeStator", "Ротор" },
            new[] { "MotorAdvancedStator", "LargeAdvancedStator", "Улучшенный ротор" },
            new[] { "Thrust", "LargeBlockLargeThrust", "Ионный двигатель" },
            new[] { "Thrust", "LargeBlockLargeHydrogenThrust", "Водородный двигатель" },
            new[] { "Gyro", "LargeBlockGyro", "Гироскоп" },
            new[] { "ShipConnector", "Connector", "Коннектор" },
            new[] { "MergeBlock", "LargeShipMergeBlock", "Соединитель" },
            new[] { "LandingGear", "LargeBlockLandingGear", "Шасси" },
            new[] { "InteriorLight", "SmallLight", "Лампа" },
            new[] { "ReflectorLight", "LargeBlockFrontLight", "Прожектор" },
            new[] { "TimerBlock", "TimerBlockLarge", "Таймер" },
            new[] { "SensorBlock", "LargeBlockSensor", "Сенсор" },
            new[] { "MyProgrammableBlock", "LargeProgrammableBlock", "Программируемый блок" },
            new[] { "Cockpit", "LargeBlockCockpitSeat", "Кокпит" },
            new[] { "RemoteControl", "LargeBlockRemoteControl", "ДУ" },
            new[] { "Reactor", "LargeBlockLargeGenerator", "Большой реактор" },
            new[] { "SolarPanel", "LargeBlockSolarPanel", "Солнечная панель" },
            new[] { "HydrogenEngine", "LargeHydrogenEngine", "Водородный генератор" },
            new[] { "Refinery", "LargeRefinery", "Очиститель" },
            new[] { "Assembler", "LargeAssembler", "Сборщик" },
            new[] { "OxygenGenerator", "", "Генератор O2/H2" },
            new[] { "Drill", "LargeBlockDrill", "Бур" },
            new[] { "ShipWelder", "LargeShipWelder", "Сварщик" },
            new[] { "ShipGrinder", "LargeShipGrinder", "Резак" },
            new[] { "AirVent", "", "Вентиляция" },
            new[] { "RadioAntenna", "LargeBlockRadioAntenna", "Антенна" },
            new[] { "SoundBlock", "LargeBlockSoundBlock", "Звуковой блок" },
            new[] { "ConveyorSorter", "LargeBlockConveyorSorter", "Сортировщик" },
        };

        public string ResolveIface(string typeId, bool hasEnabled)
        {
            string name;
            if (IfaceByType.TryGetValue(typeId, out name) && _game.FindIngameInterface(name) != null) return name;
            foreach (var cand in new[] { "IMy" + typeId, "IMy" + typeId.Replace("Block", ""), "IMy" + typeId + "Block" })
            {
                var t = _game.FindIngameInterface(cand);
                var term = _game.FindIngameInterface("IMyTerminalBlock");
                if (t != null && term != null && term.IsAssignableFrom(t)) return cand;
            }
            return null;
        }

        public BlockModel Create(string typeId, string subtype, string customName, bool hasEnabled)
        {
            var iface = ResolveIface(typeId, hasEnabled);
            if (iface == null)
            {
                // не терминальные блоки (броня, конвейеры) в симуляцию не берём
                if (customName == null) return null;
                iface = hasEnabled ? "IMyFunctionalBlock" : "IMyTerminalBlock";
            }
            var def = _game.Defs.Block(typeId, subtype);
            var b = new BlockModel
            {
                Id = _nextId++,
                TypeId = typeId, Subtype = subtype ?? "", Iface = iface,
                Name = customName ?? (def != null ? def.DisplayName : typeId),
            };
            bool large = def == null || def.CubeSize != "Small";
            var p = b.Props;
            switch (typeId)
            {
                case "BatteryBlock":
                    double max = def != null && def.MaxStoredPower > 0 ? def.MaxStoredPower : 3;
                    p["MaxStoredPower"] = max;
                    p["CurrentStoredPower"] = max * 0.3;
                    p["MaxOutput"] = def != null && def.MaxPowerOutput > 0 ? def.MaxPowerOutput : max * 4;
                    p["MaxInput"] = max * 4;
                    p["CurrentInput"] = 0.0; p["CurrentOutput"] = 0.0;
                    p["ChargeMode"] = "Auto";
                    p["_Load"] = 0.0; p["_Supply"] = 0.0;
                    break;
                case "OxygenTank":
                    p["Capacity"] = def != null && def.GasCapacity > 0 ? def.GasCapacity : (large ? 100000.0 : 1000.0);
                    p["FilledRatio"] = 0.5; p["Stockpile"] = false; p["AutoRefillBottles"] = false; p["_Flow"] = 0.0;
                    break;
                case "PistonBase":
                case "ExtendedPistonBase":
                    double hi = def != null && def.PistonMax > 0 ? def.PistonMax : (large ? 10 : 2);
                    p["HighestPosition"] = hi; p["LowestPosition"] = 0.0; p["MinLimit"] = 0.0; p["MaxLimit"] = hi;
                    p["CurrentPosition"] = 0.0; p["Velocity"] = 0.5; p["Status"] = "Stopped";
                    break;
                case "MotorStator":
                case "MotorAdvancedStator":
                    p["Angle"] = 0.0; p["TargetVelocityRPM"] = 0.0; p["UpperLimitDeg"] = 361.0; p["LowerLimitDeg"] = -361.0;
                    p["RotorLock"] = false; p["Torque"] = 1e6; p["BrakingTorque"] = 0.0; p["IsAttached"] = true;
                    break;
                case "Door": case "AirtightHangarDoor": case "AirtightSlideDoor": case "AdvancedDoor":
                    p["OpenRatio"] = 0.0; p["Status"] = "Closed";
                    break;
                case "Thrust":
                    double f = def != null && def.ForceMagnitude > 0 ? def.ForceMagnitude : (large ? 4.32e6 : 1.44e5);
                    p["MaxThrust"] = f; p["MaxEffectiveThrust"] = f; p["ThrustOverride"] = 0.0; p["CurrentThrust"] = 0.0;
                    break;
                case "ShipConnector":
                    p["Status"] = "Unconnected"; p["IsConnected"] = false; p["ThrowOut"] = false; p["CollectAll"] = false;
                    break;
                case "InteriorLight": case "ReflectorLight":
                    p["Color"] = "#FFFFFF"; p["Radius"] = 4.0; p["Intensity"] = 1.0; p["BlinkIntervalSeconds"] = 0.0;
                    break;
                case "TimerBlock":
                    p["TriggerDelay"] = 1.0; p["IsCountingDown"] = false; p["_Remaining"] = 0.0;
                    break;
                case "SensorBlock":
                    p["IsActive"] = false;
                    break;
                case "LandingGear":
                    p["IsLocked"] = false; p["LockMode"] = "Unlocked"; p["AutoLock"] = false;
                    break;
                case "MergeBlock":
                    p["IsConnected"] = false;
                    break;
                case "Reactor": case "SolarPanel": case "WindTurbine": case "HydrogenEngine":
                    p["MaxOutput"] = def != null && def.MaxPowerOutput > 0 ? def.MaxPowerOutput : 1.0;
                    p["CurrentOutput"] = 0.0;
                    if (typeId == "HydrogenEngine") p["FilledRatio"] = 1.0;
                    break;
                case "Cockpit": case "RemoteControl":
                    p["IsUnderControl"] = false; p["IsMainCockpit"] = false; p["DampenersOverride"] = true;
                    break;
                case "Refinery": case "Assembler":
                    p["IsProducing"] = false; p["IsQueueEmpty"] = true; p["UseConveyorSystem"] = true;
                    break;
                case "AirVent":
                    p["Depressurize"] = false; p["_Oxygen"] = 1.0; p["Status"] = "Pressurized";
                    break;
                case "Gyro":
                    p["GyroOverride"] = false; p["GyroPower"] = 1.0; p["Yaw"] = 0.0; p["Pitch"] = 0.0; p["Roll"] = 0.0;
                    break;
            }

            // инвентари
            int invCount = 0;
            switch (typeId)
            {
                case "Refinery": case "Assembler": case "SurvivalKit": invCount = 2; break;
                case "CargoContainer": case "Reactor": case "ShipConnector": case "Cockpit": case "Drill": case "ShipWelder":
                case "ShipGrinder": case "OxygenGenerator": case "OxygenTank": case "Collector": case "ConveyorSorter":
                case "HydrogenEngine": case "CryoChamber": case "SmallGatlingGun": case "LargeGatlingTurret": case "SmallMissileLauncher":
                case "LargeMissileTurret": case "InteriorTurret": case "SmallMissileLauncherReload": case "Parachute":
                    invCount = 1; break;
            }
            double vol = def != null ? def.InventoryVolume : 0;
            if (vol <= 0)
            {
                double v;
                if (_game.Defs.InventoryComponents.TryGetValue(subtype ?? "", out v)) vol = v;
            }
            if (vol <= 0) vol = typeId == "CargoContainer" ? (large ? 421.875 : 3.375) : (large ? 8 : 1);
            for (int i = 0; i < invCount; i++) b.Inventories.Add(new InventoryModel { MaxVolume = vol });

            // экраны
            int screens = def != null ? def.Screens : 0;
            if (typeId == "TextPanel") screens = 1;
            if (typeId == "LCDPanelsBlock") screens = Math.Max(1, screens);
            if (typeId == "MyProgrammableBlock" && screens == 0) screens = large ? 2 : 1;
            for (int i = 0; i < screens; i++)
            {
                var s = new SurfaceModel { Name = screens == 1 ? "Экран" : "Экран " + i };
                var sub = (subtype ?? "").ToLowerInvariant();
                if (sub.Contains("wide")) s.Width = 1024;
                if (typeId == "MyProgrammableBlock" && i == 1) { s.Name = "Клавиатура"; s.Width = 512; s.Height = 256; }
                b.Surfaces.Add(s);
            }
            return b;
        }
    }

    public static class Behaviors
    {
        static double D(BlockModel b, string k, double def = 0)
        {
            object o;
            if (!b.Props.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return def; }
        }
        static string S(BlockModel b, string k, string def = "")
        {
            object o; return b.Props.TryGetValue(k, out o) && o != null ? o.ToString() : def;
        }
        static bool B(BlockModel b, string k)
        {
            object o; return b.Props.TryGetValue(k, out o) && o is bool && (bool)o;
        }

        // Один тик симуляции (dt в секундах игрового времени).
        public static void Tick(BlockModel b, double dt)
        {
            var p = b.Props;
            switch (b.TypeId)
            {
                case "BatteryBlock":
                {
                    double max = D(b, "MaxStoredPower", 3), cur = D(b, "CurrentStoredPower"), load = D(b, "_Load"), supply = D(b, "_Supply");
                    string mode = S(b, "ChargeMode", "Auto");
                    double input = 0, output = 0;
                    if (b.Enabled)
                    {
                        if (mode == "Recharge") input = Math.Min(supply, D(b, "MaxInput", 12));
                        else if (mode == "Discharge") output = Math.Min(load, D(b, "MaxOutput", 12));
                        else { input = Math.Min(Math.Max(0, supply - load), D(b, "MaxInput", 12)); output = Math.Min(Math.Max(0, load - supply), D(b, "MaxOutput", 12)); }
                    }
                    if (cur >= max) input = 0;
                    if (cur <= 0) output = 0;
                    cur += (input * 0.8 - output) * dt / 3600.0;
                    p["CurrentStoredPower"] = Math.Max(0, Math.Min(max, cur));
                    p["CurrentInput"] = input; p["CurrentOutput"] = output;
                    p["IsCharging"] = input > 0;
                    p["HasCapacityRemaining"] = cur > 0;
                    break;
                }
                case "OxygenTank":
                {
                    double cap = D(b, "Capacity", 100000), fr = D(b, "FilledRatio") + D(b, "_Flow") * dt / cap;
                    p["FilledRatio"] = Math.Max(0, Math.Min(1, fr));
                    break;
                }
                case "PistonBase":
                case "ExtendedPistonBase":
                {
                    double pos = D(b, "CurrentPosition"), v = D(b, "Velocity"), lo = D(b, "MinLimit"), hi = D(b, "MaxLimit", 10);
                    if (b.Enabled && v != 0) pos += v * dt;
                    pos = Math.Max(lo, Math.Min(hi, pos));
                    p["CurrentPosition"] = pos;
                    p["Status"] = !b.Enabled || v == 0 ? "Stopped" : v > 0 ? (pos >= hi ? "Extended" : "Extending") : (pos <= lo ? "Retracted" : "Retracting");
                    break;
                }
                case "MotorStator":
                case "MotorAdvancedStator":
                {
                    if (!b.Enabled || B(b, "RotorLock")) break;
                    double deg = D(b, "Angle") * 180 / Math.PI + D(b, "TargetVelocityRPM") * 6 * dt;
                    double up = D(b, "UpperLimitDeg", 361), low = D(b, "LowerLimitDeg", -361);
                    if (up <= 360) deg = Math.Min(deg, up);
                    if (low >= -360) deg = Math.Max(deg, low);
                    if (up > 360 && low < -360) deg = ((deg % 360) + 360) % 360;
                    p["Angle"] = deg * Math.PI / 180;
                    break;
                }
                case "Door": case "AirtightHangarDoor": case "AirtightSlideDoor": case "AdvancedDoor":
                {
                    string st = S(b, "Status", "Closed");
                    double r = D(b, "OpenRatio");
                    if (!b.Enabled) break;
                    if (st == "Opening") { r = Math.Min(1, r + dt); if (r >= 1) st = "Open"; }
                    else if (st == "Closing") { r = Math.Max(0, r - dt); if (r <= 0) st = "Closed"; }
                    p["OpenRatio"] = r; p["Status"] = st;
                    break;
                }
                case "Thrust":
                {
                    double max = D(b, "MaxThrust", 1), ov = Math.Max(0, Math.Min(max, D(b, "ThrustOverride")));
                    p["ThrustOverride"] = ov;
                    p["ThrustOverridePercentage"] = max > 0 ? ov / max : 0;
                    p["CurrentThrust"] = b.Enabled ? ov : 0.0;
                    break;
                }
                case "TimerBlock":
                {
                    if (!B(b, "IsCountingDown")) break;
                    double rem = D(b, "_Remaining") - dt;
                    if (rem <= 0) { p["IsCountingDown"] = false; rem = 0; p["_Fired"] = true; }
                    p["_Remaining"] = rem;
                    break;
                }
            }
        }

        // Процентные шкалы для GUI: всё, что можно показать как заполненность.
        public static List<Dictionary<string, object>> Bars(BlockModel b, Definitions defs)
        {
            var list = new List<Dictionary<string, object>>();
            Action<string, double, string> add = (label, v, text) => list.Add(new Dictionary<string, object>
            {
                { "label", label }, { "value", double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, v)) }, { "text", text },
            });
            switch (b.TypeId)
            {
                case "BatteryBlock":
                {
                    double max = D(b, "MaxStoredPower", 1), cur = D(b, "CurrentStoredPower");
                    add("Заряд", max > 0 ? cur / max : 0, F(cur, 2) + " / " + F(max, 2) + " МВт·ч");
                    double mo = D(b, "MaxOutput", 1);
                    if (D(b, "CurrentOutput") > 0) add("Выход", D(b, "CurrentOutput") / mo, F(D(b, "CurrentOutput"), 2) + " МВт");
                    if (D(b, "CurrentInput") > 0) add("Вход", D(b, "CurrentInput") / D(b, "MaxInput", 1), F(D(b, "CurrentInput"), 2) + " МВт");
                    break;
                }
                case "OxygenTank":
                    add("Заполнение", D(b, "FilledRatio"), F(D(b, "FilledRatio") * D(b, "Capacity") / 1000, 1) + " / " + F(D(b, "Capacity") / 1000, 1) + " м³");
                    break;
                case "PistonBase":
                case "ExtendedPistonBase":
                    add("Выдвинут", D(b, "CurrentPosition") / D(b, "HighestPosition", 10), F(D(b, "CurrentPosition"), 2) + " м");
                    break;
                case "MotorStator":
                case "MotorAdvancedStator":
                {
                    double deg = D(b, "Angle") * 180 / Math.PI;
                    add("Угол", deg / 360, F(deg, 1) + "°");
                    break;
                }
                case "Door": case "AirtightHangarDoor": case "AirtightSlideDoor": case "AdvancedDoor":
                    add("Открыта", D(b, "OpenRatio"), F(D(b, "OpenRatio") * 100, 0) + " %");
                    break;
                case "Thrust":
                    add("Тяга", D(b, "CurrentThrust") / D(b, "MaxThrust", 1), F(D(b, "CurrentThrust") / 1000, 0) + " кН");
                    break;
                case "Reactor": case "SolarPanel": case "WindTurbine": case "HydrogenEngine":
                    if (b.TypeId == "HydrogenEngine") add("Топливо", D(b, "FilledRatio"), F(D(b, "FilledRatio") * 100, 0) + " %");
                    add("Выход", D(b, "CurrentOutput") / D(b, "MaxOutput", 1), F(D(b, "CurrentOutput"), 2) + " МВт");
                    break;
            }
            for (int i = 0; i < b.Inventories.Count; i++)
            {
                var inv = b.Inventories[i];
                double cur = InventoryVolume(inv, defs);
                add(b.Inventories.Count > 1 ? (i == 0 ? "Вход" : "Выход") : "Инвентарь", inv.MaxVolume > 0 ? cur / inv.MaxVolume : 0,
                    F(cur * 1000, 0) + " / " + F(inv.MaxVolume * 1000, 0) + " л");
            }
            return list;
        }

        public static double ItemVolume(ItemModel it, Definitions defs)
        {
            var d = defs.Item(it.TypeId, it.Subtype);
            return (d != null ? d.Volume : 1) / 1000.0;   // м³ на единицу
        }

        public static double ItemMass(ItemModel it, Definitions defs)
        {
            var d = defs.Item(it.TypeId, it.Subtype);
            return d != null ? d.Mass : 1;
        }

        public static double InventoryVolume(InventoryModel inv, Definitions defs)
        {
            return inv.Items.Sum(it => it.Amount * ItemVolume(it, defs));
        }

        static string F(double v, int digits) { return v.ToString("F" + digits, CultureInfo.InvariantCulture); }

        public static string DetailedInfo(BlockModel b, Definitions defs)
        {
            var def = defs.Block(b.TypeId, b.Subtype);
            var lines = new List<string> { "Тип: " + (def != null ? def.DisplayName : b.TypeId) };
            switch (b.TypeId)
            {
                case "BatteryBlock":
                    lines.Add("Макс. выход: " + F(D(b, "MaxOutput"), 2) + " MW");
                    lines.Add("Макс. ёмкость: " + F(D(b, "MaxStoredPower"), 2) + " MWh");
                    lines.Add("Текущий вход: " + F(D(b, "CurrentInput"), 2) + " MW");
                    lines.Add("Текущий выход: " + F(D(b, "CurrentOutput"), 2) + " MW");
                    lines.Add("Запасено: " + F(D(b, "CurrentStoredPower"), 2) + " MWh");
                    break;
                case "OxygenTank":
                    lines.Add("Заполнено: " + F(D(b, "FilledRatio") * 100, 1) + "% (" + F(D(b, "FilledRatio") * D(b, "Capacity"), 0) + "L/" + F(D(b, "Capacity"), 0) + "L)");
                    break;
            }
            return string.Join("\n", lines);
        }
    }
}
