using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SeHelper
{
    // Проверка whitelist. Основной путь — настоящий whitelist игры (GameWhitelist); правила ниже —
    // приближённая копия на случай, если его не удалось собрать (например, игра обновилась и поменяла классы).
    public static class Whitelist
    {
        static readonly string[] DeniedNs =
        {
            "System.IO", "System.Reflection", "System.Threading", "System.Net", "System.Runtime", "System.Diagnostics",
            "System.Security", "System.Web", "System.Data", "System.Configuration", "System.Media", "System.Windows",
            "System.Timers", "System.CodeDom", "System.Resources", "System.ComponentModel", "Microsoft",
            "Sandbox.ModAPI", "VRage.Game.ModAPI", "Sandbox.Game", "Sandbox.Engine", "Sandbox.Graphics", "Sandbox.Definitions",
            "VRage.Plugins", "VRage.FileSystem", "VRage.Utils", "VRage.Network", "VRage.Serialization", "VRage.Scripting",
            "VRageRender", "SpaceEngineers.Game.ModAPI", "Sandbox.Common.ObjectBuilders",
        };

        static readonly string[] AllowedNs =
        {
            "Sandbox.ModAPI.Ingame", "Sandbox.ModAPI.Interfaces", "VRage.Game.ModAPI.Ingame", "Sandbox.Game.EntityComponents",
            "SpaceEngineers.Game.ModAPI.Ingame", "Sandbox.Game.Gui", "Sandbox.Game.Localization", "System.Collections",
            "System.Text.RegularExpressions",
        };

        static readonly string[] DeniedTypes =
        {
            "System.Environment", "System.GC", "System.AppDomain", "System.Activator", "System.Console", "System.Type",
            "System.Runtime.InteropServices.Marshal", "System.Delegate", "System.MulticastDelegate", "System.IntPtr", "System.UIntPtr",
        };

        static readonly string[] AllowedMembers = { "System.Environment.NewLine", "System.Type.Name", "System.Type.FullName" };

        public static bool IsAllowed(ISymbol s)
        {
            if (s == null) return true;
            if (s.Kind == SymbolKind.Local || s.Kind == SymbolKind.Parameter || s.Kind == SymbolKind.RangeVariable || s.Kind == SymbolKind.Label)
                return true;
            // символы из самого скрипта всегда разрешены
            if (s.Locations.Any(l => l.IsInSource)) return true;

            // если whitelist игры собран, решает он (кроме пространств имён: «Sandbox» сам не разрешён,
            // но внутри есть разрешённое, и автокомплит должен его показывать)
            if (s.Kind != SymbolKind.Namespace)
            {
                var game = GameWhitelist.IsWhitelisted(s);
                if (game.HasValue) return game.Value;
            }

            if (s.Kind == SymbolKind.Namespace)
            {
                var n = s.ToDisplayString();
                return !Denied(n) || AllowedNs.Any(a => a.StartsWith(n + ".") || a == n) || DeniedNs.Any(d => d.StartsWith(n + "."));
            }
            var type = s as INamedTypeSymbol ?? s.ContainingType;
            if (type != null)
            {
                var full = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                int lt = full.IndexOf('<'); if (lt >= 0) full = full.Substring(0, lt);
                if (DeniedTypes.Contains(full))
                {
                    if (s is INamedTypeSymbol) return false;
                    return AllowedMembers.Contains(full + "." + s.Name);
                }
            }
            var ns = (s is INamespaceSymbol ? (INamespaceSymbol)s : s.ContainingNamespace);
            if (ns == null || ns.IsGlobalNamespace) return true;
            return !Denied(ns.ToDisplayString());
        }

        static bool IsPartiallyDenied(INamedTypeSymbol t)
        {
            var full = t.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            return AllowedMembers.Any(m => m.StartsWith(full + "."));
        }

        static bool Denied(string ns)
        {
            if (AllowedNs.Any(a => ns == a || ns.StartsWith(a + "."))) return false;
            return DeniedNs.Any(d => ns == d || ns.StartsWith(d + "."));
        }

        public static void Check(PreparedSource ps, SemanticModel model, Action<TextSpan, string> report)
        {
            var root = ps.Tree.GetRoot();
            int from = ps.Offset, to = ps.Offset + ps.UserCode.Length;
            foreach (var node in root.DescendantNodes(n => n.Span.End >= from && n.Span.Start <= to))
            {
                if (node.Span.Start < from || node.Span.Start > to) continue;

                if (node.IsKind(SyntaxKind.UnsafeStatement) || node.IsKind(SyntaxKind.FixedStatement) || node.IsKind(SyntaxKind.StackAllocArrayCreationExpression) || node.IsKind(SyntaxKind.PointerType))
                { report(node.Span, "Небезопасный код (unsafe/fixed/stackalloc/указатели) в игре запрещён"); continue; }
                if (node.IsKind(SyntaxKind.DestructorDeclaration))
                { report(node.Span, "Финализаторы (~Program) в скриптах игры запрещены"); continue; }

                var name = node as SimpleNameSyntax;
                if (name == null) continue;
                // отмечаем только самую правую часть квалифицированного имени, чтобы не дублировать ошибку
                if (name.Parent is QualifiedNameSyntax && ((QualifiedNameSyntax)name.Parent).Left == name) continue;
                var si = model.GetSymbolInfo(name);
                var sym = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
                if (sym == null) continue;
                // пространства имён проверяем только в using, в коде достаточно ругнуться на сам тип
                if (sym is INamespaceSymbol && !name.Ancestors().Any(a => a is UsingDirectiveSyntax)) continue;
                if (sym.Kind == SymbolKind.DynamicType) { report(name.Span, "dynamic в скриптах игры запрещён"); continue; }
                if (name.IsVar) continue;   // ругаемся на явное упоминание типа, а не на var
                if (IsAllowed(sym)) continue;

                var type = sym as INamedTypeSymbol;
                if (type != null)
                {
                    // Environment.NewLine разрешён: для «частично запрещённых» типов проверяем член, а не сам тип
                    var ma = name.Parent as MemberAccessExpressionSyntax;
                    if (ma != null && ma.Expression == name && IsPartiallyDenied(type)) continue;
                }
                else if (sym.ContainingType != null && !IsPartiallyDenied(sym.ContainingType))
                    continue;   // весь тип запрещён — уже отметили его имя
                report(name.Span, "Запрещено whitelist'ом игры: " + sym.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            }
        }
    }
}
