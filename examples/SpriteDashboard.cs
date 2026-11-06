// Пример рисования спрайтами: панель заряда батарей с иконками и вращающейся стрелкой.
// Пишет на LCD с именем из CustomData программируемого блока (или на экран самого PB).

List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
IMyTextSurface surface;
float angle;

public Program()
{
    GridTerminalSystem.GetBlocksOfType(batteries);
    var lcd = GridTerminalSystem.GetBlockWithName(Me.CustomData.Trim()) as IMyTextSurface;
    surface = lcd ?? Me.GetSurface(0);
    surface.ContentType = ContentType.SCRIPT;
    surface.Script = "";
    surface.ScriptBackgroundColor = new Color(5, 10, 20);
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
}

public void Main(string argument, UpdateType updateSource)
{
    float stored = 0, max = 0;
    foreach (var b in batteries) { stored += b.CurrentStoredPower; max += b.MaxStoredPower; }
    float ratio = max > 0 ? stored / max : 0;
    angle += 0.15f;

    var size = surface.SurfaceSize;
    var center = size / 2;
    using (var frame = surface.DrawFrame())
    {
        // заголовок
        var title = MySprite.CreateText("ЭНЕРГИЯ", "Debug", new Color(120, 200, 255), 1.4f, TextAlignment.CENTER);
        title.Position = new Vector2(center.X, 20);
        frame.Add(title);

        // иконка и процент
        frame.Add(new MySprite(SpriteType.TEXTURE, "IconEnergy", new Vector2(110, 170), new Vector2(96, 96), Color.Yellow));
        frame.Add(new MySprite(SpriteType.TEXT, $"{ratio:P0}", new Vector2(300, 135), null, Color.White, "Debug", TextAlignment.CENTER, 2.2f));

        // шкала: рамка, фон и заполнение (обрезаем клипом, чтобы заливка не вылезла за рамку)
        var barPos = new Vector2(40, 260);
        var barSize = new Vector2(size.X - 80, 50);
        frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", barPos + barSize / 2, barSize + 6, new Color(80, 90, 110)));
        frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", barPos + barSize / 2, barSize, new Color(15, 20, 30)));
        frame.Add(MySprite.CreateClipRect(new Rectangle((int)barPos.X, (int)barPos.Y, (int)(barSize.X * ratio), (int)barSize.Y)));
        var fill = ratio > 0.5f ? Color.Lime : ratio > 0.2f ? Color.Orange : Color.Red;
        frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", barPos + barSize / 2, barSize, fill));
        frame.Add(MySprite.CreateClearClipRect());

        // индикатор активности: круг и вращающаяся стрелка
        frame.Add(new MySprite(SpriteType.TEXTURE, "Circle", new Vector2(110, 420), new Vector2(110, 110), new Color(30, 60, 90)));
        frame.Add(new MySprite(SpriteType.TEXTURE, "Arrow", new Vector2(110, 420), new Vector2(80, 80), Color.White, null, TextAlignment.CENTER, angle));

        // иконка предмета из инвентаря
        frame.Add(new MySprite(SpriteType.TEXTURE, "MyObjectBuilder_Ore/Iron", new Vector2(300, 420), new Vector2(90, 90), Color.White));
        frame.Add(new MySprite(SpriteType.TEXT, $"{stored:0.00} / {max:0.00} MWh", new Vector2(size.X - 20, 470), null, Color.Gray, "Monospace", TextAlignment.RIGHT, 0.7f));
    }
    Echo($"Батарей: {batteries.Count}, заряд {ratio:P1}");
}
