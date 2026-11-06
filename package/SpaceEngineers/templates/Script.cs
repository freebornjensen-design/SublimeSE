// ${1:Название скрипта}
// Код ниже вставляется в программируемый блок как есть (SE: Скопировать код для игры).

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.${2:Update100};
    $0
}

public void Save()
{
    // Storage = ...;
}

public void Main(string argument, UpdateType updateSource)
{
    Echo(\$"Запуск: {updateSource}");
}
