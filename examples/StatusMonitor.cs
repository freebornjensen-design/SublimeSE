// Пример скрипта для программируемого блока: сводка по батареям, грузу и бакам на LCD.
// Аргументы: "lcd:<имя>" — выбрать панель, "off" / "on" — выключить/включить двигатели.

List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
List<IMyCargoContainer> cargo = new List<IMyCargoContainer>();
List<IMyGasTank> tanks = new List<IMyGasTank>();
List<IMyThrust> thrusters = new List<IMyThrust>();
IMyTextSurface screen;
int runs;

public Program()
{
    GridTerminalSystem.GetBlocksOfType(batteries, b => b.IsSameConstructAs(Me));
    GridTerminalSystem.GetBlocksOfType(cargo);
    GridTerminalSystem.GetBlocksOfType(tanks);
    GridTerminalSystem.GetBlocksOfType(thrusters);
    var lcd = GridTerminalSystem.GetBlockWithName(Storage == "" ? "Дисплей 3" : Storage) as IMyTextPanel;
    screen = lcd != null ? (IMyTextSurface)lcd : Me.GetSurface(0);
    screen.ContentType = ContentType.TEXT_AND_IMAGE;
    screen.FontSize = 0.8f;
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
}

public void Save()
{
    Storage = (screen as IMyTerminalBlock)?.CustomName ?? "";
}

public void Main(string argument, UpdateType updateSource)
{
    if (argument.StartsWith("lcd:"))
    {
        var lcd = GridTerminalSystem.GetBlockWithName(argument.Substring(4)) as IMyTextPanel;
        if (lcd != null) screen = lcd;
    }
    else if (argument == "off" || argument == "on")
    {
        foreach (var t in thrusters) t.Enabled = argument == "on";
    }

    float stored = 0, max = 0;
    foreach (var b in batteries) { stored += b.CurrentStoredPower; max += b.MaxStoredPower; }

    double vol = 0, maxVol = 0;
    foreach (var c in cargo)
    {
        var inv = c.GetInventory(0);
        vol += (double)inv.CurrentVolume;
        maxVol += (double)inv.MaxVolume;
    }

    var sb = new StringBuilder();
    sb.AppendLine($"Статус · запуск #{++runs}");
    sb.AppendLine($"Батареи  {Bar(max > 0 ? stored / max : 0)} {stored:0.00}/{max:0.00} MWh");
    sb.AppendLine($"Груз     {Bar(maxVol > 0 ? vol / maxVol : 0)} {vol * 1000:0} L");
    foreach (var t in tanks)
        sb.AppendLine($"{t.CustomName,-8} {Bar(t.FilledRatio)}");
    sb.AppendLine($"Двигатели: {thrusters.Count(t => t.Enabled)}/{thrusters.Count} вкл");
    screen.WriteText(sb.ToString());

    Echo($"Батарей: {batteries.Count}, контейнеров: {cargo.Count}");
    Echo($"Последний запуск: {Runtime.LastRunTimeMs:0.000} мс");
}

string Bar(double ratio, int width = 10)
{
    int n = (int)Math.Round(MathHelper.Clamp(ratio, 0, 1) * width);
    return "[" + new string('|', n) + new string('.', width - n) + $"] {ratio:P0}";
}
