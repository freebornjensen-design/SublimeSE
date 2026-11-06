using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SeHelper
{
    // Подготовленный к компиляции исходник: либо файл-шаблон с классом Program,
    // либо «голый» скрипт как в игре, который мы оборачиваем так же, как игра.
    public class PreparedSource
    {
        public string UserCode;
        public string FullText;
        public int Offset;     // смещение пользовательского кода внутри FullText
        public bool Wrapped;
        public SyntaxTree Tree;
    }

    public class Diag
    {
        public int line, col, endLine, endCol;   // 0-based, UTF-16
        public string severity, id, message;
    }

    // XmlDocumentationProvider живёт в Roslyn Workspaces, которого в игре нет, поэтому свой.
    public class XmlDocs : DocumentationProvider
    {
        readonly string _file;
        Dictionary<string, string> _members;

        public XmlDocs(string file) { _file = file; }

        protected override string GetDocumentationForSymbol(string id, System.Globalization.CultureInfo culture, System.Threading.CancellationToken ct)
        {
            if (_members == null)
            {
                var d = new Dictionary<string, string>();
                try
                {
                    foreach (var m in XDocument.Load(_file).Descendants("member"))
                    {
                        var n = (string)m.Attribute("name");
                        if (n != null) d[n] = m.ToString();
                    }
                }
                catch { }
                _members = d;
            }
            string s;
            return _members.TryGetValue(id, out s) ? s : "";
        }

        public override bool Equals(object obj) { return obj is XmlDocs && ((XmlDocs)obj)._file == _file; }
        public override int GetHashCode() { return _file.GetHashCode(); }
    }

    public class GameApi
    {
        public readonly string Bin64, ContentDir;
        public readonly List<Assembly> GameAssemblies = new List<Assembly>();
        public readonly List<MetadataReference> References = new List<MetadataReference>();
        public readonly Definitions Defs;
        public readonly Textures Tex;
        public readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.None);

        // Те же неявные пространства имён, что видит скрипт в программируемом блоке.
        public static readonly string[] ImplicitUsings =
        {
            "System", "System.Collections", "System.Collections.Generic", "System.Collections.Immutable", "System.Linq", "System.Text",
            "Sandbox.ModAPI.Ingame", "Sandbox.ModAPI.Interfaces", "Sandbox.Game.EntityComponents", "SpaceEngineers.Game.ModAPI.Ingame",
            "VRage", "VRage.Collections", "VRage.Game", "VRage.Game.Components", "VRage.Game.GUI.TextPanel", "VRage.Game.ModAPI.Ingame",
            "VRage.Game.ModAPI.Ingame.Utilities", "VRage.Game.ObjectBuilders.Definitions", "VRageMath"
        };

        static readonly string[] GameDlls =
        {
            "Sandbox.Common", "Sandbox.Game", "SpaceEngineers.Game", "SpaceEngineers.ObjectBuilders",
            "VRage", "VRage.Game", "VRage.Library", "VRage.Math", "System.Collections.Immutable"
        };

        public GameApi(string bin64, string contentDir)
        {
            Bin64 = bin64; ContentDir = contentDir;
            var fw = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            foreach (var n in new[] { "mscorlib", "System", "System.Core", "netstandard", "System.Runtime" })
            {
                var p = Path.Combine(fw, n + ".dll");
                if (File.Exists(p)) References.Add(MetadataReference.CreateFromFile(p));
            }
            foreach (var n in GameDlls)
            {
                var p = Path.Combine(bin64, n + ".dll");
                if (!File.Exists(p)) continue;
                var xml = Path.ChangeExtension(p, ".xml");
                References.Add(File.Exists(xml)
                    ? MetadataReference.CreateFromFile(p, documentation: new XmlDocs(xml))
                    : MetadataReference.CreateFromFile(p));
                try { GameAssemblies.Add(Assembly.LoadFrom(p)); } catch { }
            }
            Defs = new Definitions(contentDir);
            Tex = new Textures(contentDir, Defs);
        }

        // ---------- подготовка исходника ----------

        static readonly Regex TemplateRx = new Regex(@"class\s+\w+\s*:\s*MyGridProgram\b");

        public PreparedSource Prepare(string code, string path = null)
        {
            var ps = new PreparedSource { UserCode = code };
            if (TemplateRx.IsMatch(code))
            {
                ps.FullText = code;
                ps.Offset = 0;
            }
            else
            {
                var prefix = string.Join("", ImplicitUsings.Select(u => "using " + u + ";\n")) +
                             "public sealed class Program : MyGridProgram {\n#line 1 \"Program\"\n";
                ps.Wrapped = true;
                ps.Offset = prefix.Length;
                ps.FullText = prefix + code + "\n#line default\n}\n";
            }
            // кодировка обязательна, иначе Roslyn не выпустит PDB (а он нужен для номеров строк в стектрейсе)
            ps.Tree = CSharpSyntaxTree.ParseText(ps.FullText, ParseOptions, ps.Wrapped ? "Wrapper" : "Program", System.Text.Encoding.UTF8);
            return ps;
        }

        public CSharpCompilation Compile(PreparedSource ps, bool debug = false)
        {
            var opts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(debug ? OptimizationLevel.Debug : OptimizationLevel.Release)
                .WithPlatform(Platform.X64)
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { { "CS0105", ReportDiagnostic.Suppress } });
            return CSharpCompilation.Create("Script_" + Guid.NewGuid().ToString("N"), new[] { ps.Tree }, References, opts);
        }

        // ---------- диагностика ----------

        public List<Diag> Diagnostics(string code)
        {
            var ps = Prepare(code);
            var comp = Compile(ps);
            var model = comp.GetSemanticModel(ps.Tree);
            var list = new List<Diag>();
            foreach (var d in comp.GetDiagnostics())
            {
                if (d.Severity == DiagnosticSeverity.Hidden || d.Location.SourceTree != ps.Tree) continue;
                var dg = ToDiag(ps, d.Location.SourceSpan, d.Severity == DiagnosticSeverity.Error ? "error" : d.Severity == DiagnosticSeverity.Warning ? "warning" : "info", d.Id, d.GetMessage());
                if (dg != null) list.Add(dg);
            }
            var analyzer = GameWhitelist.Analyzer;
            if (analyzer != null)
            {
                // тот же анализатор, которым игра проверяет скрипт перед компиляцией
                var withAnalyzers = comp.WithAnalyzers(ImmutableArray.Create(analyzer));
                var seen = new HashSet<string>();
                foreach (var d in withAnalyzers.GetAnalyzerDiagnosticsAsync().Result)
                {
                    if (d.Location.SourceTree != ps.Tree) continue;
                    // `new Stopwatch()` игра отмечает дважды (тип и конструктор) с одним текстом — оставляем одну
                    var msg = GameWhitelist.Translate(d);
                    if (!seen.Add(d.Location.SourceSpan + msg)) continue;
                    var dg = ToDiag(ps, d.Location.SourceSpan, "error", "SE001", msg);
                    if (dg != null) list.Add(dg);
                }
            }
            else
                Whitelist.Check(ps, model, (span, msg) =>
                {
                    var dg = ToDiag(ps, span, "error", "SE001", msg);
                    if (dg != null) list.Add(dg);
                });
            return list;
        }

        Diag ToDiag(PreparedSource ps, Microsoft.CodeAnalysis.Text.TextSpan span, string sev, string id, string msg)
        {
            if (ps.Wrapped && (span.Start < ps.Offset || span.Start > ps.Offset + ps.UserCode.Length)) return null;
            var a = ToUser(ps, span.Start);
            var b = ToUser(ps, Math.Max(span.End, span.Start));
            return new Diag { line = a.Item1, col = a.Item2, endLine = b.Item1, endCol = b.Item2, severity = sev, id = id, message = msg };
        }

        static Tuple<int, int> ToUser(PreparedSource ps, int fullPos)
        {
            int p = Math.Max(0, Math.Min(ps.UserCode.Length, fullPos - ps.Offset));
            int line = 0, lineStart = 0;
            for (int i = 0; i < p; i++)
                if (ps.UserCode[i] == '\n') { line++; lineStart = i + 1; }
            return Tuple.Create(line, p - lineStart);
        }

        // ---------- автокомплит ----------

        public List<Dictionary<string, object>> Complete(string code, int pos)
        {
            var ps = Prepare(code);
            var comp = Compile(ps);
            var model = comp.GetSemanticModel(ps.Tree);
            var root = ps.Tree.GetRoot();
            int p = ps.Offset + Math.Max(0, Math.Min(pos, code.Length));

            var token = p > 0 ? root.FindToken(p - 1) : root.FindToken(0);
            var prev = token;
            if ((token.IsKind(SyntaxKind.IdentifierToken) || SyntaxFacts.IsKeywordKind(token.Kind())) && token.Span.End == p)
                prev = token.GetPreviousToken();

            IEnumerable<ISymbol> symbols;
            bool member = false;
            if (prev.IsKind(SyntaxKind.DotToken))
            {
                member = true;
                ExpressionSyntax left = null;
                var ma = prev.Parent as MemberAccessExpressionSyntax;
                if (ma != null) left = ma.Expression;
                var qn = prev.Parent as QualifiedNameSyntax;
                if (qn != null) left = qn.Left;
                symbols = left == null ? Enumerable.Empty<ISymbol>() : MembersOf(model, left, p);
            }
            else
            {
                symbols = model.LookupSymbols(p);
            }

            var result = new List<Dictionary<string, object>>();
            foreach (var g in symbols
                .Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared && model.IsAccessible(p, s) && Whitelist.IsAllowed(s))
                .GroupBy(s => s.Name))
            {
                var s = g.First();
                int overloads = g.Count();
                result.Add(new Dictionary<string, object>
                {
                    { "name", s.Name },
                    { "kind", KindOf(s) },
                    { "detail", Detail(s) + (overloads > 1 ? "  (+" + (overloads - 1) + ")" : "") },
                    { "params", s is IMethodSymbol && ((IMethodSymbol)s).Parameters.Length > 0 && overloads == 1 ? 1 : (s is IMethodSymbol && overloads > 1 ? 2 : 0) },
                    { "method", s is IMethodSymbol },
                    { "doc", Summary(s) },
                });
            }
            if (!member)
                foreach (var kw in Keywords)
                    result.Add(new Dictionary<string, object> { { "name", kw }, { "kind", "keyword" }, { "detail", "" }, { "method", false }, { "params", 0 }, { "doc", "" } });
            return result;
        }

        static readonly string[] Keywords =
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default",
            "delegate","do","double","else","enum","event","explicit","extern","false","finally","float","for","foreach","if","implicit",
            "in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params",
            "private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","static","string","struct","switch",
            "this","throw","true","try","typeof","uint","ulong","unchecked","ushort","using","var","virtual","void","while","nameof"
        };

        static IEnumerable<ISymbol> MembersOf(SemanticModel model, ExpressionSyntax left, int p)
        {
            var sym = model.GetSymbolInfo(left).Symbol;
            if (sym == null)
            {
                var cand = model.GetSymbolInfo(left).CandidateSymbols;
                if (cand.Length > 0) sym = cand[0];
            }
            var ns = sym as INamespaceSymbol;
            if (ns != null) return model.LookupNamespacesAndTypes(p, ns);
            var type = sym as INamedTypeSymbol;
            if (type != null && !(left is ThisExpressionSyntax))
            {
                // Color.Red — это и тип, и свойство; Roslyn сам разрешит, но покажем оба набора
                var statics = model.LookupStaticMembers(p, type);
                var ti = model.GetTypeInfo(left).Type;
                return ti != null && !SymbolEqualityComparer(ti, type) ? statics.Concat(model.LookupSymbols(p, ti, includeReducedExtensionMethods: true).Where(s => !s.IsStatic)) : statics;
            }
            var t = model.GetTypeInfo(left).Type;
            if (t == null || t.TypeKind == TypeKind.Error) return Enumerable.Empty<ISymbol>();
            return model.LookupSymbols(p, t, includeReducedExtensionMethods: true).Where(s => !s.IsStatic || s is IMethodSymbol && ((IMethodSymbol)s).IsExtensionMethod);
        }

        static bool SymbolEqualityComparer(ITypeSymbol a, ITypeSymbol b) { return a.Equals(b); }

        static string KindOf(ISymbol s)
        {
            switch (s.Kind)
            {
                case SymbolKind.Method: return "method";
                case SymbolKind.Property: return "property";
                case SymbolKind.Field: return ((IFieldSymbol)s).IsConst || s.ContainingType != null && s.ContainingType.TypeKind == TypeKind.Enum ? "constant" : "field";
                case SymbolKind.Event: return "event";
                case SymbolKind.Local: return "variable";
                case SymbolKind.Parameter: return "variable";
                case SymbolKind.Namespace: return "namespace";
                case SymbolKind.NamedType:
                    var tk = ((INamedTypeSymbol)s).TypeKind;
                    return tk == TypeKind.Interface ? "interface" : tk == TypeKind.Enum ? "enum" : tk == TypeKind.Struct ? "struct" : "class";
                default: return "other";
            }
        }

        static readonly SymbolDisplayFormat Short = SymbolDisplayFormat.MinimallyQualifiedFormat;

        static string Detail(ISymbol s)
        {
            var m = s as IMethodSymbol;
            if (m != null) return m.ReturnType.ToDisplayString(Short) + " (" + string.Join(", ", m.Parameters.Select(x => x.Type.ToDisplayString(Short) + " " + x.Name)) + ")";
            var pr = s as IPropertySymbol; if (pr != null) return pr.Type.ToDisplayString(Short);
            var f = s as IFieldSymbol; if (f != null) return f.Type.ToDisplayString(Short);
            var l = s as ILocalSymbol; if (l != null) return l.Type.ToDisplayString(Short);
            var pa = s as IParameterSymbol; if (pa != null) return pa.Type.ToDisplayString(Short);
            var e = s as IEventSymbol; if (e != null) return e.Type.ToDisplayString(Short);
            return s.ContainingNamespace != null && !s.ContainingNamespace.IsGlobalNamespace ? s.ContainingNamespace.ToDisplayString() : "";
        }

        public static string Summary(ISymbol s)
        {
            try
            {
                var xml = s.GetDocumentationCommentXml();
                if (string.IsNullOrWhiteSpace(xml) && s is IMethodSymbol && ((IMethodSymbol)s).ReducedFrom != null)
                    xml = ((IMethodSymbol)s).ReducedFrom.GetDocumentationCommentXml();
                if (string.IsNullOrWhiteSpace(xml)) return "";
                var x = XElement.Parse(xml);
                var sum = x.Element("summary");
                return sum == null ? "" : DocText(sum);
            }
            catch { return ""; }
        }

        static string DocText(XElement e)
        {
            var parts = e.Nodes().Select(n =>
            {
                var t = n as XText; if (t != null) return t.Value;
                var el = n as XElement; if (el == null) return "";
                var cref = (string)el.Attribute("cref") ?? (string)el.Attribute("name") ?? (string)el.Attribute("langword");
                if (cref != null) return cref.Contains(":") ? cref.Substring(cref.IndexOf(':') + 1).Split('.').Last() : cref;
                return el.Value;
            });
            return Regex.Replace(string.Concat(parts), @"\s+", " ").Trim();
        }

        // ---------- подсказка при наведении ----------

        static readonly SymbolDisplayFormat HoverFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeType |
                               SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeRef)
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                                  SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut);

        public Dictionary<string, object> Hover(string code, int pos)
        {
            var ps = Prepare(code);
            var comp = Compile(ps);
            var model = comp.GetSemanticModel(ps.Tree);
            var root = ps.Tree.GetRoot();
            int p = ps.Offset + Math.Max(0, Math.Min(pos, code.Length));
            var token = root.FindToken(p);
            if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;
            var node = token.Parent;
            ISymbol sym = null;
            for (var n = node; n != null && sym == null; n = n.Parent)
            {
                var si = model.GetSymbolInfo(n);
                sym = si.Symbol ?? si.CandidateSymbols.FirstOrDefault() ?? model.GetDeclaredSymbol(n);
                if (n is StatementSyntax) break;
            }
            if (sym == null) return null;
            var res = new Dictionary<string, object>
            {
                { "signature", sym.ToMinimalDisplayString(model, p, HoverFormat) },
                { "kind", KindOf(sym) },
                { "doc", Summary(sym) },
                { "allowed", Whitelist.IsAllowed(sym) },
            };
            try
            {
                var xml = sym.GetDocumentationCommentXml();
                if (!string.IsNullOrWhiteSpace(xml))
                {
                    var x = XElement.Parse(xml);
                    res["params"] = x.Elements("param").Select(e => new Dictionary<string, object> { { "name", (string)e.Attribute("name") }, { "doc", DocText(e) } }).ToList();
                    var ret = x.Element("returns");
                    if (ret != null) res["returns"] = DocText(ret);
                }
            }
            catch { }
            return res;
        }

        // ---------- поиск интерфейсов блоков ----------

        Dictionary<string, Type> _ifaceCache;
        public Type FindIngameInterface(string name)
        {
            if (_ifaceCache == null)
            {
                _ifaceCache = new Dictionary<string, Type>();
                foreach (var a in GameAssemblies)
                {
                    Type[] ts;
                    try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(t => t != null).ToArray(); }
                    foreach (var t in ts)
                        if (t.IsInterface && t.IsPublic && t.Namespace != null && (t.Namespace.EndsWith(".Ingame") || t.Namespace == "Sandbox.ModAPI.Interfaces"))
                            if (!_ifaceCache.ContainsKey(t.Name)) _ifaceCache[t.Name] = t;
                }
            }
            Type r;
            return _ifaceCache.TryGetValue(name, out r) ? r : null;
        }

        public Type FindType(string fullName)
        {
            foreach (var a in GameAssemblies)
            {
                var t = a.GetType(fullName);
                if (t != null) return t;
            }
            return Type.GetType(fullName);
        }
    }
}
