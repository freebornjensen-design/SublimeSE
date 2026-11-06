# Space Engineers: автокомплит, линтер и симулятор скриптов программируемого блока для Sublime Text 4.
# Тяжёлая работа (Roslyn, whitelist, симуляция) — в SeHelper.exe, плагин с ним общается по HTTP.

import html
import json
import os
import re
import subprocess
import threading
import time
import urllib.request
import webbrowser

import sublime
import sublime_plugin

PKG_DIR = os.path.dirname(os.path.abspath(__file__))
SETTINGS_FILE = "SpaceEngineers.sublime-settings"
STEAM_DEFAULT = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64"


def settings():
    return sublime.load_settings(SETTINGS_FILE)


def base_url():
    return "http://127.0.0.1:%d" % settings().get("helper_port", 8765)


# ---------------------------------------------------------------- хелпер

class Helper:
    proc = None
    lock = threading.Lock()
    ready = False

    @classmethod
    def request(cls, path, data=None, timeout=15):
        body = None if data is None else json.dumps(data).encode("utf-8")
        req = urllib.request.Request(base_url() + path, data=body, method="GET" if data is None else "POST",
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))

    @classmethod
    def ping(cls):
        try:
            cls.request("/api/ping", timeout=1)
            return True
        except Exception:
            return False

    @classmethod
    def ensure(cls):
        """Поднимает SeHelper, если он ещё не запущен. Вызывать только из фонового потока."""
        if cls.ready and cls.ping():
            return True
        with cls.lock:
            if cls.ping():
                cls.ready = True
                return True
            exe = os.path.join(PKG_DIR, "bin", "SeHelper.exe")
            if not os.path.exists(exe):
                sublime.status_message("SE: не найден %s — запусти install.ps1" % exe)
                return False
            bin64 = find_bin64()
            if not bin64:
                sublime.error_message("Space Engineers: не нашёл папку игры.\n"
                                      "Укажи \"game_bin64\" в Preferences → Package Settings → Space Engineers.")
                return False
            args = [exe, "--port", str(settings().get("helper_port", 8765)), "--bin64", bin64,
                    "--web", os.path.join(PKG_DIR, "web")]
            flags = 0x08000000 if os.name == "nt" else 0   # CREATE_NO_WINDOW
            cls.proc = subprocess.Popen(args, creationflags=flags, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            sublime.status_message("SE: запускаю SeHelper…")
            for _ in range(120):
                time.sleep(0.25)
                if cls.ping():
                    cls.ready = True
                    sublime.status_message("SE: SeHelper готов")
                    return True
                if cls.proc.poll() is not None:
                    break
            sublime.status_message("SE: SeHelper не стартовал (код %s)" % (cls.proc.poll(),))
            return False

    @classmethod
    def stop(cls):
        if cls.proc and cls.proc.poll() is None:
            cls.proc.kill()
        cls.proc = None
        cls.ready = False


def find_bin64():
    p = settings().get("game_bin64") or ""
    if p and os.path.exists(os.path.join(p, "Sandbox.Common.dll")):
        return p
    if os.path.exists(os.path.join(STEAM_DEFAULT, "Sandbox.Common.dll")):
        return STEAM_DEFAULT
    # библиотеки Steam на других дисках
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = winreg.QueryValueEx(k, "SteamPath")[0]
        vdf = os.path.join(steam, "steamapps", "libraryfolders.vdf")
        libs = [steam] + re.findall(r'"path"\s+"([^"]+)"', open(vdf, encoding="utf-8").read())
        for lib in libs:
            cand = os.path.join(lib.replace("\\\\", "\\"), "steamapps", "common", "SpaceEngineers", "Bin64")
            if os.path.exists(os.path.join(cand, "Sandbox.Common.dll")):
                return cand
    except Exception:
        pass
    return None


def async_call(fn, *args):
    sublime.set_timeout_async(lambda: fn(*args), 0)


def plugin_loaded():
    # файлы, открытые до загрузки плагина (например, `subl file.cs`), не получат on_activated — проверим их сами
    def check_open_views():
        print("SpaceEngineers: плагин загружен, пакет в", PKG_DIR)
        for w in sublime.windows():
            for v in w.views():
                if is_se(v):
                    lint(v)
    sublime.set_timeout_async(check_open_views, 300)


def plugin_unloaded():
    if settings().get("stop_helper_on_exit", True):
        Helper.stop()


# ---------------------------------------------------------------- что считаем скриптом SE

SE_MARKERS = re.compile(r"MyGridProgram|GridTerminalSystem|\bvoid\s+Main\s*\(\s*(string|\))|UpdateFrequency\.|IMyTerminalBlock")


def is_se(view):
    if view is None or not view.match_selector(0, "source.cs"):
        return False
    flag = view.settings().get("se_script")
    if flag is not None:
        return flag
    if settings().get("treat_all_cs_as_se", False):
        return True
    fn = view.file_name()
    if fn and os.path.exists(os.path.splitext(fn)[0] + ".se-grid.json"):
        return True
    return bool(SE_MARKERS.search(view.substr(sublime.Region(0, min(view.size(), 60000)))))


def text_of(view):
    return view.substr(sublime.Region(0, view.size()))


def utf16_offset(text, pt):
    return len(text[:pt].encode("utf-16-le")) // 2


# ---------------------------------------------------------------- линтер

DIAGS = {}   # view.id() -> список диагностик


def lint(view):
    if not view.is_valid() or not is_se(view) or not Helper.ensure():
        return
    code = text_of(view)
    try:
        res = Helper.request("/api/diagnostics", {"code": code, "path": view.file_name() or ""})
    except Exception as e:
        view.set_status("se", "SE: хелпер недоступен")
        return
    if text_of(view) != code:
        return   # пока считали, текст уже поменялся — дождёмся следующего прогона
    show_diagnostics(view, res.get("diagnostics", []))


def show_diagnostics(view, diags):
    DIAGS[view.id()] = diags
    errs, warns, err_ann, warn_ann = [], [], [], []
    inline = settings().get("inline_errors", True)
    for d in diags:
        a = view.text_point_utf16(d["line"], d["col"], clamp_column=True)
        b = view.text_point_utf16(d["endLine"], d["endCol"], clamp_column=True)
        if b <= a:
            b = view.word(a).end() if view.word(a).end() > a else a + 1
        r = sublime.Region(a, b)
        d["_region"] = (a, b)
        msg = '<span style="color:var(--foreground)">%s</span>' % html.escape(d["message"])
        if d["severity"] == "error":
            errs.append(r)
            err_ann.append(msg)
        elif d["severity"] == "warning":
            warns.append(r)
            warn_ann.append(msg)
    flags = sublime.DRAW_SQUIGGLY_UNDERLINE | sublime.DRAW_NO_FILL | sublime.DRAW_NO_OUTLINE
    view.add_regions("se_errors", errs, "region.redish", "circle", flags,
                     annotations=err_ann if inline else [], annotation_color="#e06c75")
    view.add_regions("se_warnings", warns, "region.orangish", "dot", flags,
                     annotations=warn_ann if inline else [], annotation_color="#d19a66")
    ne = len(errs)
    nw = len(warns)
    view.set_status("se", "SE: ✓" if ne == nw == 0 else "SE: %d ✗  %d ⚠" % (ne, nw))


class _Debounce:
    counters = {}

    @classmethod
    def run(cls, view, delay_ms, fn):
        vid = view.id()
        cls.counters[vid] = cls.counters.get(vid, 0) + 1
        mine = cls.counters[vid]

        def fire():
            if cls.counters.get(vid) == mine:
                fn(view)
        sublime.set_timeout_async(fire, delay_ms)


# ---------------------------------------------------------------- события

KIND = {
    "method": sublime.KIND_FUNCTION, "property": (sublime.KIND_ID_VARIABLE, "p", "Свойство"),
    "field": sublime.KIND_VARIABLE, "variable": sublime.KIND_VARIABLE, "constant": (sublime.KIND_ID_VARIABLE, "c", "Константа"),
    "event": (sublime.KIND_ID_FUNCTION, "e", "Событие"), "namespace": sublime.KIND_NAMESPACE,
    "class": sublime.KIND_TYPE, "struct": (sublime.KIND_ID_TYPE, "s", "Структура"), "interface": (sublime.KIND_ID_TYPE, "i", "Интерфейс"),
    "enum": (sublime.KIND_ID_TYPE, "e", "Перечисление"), "keyword": sublime.KIND_KEYWORD,
}


class SeEvents(sublime_plugin.EventListener):

    def on_activated_async(self, view):
        if not is_se(view):
            return
        triggers = view.settings().get("auto_complete_triggers") or []
        if not any(t.get("selector") == "source.cs" and "." in t.get("characters", "") for t in triggers):
            triggers.append({"selector": "source.cs", "characters": "."})
            view.settings().set("auto_complete_triggers", triggers)
        if view.id() not in DIAGS:
            lint(view)

    def on_modified_async(self, view):
        if is_se(view):
            _Debounce.run(view, settings().get("lint_delay_ms", 500), lint)

    def on_post_save_async(self, view):
        if not is_se(view):
            return
        lint(view)
        if settings().get("load_into_simulator_on_save", True) and Helper.ensure():
            try:
                Helper.request("/api/load", {"path": view.file_name() or "", "code": text_of(view)}, timeout=30)
                sublime.status_message("SE: скрипт загружен в симулятор")
            except Exception:
                pass

    def on_close(self, view):
        DIAGS.pop(view.id(), None)

    def on_query_completions(self, view, prefix, locations):
        if not is_se(view) or len(locations) != 1:
            return None
        cl = sublime.CompletionList()
        pt = locations[0]
        code = text_of(view)
        pos = utf16_offset(code, pt)
        member = view.substr(sublime.Region(pt - len(prefix) - 1, pt - len(prefix))) == "."

        def work():
            if not Helper.ensure():
                cl.set_completions([])
                return
            try:
                items = Helper.request("/api/complete", {"code": code, "pos": pos}, timeout=10).get("items", [])
            except Exception:
                cl.set_completions([])
                return
            out = []
            for it in items:
                name = it["name"]
                if it.get("method"):
                    contents = name + ("(${1})" if it.get("params") else "()") + "$0"
                    fmt = sublime.COMPLETION_FORMAT_SNIPPET
                else:
                    contents, fmt = name, sublime.COMPLETION_FORMAT_TEXT
                detail = it.get("detail") or ""
                doc = it.get("doc") or ""
                out.append(sublime.CompletionItem(
                    trigger=name, annotation=detail[:40], completion=contents, completion_format=fmt,
                    kind=KIND.get(it.get("kind"), sublime.KIND_AMBIGUOUS),
                    details=html.escape(doc[:220]) if doc else html.escape(detail)))
            flags = sublime.INHIBIT_WORD_COMPLETIONS | sublime.INHIBIT_EXPLICIT_COMPLETIONS if member else 0
            cl.set_completions(out, flags)

        sublime.set_timeout_async(work, 0)
        return cl

    def on_hover(self, view, point, hover_zone):
        if hover_zone != sublime.HOVER_TEXT or not is_se(view):
            return
        msgs = [d for d in DIAGS.get(view.id(), []) if "_region" in d and d["_region"][0] <= point <= d["_region"][1]]

        def work():
            info = None
            if Helper.ensure():
                code = text_of(view)
                try:
                    info = Helper.request("/api/hover", {"code": code, "pos": utf16_offset(code, point)}, timeout=5)
                except Exception:
                    info = None
            parts = []
            for d in msgs:
                color = "#e06c75" if d["severity"] == "error" else "#d19a66"
                parts.append('<div style="color:%s">%s <b>%s</b>: %s</div>' % (
                    color, "✗" if d["severity"] == "error" else "⚠", html.escape(d["id"]), html.escape(d["message"])))
            if info and info.get("signature"):
                parts.append('<div style="font-family:monospace;margin-top:4px"><b>%s</b></div>' % html.escape(info["signature"]))
                if info.get("doc"):
                    parts.append("<div>%s</div>" % html.escape(info["doc"]))
                for p in info.get("params") or []:
                    if p.get("doc"):
                        parts.append('<div><i>%s</i> — %s</div>' % (html.escape(p["name"]), html.escape(p["doc"])))
                if info.get("returns"):
                    parts.append("<div><i>возвращает</i> — %s</div>" % html.escape(info["returns"]))
                if not info.get("allowed", True):
                    parts.append('<div style="color:#e06c75">Запрещено whitelist\'ом игры</div>')
            if parts:
                body = '<body id="se-hover" style="margin:6px">%s</body>' % "".join(parts)
                sublime.set_timeout(lambda: view.show_popup(body, sublime.HIDE_ON_MOUSE_MOVE_AWAY, point, max_width=700), 0)

        sublime.set_timeout_async(work, 0)


# ---------------------------------------------------------------- команды

class SeOpenSimulatorCommand(sublime_plugin.TextCommand):
    """Открыть GUI симулятора и загрузить в него текущий скрипт."""

    def run(self, edit):
        view = self.view

        def work():
            if not Helper.ensure():
                return
            if is_se(view):
                try:
                    Helper.request("/api/load", {"path": view.file_name() or "", "code": text_of(view)}, timeout=30)
                except Exception:
                    pass
            webbrowser.open(base_url() + "/")
        async_call(work)


class SeLoadScriptCommand(sublime_plugin.TextCommand):
    """Загрузить текущий файл в симулятор без сохранения."""

    def run(self, edit):
        view = self.view

        def work():
            if not Helper.ensure():
                return
            r = Helper.request("/api/load", {"path": view.file_name() or "", "code": text_of(view)}, timeout=30)
            show_diagnostics(view, r.get("diagnostics", []))
            sublime.status_message("SE: " + (r.get("error") or "скрипт загружен в симулятор"))
        async_call(work)


class SeRunCommand(sublime_plugin.WindowCommand):
    """Запустить Main(argument) в симуляторе, как кнопкой «Выполнить» в терминале."""

    last = ""

    def run(self, argument=None):
        if argument is None:
            self.window.show_input_panel("Аргумент для Main:", SeRunCommand.last, self.go, None, None)
        else:
            self.go(argument)

    def go(self, arg):
        SeRunCommand.last = arg

        def work():
            if Helper.ensure():
                Helper.request("/api/control", {"action": "run", "argument": arg})
                st = Helper.request("/api/state")
                echo = (st.get("echo") or "").strip()
                err = st.get("error") or ""
                sublime.status_message("SE: " + (err.split("\n")[0] if err else (echo.split("\n")[0] if echo else "выполнено")))
        async_call(work)


class SeControlCommand(sublime_plugin.WindowCommand):
    """start / pause / step / reset / recompile"""

    def run(self, action, ticks=1):
        def work():
            if Helper.ensure():
                Helper.request("/api/control", {"action": action, "ticks": ticks})
                sublime.status_message("SE: " + action)
        async_call(work)


class SeImportBlueprintCommand(sublime_plugin.WindowCommand):
    def run(self):
        def work():
            if not Helper.ensure():
                return
            names = Helper.request("/api/blueprints").get("blueprints", [])
            if not names:
                sublime.status_message("SE: локальных блупринтов нет")
                return

            def chosen(i):
                if i < 0:
                    return
                async_call(lambda: (Helper.request("/api/grid/import", {"name": names[i]}, timeout=60),
                                    sublime.status_message("SE: импортирован «%s»" % names[i])))
            sublime.set_timeout(lambda: self.window.show_quick_panel(names, chosen, placeholder="Блупринт для симуляции"), 0)
        async_call(work)


class SeRestartHelperCommand(sublime_plugin.WindowCommand):
    def run(self):
        Helper.stop()
        async_call(Helper.ensure)


class SeToggleScriptCommand(sublime_plugin.TextCommand):
    """Принудительно считать (или не считать) файл скриптом SE."""

    def run(self, edit):
        s = self.view.settings()
        s.set("se_script", not is_se(self.view))
        if not s.get("se_script"):
            self.view.erase_regions("se_errors")
            self.view.erase_regions("se_warnings")
            self.view.erase_status("se")
        else:
            async_call(lint, self.view)
        sublime.status_message("SE: режим скрипта " + ("включён" if s.get("se_script") else "выключен"))


class SeListErrorsCommand(sublime_plugin.TextCommand):
    def run(self, edit):
        diags = [d for d in DIAGS.get(self.view.id(), []) if "_region" in d]
        if not diags:
            sublime.status_message("SE: ошибок нет")
            return
        items = [sublime.QuickPanelItem("%d: %s" % (d["line"] + 1, d["message"]), annotation=d["id"],
                                        kind=(sublime.KIND_ID_COLOR_REDISH if d["severity"] == "error" else sublime.KIND_ID_COLOR_ORANGISH, "!", ""))
                 for d in diags]

        def go(i):
            if i >= 0:
                a = diags[i]["_region"][0]
                self.view.sel().clear()
                self.view.sel().add(a)
                self.view.show_at_center(a)
        self.view.window().show_quick_panel(items, go)


# ---- копирование для игры

PRELUDE_RX = re.compile(r"#region\s+Prelude.*?#endregion[^\n]*\n(.*)#region\s+PreludeFooter", re.S)


def game_code(text):
    m = PRELUDE_RX.search(text)
    code = m.group(1) if m else text
    if settings().get("copy_strip_comments", False):
        code = re.sub(r"^\s*//(?!/).*\n", "", code, flags=re.M)
    return code.strip("\n") + "\n"


class SeCopyForGameCommand(sublime_plugin.TextCommand):
    """Скопировать код для вставки в программируемый блок (без обёртки шаблона)."""

    def run(self, edit):
        code = game_code(text_of(self.view))
        sublime.set_clipboard(code)
        limit = 100000
        msg = "SE: скопировано %d символов" % len(code)
        if len(code) > limit:
            msg += " — больше лимита игры (%d)!" % limit
        sublime.status_message(msg)


# ---- новый скрипт

class SeNewScriptCommand(sublime_plugin.WindowCommand):
    def run(self):
        v = self.window.new_file(syntax="Packages/C#/C#.sublime-syntax")
        v.settings().set("se_script", True)
        tpl = sublime.load_resource("Packages/SpaceEngineers/templates/Script.cs")
        v.run_command("insert_snippet", {"contents": tpl})
        v.set_name("Script.cs")


# ---- аббревиатуры в духе Emmet

def _plural(word):
    if word.endswith("y"):
        return word[:-1] + "ies"
    if word.endswith("s"):
        return word + "es"
    return word + "s"


def _base_name(type_name):
    n = re.sub(r"^IMy", "", type_name)
    n = re.sub(r"(Block|Base)$", "", n) or n
    return n[0].lower() + n[1:]


def _ident(text, fallback):
    words = re.findall(r"[A-Za-z0-9]+", text)
    if not words:
        return fallback
    s = words[0].lower() + "".join(w[:1].upper() + w[1:].lower() for w in words[1:])
    return ("_" + s) if s[0].isdigit() else s


ABBR_RX = re.compile(r"^(\s*)(gbt|gbn|gbg|upd|lcd|fe|echo|ini|prop|act):(.*)$")


def expand_abbreviation(abbr, args):
    a = [x.strip() for x in args.split(":")] if args.strip() else []
    if abbr == "gbt":   # gbt:IMyBatteryBlock[:имя]
        t = a[0] if a else "IMyTerminalBlock"
        var = a[1] if len(a) > 1 else _plural(_base_name(t))
        return ("List<{t}> ${{1:{v}}} = new List<{t}>();\n"
                "GridTerminalSystem.GetBlocksOfType(${{1:{v}}}${{2:, b => b.IsSameConstructAs(Me)}});$0").format(t=t, v=var)
    if abbr == "gbn":   # gbn:IMyTextPanel:Имя блока
        t = a[0] if a else "IMyTerminalBlock"
        name = a[1] if len(a) > 1 else "Имя блока"
        var = _ident(name, _base_name(t))
        return '{t} ${{1:{v}}} = GridTerminalSystem.GetBlockWithName("${{2:{n}}}") as {t};$0'.format(t=t, v=var, n=name)
    if abbr == "gbg":   # gbg:IMyDoor:Группа
        t = a[0] if a else "IMyTerminalBlock"
        group = a[1] if len(a) > 1 else "Группа"
        var = _ident(group, _plural(_base_name(t)))
        return ('List<{t}> ${{1:{v}}} = new List<{t}>();\n'
                'GridTerminalSystem.GetBlockGroupWithName("${{2:{g}}}")?.GetBlocksOfType(${{1:{v}}});$0').format(t=t, v=var, g=group)
    if abbr == "upd":   # upd:10 | upd:1 | upd:100 | upd:once | upd:none
        f = (a[0] if a else "10").lower()
        val = {"1": "Update1", "10": "Update10", "100": "Update100", "once": "Once", "none": "None"}.get(f, "Update10")
        return "Runtime.UpdateFrequency = UpdateFrequency.%s;$0" % val
    if abbr == "lcd":   # lcd:Имя панели
        name = a[0] if a else "LCD"
        var = _ident(name, "lcd")
        return ('IMyTextSurface ${{1:{v}}} = (GridTerminalSystem.GetBlockWithName("${{2:{n}}}") as IMyTextSurfaceProvider)?.GetSurface(0);\n'
                '${{1:{v}}}.ContentType = ContentType.TEXT_AND_IMAGE;\n'
                '${{1:{v}}}.WriteText(${{3:"текст"}});$0').format(v=var, n=name)
    if abbr == "fe":    # fe:batteries
        coll = a[0] if a else "items"
        item = coll[:-3] + "y" if coll.endswith("ies") else coll[:-1] if coll.endswith("s") else "item"
        return "foreach (var ${1:%s} in %s)\n{\n\t$0\n}" % (item, coll)
    if abbr == "echo":  # echo:x,y
        names = [x.strip() for x in args.split(",") if x.strip()] or ["value"]
        # \$ — литеральный доллар интерполяции, а не поле сниппета
        return 'Echo(\\$"%s");$0' % ", ".join("%s: {%s}" % (n, n) for n in names)
    if abbr == "ini":   # ini — разбор CustomData через MyIni
        return ('MyIni ${1:ini} = new MyIni();\n'
                'MyIniParseResult result;\n'
                'if (!${1:ini}.TryParse(Me.CustomData, out result))\n'
                '\tthrow new Exception(result.ToString());\n'
                'var ${2:value} = ${1:ini}.Get("${3:section}", "${4:key}").${5:ToString()};$0')
    if abbr == "prop":  # prop:Velocity:float
        p = a[0] if a else "Velocity"
        t = a[1] if len(a) > 1 else "float"
        return 'block.SetValue<{t}>("{p}", ${{1:value}});$0'.format(t=t, p=p)
    if abbr == "act":   # act:OnOff_On
        return 'block.ApplyAction("%s");$0' % (a[0] if a else "OnOff_On")
    return None


class SeExpandAbbreviationCommand(sublime_plugin.TextCommand):
    def run(self, edit):
        view = self.view
        for sel in list(view.sel()):
            line = view.line(sel.b)
            before = view.substr(sublime.Region(line.a, sel.b))
            m = ABBR_RX.match(before)
            if not m:
                continue
            snippet = expand_abbreviation(m.group(2), m.group(3))
            if snippet is None:
                continue
            view.erase(edit, sublime.Region(line.a + len(m.group(1)), sel.b))
            view.run_command("insert_snippet", {"contents": snippet})
            return
        view.run_command("insert_best_completion", {"default": "\t", "exact": False})


# ---- doc-комментарии в духе DocBlockr

SIG_RX = re.compile(r"^\s*(?:(?:public|private|protected|internal|static|override|virtual|abstract|sealed|readonly|async|new)\s+)*"
                    r"(?:(?P<ret>[\w<>\[\],\.\s?]+?)\s+)?(?P<name>\w+)\s*(?:<[^>]*>)?\s*\((?P<params>[^)]*)\)")


def split_params(s):
    out, depth, cur = [], 0, ""
    for ch in s:
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur)
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur)
    names = []
    for p in out:
        p = p.split("=")[0].strip()
        if p:
            names.append(p.split()[-1])
    return names


class SeDocCommentCommand(sublime_plugin.TextCommand):
    def run(self, edit):
        view = self.view
        sel = view.sel()[0]
        line = view.line(sel.b)
        indent = re.match(r"\s*", view.substr(line)).group(0)
        nxt = view.substr(view.line(line.b + 1)) if line.b + 1 < view.size() else ""
        m = SIG_RX.match(nxt)
        lines = ["/ <summary>", "/// ${1:Описание}", "/// </summary>"]
        idx = 2
        if m:
            for p in split_params(m.group("params")):
                lines.append('/// <param name="%s">${%d}</param>' % (p, idx))
                idx += 1
            ret = (m.group("ret") or "").strip()
            if ret and ret != "void" and m.group("name") != "Program":
                lines.append("/// <returns>${%d}</returns>" % idx)
        snippet = ("\n" + indent).join(lines)
        view.run_command("insert_snippet", {"contents": snippet})


class SeDocNewlineCommand(sublime_plugin.TextCommand):
    """Enter внутри /// продолжает комментарий."""

    def run(self, edit):
        for sel in self.view.sel():
            # отступ проставит сам Sublime при вставке перевода строки
            self.view.run_command("insert", {"characters": "\n"})
            self.view.run_command("insert", {"characters": "/// "})
            break
